#nullable enable

using System;

namespace StationGodMCP.Pure.Sampling;

/// <summary>
/// When sample_logic samples, in real seconds since its first sample, as the sidecar loop did: the first sample at 0,
/// then one per interval, the last at the duration; the run ends with the first sample at or after the duration.
/// Each sample is taken in the first frame at or after it is due. A late sample does not shift the grid; a frame that
/// missed whole intervals takes one sample, not a burst.
/// </summary>
internal static class SampleSchedule
{
    /// <summary>
    /// A microsecond: a frame this close before a due time counts as on it, so a grid point computed as k * interval
    /// and a clock reading of the same instant that differ in the last bit do not push the sample a frame late.
    /// </summary>
    internal const double ToleranceSeconds = 1e-6;

    /// <summary>Whether a sample due at dueS is due at nowS.</summary>
    internal static bool IsDue(double dueS, double nowS) => dueS <= nowS + ToleranceSeconds;

    /// <summary>Whether a sample taken elapsedSeconds after the first ends the run.</summary>
    internal static bool Ends(double elapsedSeconds, double durationSeconds) =>
        elapsedSeconds + ToleranceSeconds >= durationSeconds;

    /// <summary>The next due time after a sample at elapsedSeconds: the next interval point, at most the duration.</summary>
    internal static double NextDue(double elapsedSeconds, double intervalSeconds, double durationSeconds)
    {
        double step = Math.Floor(elapsedSeconds / intervalSeconds) + 1.0;
        double next = step * intervalSeconds;
        while (next <= elapsedSeconds + ToleranceSeconds)
        {
            step += 1.0;
            next = step * intervalSeconds;
        }

        return Math.Min(next, durationSeconds);
    }

    /// <summary>Elapsed seconds as the reply gives them: to the millisecond, as the sidecar rounded (to even).</summary>
    internal static double Round(double seconds) => Math.Round(seconds, 3);
}
