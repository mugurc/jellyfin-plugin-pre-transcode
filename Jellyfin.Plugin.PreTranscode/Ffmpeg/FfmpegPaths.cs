using System;
using System.IO;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Resolves the ffmpeg/ffprobe binary paths robustly. Jellyfin's <see cref="IMediaEncoder"/> does not
/// always populate <see cref="IMediaEncoder.ProbePath"/> (it can be empty even when ffmpeg is found),
/// so ffprobe is derived from the ffmpeg path as a fallback.
/// </summary>
internal static class FfmpegPaths
{
    public static string ResolveFfmpeg(IMediaEncoder mediaEncoder)
    {
        return string.IsNullOrWhiteSpace(mediaEncoder.EncoderPath) ? "ffmpeg" : mediaEncoder.EncoderPath;
    }

    public static string ResolveFfprobe(IMediaEncoder mediaEncoder)
    {
        if (!string.IsNullOrWhiteSpace(mediaEncoder.ProbePath))
        {
            return mediaEncoder.ProbePath;
        }

        var ffmpeg = ResolveFfmpeg(mediaEncoder);
        var directory = Path.GetDirectoryName(ffmpeg);
        var fileName = Path.GetFileName(ffmpeg);
        var probeName = string.IsNullOrEmpty(fileName)
            ? "ffprobe"
            : fileName.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(probeName))
        {
            probeName = "ffprobe";
        }

        return string.IsNullOrEmpty(directory) ? probeName : Path.Combine(directory, probeName);
    }
}
