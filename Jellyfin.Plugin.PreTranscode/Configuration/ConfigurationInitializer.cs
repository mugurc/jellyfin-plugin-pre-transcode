using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// Normalizes a freshly-loaded <see cref="PluginConfiguration"/>: it removes duplicate presets,
/// profiles and rules that older builds accumulated (those seeded defaults in the configuration
/// constructor, which <see cref="System.Xml.Serialization.XmlSerializer"/> then <em>appended to</em>
/// rather than replaced on every load), and it seeds first-run defaults exactly once. Pure and
/// idempotent — running it repeatedly on the same config makes no further change.
/// </summary>
internal static class ConfigurationInitializer
{
    // A field separator (ASCII Unit Separator) that cannot appear in the text config values it joins,
    // so distinct field combinations can never collide into the same signature.
    private const char Sep = '\u001f';

    /// <summary>
    /// Cleans up duplicates and, on first run, seeds defaults.
    /// </summary>
    /// <param name="config">The configuration to normalize in place.</param>
    /// <returns><c>true</c> if anything changed (and the config should be persisted).</returns>
    public static bool Normalize(PluginConfiguration config)
    {
        if (config is null)
        {
            return false;
        }

        var changed = false;

        // 1. Self-heal duplicates accumulated by the old constructor-seeding bug. Presets share stable
        //    ids ("1080p", ...), so they collapse by id; profiles and rules were re-seeded with a fresh
        //    random id each load, so they collapse by content (id excluded) and profile references are
        //    repointed to the survivor.
        changed |= DedupeByKey(config.ResolutionPresets, p => p.Id);
        changed |= DedupeProfiles(config);
        changed |= DedupeByKey(config.GlobalRules, RuleSignature);
        foreach (var library in config.LibraryOverrides)
        {
            changed |= DedupeByKey(library.Rules, RuleSignature);
        }

        // 2. Seed first-run defaults exactly once. Only fills genuinely-empty collections, so defaults an
        //    admin deleted are never resurrected. Existing (pre-flag) configs already have data here, so
        //    this seeds nothing for them — it just records that seeding is done.
        if (!config.DefaultsSeeded)
        {
            config.DefaultsSeeded = true;
            changed = true;
            SeedDefaults(config);
        }

        return changed;
    }

    private static void SeedDefaults(PluginConfiguration config)
    {
        if (config.ResolutionPresets.Count == 0)
        {
            config.ResolutionPresets.Add(new ResolutionPreset { Id = "2160p", Name = "2160p (4K UHD)", Width = 3840, Height = 2160 });
            config.ResolutionPresets.Add(new ResolutionPreset { Id = "1080p", Name = "1080p (Full HD)", Width = 1920, Height = 1080 });
            config.ResolutionPresets.Add(new ResolutionPreset { Id = "720p", Name = "720p (HD)", Width = 1280, Height = 720 });
            config.ResolutionPresets.Add(new ResolutionPreset { Id = "480p", Name = "480p (SD)", Width = 854, Height = 480 });
        }

        if (config.Profiles.Count == 0)
        {
            config.Profiles.Add(new EncodingProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Compatibility Baseline (H.264 / AAC / MP4)"
            });
        }

        if (string.IsNullOrEmpty(config.DefaultProfileId) && config.Profiles.Count > 0)
        {
            config.DefaultProfileId = config.Profiles[0].Id;
        }

        if (config.GlobalRules.Count == 0)
        {
            config.GlobalRules.Add(new TriggerRule
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
            });
        }
    }

    // Removes later items whose key was already seen, keeping the first occurrence. Returns whether
    // any item was removed.
    private static bool DedupeByKey<T>(List<T> items, Func<T, string> key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var removed = false;
        var i = 0;
        while (i < items.Count)
        {
            if (seen.Add(key(items[i])))
            {
                i++;
            }
            else
            {
                items.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    // Collapses content-identical profiles (ignoring the random id) and repoints DefaultProfileId and
    // any library-override references from a removed profile to the surviving one.
    private static bool DedupeProfiles(PluginConfiguration config)
    {
        var profiles = config.Profiles;
        var keptIdBySignature = new Dictionary<string, string>(StringComparer.Ordinal);
        var removed = false;
        var i = 0;
        while (i < profiles.Count)
        {
            var signature = ProfileSignature(profiles[i]);
            if (keptIdBySignature.TryGetValue(signature, out var keptId))
            {
                RepointProfileReferences(config, profiles[i].Id, keptId);
                profiles.RemoveAt(i);
                removed = true;
            }
            else
            {
                keptIdBySignature[signature] = profiles[i].Id;
                i++;
            }
        }

        return removed;
    }

    private static void RepointProfileReferences(PluginConfiguration config, string fromId, string toId)
    {
        if (string.Equals(config.DefaultProfileId, fromId, StringComparison.Ordinal))
        {
            config.DefaultProfileId = toId;
        }

        foreach (var library in config.LibraryOverrides)
        {
            if (string.Equals(library.ProfileId, fromId, StringComparison.Ordinal))
            {
                library.ProfileId = toId;
            }
        }
    }

    private static string RuleSignature(TriggerRule rule)
    {
        var sb = new StringBuilder();
        Field(sb, rule.Name);
        Field(sb, rule.Enabled ? "1" : "0");
        Field(sb, ((int)rule.Combine).ToString(CultureInfo.InvariantCulture));
        foreach (var condition in rule.Conditions)
        {
            Field(sb, ((int)condition.Type).ToString(CultureInfo.InvariantCulture));
            Field(sb, ((int)condition.Operator).ToString(CultureInfo.InvariantCulture));
            Field(sb, condition.Value);
        }

        return sb.ToString();
    }

    private static string ProfileSignature(EncodingProfile p)
    {
        // Every field except the (randomly-generated) Id, so content-identical profiles collapse.
        var sb = new StringBuilder();
        Field(sb, p.Name);
        Field(sb, p.VideoCodec);
        Field(sb, p.VideoEncoder);
        Field(sb, ((int)p.VideoQualityMode).ToString(CultureInfo.InvariantCulture));
        Field(sb, p.Crf.ToString(CultureInfo.InvariantCulture));
        Field(sb, p.VideoBitrateKbps.ToString(CultureInfo.InvariantCulture));
        Field(sb, p.VideoMaxBitrateKbps.ToString(CultureInfo.InvariantCulture));
        Field(sb, p.Preset);
        Field(sb, p.ExtraVideoArgs);
        Field(sb, ((int)p.ResolutionMode).ToString(CultureInfo.InvariantCulture));
        Field(sb, p.MaxWidth.ToString(CultureInfo.InvariantCulture));
        Field(sb, p.MaxHeight.ToString(CultureInfo.InvariantCulture));
        Field(sb, p.ResolutionPresetId);
        Field(sb, p.AudioCodec);
        Field(sb, p.AudioEncoder);
        Field(sb, p.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
        Field(sb, ((int)p.ChannelPolicy).ToString(CultureInfo.InvariantCulture));
        Field(sb, p.MaxAudioChannels.ToString(CultureInfo.InvariantCulture));
        Field(sb, p.TonemapHdr ? "1" : "0");
        Field(sb, p.TonemapAlgorithm);
        Field(sb, p.Container);
        Field(sb, ((int)p.OutputMode).ToString(CultureInfo.InvariantCulture));
        Field(sb, p.OutputDirectory);
        Field(sb, p.AlternateVersionLabel);
        Field(sb, p.ExtraOutputArgs);
        return sb.ToString();
    }

    private static void Field(StringBuilder sb, string? value)
    {
        sb.Append(value ?? string.Empty).Append(Sep);
    }
}
