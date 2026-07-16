using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// Background service that drains the <see cref="IJobQueue"/>, honouring the configured maximum
/// concurrency and the enabled/paused state. Runs for the lifetime of the server.
/// </summary>
internal sealed class QueueProcessor : IHostedService, IQueueController, IDisposable
{
    private readonly IJobQueue _queue;
    private readonly TranscodeExecutor _executor;
    private readonly ILogger<QueueProcessor> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    private CancellationTokenSource? _stopCts;
    private Task? _loop;
    private int _inFlight;

    public QueueProcessor(IJobQueue queue, TranscodeExecutor executor, ILogger<QueueProcessor> logger)
    {
        _queue = queue;
        _executor = executor;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopCts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_stopCts.Token), CancellationToken.None);
        _logger.LogInformation("Pre-Transcode queue processor started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopCts is not null)
        {
            await _stopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    /// <summary>
    /// Cancels a job whether it is queued or actively running.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns><c>true</c> if the job existed.</returns>
    public bool CancelJob(string id)
    {
        if (_active.TryGetValue(id, out var cts))
        {
#pragma warning disable CA1849 // synchronous Cancel is intentional: CancelJob is a synchronous API
            cts.Cancel();
#pragma warning restore CA1849
            return true;
        }

        return _queue.Cancel(id);
    }

    public void Dispose()
    {
        _stopCts?.Dispose();
        foreach (var cts in _active.Values)
        {
            cts.Dispose();
        }
    }

    private async Task RunLoopAsync(CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var maxConcurrency = Math.Max(1, config?.MaxConcurrentJobs ?? 1);
                var enabled = config?.Enabled == true && !_queue.IsPaused;

                if (!enabled || Volatile.Read(ref _inFlight) >= maxConcurrency)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stopToken).ConfigureAwait(false);
                    continue;
                }

                var job = _queue.ClaimNextPending();
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stopToken).ConfigureAwait(false);
                    continue;
                }

                StartJob(job, stopToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue processor loop error");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void StartJob(TranscodeJob job, CancellationToken stopToken)
    {
        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _active[job.Id] = jobCts;
        Interlocked.Increment(ref _inFlight);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _executor.ExecuteAsync(job, jobCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error running job {Id}", job.Id);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    if (_active.TryRemove(job.Id, out var removed))
                    {
                        removed.Dispose();
                    }
                }
            },
            CancellationToken.None);
    }
}
