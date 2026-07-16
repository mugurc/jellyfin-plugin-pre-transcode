using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// A dashboard scheduled task ("Pre-Transcode: sweep library") that evaluates the whole library and
/// queues matches. Can be run manually or on a schedule from Jellyfin's Scheduled Tasks page,
/// independent of the automatic post-scan hook.
/// </summary>
public sealed class PreTranscodeSweepTask : IScheduledTask
{
    private readonly ItemEvaluator _evaluator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreTranscodeSweepTask"/> class.
    /// </summary>
    /// <param name="evaluator">The item evaluator.</param>
    public PreTranscodeSweepTask(ItemEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    /// <inheritdoc />
    public string Name => "Pre-Transcode: sweep library";

    /// <inheritdoc />
    public string Key => "PreTranscodeLibrarySweep";

    /// <inheritdoc />
    public string Description => "Evaluate every library item against the Pre-Transcode rules and queue the ones that match.";

    /// <inheritdoc />
    public string Category => "Pre-Transcode";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _evaluator.SweepAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily at 03:30 by default. Harmless until the admin enables rules (nothing matches),
        // and easily changed or removed from the dashboard's Scheduled Tasks page.
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks
            }
        };
    }
}
