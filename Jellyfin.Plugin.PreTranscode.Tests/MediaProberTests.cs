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
