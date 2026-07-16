using System;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Applies the profile's output-handling policy to a verified temp output file and returns the final
/// path. The source is only ever deleted for <see cref="OutputHandlingMode.ReplaceInPlace"/>, and only
/// after the new file is safely in place.
/// </summary>
internal static class OutputApplier
{
    public static string Apply(EncodingProfile profile, string sourcePath, string tempOutputPath)
    {
        var extension = ContainerExtension(profile.Container);
        return profile.OutputMode switch
        {
            OutputHandlingMode.ReplaceInPlace => ReplaceInPlace(sourcePath, tempOutputPath, extension),
            OutputHandlingMode.AddAsAlternateVersion => AddAsSibling(sourcePath, tempOutputPath, extension),
            _ => WriteToSeparateDirectory(profile, sourcePath, tempOutputPath, extension)
        };
    }

    /// <summary>
    /// Maps a container/muxer name to a file extension.
    /// </summary>
    /// <param name="container">The container/muxer name.</param>
    /// <returns>The file extension (with leading dot).</returns>
    public static string ContainerExtension(string container)
    {
        return container.ToLowerInvariant() switch
        {
            "mp4" => ".mp4",
            "matroska" or "mkv" => ".mkv",
            "webm" => ".webm",
            "mov" => ".mov",
            "" => ".mp4",
            _ => "." + container
        };
    }

    private static string ReplaceInPlace(string sourcePath, string tempOutputPath, string extension)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = Path.Combine(directory, stem + extension);

        File.Move(tempOutputPath, finalPath, overwrite: true);

        if (!string.Equals(finalPath, sourcePath, StringComparison.OrdinalIgnoreCase) && File.Exists(sourcePath))
        {
            File.Delete(sourcePath);
        }

        return finalPath;
    }

    private static string AddAsSibling(string sourcePath, string tempOutputPath, string extension)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = MakeUnique(Path.Combine(directory, stem + " - pretranscode" + extension));

        File.Move(tempOutputPath, finalPath);
        return finalPath;
    }

    private static string WriteToSeparateDirectory(EncodingProfile profile, string sourcePath, string tempOutputPath, string extension)
    {
        var directory = string.IsNullOrWhiteSpace(profile.OutputDirectory)
            ? Path.GetDirectoryName(sourcePath) ?? "."
            : profile.OutputDirectory;

        Directory.CreateDirectory(directory);

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = MakeUnique(Path.Combine(directory, stem + extension));

        File.Move(tempOutputPath, finalPath);
        return finalPath;
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, stem + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }
}
