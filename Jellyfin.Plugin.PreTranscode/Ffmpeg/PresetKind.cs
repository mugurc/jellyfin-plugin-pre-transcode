namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// How an encoder's preset/speed setting is expressed.
/// </summary>
public enum PresetKind
{
    /// <summary>ffmpeg does not enumerate any preset values for this encoder.</summary>
    None,

    /// <summary>A discrete list of named preset values.</summary>
    NamedList,

    /// <summary>An integer range (min..max).</summary>
    IntRange
}
