using System.Linq;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Tests for <see cref="FfmpegOutputParser"/> using output captured verbatim from a real
/// ffmpeg 7.1.4-Jellyfin binary, so the parser is validated against real-world formatting.
/// </summary>
public class FfmpegOutputParserTests
{
    // Real "ffmpeg -codecs" lines (verbatim), including the legend, a non-encodable codec,
    // codecs whose encoder shares the codec name (no "(encoders: ...)" group), and hardware encoders.
    private const string CodecsOutput = """
        Codecs:
         D..... = Decoding supported
         .E.... = Encoding supported
         ..V... = Video codec
         ..A... = Audio codec
         -------
         DEV.L. av1                  Alliance for Open Media AV1 (decoders: libdav1d av1 av1_cuvid av1_qsv) (encoders: libsvtav1 av1_nvenc av1_qsv av1_amf)
         DEV.LS h264                 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (decoders: h264 h264_qsv h264_cuvid) (encoders: libx264 libx264rgb h264_amf h264_mf h264_nvenc h264_qsv)
         DEV.L. hevc                 H.265 / HEVC (High Efficiency Video Coding) (decoders: hevc hevc_qsv hevc_cuvid) (encoders: libx265 hevc_amf hevc_d3d12va hevc_mf hevc_nvenc hevc_qsv)
         D.V.L. mpeg4                MPEG-4 part 2 (decoders: mpeg4 mpeg4_cuvid)
         DEA.L. aac                  AAC (Advanced Audio Coding) (decoders: aac aac_fixed libfdk_aac) (encoders: aac libfdk_aac aac_mf)
         D.A.L. aac_latm             AAC LATM (Advanced Audio Coding LATM syntax)
         DEAIL. eac3                 ATSC A/52B (AC-3, E-AC-3)
         DEAI.S flac                 FLAC (Free Lossless Audio Codec)
         DEAIL. opus                 Opus (Opus Interactive Audio Codec) (decoders: opus libopus) (encoders: opus libopus)
        """;

    private const string MuxersOutput = """
        File formats:
         D. = Demuxing supported
         .E = Muxing supported
         --
         E  ipod            iPod H.264 MP4 (MPEG-4 Part 14)
         E  matroska        Matroska
         E  mov             QuickTime / MOV
         E  mp4             MP4 (MPEG-4 Part 14)
         E  webm            WebM
        """;

    private const string TonemapHelp = """
        Filter tonemap
          Conversion to/from different dynamic ranges.
        tonemap AVOptions:
           tonemap           <int>        ..FV....... tonemap algorithm selection (from 0 to 6) (default none)
             linear          1            ..FV.......
             gamma           2            ..FV.......
             clip            3            ..FV.......
             reinhard        4            ..FV.......
             hable           5            ..FV.......
             mobius          6            ..FV.......
           param             <double>     ..FV....... tonemap parameter (from DBL_MIN to DBL_MAX) (default nan)
        """;

    private const string Libx264Help = """
        Encoder libx264 [libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10]:
            General capabilities: dr1 delay threads
            Threading capabilities: other
        libx264 AVOptions:
          -preset            <string>     E..V....... Set the encoding preset (cf. x264 --fullhelp) (default "medium")
          -tune              <string>     E..V....... Tune the encoding params (cf. x264 --fullhelp)
        """;

    private const string LibSvtAv1Help = """
        Encoder libsvtav1 [SVT-AV1(Scalable Video Technology for AV1) encoder]:
            General capabilities: dr1 delay
        libsvtav1 AVOptions:
          -preset            <int>        E..V....... Encoding preset (from -2 to 13) (default -2)
          -crf               <int>        E..V....... Constant Rate Factor value (from 0 to 63) (default 0)
        """;

    private const string H264NvencHelp = """
        Encoder h264_nvenc [NVIDIA NVENC H.264 encoder]:
            General capabilities: dr1 delay hardware
        h264_nvenc AVOptions:
          -preset            <int>        E..V....... Set the encoding preset (from 0 to 18) (default p4)
             default         0            E..V.......
             slow            1            E..V....... hq 2 passes
             medium          2            E..V....... hq 1 pass
             p4              15           E..V....... medium (default)
             p7              18           E..V....... slowest (best quality)
          -tune              <int>        E..V....... Set the encoding tuning info (from 1 to 4) (default hq)
        """;

    [Fact]
    public void ParseCodecs_ExtractsEncodableVideoAndAudioCodecs()
    {
        var codecs = FfmpegOutputParser.ParseCodecs(CodecsOutput);
        var names = codecs.Select(c => c.Name).ToList();

        Assert.Contains("h264", names);
        Assert.Contains("av1", names);
        Assert.Contains("hevc", names);
        Assert.Contains("aac", names);
        Assert.Contains("eac3", names);
        Assert.Contains("flac", names);
        Assert.Contains("opus", names);
    }

    [Fact]
    public void ParseCodecs_SkipsNonEncodableAndLegendLines()
    {
        var codecs = FfmpegOutputParser.ParseCodecs(CodecsOutput);
        var names = codecs.Select(c => c.Name).ToList();

        // aac_latm has no 'E' flag -> not an encode target.
        Assert.DoesNotContain("aac_latm", names);

        // mpeg4 here is decode-only (D.V.L.) -> excluded.
        Assert.DoesNotContain("mpeg4", names);

        // Legend rows must never be parsed as codecs.
        Assert.All(names, n => Assert.DoesNotContain("=", n));
    }

    [Fact]
    public void ParseCodecs_MapsCodecToItsEncoders()
    {
        var codecs = FfmpegOutputParser.ParseCodecs(CodecsOutput);
        var h264 = codecs.Single(c => c.Name == "h264");

        Assert.Equal(CodecMediaType.Video, h264.MediaType);
        var encoderNames = h264.Encoders.Select(e => e.Name).ToList();
        Assert.Contains("libx264", encoderNames);
        Assert.Contains("h264_nvenc", encoderNames);
    }

    [Fact]
    public void ParseCodecs_FlagsHardwareEncoders()
    {
        var codecs = FfmpegOutputParser.ParseCodecs(CodecsOutput);
        var h264 = codecs.Single(c => c.Name == "h264");

        Assert.False(h264.Encoders.Single(e => e.Name == "libx264").IsHardware);
        Assert.True(h264.Encoders.Single(e => e.Name == "h264_nvenc").IsHardware);
    }

    [Fact]
    public void ParseCodecs_UsesCodecNameWhenNoEncoderGroup()
    {
        var codecs = FfmpegOutputParser.ParseCodecs(CodecsOutput);
        var eac3 = codecs.Single(c => c.Name == "eac3");

        Assert.Equal(CodecMediaType.Audio, eac3.MediaType);
        Assert.Equal("eac3", Assert.Single(eac3.Encoders).Name);
    }

    [Fact]
    public void ParseCodecs_CleansEncoderDecoderGroupsFromDescription()
    {
        var codecs = FfmpegOutputParser.ParseCodecs(CodecsOutput);
        var h264 = codecs.Single(c => c.Name == "h264");

        Assert.DoesNotContain("encoders:", h264.Description);
        Assert.DoesNotContain("decoders:", h264.Description);
        Assert.Contains("H.264", h264.Description);
    }

    [Fact]
    public void ParseMuxers_ExtractsContainers()
    {
        var containers = FfmpegOutputParser.ParseMuxers(MuxersOutput).Select(c => c.Name).ToList();

        Assert.Contains("mp4", containers);
        Assert.Contains("matroska", containers);
        Assert.Contains("mov", containers);
        Assert.Contains("webm", containers);
    }

    [Fact]
    public void ParseTonemapModes_ExtractsAlgorithmsPlusNone()
    {
        var modes = FfmpegOutputParser.ParseTonemapModes(TonemapHelp);

        Assert.Equal(new[] { "none", "linear", "gamma", "clip", "reinhard", "hable", "mobius" }, modes);
    }

    [Fact]
    public void ParseEncoderPresets_StringPresetWithoutConstants_IsNone()
    {
        var info = FfmpegOutputParser.ParseEncoderPresets(Libx264Help, "libx264");

        Assert.Equal(PresetKind.None, info.Kind);
        Assert.Equal("medium", info.Default);
    }

    [Fact]
    public void ParseEncoderPresets_IntRange_IsParsed()
    {
        var info = FfmpegOutputParser.ParseEncoderPresets(LibSvtAv1Help, "libsvtav1");

        Assert.Equal(PresetKind.IntRange, info.Kind);
        Assert.Equal(-2, info.RangeMin);
        Assert.Equal(13, info.RangeMax);
        Assert.True(info.FromFfmpeg);
    }

    [Fact]
    public void ParseEncoderPresets_NamedConstants_AreParsed()
    {
        var info = FfmpegOutputParser.ParseEncoderPresets(H264NvencHelp, "h264_nvenc");

        Assert.Equal(PresetKind.NamedList, info.Kind);
        Assert.Contains("p4", info.Values);
        Assert.Contains("p7", info.Values);
        Assert.Contains("medium", info.Values);
        Assert.True(info.FromFfmpeg);
    }
}
