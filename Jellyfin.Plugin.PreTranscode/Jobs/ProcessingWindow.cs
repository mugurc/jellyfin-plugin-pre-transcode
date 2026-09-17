using System;
using System.Globalization;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// The configured daily processing window, evaluated against the wall clock. Pure: everything it needs
/// is passed in, so the edge cases (a window over midnight, a half-configured one) are unit-testable.
/// </summary>
internal static class ProcessingWindow
{
    internal const int MinutesPerDay = 1440;

    internal static int Normalize(int minutes)
    {
        var wrapped = minutes % MinutesPerDay;
        return wrapped < 0 ? wrapped + MinutesPerDay : wrapped;
    }

    internal static string Format(int minutes)
    {
        var m = Normalize(minutes);
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", m / 60, m % 60);
    }

    internal static bool IsOpen(PluginConfiguration? config, DateTime localNow)
        => config?.ProcessingWindowEnabled != true
            || IsOpen(config.ProcessingWindowStartMinutes, config.ProcessingWindowStopMinutes, (localNow.Hour * 60) + localNow.Minute);

    internal static bool IsOpen(int startMinutes, int stopMinutes, int minuteOfDay)
    {
        var start = Normalize(startMinutes);
        var stop = Normalize(stopMinutes);
        var now = Normalize(minuteOfDay);

        // Equal ends mean "all day", never "never": the settings page offers both as 00:00, and an admin
        // who ticks the box before picking times must not find the queue stopped for ever.
        if (start == stop)
        {
            return true;
        }

        return start < stop ? now >= start && now < stop : now >= start || now < stop;
    }
}
