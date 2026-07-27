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
    /// Gets or sets a value indicating whether the job queue is paused. Persisted so that pausing to free
    /// the CPU survives a restart — an admin who paused the queue and then updated the server would
    /// otherwise find encodes running again with no action on their part.
    /// </summary>
    public bool QueuePaused { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of finished (completed/failed/cancelled/skipped) jobs kept in the
    /// queue file; <c>0</c> means unlimited, which is the default and the historical behaviour.
    /// <para>
    /// Worth setting on a large library: the whole queue is re-serialised to disk on every status
    /// update, so a history that only ever grows makes each of those writes slower forever. Trimming is
    /// oldest-first and never touches pending or running jobs.
    /// </para>
    /// </summary>
    public int MaxFinishedJobsKept { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every evaluation decision is written to the Jellyfin log:
    /// the probed facts about each file, each trigger-rule condition with its configured value, the
    /// actual value it was compared against and whether it passed, and the reason an item was skipped.
    /// <para>
    /// Off by default — it logs a block per library item, so a sweep over a large library is verbose. It
    /// exists because every other skip path is silent, which makes a rule that does not fire impossible
    /// to diagnose. Logged at Information level so it is visible without lowering the server's log level.
    /// </para>
    /// </summary>
    public bool VerboseRuleLogging { get; set; }

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
