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
}
