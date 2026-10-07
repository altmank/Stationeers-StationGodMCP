#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Profiling;

/// <summary>p50, p95 and the maximum of a set of samples (nearest rank); all null when there are none.</summary>
internal readonly struct Spread
{
    private Spread(double p50, double p95, double max)
    {
        P50 = p50;
        P95 = p95;
        Max = max;
    }

    internal double? P50 { get; }

    internal double? P95 { get; }

    internal double? Max { get; }

    /// <summary>The first count values; sorts them in place.</summary>
    internal static Spread Of(double[] values, int count)
    {
        if (count == 0)
        {
            return default;
        }

        Array.Sort(values, 0, count);
        return new Spread(Rank(values, count, 0.50), Rank(values, count, 0.95), values[count - 1]);
    }

    // Nearest rank: the smallest value with at least fraction of the samples at or below it.
    private static double Rank(double[] sorted, int count, double fraction) =>
        sorted[Math.Max(0, (int)Math.Ceiling(fraction * count) - 1)];
}

/// <summary>A name and its milliseconds: a scope or a call in one frame.</summary>
internal sealed class NamedMs
{
    internal NamedMs(string name, double ms)
    {
        Name = name;
        Ms = ms;
    }

    internal string Name { get; }

    internal double Ms { get; }
}

/// <summary>
/// One scope or timed method since profiling went on (or was reset): total and self milliseconds (self leaves out
/// the scopes inside it; a timed method's self is its total), how often it ran and its mean, and over the frame
/// window the 95th percentile and maximum of its milliseconds per frame it ran in.
/// </summary>
internal sealed class SlotStat
{
    internal SlotStat(string name, string kind, double totalMs, double selfMs, long count, Spread perFrame)
    {
        Name = name;
        Kind = kind;
        TotalMs = totalMs;
        SelfMs = selfMs;
        Count = count;
        PerFrame = perFrame;
    }

    internal string Name { get; }

    /// <summary>scope (a manual scope) or method (a [Profiled] method).</summary>
    internal string Kind { get; }

    internal double TotalMs { get; }

    internal double SelfMs { get; }

    internal long Count { get; }

    internal double MeanMs => Count > 0 ? TotalMs / Count : 0.0;

    internal Spread PerFrame { get; }
}

/// <summary>The costliest frame since on or reset: its time, heap growth, each scope's self time and its calls.</summary>
internal sealed class WorstFrame
{
    internal WorstFrame(long frame, double ms, long? heapDeltaBytes, List<NamedMs> scopes, List<NamedMs> calls,
        int callsNotListed)
    {
        Frame = frame;
        Ms = ms;
        HeapDeltaBytes = heapDeltaBytes;
        Scopes = scopes;
        Calls = calls;
        CallsNotListed = callsNotListed;
    }

    internal long Frame { get; }

    internal double Ms { get; }

    internal long? HeapDeltaBytes { get; }

    /// <summary>Self milliseconds per scope, the most first, unscoped included.</summary>
    internal List<NamedMs> Scopes { get; }

    /// <summary>The calls it ran, the slowest first (the first ProfileRecorder.CallsPerFrame).</summary>
    internal List<NamedMs> Calls { get; }

    internal int CallsNotListed { get; }
}

/// <summary>One stretch of a job holding the game tick: from asking for the hold to letting it go.</summary>
internal sealed class TickHold
{
    internal TickHold(string job, string? tool, double ms, long frames, bool holding)
    {
        Job = job;
        Tool = tool;
        Ms = ms;
        Frames = frames;
        Holding = holding;
    }

    internal string Job { get; }

    internal string? Tool { get; }

    internal double Ms { get; }

    internal long Frames { get; }

    /// <summary>Still held when read: Ms and Frames so far.</summary>
    internal bool Holding { get; }
}

/// <summary>Tick holds since on or reset: count, total and longest, and the most recent ones, newest first.</summary>
internal sealed class TickHoldSummary
{
    internal TickHoldSummary(long count, double totalMs, double maxMs, List<TickHold> recent)
    {
        Count = count;
        TotalMs = totalMs;
        MaxMs = maxMs;
        Recent = recent;
    }

    internal long Count { get; }

    internal double TotalMs { get; }

    internal double MaxMs { get; }

    internal List<TickHold> Recent { get; }
}

/// <summary>The CSV's state: its current file, whether rows still go to it, rows written and dropped, and its failure.</summary>
internal sealed class CsvState
{
    internal CsvState(string path, bool writing, long written, long dropped, string? failure)
    {
        Path = path;
        Writing = writing;
        Written = written;
        Dropped = dropped;
        Failure = failure;
    }

    internal string Path { get; }

    internal bool Writing { get; }

    internal long Written { get; }

    internal long Dropped { get; }

    internal string? Failure { get; }
}

/// <summary>A profiling session's report, computed on demand on the main thread.</summary>
internal sealed class ProfileSnapshot
{
    internal ProfileSnapshot(bool enabled, double sinceS, double slowFrameMs, long framesRecorded, int windowFrames,
        Spread frameMs, Spread heapDeltaBytes, int heapDeltasDropped, List<SlotStat> slots, WorstFrame? worst,
        List<MethodTiming> calls, TickHoldSummary tickHolds, long slowFrames, CsvState? csv)
    {
        Enabled = enabled;
        SinceS = sinceS;
        SlowFrameMs = slowFrameMs;
        FramesRecorded = framesRecorded;
        WindowFrames = windowFrames;
        FrameMs = frameMs;
        HeapDeltaBytes = heapDeltaBytes;
        HeapDeltasDropped = heapDeltasDropped;
        Slots = slots;
        Worst = worst;
        Calls = calls;
        TickHolds = tickHolds;
        SlowFrames = slowFrames;
        Csv = csv;
    }

    internal bool Enabled { get; }

    /// <summary>Seconds since profiling went on or was reset.</summary>
    internal double SinceS { get; }

    internal double SlowFrameMs { get; }

    internal long FramesRecorded { get; }

    /// <summary>The frames the percentiles cover: the last ProfileRecorder.WindowFrames at most.</summary>
    internal int WindowFrames { get; }

    internal Spread FrameMs { get; }

    /// <summary>Heap growth per frame; a frame whose heap shrank (a collection ran) is left out.</summary>
    internal Spread HeapDeltaBytes { get; }

    internal int HeapDeltasDropped { get; }

    /// <summary>Every scope and timed method that ran, the most self time first.</summary>
    internal List<SlotStat> Slots { get; }

    internal WorstFrame? Worst { get; }

    /// <summary>Per call method since on or reset: queue wait, execute, serialise and reply bytes.</summary>
    internal List<MethodTiming> Calls { get; }

    internal TickHoldSummary TickHolds { get; }

    internal long SlowFrames { get; }

    internal CsvState? Csv { get; }
}
