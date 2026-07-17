using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// File-backed <see cref="IJobQueue"/>. All state is persisted to a JSON file under the plugin's
/// data folder so the queue survives restarts.
/// </summary>
internal sealed class JobQueue : IJobQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<JobQueue> _logger;
    private readonly string _filePath;
    private readonly object _sync = new();
    private readonly List<TranscodeJob> _jobs = new();

    public JobQueue(IApplicationPaths applicationPaths, ILogger<JobQueue> logger)
    {
        _logger = logger;
        var dir = Path.Combine(applicationPaths.DataPath, "pretranscode");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "queue.json");
        Load();
    }

    public bool IsPaused { get; set; }

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

    public bool Enqueue(TranscodeJob job)
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

            _jobs.Add(job);
            Save();
            return true;
        }
    }

    public void Update(TranscodeJob job)
    {
        lock (_sync)
        {
            var index = _jobs.FindIndex(j => string.Equals(j.Id, job.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _jobs[index] = job;
            }

            Save();
        }
    }

    public TranscodeJob? ClaimNextPending()
    {
        lock (_sync)
        {
            if (IsPaused)
            {
                return null;
            }

            var job = _jobs
                .Where(j => j.Status == JobStatus.Pending)
                .OrderBy(j => j.CreatedUtc)
                .FirstOrDefault();

            if (job is null)
            {
                return null;
            }

            job.Status = JobStatus.Processing;
            job.StartedUtc = DateTime.UtcNow;
            job.AttemptCount++;
            Save();
            return job;
        }
    }

    public bool Cancel(string id)
    {
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
                Save();
            }

            return true;
        }
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

            job.Status = JobStatus.Pending;
            job.Progress = 0;
            job.ErrorMessage = string.Empty;
            job.LogExcerpt = string.Empty;
            job.StatusDetail = string.Empty;
            job.StartedUtc = null;
            job.FinishedUtc = null;
            Save();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_sync)
        {
            var removed = _jobs.RemoveAll(j => string.Equals(j.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    public void ClearFinished()
    {
        lock (_sync)
        {
            _jobs.RemoveAll(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped);
            Save();
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

    private void Save()
    {
        try
        {
            // Write to a temp file and atomically rename over the target. File.WriteAllText truncates in
            // place first, so a crash mid-write would leave a half-written file that fails to parse on the
            // next start (and would then be discarded). The temp file lives in the same directory as the
            // target, so the rename is a same-volume atomic operation.
            var json = JsonSerializer.Serialize(_jobs, JsonOptions);
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist job queue");
        }
    }
}
