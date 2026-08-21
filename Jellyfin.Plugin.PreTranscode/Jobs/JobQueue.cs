using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// File-backed <see cref="IJobQueue"/>. All state is persisted to a JSON file under the plugin's
/// data folder so the queue survives restarts.
/// <para>
/// The whole list is rewritten on every save, which is fine for a handful of jobs and ruinous for a
/// large library: at 50,000 jobs the file is ~33 MB and one save costs ~95 ms (80 ms to serialise,
/// 15 ms to write) with the queue lock held throughout. A sweep that enqueues 50,000 items used to do
/// that 50,000 times over a growing file — tens of minutes of pure I/O, hundreds of gigabytes written,
/// and the lock held almost continuously, which is what made the queue page crawl while a sweep ran.
/// </para>
/// <para>
/// So the two high-frequency, low-value writes — enqueueing, and the transient "probing"/"verifying"
/// status detail — are coalesced into one write every <see cref="FlushInterval"/> instead. Everything
/// whose loss would actually cost something still writes through immediately: a job reaching a final
/// state, a claim, and every admin action. A hard kill can therefore lose at most a few seconds of
/// queued-but-not-yet-started work, which the next sweep re-creates.
/// </para>
/// </summary>
internal sealed class JobQueue : IJobQueue, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // How long a coalesced change may sit unwritten. Nothing reads the file while the server runs — the
    // pages are served from memory — so this trades only crash exposure against write volume, and what
    // is exposed is work the next sweep would queue again anyway.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<JobQueue> _logger;
    private readonly string _filePath;
    private readonly object _sync = new();
    private readonly List<TranscodeJob> _jobs = new();

    // Held across a whole flush so a periodic flush and a write-through flush cannot interleave their
    // writes, and so Dispose's final flush waits for one already in progress. Always taken BEFORE
    // _sync — never the other way round — so the two can never deadlock.
    private readonly object _flushLock = new();

    private readonly Timer _flushTimer;

    // Written from API threads (Pause/Resume) and read from the queue loop and the start-time suspend
    // callback without taking _sync; volatile gives those lock-free reads a fresh value promptly.
    private volatile bool _isPaused;

    // Set by every mutation, cleared by the flush that persists it. Guarded by _sync.
    private bool _dirty;

    private volatile bool _disposed;

    public JobQueue(IApplicationPaths applicationPaths, ILogger<JobQueue> logger)
    {
        _logger = logger;
        var dir = Path.Combine(applicationPaths.DataPath, "pretranscode");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "queue.json");
        Load();

        // Created stopped and armed straight after, so the callback cannot observe a half-constructed
        // instance. One-shot and re-armed at the end of each tick rather than periodic: a flush that
        // outlasts the interval on a slow disk then cannot pile callbacks up behind itself.
        _flushTimer = new Timer(_ => OnFlushTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _flushTimer.Change(FlushInterval, Timeout.InfiniteTimeSpan);
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => _isPaused = value;
    }

    public void Dispose()
    {
        _disposed = true;
        _flushTimer.Dispose();

        // Persist whatever the last tick did not. Takes _flushLock, so it waits for a flush already in
        // flight instead of racing it.
        Flush();
    }

    public IReadOnlyList<TranscodeJob> GetJobs()
    {
        lock (_sync)
        {
            return _jobs.ToList();
        }
    }

    public TranscodeJob? Get(string id)
    {
        lock (_sync)
        {
            return _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
        }
    }

    public bool Enqueue(TranscodeJob job, Func<IReadOnlyList<TranscodeJob>, bool>? isRedundant = null)
    {
        lock (_sync)
        {
            var duplicate = _jobs.Any(j =>
                string.Equals(j.SourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase)
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing));

            if (duplicate)
            {
                return false;
            }

            // Final redundancy check, atomic with the add: an earlier job for this source may have
            // completed (and produced its output) while the caller was probing, which the caller's
            // pre-probe checks could not have seen.
            if (isRedundant is not null && isRedundant(_jobs))
            {
                return false;
            }

            _jobs.Add(job);
            TrimFinished();

            // Coalesced, not written through. This is the sweep's inner loop: writing the whole file per
            // item made a large sweep O(n^2) in bytes. A queued-but-unstarted job lost to a hard kill is
            // re-created by the next sweep, so nothing is actually at stake here.
            _dirty = true;
            return true;
        }
    }

    public void Update(TranscodeJob job)
    {
        bool writeThrough;
        lock (_sync)
        {
            var index = _jobs.FindIndex(j => string.Equals(j.Id, job.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _jobs[index] = job;
            }

            TrimFinished();
            _dirty = true;

            // A job reaching a final state is the one record worth paying a full write for: losing a
            // Completed record makes the next sweep re-encode a file that is already done — and under
            // Replace-in-place, with the compliance skip switched off, that means re-encoding the
            // previous encode. The "probing"/"transcoding"/"verifying" details in between are display
            // text and can wait for the timer.
            writeThrough = JobQuery.IsFinished(job.Status);
        }

        if (writeThrough)
        {
            Flush();
        }
    }

    public TranscodeJob? ClaimNextPending()
    {
        TranscodeJob? claimed;
        lock (_sync)
        {
            if (IsPaused)
            {
                return null;
            }

            // Single pass for the oldest pending job, rather than sorting the whole (potentially large,
            // never-pruned) list on every claim.
            var job = _jobs
                .Where(j => j.Status == JobStatus.Pending)
                .MinBy(j => j.CreatedUtc);

            if (job is null)
            {
                return null;
            }

            job.Status = JobStatus.Processing;
            job.StartedUtc = DateTime.UtcNow;
            job.AttemptCount++;
            _dirty = true;
            claimed = job;
        }

        // Outside the lock: AttemptCount is what stops a file that fails every time from being retried
        // for ever, so it is kept as durable as it was before. One write per job start is nothing next
        // to the encode that follows it.
        Flush();
        return claimed;
    }

    public bool Cancel(string id)
    {
        var cancelled = false;
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
            if (job is null)
            {
                return false;
            }

            if (job.Status == JobStatus.Pending)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedUtc = DateTime.UtcNow;
                _dirty = true;
                cancelled = true;
            }
        }

        // Admin actions write through: an action that silently un-does itself after a restart is worse
        // than the write it costs, and they happen at human frequency.
        if (cancelled)
        {
            Flush();
        }

        return true;
    }

    public int CancelAllPending()
    {
        var cancelled = 0;
        var now = DateTime.UtcNow;

        lock (_sync)
        {
            // One pass over the list and one write, rather than a lookup and a full-file write per job.
            foreach (var job in _jobs)
            {
                if (job.Status != JobStatus.Pending)
                {
                    continue;
                }

                job.Status = JobStatus.Cancelled;
                job.FinishedUtc = now;
                cancelled++;
            }

            if (cancelled > 0)
            {
                _dirty = true;
            }
        }

        if (cancelled > 0)
        {
            Flush();
        }

        return cancelled;
    }

    public bool Requeue(string id)
    {
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
            if (job is null)
            {
                return false;
            }

            // Never re-queue a job that is still running: flipping a Processing job back to Pending
            // leaves the executor running while the loop re-claims the same job, producing a second run
            // on the same temp path (corruption at concurrency >= 2, a wasted re-encode at 1). Cancel it
            // first if you want to restart it.
            if (job.Status == JobStatus.Processing)
            {
                return false;
            }

            job.Status = JobStatus.Pending;
            job.Progress = 0;
            job.ErrorMessage = string.Empty;
            job.LogExcerpt = string.Empty;
            job.StatusDetail = string.Empty;
            job.StartedUtc = null;
            job.FinishedUtc = null;
            _dirty = true;
        }

        Flush();
        return true;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_sync)
        {
            removed = _jobs.RemoveAll(j => string.Equals(j.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                _dirty = true;
            }
        }

        if (removed)
        {
            Flush();
        }

        return removed;
    }

    public void ClearFinished()
    {
        lock (_sync)
        {
            _jobs.RemoveAll(j => JobQuery.IsFinished(j.Status));
            _dirty = true;
        }

        Flush();
    }

    /// <summary>
    /// Drops the oldest finished jobs beyond the configured cap. Pure over the job list so the policy is
    /// unit-testable; the caller holds the lock and persists afterwards.
    /// </summary>
    /// <param name="jobs">The job list, mutated in place.</param>
    /// <param name="maxFinishedKept">The cap; <c>0</c> or less means unlimited.</param>
    /// <returns>How many jobs were removed.</returns>
    internal static int TrimFinished(List<TranscodeJob> jobs, int maxFinishedKept)
    {
        if (maxFinishedKept <= 0)
        {
            return 0;
        }

        // Pending and Processing jobs are the live queue and are never eligible, however old they are.
        var finished = jobs
            .Where(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped)
            .ToList();

        var excess = finished.Count - maxFinishedKept;
        if (excess <= 0)
        {
            return 0;
        }

        // Oldest first. FinishedUtc is the natural key; fall back to CreatedUtc for a record written by an
        // older build (or one that somehow finished without a timestamp) so ordering is always total.
        var doomed = finished
            .OrderBy(j => j.FinishedUtc ?? j.CreatedUtc)
            .Take(excess)
            .Select(j => j.Id)
            .ToHashSet(StringComparer.Ordinal);

        return jobs.RemoveAll(j => doomed.Contains(j.Id));
    }

    private void TrimFinished()
    {
        var cap = Plugin.Instance?.Configuration.MaxFinishedJobsKept ?? 0;
        var removed = TrimFinished(_jobs, cap);
        if (removed > 0)
        {
            _logger.LogInformation("Trimmed {Count} old finished job(s) to stay within the {Cap}-job history limit", removed, cap);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<TranscodeJob>>(json, JsonOptions);
            if (loaded is not null)
            {
                _jobs.AddRange(loaded);
            }

            // Any job left "Processing" from a crash is reset so it will be retried.
            foreach (var job in _jobs.Where(j => j.Status == JobStatus.Processing))
            {
                job.Status = JobStatus.Pending;
                job.Progress = 0;
                job.StatusDetail = string.Empty;
            }

            _logger.LogInformation("Loaded {Count} pre-transcode job(s) from disk", _jobs.Count);
        }
        catch (Exception ex)
        {
            // Preserve the unreadable file instead of letting the next Save() overwrite it with an empty
            // queue — a corrupt file is recoverable by hand, a silently-truncated one is not.
            _logger.LogError(ex, "Failed to load job queue; starting empty and preserving the existing file");
            _jobs.Clear();
            TryPreserveCorruptFile();
        }
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            // Keep the FIRST corrupt copy for forensics; a later corruption must not overwrite it
            // (overwrite: true would discard the very evidence this preserves).
            var corruptPath = _filePath + ".corrupt";
            if (File.Exists(_filePath) && !File.Exists(corruptPath))
            {
                File.Move(_filePath, corruptPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnFlushTick()
    {
        Flush();

        if (_disposed)
        {
            return;
        }

        try
        {
            _flushTimer.Change(FlushInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and here; nothing left to schedule.
        }
    }

    /// <summary>
    /// Writes the queue out if anything has changed since the last write. Serialising needs the queue
    /// lock — jobs are mutated in place — but the file write does not, so the lock is released first:
    /// at 50,000 jobs that is the difference between holding it for ~15 ms and for ~95 ms.
    /// </summary>
    private void Flush()
    {
        lock (_flushLock)
        {
            string json;
            lock (_sync)
            {
                if (!_dirty)
                {
                    return;
                }

                json = JsonSerializer.Serialize(_jobs, JsonOptions);
                _dirty = false;
            }

            if (!TryWrite(json))
            {
                // Put the flag back so the next tick retries. Leaving it clear would strand every change
                // made so far behind a single transient failure (a full disk, a locked file) until some
                // later mutation happened to set it again.
                lock (_sync)
                {
                    _dirty = true;
                }
            }
        }
    }

    private bool TryWrite(string json)
    {
        try
        {
            // Write to a temp file and atomically rename over the target. File.WriteAllText truncates in
            // place first, so a crash mid-write would leave a half-written file that fails to parse on the
            // next start (and would then be discarded). The temp file lives in the same directory as the
            // target, so the rename is a same-volume atomic operation.
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist job queue");
            return false;
        }
    }
}
