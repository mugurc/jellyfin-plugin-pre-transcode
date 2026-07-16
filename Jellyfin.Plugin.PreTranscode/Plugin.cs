using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.PreTranscode.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PreTranscode;

/// <summary>
/// The Pre-Transcode plugin: proactively converts media to an admin-defined
/// compatibility baseline so repeated live transcoding is avoided.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Runs once the saved config has been loaded by the base constructor: removes any duplicate
        // presets/profiles/rules left by older builds and seeds first-run defaults. Persist only if it
        // actually changed something, so we don't rewrite the config on every startup.
        if (ConfigurationInitializer.Normalize(Configuration))
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
            }
        ];
    }
}
