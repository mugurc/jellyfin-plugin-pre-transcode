using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    // How long a shutdown waits for running encodes to unwind before giving up on them.
    private const int DrainTimeoutMs = 30000;
    private const int DrainPollMs = 200;

    private readonly IJobQueue _queue;
    private readonly TranscodeExecutor _executor;
    private readonly ILogger<QueueProcessor> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<string, Process> _activeProcesses = new();

    // Tracks which running encodes are currently OS-suspended, guarded by _suspendLock and kept in step
    // with _activeProcesses. A process must be suspended at most once: on Windows NtSuspendProcess
    // increments a per-thread suspend count, so a double-suspend needs two resumes — a single Resume()
    // would then leave the encode frozen forever. This set makes Pause()/Resume() and the start-time
    // suspend idempotent.
    private readonly HashSet<string> _suspended = new();
    private readonly object _suspendLock = new();

    // Ids of jobs cancelled in the narrow window after they were claimed (marked Processing) but before
    // StartJob registered their CTS in _active. StartJob drains this the moment it registers, so a cancel
    // that lands in that window is honoured instead of silently reported as succeeded while the encode
    // runs to completion.
    private readonly ConcurrentDictionary<string, byte> _cancelRequested = new();

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
        // Restore the persisted pause state before the loop can claim anything. An admin who paused the
        // queue to free the CPU and then restarted (or updated) the server would otherwise come back to
        // encodes running again, with nothing in the UI to explain why.
        var paused = Plugin.Instance?.Configuration.QueuePaused == true;
        _queue.IsPaused = paused;
        if (paused)
        {
            _logger.LogInformation("Pre-Transcode queue is paused (restored from the saved configuration)");
        }

        // Nothing is encoding yet, so anything still in the temp directory is debris from a cancel that
        // raced the dying ffmpeg, or from a crash that never reached the cleanup at all.
        _executor.CleanTempDirectory();

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

        await DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the running encodes to finish unwinding. The loop stops in microseconds; the encodes
    /// still have to kill their ffmpeg, delete their temp file and record their state.
    /// <para>
    /// Nothing waited for them before. If the host won the race it exited first, and ffmpeg — reparented
    /// to init — carried on transcoding at full CPU into a temp path derived from the job id, with no UI
    /// left to stop it. On the next start that job is reset to Pending, re-claimed, and rebuilds the very
    /// same temp path: two ffmpeg processes writing one file, and a result that can still satisfy the
    /// duration check.
    /// </para>
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        // _inFlight is incremented on the loop thread before the encode task starts and decremented in its
        // finally, and the loop has already exited, so nothing can add to it while this drains.
        for (var waited = 0; Volatile.Read(ref _inFlight) > 0 && waited < DrainTimeoutMs; waited += DrainPollMs)
        {
            try
            {
                await Task.Delay(DrainPollMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var stragglers = Volatile.Read(ref _inFlight);
        if (stragglers > 0)
        {
            _logger.LogWarning("{Count} encode(s) had not finished stopping; leaving them to the shutdown", stragglers);
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
            TryCancel(cts);
            return true;
        }

        // The job may have just been claimed (marked Processing) but not yet registered in _active.
        // JobQueue.Cancel only cancels a still-Pending job, so without this a cancel landing in that
        // window would report success yet let the encode run to completion. Record the request so
        // StartJob cancels it the instant it registers, and re-check _active in case it registered
        // between the lookup above and here.
        if (_queue.Get(id)?.Status == JobStatus.Processing)
        {
            _cancelRequested[id] = 0;
            if (_active.TryGetValue(id, out var late))
            {
                TryCancel(late);
            }

            return true;
        }

        return _queue.Cancel(id);
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
#pragma warning disable CA1849 // synchronous Cancel is intentional: the caller is a synchronous API
            cts.Cancel();
#pragma warning restore CA1849
        }
        catch (ObjectDisposedException)
        {
            // The job finished and its CTS was disposed between the lookup and the cancel; it is already
            // effectively cancelled. Never let this escape — CancelAll iterates jobs and one racing
            // completion must not abort cancelling the rest.
        }
    }

    // The paused flag is written INSIDE _suspendLock, and so is every change to _suspended. They are one
    // piece of state and were previously updated separately, which let them disagree:
    //
    //   * Resume cleared _suspended and released the lock, then an encode that was still starting took
    //     the lock, read the not-yet-cleared flag, and suspended itself into a set nobody would ever
    //     drain again — SIGSTOPped for ever, holding an _inFlight slot, with the queue reporting itself
    //     as running. At a concurrency of 1 that is the whole queue, permanently stalled.
    //   * Pause set the flag and was preempted before taking the lock; a Resume that slipped in between
    //     found nothing to resume and cleared the flag, after which Pause froze every running encode
    //     anyway. Same permanent stall, reachable by double-clicking Pause/Resume or from two browser
    //     tabs.
    //
    // Holding the lock across both makes the start-time hook — which reads the flag under the same lock —
    // see a flag and a set that always agree. _suspended.Add still gates each suspend, so no process can
    // be suspended twice and be left frozen after a single Resume.
    public void Pause()
    {
        lock (_suspendLock)
        {
            _queue.IsPaused = true;
            foreach (var (id, process) in _activeProcesses)
            {
                if (_suspended.Add(id))
                {
                    ProcessSuspender.Suspend(process);
                }
            }
        }

        PersistPaused(true);
    }

    public void Resume()
    {
        lock (_suspendLock)
        {
            // Unfreeze what we actually suspended, exactly once each, before letting the loop claim more.
            foreach (var id in _suspended)
            {
                if (_activeProcesses.TryGetValue(id, out var process))
                {
                    ProcessSuspender.Resume(process);
                }
            }

            _suspended.Clear();
            _queue.IsPaused = false;
        }

        PersistPaused(false);
    }

    // Best-effort: the in-memory flag is what actually gates the loop, so a config write that fails must
    // not turn Pause into an exception the API surfaces as a 500 — it only costs the state on restart.
    private void PersistPaused(bool paused)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is not null && plugin.Configuration.QueuePaused != paused)
            {
                plugin.Configuration.QueuePaused = paused;
                plugin.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the queue's paused state; it will not survive a restart");
        }
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

        // Honour a cancel that arrived while this job was claimed but not yet registered above.
        if (_cancelRequested.TryRemove(job.Id, out _))
        {
            TryCancel(jobCts);
        }

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
                            // already paused, so nothing slips through and runs unpaused. _suspended.Add
                            // gates it so this can never double-suspend with a concurrent Pause().
                            lock (_suspendLock)
                            {
                                if (_queue.IsPaused && _suspended.Add(job.Id))
                                {
                                    ProcessSuspender.Suspend(process);
                                }
                            }
                        }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error running job {Id}", job.Id);
                }
                finally
                {
                    // A cancel that came from the server stopping is not an admin cancelling the job, but
                    // the executor cannot tell the two apart and records both as Cancelled — which is
                    // final, so a clean restart threw away a four-hour encode and left it needing the next
                    // sweep to notice, while a kill -9 (leaving it Processing) was correctly resumed.
                    // Putting it back to Pending makes the graceful path at least as good as the crash.
                    if (stopToken.IsCancellationRequested && _queue.Get(job.Id)?.Status == JobStatus.Cancelled)
                    {
                        _queue.Requeue(job.Id);
                    }

                    Interlocked.Decrement(ref _inFlight);
                    _activeProcesses.TryRemove(job.Id, out _);
                    _cancelRequested.TryRemove(job.Id, out _);
                    lock (_suspendLock)
                    {
                        _suspended.Remove(job.Id);
                    }

                    if (_active.TryRemove(job.Id, out var removed))
                    {
                        removed.Dispose();
                    }
                }
            },
            CancellationToken.None);
    }
}
