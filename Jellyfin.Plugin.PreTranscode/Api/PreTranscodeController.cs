using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Api;

/// <summary>
/// Admin-only API surface for the Pre-Transcode plugin: ffmpeg capability discovery (for the config
/// dashboard) and job-queue management (for the queue/status page).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("PreTranscode")]
[Produces(MediaTypeNames.Application.Json)]
public class PreTranscodeController : ControllerBase
{
    private readonly IFfmpegCapabilitiesService _capabilities;
    private readonly IJobQueue _queue;
    private readonly IQueueController _queueController;
    private readonly ItemEvaluator _evaluator;
    private readonly ILogger<PreTranscodeController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreTranscodeController"/> class.
    /// </summary>
    /// <param name="capabilities">The ffmpeg capabilities service.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="queueController">The runtime queue controller.</param>
    /// <param name="evaluator">The library item evaluator.</param>
    /// <param name="logger">The logger.</param>
    public PreTranscodeController(
        IFfmpegCapabilitiesService capabilities,
        IJobQueue queue,
        IQueueController queueController,
        ItemEvaluator evaluator,
        ILogger<PreTranscodeController> logger)
    {
        _capabilities = capabilities;
        _queue = queue;
        _queueController = queueController;
        _evaluator = evaluator;
        _logger = logger;
    }

    /// <summary>
    /// Gets the capabilities discovered from the server's ffmpeg binary.
    /// </summary>
    /// <param name="refresh">When <c>true</c>, forces a re-probe instead of returning the cached result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered ffmpeg capabilities.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FfmpegCapabilities>> GetCapabilities([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        return Ok(await _capabilities.GetCapabilitiesAsync(refresh, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Gets the valid preset/speed values for a specific encoder.
    /// </summary>
    /// <param name="encoder">The encoder name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered preset information.</returns>
    [HttpGet("Encoders/{encoder}/Presets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EncoderPresetInfo>> GetEncoderPresets([FromRoute][Required] string encoder, CancellationToken cancellationToken = default)
    {
        return Ok(await _capabilities.GetEncoderPresetsAsync(encoder, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists all jobs in the queue.
    /// </summary>
    /// <returns>All jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TranscodeJob>> GetJobs()
    {
        return Ok(_queue.GetJobs());
    }

    /// <summary>
    /// Gets a summary of queue status and job counts.
    /// </summary>
    /// <returns>The queue status.</returns>
    [HttpGet("Queue/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetQueueStatus()
    {
        // Single pass rather than five separate Count() enumerations; this endpoint is polled every 2s.
        int pending = 0, processing = 0, completed = 0, failed = 0, skipped = 0;
        var jobs = _queue.GetJobs();
        foreach (var job in jobs)
        {
            switch (job.Status)
            {
                case JobStatus.Pending: pending++; break;
                case JobStatus.Processing: processing++; break;
                case JobStatus.Completed: completed++; break;
                case JobStatus.Failed: failed++; break;
                case JobStatus.Skipped: skipped++; break;
                default: break;
            }
        }

        return Ok(new
        {
            IsPaused = _queue.IsPaused,
            Pending = pending,
            Processing = processing,
            Completed = completed,
            Failed = failed,
            Skipped = skipped,
            Total = jobs.Count
        });
    }

    /// <summary>
    /// Starts a library sweep in the background: evaluates every library item against the active
    /// rules and queues the matches. Same work as the "Pre-Transcode: sweep library" scheduled task,
    /// triggerable from the plugin page. Returns immediately; watch the queue page for progress.
    /// </summary>
    /// <returns>Accepted.</returns>
    [HttpPost("Sweep")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult StartSweep()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _evaluator.SweepAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manually triggered sweep failed");
            }
        });

        return Accepted();
    }

    /// <summary>
    /// Manually enqueues a job for a file path.
    /// </summary>
    /// <param name="request">The enqueue request.</param>
    /// <returns>The created job, or a conflict result if a duplicate was skipped.</returns>
    [HttpPost("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<TranscodeJob> EnqueueJob([FromBody] EnqueueJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return BadRequest("SourcePath is required.");
        }

        var job = new TranscodeJob
        {
            SourcePath = request.SourcePath,
            ProfileId = request.ProfileId,
            ItemId = request.ItemId,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? System.IO.Path.GetFileName(request.SourcePath) : request.DisplayName,
            LibraryId = request.LibraryId,
            CreatedUtc = DateTime.UtcNow
        };

        return _queue.Enqueue(job) ? Ok(job) : Conflict("An active job already exists for this file.");
    }

    /// <summary>
    /// Cancels a job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/{id}/Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelJob([FromRoute][Required] string id)
    {
        return _queueController.CancelJob(id) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Cancels every pending and processing job at once (any running encode is aborted).
    /// </summary>
    /// <returns>The number of jobs cancelled.</returns>
    [HttpPost("Jobs/CancelAll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> CancelAllJobs()
    {
        var active = _queue.GetJobs()
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing)
            .ToList();

        var cancelled = 0;
        foreach (var job in active)
        {
            if (_queueController.CancelJob(job.Id))
            {
                cancelled++;
            }
        }

        _logger.LogInformation("Bulk-cancelled {Count} job(s)", cancelled);
        return Ok(new { Cancelled = cancelled });
    }

    /// <summary>
    /// Re-queues a finished or failed job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/{id}/Requeue")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RequeueJob([FromRoute][Required] string id)
    {
        return _queue.Requeue(id) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Removes a job from the queue.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Jobs/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemoveJob([FromRoute][Required] string id)
    {
        // Cancel first so a running encode is actually stopped. Removing the record alone would leave the
        // ffmpeg process running (orphaned) and, because the source no longer has a queued job, let a
        // later sweep re-enqueue and re-transcode the same file into a duplicate output.
        _queueController.CancelJob(id);
        return _queue.Remove(id) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Removes all finished (completed/failed/cancelled/skipped) jobs.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/ClearFinished")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearFinished()
    {
        _queue.ClearFinished();
        return NoContent();
    }

    /// <summary>
    /// Pauses the queue (no new jobs are started).
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Queue/Pause")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult PauseQueue()
    {
        _queueController.Pause();
        return NoContent();
    }

    /// <summary>
    /// Resumes the queue.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Queue/Resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ResumeQueue()
    {
        _queueController.Resume();
        return NoContent();
    }
}
