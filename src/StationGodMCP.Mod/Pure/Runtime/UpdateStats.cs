#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// StationGod's whole Update per frame since the mod loaded: requests, subscription samples, job steps, previews,
/// highlights and the flight recorder, where frame_ms (DispatchStats) counts the requests and samples only. Server
/// info's update_ms (with include_counters) reports it, and the cost gate (scoreboard drivers/gate.py) takes a call's
/// worst frame from it: the longest frame, the frames over the request budget, every frame in FrameHistogram's
/// buckets, and a ring of the latest frames over RecentOverMs with their frame numbers, so a reader can take the
/// worst frame between two of its own readings and leave its own frames out. Fixed arrays, nothing allocated per
/// frame; main thread writes, server info reads, one lock.
/// </summary>
internal sealed class UpdateStats
{
    /// <summary>Frames over this many ms go into the recent ring.</summary>
    internal const double RecentOverMs = 1.0;

    /// <summary>How many of the latest frames over RecentOverMs the ring keeps.</summary>
    internal const int RecentCapacity = 256;

    private readonly object _gate = new object();
    private readonly FrameHistogram _histogram = new FrameHistogram();
    private readonly long[] _recentFrames = new long[RecentCapacity];
    private readonly double[] _recentMs = new double[RecentCapacity];
    private int _recentNext;
    private int _recentCount;
    private long _lastFrame;
    private double _worstMs;
    private long? _worstFrame;
    private long _overBudget;

    /// <summary>One frame's Update: Unity's frame number, its ms, and the request budget then (0 = unlimited).</summary>
    internal void Frame(long frame, double ms, double budgetMs)
    {
        double counted = ms > 0.0 ? ms : 0.0;
        lock (_gate)
        {
            _lastFrame = frame;
            _histogram.Add(counted);
            if (_worstFrame == null || counted > _worstMs)
            {
                _worstMs = counted;
                _worstFrame = frame;
            }

            _overBudget += budgetMs > 0.0 && counted > budgetMs ? 1 : 0;
            if (counted > RecentOverMs)
            {
                _recentFrames[_recentNext] = frame;
                _recentMs[_recentNext] = counted;
                _recentNext = (_recentNext + 1) % RecentCapacity;
                _recentCount = Math.Min(_recentCount + 1, RecentCapacity);
            }
        }
    }

    /// <summary>The counters now; the reading's frame is the given one (the frame server info runs in).</summary>
    internal UpdateReading Read(long frame)
    {
        lock (_gate)
        {
            long[] frames = new long[_recentCount];
            double[] ms = new double[_recentCount];
            int first = (_recentNext - _recentCount + RecentCapacity) % RecentCapacity;
            for (int index = 0; index < _recentCount; index++)
            {
                frames[index] = _recentFrames[(first + index) % RecentCapacity];
                ms[index] = _recentMs[(first + index) % RecentCapacity];
            }

            return new UpdateReading(frame, _histogram.Read(), _worstFrame == null ? null : _worstMs, _worstFrame,
                _overBudget, frames, ms);
        }
    }
}

/// <summary>UpdateStats as read at one moment: the recent ring oldest first.</summary>
internal sealed class UpdateReading
{
    internal UpdateReading(long frame, FrameHistogramReading histogram, double? worstMs, long? worstFrame,
        long overBudget, long[] recentFrames, double[] recentMs)
    {
        Frame = frame;
        Histogram = histogram;
        WorstMs = worstMs;
        WorstFrame = worstFrame;
        OverBudget = overBudget;
        RecentFrames = recentFrames;
        RecentMs = recentMs;
    }

    internal long Frame { get; }

    internal FrameHistogramReading Histogram { get; }

    internal double? WorstMs { get; }

    internal long? WorstFrame { get; }

    internal long OverBudget { get; }

    internal long[] RecentFrames { get; }

    internal double[] RecentMs { get; }
}
