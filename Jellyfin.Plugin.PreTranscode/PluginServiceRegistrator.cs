using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.PreTranscode;

/// <summary>
/// Registers the plugin's services into Jellyfin's dependency-injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IFfmpegCapabilitiesService, FfmpegCapabilitiesService>();
        serviceCollection.AddSingleton<IMediaProber, MediaProber>();
        serviceCollection.AddSingleton<IJobQueue, JobQueue>();
        serviceCollection.AddSingleton<TranscodeExecutor>();

        serviceCollection.AddSingleton<QueueProcessor>();
        serviceCollection.AddSingleton<IQueueController>(sp => sp.GetRequiredService<QueueProcessor>());
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<QueueProcessor>());

        // ItemEvaluator is shared by the (reflection-discovered, public) scan/sweep tasks and the monitor.
        // Jellyfin discovers ILibraryPostScanTask/IScheduledTask via reflection over public types, not DI,
        // so PreTranscodeScanTask/PreTranscodeSweepTask are not registered here.
        serviceCollection.AddSingleton<ItemEvaluator>();
        serviceCollection.AddSingleton<LibraryMonitor>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LibraryMonitor>());
    }
}
