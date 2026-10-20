using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.PreTranscode.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode;

/// <summary>
/// The Pre-Transcode plugin: proactively converts media to an admin-defined
/// compatibility baseline so repeated live transcoding is avoided.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    // True only while the constructor's first read of Configuration runs (see there).
    private bool _loadingConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;

        // BasePlugin loads the file lazily, on this first read, and when it cannot read it — a character
        // XML forbids, a hand edit gone wrong — it swallows the error and saves a fresh default over it.
        // That save comes through SaveConfiguration below while this flag is up, which is the one moment
        // the unreadable file can still be copied aside.
        _loadingConfiguration = true;
        PluginConfiguration config;
        try
        {
            config = Configuration;
        }
        finally
        {
            _loadingConfiguration = false;
        }

        // Removes any duplicate presets/profiles/rules left by older builds and seeds first-run defaults.
        // Persist only if it actually changed something, so we don't rewrite the config on every startup.
        if (ConfigurationInitializer.Normalize(config))
        {
            SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public override string Name => "Pre-Transcode";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("8da245f6-7244-449b-9f32-46043f34b5f0");

    /// <inheritdoc />
    public override string Description =>
        "Proactively pre-transcodes library media to an admin-defined compatibility baseline, "
        + "eliminating repeated live CPU transcoding for the same files.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Replaces the configuration with one posted to <c>/Plugins/{id}/Configuration</c> — by the settings
    /// page or any API client — after making it safe to store.
    /// </summary>
    /// <param name="configuration">The new configuration.</param>
    /// <exception cref="ArgumentException">An enum holds an undefined value; Jellyfin answers 400 and nothing is stored.</exception>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming)
        {
            // Before the base replaces anything: a value the XML file cannot round-trip loses the whole
            // config at the next start (see ConfigurationSanitizer).
            var stripped = ConfigurationSanitizer.Sanitize(incoming);
            if (stripped.Count > 0)
            {
                _logger.LogWarning("Removed characters that cannot be stored in the configuration file from {Fields}", string.Join(", ", stripped));
            }

            // The paused flag belongs to the queue, which persists it itself (QueueProcessor saves the live
            // config directly, never through here). The settings page posts back the whole config it
            // loaded, so a tab opened before a pause would otherwise un-pause it on disk, and the next
            // restart would resume encoding nobody asked for.
            incoming.QueuePaused = Configuration.QueuePaused;
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public override void SaveConfiguration(PluginConfiguration config)
    {
        if (_loadingConfiguration)
        {
            BackUpUnreadableConfiguration();
        }

        base.SaveConfiguration(config);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", ns)
            },
            // EnableInMainMenu puts the queue/status page in the dashboard's own sidebar, so the
            // day-to-day view is one click away instead of buried under Plugins -> Pre-Transcode.
            new PluginPageInfo
            {
                Name = "PreTranscodeQueue",
                DisplayName = "Pre-Transcode",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.queuePage.html", ns),
                EnableInMainMenu = true,
                MenuIcon = "video_settings"
            },
            // The full job list, deliberately NOT in the main menu: it is reached from the control
            // center's "Open full queue"/"Open full history" links, which pre-select its filter. Keeping
            // the long list on its own page is what stops the day-to-day view from rendering a table
            // hundreds of screens long on a large library.
            new PluginPageInfo
            {
                Name = "PreTranscodeJobs",
                DisplayName = "Pre-Transcode jobs",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.jobsPage.html", ns)
            }
        ];
    }

    // Called only when BasePlugin failed to load the config file and is about to save defaults over it.
    // Missing (first run) or empty, there is nothing to keep. Never throws: the plugin must still load.
    private void BackUpUnreadableConfiguration()
    {
        var path = ConfigurationFilePath;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                return;
            }

            // BasePlugin swallowed the reason; reading once more recovers it for the log, where it names
            // the line of the file to fix.
            Exception? reason = null;
            try
            {
                XmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), path);
            }
            catch (Exception ex)
            {
                reason = ex;
            }

            // Timestamped, so a later failure can never overwrite the copy of an earlier one.
            var backup = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".bak";
            File.Copy(path, backup, true);
            _logger.LogError(
                reason,
                "Could not read the Pre-Transcode configuration file {Path}, so Jellyfin is replacing it with defaults. The unreadable file was kept as {Backup}: to recover, stop the server, fix that copy and put it back under the original name",
                path,
                backup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the Pre-Transcode configuration file {Path}, and could not keep a copy of it before Jellyfin replaces it with defaults", path);
        }
    }
}
