using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class ConfigurationInitializerTests
{
    [Fact]
    public void FreshConfig_SeedsDefaultsOnce()
    {
        var config = new PluginConfiguration();

        var changed = ConfigurationInitializer.Normalize(config);

        Assert.True(changed);
        Assert.True(config.DefaultsSeeded);
        Assert.Equal(4, config.ResolutionPresets.Count);
        Assert.Single(config.Profiles);
        Assert.Single(config.GlobalRules);
        Assert.Equal(config.Profiles[0].Id, config.DefaultProfileId);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var config = new PluginConfiguration();
        ConfigurationInitializer.Normalize(config);

        // A second (and third) pass must not change anything or re-add defaults.
        Assert.False(ConfigurationInitializer.Normalize(config));
        Assert.False(ConfigurationInitializer.Normalize(config));
        Assert.Equal(4, config.ResolutionPresets.Count);
        Assert.Single(config.Profiles);
        Assert.Single(config.GlobalRules);
    }

    [Fact]
    public void SeededConfig_WithEmptiedCollections_IsNotReseeded()
    {
        // An admin deliberately removed every rule/preset after first-run seeding.
        var config = new PluginConfiguration { DefaultsSeeded = true };

        var changed = ConfigurationInitializer.Normalize(config);

        Assert.False(changed);
        Assert.Empty(config.ResolutionPresets);
        Assert.Empty(config.GlobalRules);
        Assert.Empty(config.Profiles);
    }

    [Fact]
    public void DuplicatePresets_AreCollapsedById()
    {
        var config = new PluginConfiguration { DefaultsSeeded = true };
        config.ResolutionPresets.Add(new ResolutionPreset { Id = "1080p", Name = "1080p", Width = 1920, Height = 1080 });
        config.ResolutionPresets.Add(new ResolutionPreset { Id = "720p", Name = "720p", Width = 1280, Height = 720 });
        config.ResolutionPresets.Add(new ResolutionPreset { Id = "1080p", Name = "1080p", Width = 1920, Height = 1080 });
        config.ResolutionPresets.Add(new ResolutionPreset { Id = "720p", Name = "720p", Width = 1280, Height = 720 });

        var changed = ConfigurationInitializer.Normalize(config);

        Assert.True(changed);
        Assert.Equal(2, config.ResolutionPresets.Count);
        Assert.Equal(new[] { "1080p", "720p" }, config.ResolutionPresets.Select(p => p.Id));
    }

    [Fact]
    public void DuplicateRules_WithDifferentRandomIds_AreCollapsedByContent()
    {
        var config = new PluginConfiguration { DefaultsSeeded = true };
        config.GlobalRules.Add(MakeExampleRule("id-a"));
        config.GlobalRules.Add(MakeExampleRule("id-b")); // identical content, different id
        config.GlobalRules.Add(MakeExampleRule("id-c"));

        var changed = ConfigurationInitializer.Normalize(config);

        Assert.True(changed);
        Assert.Single(config.GlobalRules);
        Assert.Equal("id-a", config.GlobalRules[0].Id); // first occurrence kept
    }

    [Fact]
    public void DuplicateProfiles_AreCollapsed_AndReferencesRepointed()
    {
        var config = new PluginConfiguration { DefaultsSeeded = true };
        config.Profiles.Add(new EncodingProfile { Id = "keep", Name = "Baseline" });
        config.Profiles.Add(new EncodingProfile { Id = "dup", Name = "Baseline" }); // identical content
        config.DefaultProfileId = "dup";
        config.LibraryOverrides.Add(new LibraryOverride { LibraryId = "lib1", ProfileId = "dup" });

        var changed = ConfigurationInitializer.Normalize(config);

        Assert.True(changed);
        Assert.Single(config.Profiles);
        Assert.Equal("keep", config.Profiles[0].Id);
        Assert.Equal("keep", config.DefaultProfileId);          // repointed off the removed profile
        Assert.Equal("keep", config.LibraryOverrides[0].ProfileId);
    }

    [Fact]
    public void DistinctProfiles_AreNotCollapsed()
    {
        var config = new PluginConfiguration { DefaultsSeeded = true };
        config.Profiles.Add(new EncodingProfile { Id = "a", Name = "H.264", VideoCodec = "h264" });
        config.Profiles.Add(new EncodingProfile { Id = "b", Name = "HEVC", VideoCodec = "hevc" });

        var changed = ConfigurationInitializer.Normalize(config);

        // Nothing to dedupe here; only the DefaultsSeeded flag is already set, so no change.
        Assert.False(changed);
        Assert.Equal(2, config.Profiles.Count);
    }

    // The end-to-end reproduction of the reported bug: the constructor must NOT pre-seed collections,
    // otherwise XmlSerializer appends the saved items to the seeded ones and they multiply on each load.
    [Fact]
    public void XmlRoundTrip_DoesNotDuplicateCollections()
    {
        var original = new PluginConfiguration();
        ConfigurationInitializer.Normalize(original); // seed defaults, as first run would

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, original);
            xml = writer.ToString();
        }

        PluginConfiguration reloaded;
        using (var reader = new StringReader(xml))
        {
            reloaded = (PluginConfiguration)serializer.Deserialize(reader)!;
        }

        // Deserialization must not have appended the constructor's items on top of the saved ones.
        Assert.Equal(4, reloaded.ResolutionPresets.Count);
        Assert.Single(reloaded.Profiles);
        Assert.Single(reloaded.GlobalRules);

        // And normalizing the reloaded config is a no-op (already seeded, no duplicates).
        Assert.False(ConfigurationInitializer.Normalize(reloaded));
        Assert.Equal(4, reloaded.ResolutionPresets.Count);
        Assert.Single(reloaded.Profiles);
        Assert.Single(reloaded.GlobalRules);
    }

    private static TriggerRule MakeExampleRule(string id)
    {
        return new TriggerRule
        {
            Id = id,
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
        };
    }
}
