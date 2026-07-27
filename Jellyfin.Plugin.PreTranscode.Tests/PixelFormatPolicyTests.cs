using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// The output bit-depth policy. The plugin used to emit <c>-pix_fmt yuv420p</c> unconditionally, which
/// silently flattened every 10-bit source to 8 bits and — on an HDR source with tone-mapping off — left
/// 8-bit samples still tagged as HDR, the combination that causes banding.
/// </summary>
public class PixelFormatPolicyTests
{
    // Exactly what ffmpeg 8.1.1 reports for these encoders (captured from `ffmpeg -h encoder=NAME`).
    // The families genuinely differ, which is why the correct format is discovered and never assumed.
    private static readonly string[] Libx265 =
    {
        "yuv420p", "yuvj420p", "yuv422p", "yuvj422p", "yuv444p", "yuvj444p", "gbrp",
        "yuv420p10le", "yuv422p10le", "yuv444p10le", "gbrp10le", "yuv420p12le"
    };

    private static readonly string[] HevcNvenc =
    {
        "yuv420p", "nv12", "p010le", "yuv444p", "p016le", "nv16", "p210le", "bgr0", "cuda", "d3d11"
    };

    // H.264 via QSV offers no 10-bit format at all; asking for one would fail the encode.
    private static readonly string[] H264Qsv = { "nv12", "qsv" };

    private static EncodingProfile Profile(string codec = "hevc", PixelFormatMode mode = PixelFormatMode.Auto)
    {
        return new EncodingProfile { VideoCodec = codec, VideoEncoder = "libx265", PixelFormatMode = mode };
    }

    private static MediaProbeInfo Source(int bitDepth = 10, bool hdr = false)
    {
        return new MediaProbeInfo
        {
            VideoCodec = "hevc",
            BitDepth = bitDepth,
            PixelFormat = bitDepth > 8 ? "yuv420p10le" : "yuv420p",
            IsHdr = hdr
        };
    }

    [Fact]
    public void EightBitSource_StaysEightBit()
    {
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(Profile(), Source(bitDepth: 8), Libx265));
    }

    // The regression this policy exists for.
    [Fact]
    public void Auto_KeepsTenBitForHevc()
    {
        Assert.Equal("yuv420p10le", FfmpegCommandBuilder.ChoosePixelFormat(Profile(), Source(), Libx265));
    }

    [Fact]
    public void Auto_UsesTheFormatTheEncoderActuallyAdvertises()
    {
        var profile = Profile();
        profile.VideoEncoder = "hevc_nvenc";
        Assert.Equal("p010le", FfmpegCommandBuilder.ChoosePixelFormat(profile, Source(), HevcNvenc));
    }

    // H.264 High10 is not hardware-decodable on most clients, and playing everywhere is the point.
    [Fact]
    public void Auto_ForcesEightBitForH264()
    {
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(Profile("h264"), Source(), Libx265));
    }

    // ...unless the source is HDR, where 8-bit with surviving HDR tags is never a correct output.
    [Fact]
    public void Auto_KeepsTenBitForUntonemappedHdrEvenOnH264()
    {
        Assert.Equal("yuv420p10le", FfmpegCommandBuilder.ChoosePixelFormat(Profile("h264"), Source(hdr: true), Libx265));
    }

    [Fact]
    public void Tonemapping_AlwaysForcesEightBit()
    {
        var profile = Profile();
        profile.TonemapHdr = true;
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(profile, Source(hdr: true), Libx265));
    }

    [Fact]
    public void ForceYuv420p_IsTheOldBehaviour()
    {
        var profile = Profile(mode: PixelFormatMode.ForceYuv420p);
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(profile, Source(hdr: true), Libx265));
    }

    [Fact]
    public void KeepSourceBitDepth_AppliesEvenToH264()
    {
        var profile = Profile("h264", PixelFormatMode.KeepSourceBitDepth);
        Assert.Equal("yuv420p10le", FfmpegCommandBuilder.ChoosePixelFormat(profile, Source(), Libx265));
    }

    // An encoder that lists formats but no 10-bit one must fall back rather than fail the encode.
    [Fact]
    public void EncoderWithoutATenBitFormat_FallsBackToEightBit()
    {
        var profile = Profile("h264", PixelFormatMode.KeepSourceBitDepth);
        profile.VideoEncoder = "h264_qsv";
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(profile, Source(), H264Qsv));
    }

    // VAAPI reports only its hardware surface type, so nothing is known: never guess.
    [Fact]
    public void UnknownEncoderCapabilities_FallBackToEightBit()
    {
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(Profile(), Source(), Array.Empty<string>()));
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(Profile(), Source(), null));
    }

    // A probe that could not determine the depth must not be treated as 10-bit.
    [Fact]
    public void UnknownSourceBitDepth_IsTreatedAsEightBit()
    {
        Assert.Equal("yuv420p", FfmpegCommandBuilder.ChoosePixelFormat(Profile(), Source(bitDepth: 0), Libx265));
    }

    [Fact]
    public void BuildArguments_EmitsTheChosenFormat()
    {
        var args = FfmpegCommandBuilder.BuildArguments(
            Profile(), Source(), Array.Empty<ResolutionPreset>(), "in.mkv", "out.mkv", Libx265);

        var index = ((List<string>)args).IndexOf("-pix_fmt");
        Assert.True(index >= 0);
        Assert.Equal("yuv420p10le", args[index + 1]);
    }

    // A copy profile re-muxes without re-encoding, so no pixel format may be imposed at all.
    [Fact]
    public void CopyProfile_EmitsNoPixelFormat()
    {
        var profile = new EncodingProfile { VideoCodec = "copy", AudioCodec = "copy", Container = "matroska" };
        var args = FfmpegCommandBuilder.BuildArguments(
            profile, Source(), Array.Empty<ResolutionPreset>(), "in.mkv", "out.mkv", Libx265);

        Assert.DoesNotContain("-pix_fmt", args);
    }

    [Theory]
    [InlineData("yuv420p10le", 10)]
    [InlineData("yuv420p", 8)]
    [InlineData("p010le", 10)]
    [InlineData("p016le", 16)]
    [InlineData("gbrp12le", 12)]
    [InlineData("yuva420p10le", 10)]
    [InlineData("yuv444p16le", 16)]
    // Semi-planar: the digit after 'p' is the chroma layout, not part of the depth. p210le is 4:2:2
    // 10-bit and p216le is 4:2:2 16-bit — reading three digits would call them 210-bit and 216-bit.
    [InlineData("p210le", 10)]
    [InlineData("p216le", 16)]
    // Traps a naive "contains a number" test would fall into: yuv410p is 4:1:0 8-bit, nv12 is 8-bit.
    [InlineData("yuv410p", 8)]
    [InlineData("nv12", 8)]
    [InlineData("", 0)]
    public void BitDepthFromPixelFormat_ReadsTheFormatName(string pixelFormat, int expected)
    {
        Assert.Equal(expected, MediaProber.BitDepthFromPixelFormat(pixelFormat));
    }

    [Fact]
    public void Probe_PrefersBitsPerRawSample()
    {
        const string Json = @"{
            ""format"": { ""format_name"": ""matroska,webm"", ""duration"": ""3.0"" },
            ""streams"": [
                { ""codec_type"": ""video"", ""codec_name"": ""hevc"", ""width"": 1920, ""height"": 1080,
                  ""pix_fmt"": ""yuv420p10le"", ""bits_per_raw_sample"": ""10"" }
            ]
        }";

        Assert.Equal(10, MediaProber.Parse(Json, "/media/a.mkv").BitDepth);
    }

    // Not a hypothetical fallback: ffprobe 8.1.1 reports NO bits_per_raw_sample for libx265-encoded
    // Main 10 output, so the pixel-format path is what actually carries the depth in the common case.
    // This JSON is the real ffprobe output for a `-c:v libx265 -pix_fmt yuv420p10le` file, trimmed.
    [Fact]
    public void Probe_FallsBackToThePixelFormatWhenBitsPerRawSampleIsAbsent()
    {
        const string Json = @"{
            ""format"": { ""format_name"": ""matroska,webm"", ""duration"": ""2.000000"" },
            ""streams"": [
                { ""codec_type"": ""video"", ""codec_name"": ""hevc"", ""profile"": ""Main 10"",
                  ""width"": 320, ""height"": 240, ""pix_fmt"": ""yuv420p10le"", ""r_frame_rate"": ""25/1"" }
            ]
        }";

        var info = MediaProber.Parse(Json, "/media/a.mkv");
        Assert.Equal("yuv420p10le", info.PixelFormat);
        Assert.Equal(10, info.BitDepth);
    }

    // Verbatim first lines of a real `ffmpeg -h encoder=libx265` run.
    [Fact]
    public void ParsesTheSupportedPixelFormatsLine()
    {
        const string Help = @"Encoder libx265 [libx265 H.265 / HEVC]:
    General capabilities: dr1 delay threads
    Threading capabilities: other
    Supported pixel formats: yuv420p yuvj420p yuv422p yuv420p10le yuv444p10le gray
libx265 AVOptions:
  -crf               <float>      E..V....... set the x265 crf (from -1 to FLT_MAX)";

        var info = FfmpegOutputParser.ParseEncoderPresets(Help, "libx265");

        Assert.Contains("yuv420p10le", info.PixelFormats);
        Assert.Contains("yuv420p", info.PixelFormats);
        Assert.DoesNotContain("libx265", info.PixelFormats);
    }

    [Fact]
    public void EncoderHelpWithoutAPixelFormatLine_YieldsAnEmptyList()
    {
        var info = FfmpegOutputParser.ParseEncoderPresets("Encoder hevc_vaapi:\n    General capabilities: delay hardware\n", "hevc_vaapi");
        Assert.Empty(info.PixelFormats);
    }
}
