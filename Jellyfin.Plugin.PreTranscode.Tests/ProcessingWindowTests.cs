using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Jobs;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// The daily processing window (issue #7): a download at 19:00 must not start encoding while the
/// machine is in use. The verdict is pure wall-clock arithmetic, so the awkward cases — a window that
/// runs over midnight, and a box ticked before any time was chosen — are pinned here.
/// </summary>
public class ProcessingWindowTests
{
    private static PluginConfiguration Config(bool enabled, int start, int stop)
        => new()
        {
            ProcessingWindowEnabled = enabled,
            ProcessingWindowStartMinutes = start,
            ProcessingWindowStopMinutes = stop
        };

    [Fact]
    public void IsOpen_NoConfig_IsOpen()
    {
        Assert.True(ProcessingWindow.IsOpen(null, new System.DateTime(2026, 9, 17, 3, 0, 0)));
    }

    [Fact]
    public void IsOpen_Disabled_IsAlwaysOpen()
    {
        // The times are still stored while the feature is off; they must not gate anything.
        var config = Config(false, 2 * 60, 6 * 60);
        Assert.True(ProcessingWindow.IsOpen(config, new System.DateTime(2026, 9, 17, 12, 0, 0)));
    }

    [Fact]
    public void IsOpen_EnabledConfig_UsesTheWallClock()
    {
        var config = Config(true, 2 * 60, 6 * 60);
        Assert.False(ProcessingWindow.IsOpen(config, new System.DateTime(2026, 9, 17, 12, 30, 0)));
        Assert.True(ProcessingWindow.IsOpen(config, new System.DateTime(2026, 9, 17, 2, 30, 0)));
    }

    [Fact]
    public void IsOpen_DaytimeWindow_IsHalfOpenInterval()
    {
        // 09:00-17:00: the start minute counts as inside, the stop minute as outside.
        Assert.False(ProcessingWindow.IsOpen(9 * 60, 17 * 60, (8 * 60) + 59));
        Assert.True(ProcessingWindow.IsOpen(9 * 60, 17 * 60, 9 * 60));
        Assert.True(ProcessingWindow.IsOpen(9 * 60, 17 * 60, (16 * 60) + 59));
        Assert.False(ProcessingWindow.IsOpen(9 * 60, 17 * 60, 17 * 60));
    }

    [Fact]
    public void IsOpen_OvernightWindow_WrapsPastMidnight()
    {
        // 22:00-06:00 is the whole point of the feature and is the case a naive start<=now<stop breaks.
        Assert.True(ProcessingWindow.IsOpen(22 * 60, 6 * 60, (23 * 60) + 30));
        Assert.True(ProcessingWindow.IsOpen(22 * 60, 6 * 60, 0));
        Assert.True(ProcessingWindow.IsOpen(22 * 60, 6 * 60, (5 * 60) + 59));
        Assert.False(ProcessingWindow.IsOpen(22 * 60, 6 * 60, 6 * 60));
        Assert.False(ProcessingWindow.IsOpen(22 * 60, 6 * 60, 12 * 60));
        Assert.False(ProcessingWindow.IsOpen(22 * 60, 6 * 60, (21 * 60) + 59));
    }

    [Fact]
    public void IsOpen_EqualEnds_IsOpenAllDay()
    {
        // Both selects default to 00:00, so an admin who ticks the box and saves must not stop the queue
        // for ever — "never" is not a reachable configuration.
        Assert.True(ProcessingWindow.IsOpen(0, 0, 0));
        Assert.True(ProcessingWindow.IsOpen(0, 0, 13 * 60));
        Assert.True(ProcessingWindow.IsOpen(9 * 60, 9 * 60, 3 * 60));
        Assert.True(ProcessingWindow.IsOpen(Config(true, 0, 0), new System.DateTime(2026, 9, 17, 19, 0, 0)));
    }

    [Fact]
    public void Normalize_WrapsOutOfRangeMinutes()
    {
        Assert.Equal(60, ProcessingWindow.Normalize(1500));
        Assert.Equal(1410, ProcessingWindow.Normalize(-30));
        Assert.Equal(0, ProcessingWindow.Normalize(ProcessingWindow.MinutesPerDay));
        Assert.Equal(90, ProcessingWindow.Normalize(90));
    }

    [Fact]
    public void Format_IsZeroPaddedHoursAndMinutes()
    {
        Assert.Equal("01:30", ProcessingWindow.Format(90));
        Assert.Equal("00:00", ProcessingWindow.Format(0));
        Assert.Equal("23:30", ProcessingWindow.Format((23 * 60) + 30));
        Assert.Equal("01:00", ProcessingWindow.Format(1500));
    }
}
