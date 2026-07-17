using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    // Spawning ffprobe is by far the most expensive step, and the same unchanged file is probed
    // repeatedly: on every sweep, and again by the executor for a file the evaluator just probed. Cache
    // results keyed by path and validated by last-write-time + size, so an unchanged file is never
    // re-probed. Bounded so a huge library cannot grow it without limit.
    private const int MaxCacheEntries = 20000;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<MediaProber> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MediaProber(IMediaEncoder mediaEncoder, ILogger<MediaProber> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public async Task<MediaProbeInfo?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var (mtimeTicks, size) = StatOrZero(path);
        if (mtimeTicks != 0
            && _cache.TryGetValue(path, out var cached)
            && cached.MtimeTicks == mtimeTicks
            && cached.Size == size)
        {
            return cached.Info;
        }

        var probePath = FfmpegPaths.ResolveFfprobe(_mediaEncoder);
        string json;
        try
        {
            json = await ProcessRunner.RunAsync(probePath, BuildProbeArguments(path), 60000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", path);
            return null;
        }

        MediaProbeInfo info;
        try
        {
            info = Parse(json, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to parse ffprobe output for {Path}", path);
            return null;
        }

        if (mtimeTicks != 0)
        {
            if (_cache.Count >= MaxCacheEntries)
            {
                TrimCache();
            }

            _cache[path] = new CacheEntry(mtimeTicks, size, info);
        }

        return info;
    }

    private static (long MtimeTicks, long Size) StatOrZero(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private void TrimCache()
    {
        // Evicting an arbitrary quarter only forces those files to be re-probed later; correctness is
        // unaffected because every entry is re-validated against the file's mtime/size on read.
        foreach (var key in _cache.Keys.Take(_cache.Count / 4))
        {
            _cache.TryRemove(key, out _);
        }
    }

    // The path is passed as its own argv element so a filename containing quotes, spaces or a leading
    // dash cannot inject extra ffprobe options (e.g. a file literally named `x" -o "/etc/y` writing to
    // an attacker-chosen path). Do not fold this back into a single command string.
    internal static IReadOnlyList<string> BuildProbeArguments(string path)
    {
        return new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            "-protocol_whitelist", "file,crypto,data",
            path
        };
    }

    internal static MediaProbeInfo Parse(string json, string path)
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
        double videoStreamBitrate = 0;
        var audioStreams = new List<AudioStreamInfo>();
        var subtitleStreams = new List<SubtitleStreamInfo>();
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
                    videoStreamBitrate = GetDouble(stream, "bit_rate");
                    info.VideoFramerate = ParseRate(GetString(stream, "r_frame_rate"));
                    info.IsHdr = DetectHdr(stream);
                    info.IsDolbyVision = DetectDolbyVision(stream);
                }
                else if (string.Equals(type, "audio", StringComparison.Ordinal))
                {
                    audioStreams.Add(new AudioStreamInfo
                    {
                        Codec = GetString(stream, "codec_name"),
                        Channels = (int)GetDouble(stream, "channels"),
                        BitrateKbps = (int)(GetDouble(stream, "bit_rate") / 1000d),
                        Language = GetLanguage(stream)
                    });
                }
                else if (string.Equals(type, "subtitle", StringComparison.Ordinal))
                {
                    subtitleStreams.Add(new SubtitleStreamInfo
                    {
                        Codec = GetString(stream, "codec_name"),
                        Language = GetLanguage(stream)
                    });
                }
            }
        }

        // Prefer the video stream's own bit_rate. Matroska stores no per-stream bitrate, so ffprobe
        // omits it for mkv; falling back to the format-level total there would wrongly add the audio
        // and subtitle bitrate to the video figure. Only trust the total when the video stream is the
        // sole stream; otherwise report unknown (0) rather than an inflated value.
        info.VideoBitrateKbps = (int)((videoStreamBitrate > 0
            ? videoStreamBitrate
            : (audioStreams.Count == 0 && subtitleStreams.Count == 0 ? overallBitrate : 0)) / 1000d);

        info.AudioStreams = audioStreams;
        info.SubtitleStreams = subtitleStreams;

        // Keep the scalar first-audio fields (consumed by the rule engine and the compliance check).
        if (audioStreams.Count > 0)
        {
            info.AudioCodec = audioStreams[0].Codec;
            info.AudioChannels = audioStreams[0].Channels;
            info.AudioBitrateKbps = audioStreams[0].BitrateKbps;
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

    private static string GetLanguage(JsonElement stream)
    {
        return stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            ? GetString(tags, "language")
            : string.Empty;
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

    private sealed record CacheEntry(long MtimeTicks, long Size, MediaProbeInfo Info);
}
