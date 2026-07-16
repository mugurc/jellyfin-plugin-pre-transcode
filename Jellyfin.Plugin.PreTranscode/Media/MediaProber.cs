using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Media;

/// <summary>
/// Default <see cref="IMediaProber"/> that shells out to ffprobe (path from <see cref="IMediaEncoder.ProbePath"/>).
/// </summary>
internal sealed class MediaProber : IMediaProber
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<MediaProber> _logger;

    public MediaProber(IMediaEncoder mediaEncoder, ILogger<MediaProber> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public async Task<MediaProbeInfo?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var probePath = FfmpegPaths.ResolveFfprobe(_mediaEncoder);
        var arguments = "-v quiet -print_format json -show_format -show_streams \"" + path + "\"";
        string json;
        try
        {
            json = await ProcessRunner.RunAsync(probePath, arguments, 60000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", path);
            return null;
        }

        try
        {
            return Parse(json, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to parse ffprobe output for {Path}", path);
            return null;
        }
    }

    private static MediaProbeInfo Parse(string json, string path)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new MediaProbeInfo { Path = path };

        if (root.TryGetProperty("format", out var format))
        {
            info.Container = GetString(format, "format_name");
            info.DurationSeconds = GetDouble(format, "duration");
            info.FileSizeBytes = (long)GetDouble(format, "size");
        }

        var overallBitrate = root.TryGetProperty("format", out var fmt) ? GetDouble(fmt, "bit_rate") : 0;

        var videoFound = false;
        var audioFound = false;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = GetString(stream, "codec_type");
                if (!videoFound && string.Equals(type, "video", StringComparison.Ordinal))
                {
                    videoFound = true;
                    info.VideoCodec = GetString(stream, "codec_name");
                    info.Width = (int)GetDouble(stream, "width");
                    info.Height = (int)GetDouble(stream, "height");
                    info.PixelFormat = GetString(stream, "pix_fmt");
                    var vbr = GetDouble(stream, "bit_rate");
                    info.VideoBitrateKbps = (int)((vbr > 0 ? vbr : overallBitrate) / 1000d);
                    info.VideoFramerate = ParseRate(GetString(stream, "r_frame_rate"));
                    info.IsHdr = DetectHdr(stream);
                    info.IsDolbyVision = DetectDolbyVision(stream);
                }
                else if (!audioFound && string.Equals(type, "audio", StringComparison.Ordinal))
                {
                    audioFound = true;
                    info.AudioCodec = GetString(stream, "codec_name");
                    info.AudioChannels = (int)GetDouble(stream, "channels");
                    info.AudioBitrateKbps = (int)(GetDouble(stream, "bit_rate") / 1000d);
                }
            }
        }

        return info;
    }

    private static bool DetectHdr(JsonElement stream)
    {
        var transfer = GetString(stream, "color_transfer");
        return string.Equals(transfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
            || string.Equals(transfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectDolbyVision(JsonElement stream)
    {
        var tag = GetString(stream, "codec_tag_string");
        if (tag.StartsWith("dv", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                var sideType = GetString(entry, "side_data_type");
                if (sideType.Contains("dolby vision", StringComparison.OrdinalIgnoreCase)
                    || sideType.Contains("dovi", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static double GetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static double ParseRate(string rate)
    {
        if (string.IsNullOrEmpty(rate))
        {
            return 0;
        }

        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den != 0)
        {
            return Math.Round(num / den, 3);
        }

        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : 0;
    }
}
