using System;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// A unit of pre-transcoding work. Persisted to disk so the queue survives server restarts.
/// </summary>
public class TranscodeJob
{
    /// <summary>
    /// Gets or sets the unique job id.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the Jellyfin item id this job relates to (for reference), if known.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human-readable name for display in the queue UI.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library id this item belongs to (for reference).
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the encoding profile to use (empty = global default).
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current status.
    /// </summary>
    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>
    /// Gets or sets the progress percentage (0-100).
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets a short human-readable status detail (e.g. "probing", "verifying").
    /// </summary>
    public string StatusDetail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error message when <see cref="Status"/> is <see cref="JobStatus.Failed"/>.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an excerpt of the ffmpeg log captured on failure.
    /// </summary>
    public string LogExcerpt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the final output path when completed.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of processing attempts.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Gets or sets when the job was created (UTC).
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when processing last started (UTC).
    /// </summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the job finished (UTC).
    /// </summary>
    public DateTime? FinishedUtc { get; set; }
}
