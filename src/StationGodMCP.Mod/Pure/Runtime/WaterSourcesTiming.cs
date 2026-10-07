#nullable enable

using System.Globalization;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Pure;

/// <summary>
/// Where one water_sources call spent its main-thread time: waiting for and copying the game's atmosphere list and
/// pipe network list (each under the list's own lock, which the game tick also holds for its passes over every
/// atmosphere), scanning the atmospheres, building the reply's rows; and the collections that ran meanwhile.
/// </summary>
internal readonly struct WaterSourcesTiming
{
    internal WaterSourcesTiming(double totalMs, double atmosphereLockMs, double atmosphereListMs, double networkLockMs,
        double networkListMs, double scanMs, double rowsMs, int atmospheres, int sources, int collections)
    {
        TotalMs = totalMs;
        AtmosphereLockMs = atmosphereLockMs;
        AtmosphereListMs = atmosphereListMs;
        NetworkLockMs = networkLockMs;
        NetworkListMs = networkListMs;
        ScanMs = scanMs;
        RowsMs = rowsMs;
        Atmospheres = atmospheres;
        Sources = sources;
        Collections = collections;
    }

    internal double TotalMs { get; }

    internal double AtmosphereLockMs { get; }

    /// <summary>The atmosphere list's lock wait and copy together.</summary>
    internal double AtmosphereListMs { get; }

    internal double NetworkLockMs { get; }

    /// <summary>The pipe network list's lock wait, copy and merge into the atmospheres together.</summary>
    internal double NetworkListMs { get; }

    internal double ScanMs { get; }

    internal double RowsMs { get; }

    internal int Atmospheres { get; }

    internal int Sources { get; }

    /// <summary>Garbage collections (GC.CollectionCount(0)) that ran during the call.</summary>
    internal int Collections { get; }
}

/// <summary>
/// The warning a slow water_sources call logs: over ThresholdMs, at most one per IntervalS (the rest counted in the
/// next), naming where the time went, so a long main-thread stall can be told apart: lock waits (the game tick held a
/// list), collections, or the scan itself.
/// </summary>
internal sealed class WaterSourcesWarning
{
    internal const double ThresholdMs = 250.0;
    internal const double IntervalS = 60.0;

    private readonly SlowFrameGate _gate = new SlowFrameGate(IntervalS);

    /// <summary>The warning for a call that ended at nowS, or null when it was quick or one was logged recently.</summary>
    internal string? Check(double nowS, in WaterSourcesTiming timing)
    {
        if (timing.TotalMs < ThresholdMs || !_gate.Allow(nowS, out int passed))
        {
            return null;
        }

        string message = string.Format(CultureInfo.InvariantCulture,
            "water_sources held the main thread {0:0.0} ms: atmosphere list {1:0.0} ms (lock wait {2:0.0} ms, {3} " +
            "atmospheres), pipe network list {4:0.0} ms (lock wait {5:0.0} ms), scan {6:0.0} ms, rows {7:0.0} ms " +
            "({8} sources), {9} garbage collection(s) during it",
            timing.TotalMs, timing.AtmosphereListMs, timing.AtmosphereLockMs, timing.Atmospheres, timing.NetworkListMs,
            timing.NetworkLockMs, timing.ScanMs, timing.RowsMs, timing.Sources, timing.Collections);
        return passed > 0
            ? message + string.Format(CultureInfo.InvariantCulture, "; {0} slow call(s) not logged before it.", passed)
            : message + ".";
    }
}
