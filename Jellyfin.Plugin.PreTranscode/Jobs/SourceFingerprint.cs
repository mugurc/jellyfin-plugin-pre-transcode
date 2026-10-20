using System;
using System.IO;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// What a source file looked like when its encode began — enough to tell, before the result is applied,
/// that the file now at that path is not the one that was encoded.
/// </summary>
/// <param name="Exists">Whether the file existed.</param>
/// <param name="Length">Its size in bytes.</param>
/// <param name="LastWriteUtc">Its last-write time, in UTC.</param>
internal readonly record struct SourceFingerprint(bool Exists, long Length, DateTime LastWriteUtc)
{
    /// <summary>
    /// Reads the fingerprint of the file at <paramref name="path"/>. A file that cannot be read counts as
    /// missing, so a source that turns unreadable mid-encode is treated as gone rather than as unchanged.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The fingerprint.</returns>
    public static SourceFingerprint Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new SourceFingerprint(true, info.Length, info.LastWriteTimeUtc) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
