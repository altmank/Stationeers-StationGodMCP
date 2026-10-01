#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// How much main-thread time one frame may spend on requests ([Performance] RequestBudgetMs, default 4 ms; 0 =
/// unlimited). The first request of a frame always runs, so the queue always moves; after each one the dispatcher asks
/// MayServeAnother. While a job holds the game tick every request millisecond lengthens the pause, so the budget is
/// then at most JobHeldMs. Requests stay first in, first out.
/// </summary>
internal sealed class FrameBudget
{
    internal const double DefaultMs = 4.0;

    internal const double JobHeldMs = 2.0;

    private FrameBudget(double limitMs)
    {
        LimitMs = limitMs;
    }

    /// <summary>The frame's limit; PositiveInfinity when unlimited.</summary>
    internal double LimitMs { get; }

    internal bool Unlimited => double.IsPositiveInfinity(LimitMs);

    /// <summary>The limit for this frame: the configured one (0 = unlimited), at most JobHeldMs while a job holds the tick.</summary>
    internal static FrameBudget For(double configuredMs, bool jobHoldsTick)
    {
        double limit = configuredMs > 0.0 ? configuredMs : double.PositiveInfinity;
        return new FrameBudget(jobHoldsTick ? Math.Min(limit, JobHeldMs) : limit);
    }

    /// <summary>Whether another request may start, after served requests took elapsedMs so far this frame.</summary>
    internal bool MayServeAnother(int served, double elapsedMs) => served == 0 || elapsedMs < LimitMs;

    /// <summary>A configured value as the setting reads it: negative or not a number is refused (null).</summary>
    internal static double? Configured(double value) => double.IsNaN(value) || value < 0.0 ? null : value;
}

/// <summary>
/// The dispatcher's per-frame counters since the mod loaded: frames that served a request, how many requests and how
/// much main-thread time per frame (mean and maximum), frames the budget stopped with requests still queued, and
/// requests that expired in the queue before the main thread reached them. Main thread writes, mod_info reads; one lock.
/// </summary>
internal sealed class DispatchStats
{
    private readonly object _gate = new object();
    private long _busyFrames;
    private long _served;
    private int _maxPerFrame;
    private Tally _frameMs;
    private long _budgetStops;
    private long _expired;

    /// <summary>One frame's work: requests served, their main-thread time, and whether the budget stopped it early.</summary>
    internal void Frame(int served, double elapsedMs, bool budgetStopped)
    {
        if (served == 0 && !budgetStopped)
        {
            return;
        }

        lock (_gate)
        {
            _busyFrames++;
            _served += served;
            _maxPerFrame = Math.Max(_maxPerFrame, served);
            _frameMs = _frameMs.With(elapsedMs);
            _budgetStops += budgetStopped ? 1 : 0;
        }
    }

    /// <summary>A request whose client gave up waiting before the main thread reached it (game_timeout).</summary>
    internal void Expired()
    {
        lock (_gate)
        {
            _expired++;
        }
    }

    internal DispatchSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DispatchSnapshot(_busyFrames, _served, _maxPerFrame, _frameMs, _budgetStops, _expired);
        }
    }
}

/// <summary>DispatchStats as read at one moment.</summary>
internal sealed class DispatchSnapshot
{
    internal DispatchSnapshot(long busyFrames, long served, int maxPerFrame, Tally frameMs, long budgetStops,
        long expired)
    {
        BusyFrames = busyFrames;
        Served = served;
        MaxPerFrame = maxPerFrame;
        FrameMs = frameMs;
        BudgetStops = budgetStops;
        Expired = expired;
    }

    internal long BusyFrames { get; }

    internal long Served { get; }

    internal int MaxPerFrame { get; }

    internal Tally FrameMs { get; }

    internal long BudgetStops { get; }

    internal long Expired { get; }

    internal double? MeanPerFrame => BusyFrames > 0 ? (double)Served / BusyFrames : null;
}
