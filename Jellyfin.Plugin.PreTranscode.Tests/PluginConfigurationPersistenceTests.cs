using System;
using System.IO;
using System.Linq;
using System.Xml;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// The config's whole life through Jellyfin's real <c>BasePlugin&lt;T&gt;</c>: posted by the settings page or
/// the API (<see cref="Plugin.UpdateConfiguration"/>), written to disk, and read back by the next start.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class PluginConfigurationPersistenceTests : IDisposable
{
    private readonly PluginTestHost _host = new();

    public void Dispose() => _host.Dispose();

    // What an admin builds up over time and would lose: two profiles, an override pointing at the second,
    // an enabled global rule and a custom preset. A deep copy, like the page's JSON round trip, so editing
    // it never touches the live config.
    private PluginConfiguration RichConfig(PluginConfiguration live)
    {
        using var buffer = new MemoryStream();
        _host.Serializer.SerializeToStream(live, buffer);
        buffer.Position = 0;
        var config = (PluginConfiguration)_host.Serializer.DeserializeFromStream(typeof(PluginConfiguration), buffer)!;

        config.HardwareDecoder = "videotoolbox";
        config.Profiles.Add(new EncodingProfile { Id = "p2", Name = "Phones", Container = "matroska", OutputDirectory = "/srv/out" });
        config.ResolutionPresets.Add(new ResolutionPreset { Id = "540p", Name = "540p", Width = 960, Height = 540 });
        config.GlobalRules[0].Enabled = true;
        config.LibraryOverrides.Add(new LibraryOverride { LibraryId = "lib-tv", LibraryName = "TV", ProfileId = "p2" });
        return config;
    }

    private static void AssertRichConfigSurvived(PluginConfiguration loaded)
    {
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.Equal("p2", loaded.Profiles[1].Id);
        Assert.Equal("matroska", loaded.Profiles[1].Container);
        Assert.Equal(5, loaded.ResolutionPresets.Count);
        Assert.True(Assert.Single(loaded.GlobalRules).Enabled);
        Assert.Equal("p2", Assert.Single(loaded.LibraryOverrides).ProfileId);
    }

    [Fact]
    public void ControlCharacters_InTextFields_AreStrippedSoTheConfigSurvivesARestart()
    {
        var plugin = _host.Start();
        var incoming = RichConfig(plugin.Configuration);
        incoming.Profiles[0].Name = "Main\u001b[0m";
        incoming.Profiles[1].ExtraVideoArgs = "-tune film\u0007";
        incoming.Profiles[1].OutputDirectory = "/srv/\u0001out";
        incoming.GlobalRules[0].Conditions[0].Value = "h264\u001b";
        incoming.LibraryOverrides[0].LibraryName = "TV\u0000";
        incoming.HardwareDecoder = "videotoolbox\u001f";

        plugin.UpdateConfiguration(incoming);

        // Without the strip this saved fine and only failed at the next start, where BasePlugin replaced the
        // unreadable file with defaults: one seeded profile, no override, the example rule switched off.
        var restarted = _host.Start().Configuration;
        AssertRichConfigSurvived(restarted);
        Assert.Equal("Main[0m", restarted.Profiles[0].Name);
        Assert.Equal("-tune film", restarted.Profiles[1].ExtraVideoArgs);
        Assert.Equal("/srv/out", restarted.Profiles[1].OutputDirectory);
        Assert.Equal("h264", restarted.GlobalRules[0].Conditions[0].Value);
        Assert.Equal("TV", restarted.LibraryOverrides[0].LibraryName);
        Assert.Equal("videotoolbox", restarted.HardwareDecoder);
        Assert.Empty(_host.Backups());
        Assert.Contains(_host.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Profiles[1].ExtraVideoArgs", StringComparison.Ordinal));
    }

    [Fact]
    public void UndefinedEnumValue_IsRejected_AndNeitherTheLiveConfigNorTheFileChanges()
    {
        var plugin = _host.Start();
        plugin.UpdateConfiguration(RichConfig(plugin.Configuration));
        var live = plugin.Configuration;
        var fileBefore = File.ReadAllBytes(_host.ConfigFile);

        // "ChannelPolicy": 9 over the API — the JSON layer accepts any integer for an enum.
        var incoming = RichConfig(plugin.Configuration);
        incoming.Profiles[1].ChannelPolicy = (AudioChannelPolicy)9;

        var ex = Assert.Throws<ArgumentException>(() => plugin.UpdateConfiguration(incoming));
        Assert.Contains("Profiles[1].ChannelPolicy = 9", ex.Message, StringComparison.Ordinal);

        // Before, the serializer threw half-way through the write: the file was left ending at that value,
        // and the next start loaded it without a single preset, rule or override.
        Assert.Same(live, plugin.Configuration);
        Assert.Equal(fileBefore, File.ReadAllBytes(_host.ConfigFile));
        AssertRichConfigSurvived(_host.Start().Configuration);
    }

    [Fact]
    public void UndefinedEnumValues_AreRejectedWhereverTheyAreInTheGraph()
    {
        var plugin = _host.Start();
        var incoming = RichConfig(plugin.Configuration);
        incoming.Profiles[0].VideoQualityMode = (QualityMode)7;
        incoming.GlobalRules[0].Combine = (ConditionCombine)5;
        incoming.LibraryOverrides[0].Rules.Add(new TriggerRule { Conditions = { new RuleCondition { Operator = (ComparisonOperator)42 } } });

        var ex = Assert.Throws<ArgumentException>(() => plugin.UpdateConfiguration(incoming));

        Assert.Contains("Profiles[0].VideoQualityMode = 7", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GlobalRules[0].Combine = 5", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LibraryOverrides[0].Rules[0].Conditions[0].Operator = 42", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingFromAStaleTab_KeepsTheQueuePausedFlagTheQueueSet()
    {
        var plugin = _host.Start();

        // The settings page was opened while the queue was running...
        var stalePage = RichConfig(plugin.Configuration);
        Assert.False(stalePage.QueuePaused);

        // ...then the queue was paused from the control center (QueueProcessor.PersistPaused does exactly this)...
        plugin.Configuration.QueuePaused = true;
        plugin.SaveConfiguration();

        // ...and then the settings were saved from the old tab.
        plugin.UpdateConfiguration(stalePage);

        Assert.True(plugin.Configuration.QueuePaused);
        Assert.True(_host.Start().Configuration.QueuePaused);
    }

    [Fact]
    public void SavingSettings_CannotPauseTheQueueEither()
    {
        var plugin = _host.Start();
        var incoming = RichConfig(plugin.Configuration);
        incoming.QueuePaused = true;

        plugin.UpdateConfiguration(incoming);

        Assert.False(plugin.Configuration.QueuePaused);
    }

    [Fact]
    public void NullListsAndEntries_FromTheApi_AreStoredAsEmpty()
    {
        var plugin = _host.Start();
        var incoming = RichConfig(plugin.Configuration);
        incoming.ResolutionPresets = null!;
        incoming.Profiles.Insert(0, null!);
        incoming.LibraryOverrides[0].Rules = null!;

        plugin.UpdateConfiguration(incoming);

        // Code everywhere iterates these without a null check; a null profile entry would also have been
        // written to the file and made the next start's Normalize throw, so the plugin failed to load.
        Assert.Empty(plugin.Configuration.ResolutionPresets);
        Assert.Equal(2, plugin.Configuration.Profiles.Count);
        Assert.Empty(plugin.Configuration.LibraryOverrides[0].Rules);
        Assert.Equal(2, _host.Start().Configuration.Profiles.Count);
    }

    [Fact]
    public void UnreadableConfigFile_IsKeptAsABackup_BeforeBasePluginSavesDefaultsOverIt()
    {
        // A file an older build wrote with a control character in it: it saved, but nothing can read it.
        var poisoned = RichConfig(_host.Start().Configuration);
        poisoned.Profiles[1].Name = "Phones\u001b";
        _host.Serializer.SerializeToFile(poisoned, _host.ConfigFile);
        var original = File.ReadAllBytes(_host.ConfigFile);

        var loaded = _host.Start().Configuration;

        // BasePlugin's own behaviour is unchanged — it cannot be, it happens before the plugin sees the
        // file — so the config is back to defaults...
        Assert.Single(loaded.Profiles);
        Assert.Empty(loaded.LibraryOverrides);

        // ...but the admin's file is still there to recover from, and the log says where and why.
        var backup = Assert.Single(_host.Backups());
        Assert.StartsWith(_host.ConfigFile + ".unreadable-", backup, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(backup));
        var error = Assert.Single(_host.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(backup, error.Message, StringComparison.Ordinal);
        Assert.IsType<XmlException>(error.Exception?.InnerException);
    }

    [Fact]
    public void FirstRun_AndNormalRestarts_MakeNoBackup()
    {
        Assert.False(File.Exists(_host.ConfigFile));
        _host.Start();
        Assert.True(File.Exists(_host.ConfigFile));

        _host.Start();
        _host.Start();

        Assert.Empty(_host.Backups());
        Assert.DoesNotContain(_host.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }
}
