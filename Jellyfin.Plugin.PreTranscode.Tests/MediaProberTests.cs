using System.Linq;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class MediaProberTests
{
    // Matroska stores no per-stream bitrate, so ffprobe omits bit_rate on the video stream; the total
    // format bitrate includes the audio and must NOT be attributed to the video figure.
    private const string MkvJson = @"{
        ""format"": { ""format_name"": ""matroska,webm"", ""duration"": ""3.0"", ""size"": ""1000000"", ""bit_rate"": ""5640000"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""h264"", ""width"": 1920, ""height"": 1080, ""r_frame_rate"": ""25/1"" },
            { ""codec_type"": ""audio"", ""codec_name"": ""ac3"", ""channels"": 6, ""bit_rate"": ""640000"" }
        ]
    }";

    // A single-stream file whose video stream also lacks bit_rate: here the format total is video-only,
    // so using it is correct.
    private const string VideoOnlyMkvJson = @"{
        ""format"": { ""format_name"": ""matroska,webm"", ""duration"": ""3.0"", ""bit_rate"": ""5000000"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""h264"", ""width"": 1920, ""height"": 1080, ""r_frame_rate"": ""25/1"" }
        ]
    }";

    private const string Mp4Json = @"{
        ""format"": { ""format_name"": ""mov,mp4,m4a,3gp,3g2,mj2"", ""duration"": ""3.0"", ""bit_rate"": ""5640000"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""h264"", ""width"": 1920, ""height"": 1080, ""bit_rate"": ""5000000"", ""r_frame_rate"": ""25/1"" },
            { ""codec_type"": ""audio"", ""codec_name"": ""aac"", ""channels"": 2, ""bit_rate"": ""640000"" }
        ]
    }";

    // An mp4 whose first "video" stream is embedded cover art (disposition.attached_pic=1) followed by
    // the real H.264 video. The parser must ignore the cover and describe the real video.
    private const string CoverArtJson = @"{
        ""format"": { ""format_name"": ""mov,mp4,m4a,3gp,3g2,mj2"", ""duration"": ""3.0"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""mjpeg"", ""width"": 600, ""height"": 600, ""disposition"": { ""attached_pic"": 1 } },
            { ""codec_type"": ""video"", ""codec_name"": ""h264"", ""width"": 1920, ""height"": 1080, ""bit_rate"": ""5000000"", ""r_frame_rate"": ""25/1"" },
            { ""codec_type"": ""audio"", ""codec_name"": ""aac"", ""channels"": 2 }
        ]
    }";

    // ffprobe omits format.duration for some inputs and reports it per stream instead. Duration 0 reads
    // as "unknown" to the rule engine, which fails every VideoDurationMinutes condition for any operator,
    // so a duration-driven rule set goes silently dead on these files unless the fallback picks it up.
    private const string NoFormatDurationJson = @"{
        ""format"": { ""format_name"": ""matroska,webm"", ""size"": ""1000000"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""hevc"", ""width"": 1920, ""height"": 1080, ""duration"": ""7200.5"" },
            { ""codec_type"": ""audio"", ""codec_name"": ""aac"", ""channels"": 2, ""duration"": ""3600.0"" }
        ]
    }";

    // mkvmerge-written Matroska: no format duration, no per-stream duration either — only the DURATION
    // tag, which is a timecode string rather than a number.
    private const string DurationTagOnlyJson = @"{
        ""format"": { ""format_name"": ""matroska,webm"", ""size"": ""1000000"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""hevc"", ""width"": 1920, ""height"": 1080,
              ""tags"": { ""DURATION"": ""02:00:00.123000000"" } }
        ]
    }";

    [Fact]
    public void AttachedPicCoverArt_IsNotMistakenForTheVideoStream()
    {
        var info = MediaProber.Parse(CoverArtJson, "/media/a.mp4");
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
    }

    // Found by a live run against a non-media file: ffprobe exits 1 but still prints "{  }", which parses
    // into a perfectly valid, perfectly empty MediaProbeInfo. Accepting that sent the file to the encoder,
    // which failed with a raw AVERROR ("ffmpeg exited with code -1094995529") instead of the plugin
    // reporting that it could not read the file.
    [Theory]
    [InlineData("{  }")]
    [InlineData(@"{ ""streams"": [] }")]
    [InlineData(@"{ ""format"": { } }")]
    public void EmptyProbeResult_IsNotMedia(string json)
    {
        Assert.False(MediaProber.LooksLikeMedia(MediaProber.Parse(json, "/media/garbage.mkv")));
    }

    [Theory]
    [InlineData(MkvJson)]
    [InlineData(Mp4Json)]
    [InlineData(CoverArtJson)]
    public void RealProbeResult_IsMedia(string json)
    {
        Assert.True(MediaProber.LooksLikeMedia(MediaProber.Parse(json, "/media/a.mkv")));
    }

    // A container with no readable streams is still media — the check must not be so strict that it
    // rejects a file the parser merely handled imperfectly.
    [Fact]
    public void ContainerAloneCountsAsMedia()
    {
        const string Json = @"{ ""format"": { ""format_name"": ""matroska,webm"" } }";
        Assert.True(MediaProber.LooksLikeMedia(MediaProber.Parse(Json, "/media/a.mkv")));
    }

    [Fact]
    public void Duration_FallsBackToTheLongestStream_WhenFormatOmitsIt()
    {
        var info = MediaProber.Parse(NoFormatDurationJson, "/media/a.mkv");
        Assert.Equal(7200.5, info.DurationSeconds);
    }

    [Fact]
    public void Duration_FallsBackToTheMatroskaDurationTag()
    {
        var info = MediaProber.Parse(DurationTagOnlyJson, "/media/a.mkv");
        Assert.Equal(7200.123, info.DurationSeconds, 3);
    }

    [Fact]
    public void Duration_PrefersTheFormatValueWhenPresent()
    {
        var info = MediaProber.Parse(MkvJson, "/media/a.mkv");
        Assert.Equal(3.0, info.DurationSeconds);
    }

    [Theory]
    [InlineData("02:00:00.123000000", 7200.123)]
    [InlineData("00:41:03.5", 2463.5)]
    [InlineData("", 0)]
    [InlineData("not a timecode", 0)]
    [InlineData("12:34", 0)]
    public void ParseTimecode_HandlesTheMatroskaShapeAndRejectsTheRest(string input, double expected)
    {
        Assert.Equal(expected, MediaProber.ParseTimecode(input), 3);
    }

    [Fact]
    public void VideoBitrate_NotInflatedByTotal_WhenPerStreamMissingAndOtherStreamsExist()
    {
        var info = MediaProber.Parse(MkvJson, "/media/a.mkv");

        // Must be reported as unknown (0), NOT the 5640 kbps total that includes the 640 kbps audio.
        Assert.Equal(0, info.VideoBitrateKbps);
    }

    [Fact]
    public void VideoBitrate_UsesTotal_WhenVideoIsTheOnlyStream()
    {
        var info = MediaProber.Parse(VideoOnlyMkvJson, "/media/a.mkv");
        Assert.Equal(5000, info.VideoBitrateKbps);
    }

    [Fact]
    public void VideoBitrate_UsesPerStreamValue_WhenReported()
    {
        var info = MediaProber.Parse(Mp4Json, "/media/a.mp4");
        Assert.Equal(5000, info.VideoBitrateKbps);
    }

    [Fact]
    public void ProbeArguments_PassPathAsSingleTokenSoItCannotInject()
    {
        var malicious = "/media/movie\" -o \"/config/pwned.json";
        var args = MediaProber.BuildProbeArguments(malicious);

        // The whole path — quotes and spaces included — is exactly one argv element, so ffprobe can never
        // see an injected -o option.
        Assert.Equal(malicious, args.Last());
        Assert.DoesNotContain(args.Take(args.Count - 1), a => a.Contains("pwned", System.StringComparison.Ordinal));
        Assert.Contains("-show_streams", args);
    }

    [Fact]
    public void ProbeArguments_RestrictInputToLocalFileProtocols()
    {
        var args = MediaProber.BuildProbeArguments("/media/movie.mkv");
        var i = args.ToList().IndexOf("-protocol_whitelist");
        Assert.True(i >= 0, "ffprobe must be given a protocol whitelist");
        Assert.Equal("file,crypto,data", args[i + 1]);
    }
    // The exact ffprobe output from issue #14: a UHD remux whose Matroska container asks for 280px off the
    // top and bottom. width/height are the CODED size and the crop arrives as side data.
    private const string ContainerCropJson = @"{
        ""format"": { ""format_name"": ""matroska,webm"", ""duration"": ""7200.0"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""hevc"", ""profile"": ""Main 10"",
              ""width"": 3840, ""height"": 2160, ""coded_width"": 3840, ""coded_height"": 2160,
              ""sample_aspect_ratio"": ""1:1"", ""display_aspect_ratio"": ""16:9"",
              ""pix_fmt"": ""yuv420p10le"", ""color_transfer"": ""smpte2084"",
              ""r_frame_rate"": ""24000/1001"",
              ""side_data_list"": [ { ""side_data_type"": ""Frame Cropping"",
                  ""crop_top"": 280, ""crop_bottom"": 280, ""crop_left"": 0, ""crop_right"": 0 } ] },
            { ""codec_type"": ""audio"", ""codec_name"": ""truehd"", ""channels"": 8 }
        ]
    }";

    // A crop that would leave no picture at all. Propagating it would describe a 3840x0 frame, so it is
    // ignored in favour of the coded size.
    private const string AbsurdCropJson = @"{
        ""format"": { ""format_name"": ""matroska,webm"", ""duration"": ""60.0"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""h264"", ""width"": 1920, ""height"": 1080,
              ""r_frame_rate"": ""25/1"",
              ""side_data_list"": [ { ""side_data_type"": ""Frame Cropping"",
                  ""crop_top"": 600, ""crop_bottom"": 600, ""crop_left"": 0, ""crop_right"": 0 } ] }
        ]
    }";

    [Fact]
    public void ContainerCrop_IsAppliedToTheDisplayedSize()
    {
        var info = MediaProber.Parse(ContainerCropJson, "/media/1917.mkv");

        // What the output will actually contain, and what every rule and the compliance check should judge.
        Assert.Equal(3840, info.Width);
        Assert.Equal(1600, info.Height);

        // The raw figures are kept: an ffmpeg older than 7.1 does not apply the crop, and the verifier
        // accepts that shape too.
        Assert.Equal(3840, info.CodedWidth);
        Assert.Equal(2160, info.CodedHeight);
        Assert.True(info.HasContainerCrop);
    }

    [Fact]
    public void NoCropMetadata_LeavesTheCodedSizeAlone()
    {
        var info = MediaProber.Parse(MkvJson, "/media/a.mkv");

        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(1920, info.CodedWidth);
        Assert.Equal(1080, info.CodedHeight);
        Assert.False(info.HasContainerCrop);
    }

    [Fact]
    public void AbsurdCrop_IsIgnored()
    {
        var info = MediaProber.Parse(AbsurdCropJson, "/media/a.mkv");

        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.False(info.HasContainerCrop);
    }

    // A portrait phone clip, exactly as jellyfin-ffprobe 7.1.4 reports it: the coded frame is landscape
    // and the display matrix turns it. ffmpeg rotates while decoding, so the encode produces 1080x1920 —
    // and a probe that read 1920x1080 made the verifier reject every such encode, and made a
    // "scale=1280:-2" longest-edge cap enlarge the 1080-wide picture to 1280x2276.
    private static string RotatedJson(string rotationEntry) => @"{
        ""format"": { ""format_name"": ""mov,mp4,m4a,3gp,3g2,mj2"", ""duration"": ""3.0"" },
        ""streams"": [
            { ""codec_type"": ""video"", ""codec_name"": ""hevc"", ""width"": 1920, ""height"": 1080,
              ""coded_width"": 1920, ""coded_height"": 1088, ""pix_fmt"": ""yuv420p"", ""r_frame_rate"": ""30/1"",
              " + rotationEntry + @" },
            { ""codec_type"": ""audio"", ""codec_name"": ""aac"", ""channels"": 1 }
        ]
    }";

    [Theory]
    [InlineData(90, 90)]
    [InlineData(-90, 270)]
    [InlineData(270, 270)]
    public void DisplayMatrixQuarterTurn_SwapsTheDisplayedSize(int rotation, int normalized)
    {
        var json = RotatedJson(@"""side_data_list"": [ { ""side_data_type"": ""Display Matrix"", ""displaymatrix"": ""..."", ""rotation"": "
            + rotation.ToString(System.Globalization.CultureInfo.InvariantCulture) + " } ]");
        var info = MediaProber.Parse(json, "/media/portrait.mp4");

        Assert.Equal(1080, info.Width);
        Assert.Equal(1920, info.Height);
        Assert.Equal(normalized, info.Rotation);

        // The coded figures stay as ffprobe reported them, and turning a frame is not cropping it.
        Assert.Equal(1920, info.CodedWidth);
        Assert.Equal(1080, info.CodedHeight);
        Assert.False(info.HasContainerCrop);
    }

    [Fact]
    public void HalfTurn_KeepsTheShape()
    {
        var info = MediaProber.Parse(
            RotatedJson(@"""side_data_list"": [ { ""side_data_type"": ""Display Matrix"", ""rotation"": -180 } ]"),
            "/media/upside-down.mp4");

        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(180, info.Rotation);
    }

    // Older ffprobe builds report the turn as a "rotate" tag rather than side data.
    [Fact]
    public void LegacyRotateTag_IsHonoured()
    {
        var info = MediaProber.Parse(RotatedJson(@"""tags"": { ""rotate"": ""90"" }"), "/media/portrait.mp4");

        Assert.Equal(1080, info.Width);
        Assert.Equal(1920, info.Height);
        Assert.False(info.HasContainerCrop);
    }

    // Matroska can carry both. The crop is in coded pixels and ffmpeg applies it before it rotates, so
    // 640x360 less 40 top and bottom is 640x280, displayed as 280x640 — measured on 7.1.4 and 9.0.2.
    [Fact]
    public void ContainerCropAndRotation_CropFirstThenTurn()
    {
        const string Json = @"{
            ""format"": { ""format_name"": ""matroska,webm"", ""duration"": ""1.0"" },
            ""streams"": [
                { ""codec_type"": ""video"", ""codec_name"": ""h264"", ""width"": 640, ""height"": 360,
                  ""side_data_list"": [
                    { ""side_data_type"": ""Frame Cropping"", ""crop_top"": 40, ""crop_bottom"": 40, ""crop_left"": 0, ""crop_right"": 0 },
                    { ""side_data_type"": ""Display Matrix"", ""rotation"": 90 } ] }
            ]
        }";
        var info = MediaProber.Parse(Json, "/media/a.mkv");

        Assert.Equal(280, info.Width);
        Assert.Equal(640, info.Height);
        Assert.Equal(640, info.CodedWidth);
        Assert.Equal(360, info.CodedHeight);
        Assert.True(info.HasContainerCrop);
    }

    [Fact]
    public void NoRotation_ReadsAsZero()
    {
        var info = MediaProber.Parse(MkvJson, "/media/a.mkv");
        Assert.Equal(0, info.Rotation);
    }

}
