using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// Plugin configuration. Holds the reusable encoding profiles, the global rules engine, the
/// editable resolution presets and the per-library overrides. Persisted by Jellyfin via
/// <see cref="System.Xml.Serialization.XmlSerializer"/>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    /// <remarks>
    /// The collections deliberately start <b>empty</b>. Jellyfin loads a saved config by constructing
    /// this object and then letting <see cref="System.Xml.Serialization.XmlSerializer"/> <em>add</em>
    /// each stored element to these lists — it does not clear them first. Any defaults seeded in the
    /// constructor would therefore be <em>appended to</em>, and duplicated alongside, the saved items
    /// on every load (the "presets/rules multiply after each update" bug). First-run defaults are
    /// instead seeded exactly once, after loading, by <see cref="ConfigurationInitializer.Normalize"/>.
    /// </remarks>
    public PluginConfiguration()
    {
        Profiles = new List<EncodingProfile>();
        ResolutionPresets = new List<ResolutionPreset>();
        GlobalRules = new List<TriggerRule>();
        LibraryOverrides = new List<LibraryOverride>();
    }

    /// <summary>
    /// Gets or sets a value indicating whether first-run defaults have already been seeded. Prevents
    /// re-seeding defaults an admin has deliberately removed. Absent (false) in configs written by
    /// versions before this flag existed; those are treated as already-populated and only de-duplicated.
    /// </summary>
    public bool DefaultsSeeded { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled. When off, nothing is evaluated or queued.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether newly-added/changed items are evaluated and queued
    /// automatically after a library scan. Off by default (safe).
    /// </summary>
    public bool ProcessNewItemsAutomatically { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent transcode jobs. Defaults to 1 (CPU-friendly).
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>
    /// Gets or sets the number of seconds a file's size/timestamp must be stable before it is
    /// eligible for queueing (guards against in-progress downloads).
    /// </summary>
    public int FileStabilitySeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the id of the default <see cref="EncodingProfile"/> used when a library has no override.
    /// </summary>
    public string DefaultProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the reusable encoding profiles.
    /// </summary>
    public List<EncodingProfile> Profiles { get; set; }

    /// <summary>
    /// Gets or sets the editable resolution presets.
    /// </summary>
    public List<ResolutionPreset> ResolutionPresets { get; set; }

    /// <summary>
    /// Gets or sets the global rule set (applied to libraries that use global rules).
    /// </summary>
    public List<TriggerRule> GlobalRules { get; set; }

    /// <summary>
    /// Gets or sets the per-library overrides.
    /// </summary>
    public List<LibraryOverride> LibraryOverrides { get; set; }
}
