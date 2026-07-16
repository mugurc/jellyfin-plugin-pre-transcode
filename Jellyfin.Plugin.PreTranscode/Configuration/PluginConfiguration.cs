using System;
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
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class with safe defaults:
    /// the plugin loads but does nothing until an admin enables at least one rule.
    /// </summary>
    public PluginConfiguration()
    {
        ResolutionPresets = new List<ResolutionPreset>
        {
            new ResolutionPreset { Id = "2160p", Name = "2160p (4K UHD)", Width = 3840, Height = 2160 },
            new ResolutionPreset { Id = "1080p", Name = "1080p (Full HD)", Width = 1920, Height = 1080 },
            new ResolutionPreset { Id = "720p", Name = "720p (HD)", Width = 1280, Height = 720 },
            new ResolutionPreset { Id = "480p", Name = "480p (SD)", Width = 854, Height = 480 }
        };

        var defaultProfile = new EncodingProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Compatibility Baseline (H.264 / AAC / MP4)"
        };
        Profiles = new List<EncodingProfile> { defaultProfile };
        DefaultProfileId = defaultProfile.Id;

        GlobalRules = new List<TriggerRule>
        {
            new TriggerRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Example: video is not H.264",
                Enabled = false,
                Combine = ConditionCombine.All,
                Conditions = new List<RuleCondition>
                {
                    new RuleCondition
                    {
                        Type = ConditionType.VideoCodec,
                        Operator = ComparisonOperator.NotEquals,
                        Value = "h264"
                    }
                }
            }
        };

        LibraryOverrides = new List<LibraryOverride>();
    }

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
