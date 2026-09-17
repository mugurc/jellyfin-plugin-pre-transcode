namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// What the processing window is doing right now, for the queue pages.
/// </summary>
public sealed class QueueScheduleState
{
    /// <summary>Gets or sets a value indicating whether the window is configured at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the wall clock is inside the window.</summary>
    public bool Open { get; set; }

    /// <summary>Gets or sets a value indicating whether an admin resumed the queue anyway, until the window's next edge.</summary>
    public bool Overridden { get; set; }

    /// <summary>Gets or sets the window's start as "HH:mm" in server local time.</summary>
    public string Start { get; set; } = string.Empty;

    /// <summary>Gets or sets the window's stop as "HH:mm" in server local time.</summary>
    public string Stop { get; set; } = string.Empty;

    /// <summary>Gets or sets the next edge as "HH:mm", or empty when the window never changes state.</summary>
    public string NextChange { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether the window is what is holding the queue back.</summary>
    public bool Holding => Enabled && !Open && !Overridden;
}
