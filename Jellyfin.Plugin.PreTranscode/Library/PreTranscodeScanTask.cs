using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// Runs after each Jellyfin library scan. When automatic processing is enabled, it evaluates the
/// library and queues any items that match the rules (deduplicated by the queue).
/// </summary>
public sealed class PreTranscodeScanTask : ILibraryPostScanTask
{
    private readonly ItemEvaluator _evaluator;
    private readonly ILogger<PreTranscodeScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreTranscodeScanTask"/> class.
    /// </summary>
    /// <param name="evaluator">The item evaluator.</param>
    /// <param name="logger">The logger.</param>
    public PreTranscodeScanTask(ItemEvaluator evaluator, ILogger<PreTranscodeScanTask> logger)
    {
        _evaluator = evaluator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.ProcessNewItemsAutomatically)
        {
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Pre-Transcode post-scan evaluation starting");
        await _evaluator.SweepAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
