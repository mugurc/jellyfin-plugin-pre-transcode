using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Media;

/// <summary>
/// Default <see cref="IMediaProber"/> that shells out to ffprobe (path from <see cref="IMediaEncoder.ProbePath"/>).
/// </summary>
internal sealed partial class MediaProber : IMediaProber
{
    // Spawning ffprobe is by far the most expensive step, and the same unchanged file is probed
    // repeatedly: on every sweep, and again by the executor for a file the evaluator just probed. Cache
    // results keyed by path and validated by last-write-time + size, so an unchanged file is never
    // re-probed. Bounded so a huge library cannot grow it without limit.
    private const int MaxCacheEntries = 20000;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<MediaProber> _logger;

    // Case-sensitivity of the cache key must match the filesystem: on Linux "/m/A.mkv" and "/m/a.mkv"
    // are different files and must not share a cache entry; on Windows they are the same file.
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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

        // ffprobe exits non-zero on a file that is not media, but with "-v quiet" it still prints an
        // empty JSON object — which parses perfectly well into an all-default MediaProbeInfo. Reporting
        // that as a successful probe sends a file with no streams all the way to the encoder, which then
        // fails with a raw AVERROR the admin has to decode ("ffmpeg exited with code -1094995529").
        // A probe that found no container and not a single stream did not find media.
        if (!LooksLikeMedia(info))
        {
            _logger.LogWarning("ffprobe found no container or streams in {Path}; treating it as unreadable", path);
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
        double longestStreamDuration = 0;
        var audioStreams = new List<AudioStreamInfo>();
        var subtitleStreams = new List<SubtitleStreamInfo>();
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                longestStreamDuration = Math.Max(longestStreamDuration, StreamDuration(stream));

                var type = GetString(stream, "codec_type");
                if (!videoFound && string.Equals(type, "video", StringComparison.Ordinal) && !IsAttachedPic(stream))
                {
                    videoFound = true;
                    info.VideoCodec = GetString(stream, "codec_name");
                    info.CodedWidth = (int)GetDouble(stream, "width");
                    info.CodedHeight = (int)GetDouble(stream, "height");
                    var (displayWidth, displayHeight) = ApplyContainerCrop(stream, info.CodedWidth, info.CodedHeight);
                    info.Rotation = ReadRotation(stream);

                    // The crop is stated in coded pixels and ffmpeg applies it before it rotates, so the
                    // turn comes second: a 640x360 frame cropped to 640x280 and turned shows as 280x640.
                    info.Width = info.IsQuarterTurn ? displayHeight : displayWidth;
                    info.Height = info.IsQuarterTurn ? displayWidth : displayHeight;
                    info.PixelFormat = GetString(stream, "pix_fmt");
                    info.BitDepth = ReadBitDepth(stream, info.PixelFormat);
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

        // Not every container carries a format-level duration: ffprobe omits it for raw/streamed inputs and
        // for Matroska files muxed without a Segment duration, and reports it per stream (or only in the
        // Matroska DURATION tag) instead. Duration 0 means "unknown" to the rule engine, which fails every
        // VideoDurationMinutes condition regardless of operator — so a rule set built on duration would go
        // silently dead on those files. Fall back to the longest stream.
        if (info.DurationSeconds <= 0)
        {
            info.DurationSeconds = longestStreamDuration;
        }

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

    // Whether a parsed probe describes actual media. Deliberately generous — any one of a container
    // name, a video codec or a single audio/subtitle stream is enough — so this can only reject a result
    // that carries no information at all, never a real file the parser handled imperfectly.
    internal static bool LooksLikeMedia(MediaProbeInfo info)
    {
        return !string.IsNullOrEmpty(info.Container)
            || !string.IsNullOrEmpty(info.VideoCodec)
            || info.AudioStreams.Count > 0
            || info.SubtitleStreams.Count > 0;
    }

    // Bits per sample. ffprobe reports bits_per_raw_sample for most video codecs; when it doesn't, the
    // pixel format name carries the same information.
    private static int ReadBitDepth(JsonElement stream, string pixelFormat)
    {
        var reported = (int)GetDouble(stream, "bits_per_raw_sample");
        return reported > 0 ? reported : BitDepthFromPixelFormat(pixelFormat);
    }

    // "yuv420p10le" -> 10, "p010le" -> 10, "p216le" -> 16, "gbrp12le" -> 12, "yuv420p" -> 8, "nv12" -> 8.
    //
    // Anchored on the trailing endianness suffix that every ffmpeg pixel format above 8 bits carries, and
    // on the plane marker 'p' that precedes the depth. A plain "contains a number" test would read
    // "yuv410p" (a real 4:1:0 format) as 10-bit and "nv12" as 12-bit — both are 8-bit — and would then
    // hand the encoder a depth the source never had. The optional digit between 'p' and the depth covers
    // the semi-planar family, where the chroma layout is encoded there: p010le/p210le are both 10-bit
    // (4:2:0 and 4:2:2), p216le is 16-bit. Anything unrecognised reads as 8-bit, the safe default.
    internal static int BitDepthFromPixelFormat(string pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat))
        {
            return 0;
        }

        var match = PixelFormatDepthRegex().Match(pixelFormat);
        return match.Success && int.TryParse(match.Groups["bits"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            ? bits
            : 8;
    }

    [GeneratedRegex(@"p\d?(?<bits>\d{2})(le|be)$", RegexOptions.IgnoreCase)]
    private static partial Regex PixelFormatDepthRegex();

    /// <summary>
    /// The chroma subsampling a pixel format name describes, as the usual J:a:b digits ("420", "422",
    /// "444"; "400" for greyscale), or <c>null</c> when the name is not recognised.
    /// </summary>
    /// <remarks>
    /// Planar YUV names carry it outright ("yuv420p10le", "yuvj422p", "yuva444p"); the semi-planar ones
    /// encode it in their digits (nv12 is 4:2:0, nv16 4:2:2, nv24 4:4:4; p010 is 4:2:0, p210 4:2:2,
    /// p410 4:4:4). RGB has no chroma planes to subsample, so it is 4:4:4 for every purpose here.
    /// Anything else — a hardware surface name such as "vaapi", an empty probe — is unknown rather than
    /// guessed, so a caller can tell "differs" from "could not tell".
    /// </remarks>
    /// <param name="pixelFormat">The ffmpeg pixel format name.</param>
    /// <returns>The subsampling, or <c>null</c>.</returns>
    internal static string? ChromaSubsampling(string pixelFormat)
    {
        var format = (pixelFormat ?? string.Empty).ToLowerInvariant();
        var planar = PlanarYuvRegex().Match(format);
        if (planar.Success)
        {
            return planar.Groups["chroma"].Value;
        }

        if (format.StartsWith("gray", StringComparison.Ordinal) || format.StartsWith("ya", StringComparison.Ordinal))
        {
            return "400";
        }

        if (format.StartsWith("gbr", StringComparison.Ordinal) || format.Contains("rgb", StringComparison.Ordinal)
            || format.Contains("bgr", StringComparison.Ordinal))
        {
            return "444";
        }

        var deep = SemiPlanarDeepRegex().Match(format);
        if (deep.Success)
        {
            return deep.Groups["layout"].Value switch
            {
                "0" => "420",
                "2" => "422",
                "4" => "444",
                _ => null
            };
        }

        return format switch
        {
            "nv12" or "nv21" => "420",
            "nv16" or "nv61" or "nv20le" or "nv20be" => "422",
            "nv24" or "nv42" => "444",
            "yuyv422" or "uyvy422" or "yvyu422" => "422",
            _ => null
        };
    }

    [GeneratedRegex(@"^yuvj?a?(?<chroma>4[0-4][0-4])p")]
    private static partial Regex PlanarYuvRegex();

    // p010le, p016le, p210le, p410le …: the digit after 'p' is the chroma layout, the rest the depth.
    [GeneratedRegex(@"^p(?<layout>\d)\d{2}(le|be)$")]
    private static partial Regex SemiPlanarDeepRegex();

    // The display rotation, normalised to 0/90/180/270. Current ffprobe states it as a "Display Matrix"
    // side-data entry with a signed "rotation" (-90 for the usual portrait phone clip); older builds
    // exposed the same thing as a "rotate" tag. Only the turn matters here — the sign says which way,
    // and both ways swap width and height.
    private static int ReadRotation(JsonElement stream)
    {
        double? degrees = null;
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (GetString(entry, "side_data_type").Contains("display matrix", StringComparison.OrdinalIgnoreCase)
                    && entry.TryGetProperty("rotation", out _))
                {
                    degrees = GetDouble(entry, "rotation");
                    break;
                }
            }
        }

        if (degrees is null && stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            degrees = GetDouble(tags, "rotate");
        }

        var rounded = (int)Math.Round(degrees ?? 0) % 360;
        return rounded < 0 ? rounded + 360 : rounded;
    }

    // A single stream's duration: the numeric "duration" field when ffprobe supplies it, otherwise the
    // Matroska "DURATION" tag, which is a timecode string ("01:59:59.123000000") rather than a number.
    internal static double StreamDuration(JsonElement stream)
    {
        var seconds = GetDouble(stream, "duration");
        if (seconds > 0)
        {
            return seconds;
        }

        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        // Matroska tag names are case-preserving and muxers differ ("DURATION" from mkvmerge, "duration"
        // from some others), so probe both spellings rather than assuming one.
        var timecode = GetString(tags, "DURATION");
        if (string.IsNullOrEmpty(timecode))
        {
            timecode = GetString(tags, "duration");
        }

        return ParseTimecode(timecode);
    }

    // "HH:MM:SS.fffffffff" -> seconds. Returns 0 for anything that is not that shape, so a malformed or
    // absent tag reads as "unknown" exactly like a missing field.
    internal static double ParseTimecode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var parts = value.Split(':');
        if (parts.Length != 3
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return 0;
        }

        var total = (hours * 3600) + (minutes * 60) + seconds;
        return total > 0 ? total : 0;
    }

    // Embedded cover art (common in mp4/mov, and often the first stream) is reported by ffprobe as a
    // "video" stream with disposition.attached_pic=1. Treating it as the real video makes every decision
    // (codec, dimensions, HDR) reflect the poster thumbnail, so it must be skipped.
    private static bool IsAttachedPic(JsonElement stream)
    {
        return stream.TryGetProperty("disposition", out var disposition)
            && disposition.ValueKind == JsonValueKind.Object
            && disposition.TryGetProperty("attached_pic", out var attached)
            && attached.ValueKind == JsonValueKind.Number
            && attached.GetInt32() == 1;
    }

    private static bool DetectHdr(JsonElement stream)
    {
        var transfer = GetString(stream, "color_transfer");
        return string.Equals(transfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
            || string.Equals(transfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectDolbyVision(JsonElement stream)
    {
        // Match the exact Dolby Vision codec tags only. A "dv" prefix also matches legacy DV (Digital
        // Video: dvsd, dvhd, dv25, dv50, dvc …), which is not Dolby Vision and would be wrongly routed
        // into DV-specific handling.
        var tag = GetString(stream, "codec_tag_string").ToLowerInvariant();
        if (tag is "dvhe" or "dvh1" or "dvav" or "dva1")
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

    // Matroska can ask for a crop in the container rather than the bitstream (the PixelCrop* elements,
    // which ffprobe reports as "Frame Cropping" side data). ffprobe keeps width/height at the CODED size
    // and states the crop separately, while ffmpeg from 7.1 onwards applies it by default
    // (-apply_cropping), so the encode produces the cropped picture. Reading width/height at face value
    // therefore describes a frame the output will not contain: a 3840x2160 UHD remux with 280px cropped
    // top and bottom encodes to 3840x1600, and an output verifier comparing shapes rejected the correct
    // result after hours of encoding (issue #14).
    private static (int Width, int Height) ApplyContainerCrop(JsonElement stream, int codedWidth, int codedHeight)
    {
        if (codedWidth <= 0 || codedHeight <= 0
            || !stream.TryGetProperty("side_data_list", out var sideData)
            || sideData.ValueKind != JsonValueKind.Array)
        {
            return (codedWidth, codedHeight);
        }

        foreach (var entry in sideData.EnumerateArray())
        {
            if (!GetString(entry, "side_data_type").Contains("crop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var top = (int)GetDouble(entry, "crop_top");
            var bottom = (int)GetDouble(entry, "crop_bottom");
            var left = (int)GetDouble(entry, "crop_left");
            var right = (int)GetDouble(entry, "crop_right");

            // A negative or oversized crop is not something to reason about: it would describe a frame
            // with no picture in it. Treat a nonsensical entry as no crop rather than propagating it.
            if (top < 0 || bottom < 0 || left < 0 || right < 0)
            {
                continue;
            }

            var width = codedWidth - left - right;
            var height = codedHeight - top - bottom;
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            return (width, height);
        }

        return (codedWidth, codedHeight);
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
