using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
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
    private readonly ConcurrentDictionary<string, Process> _activeProcesses = new();

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
            try
            {
#pragma warning disable CA1849 // synchronous Cancel is intentional: CancelJob is a synchronous API
                cts.Cancel();
#pragma warning restore CA1849
            }
            catch (ObjectDisposedException)
            {
                // The job finished and its CTS was disposed between the lookup and the cancel; it is
                // already effectively cancelled. Never let this escape — CancelAll iterates jobs and
                // one racing completion must not abort cancelling the rest.
            }

            return true;
        }

        return _queue.Cancel(id);
    }

    public void Pause()
    {
        // Stop claiming new jobs first, then freeze whatever is already running.
        _queue.IsPaused = true;
        foreach (var process in _activeProcesses.Values)
        {
            ProcessSuspender.Suspend(process);
        }
    }

    public void Resume()
    {
        // Unfreeze running encodes before allowing new ones to be claimed.
        foreach (var process in _activeProcesses.Values)
        {
            ProcessSuspender.Resume(process);
        }

        _queue.IsPaused = false;
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

                // Clamp both ends: the config value is a raw int with only a client-side max, so a
                // hand-edited or API-set MaxConcurrentJobs could otherwise spawn an unbounded number of
                // ffmpeg processes and exhaust the host.
                var maxConcurrency = Math.Clamp(config?.MaxConcurrentJobs ?? 1, 1, 32);
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
                    await _executor.ExecuteAsync(
                        job,
                        jobCts.Token,
                        process =>
                        {
                            _activeProcesses[job.Id] = process;

                            // Close the race where the queue was paused between claiming this job and the
                            // encode actually starting: suspend the fresh process immediately if we are
                            // already paused, so nothing slips through and runs unpaused.
                            if (_queue.IsPaused)
                            {
                                ProcessSuspender.Suspend(process);
                            }
                        }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error running job {Id}", job.Id);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    _activeProcesses.TryRemove(job.Id, out _);
                    if (_active.TryRemove(job.Id, out var removed))
                    {
                        removed.Dispose();
                    }
                }
            },
            CancellationToken.None);
    }
}
