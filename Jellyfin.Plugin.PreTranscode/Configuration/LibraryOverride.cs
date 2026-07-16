using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// Per-library configuration. Lets different Jellyfin libraries (Movies, TV, Home Videos, ...) use
/// different target profiles and/or rule sets instead of a single global policy.
/// </summary>
public class LibraryOverride
{
    /// <summary>
    /// Gets or sets the Jellyfin library (virtual folder) identifier this override applies to.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library display name (for reference in the UI).
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether pre-transcoding is enabled for this library.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the id of the <see cref="EncodingProfile"/> to use for this library.
    /// When empty, the global default profile is used.
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to use the global rule set (<c>true</c>) or this
    /// library's own <see cref="Rules"/> (<c>false</c>).
    /// </summary>
    public bool UseGlobalRules { get; set; } = true;

    /// <summary>
    /// Gets or sets this library's own rule set (used when <see cref="UseGlobalRules"/> is <c>false</c>).
    /// </summary>
    public List<TriggerRule> Rules { get; set; } = new List<TriggerRule>();
}
