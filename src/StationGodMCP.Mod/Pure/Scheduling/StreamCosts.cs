#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>
/// What each (owner, stream) has cost the main thread (protocol.md, Protocol methods, stream_costs): its calls, their
/// whole main-thread milliseconds from the start of the call to its serialised reply, and the frames in which one of its
/// calls finished with the frame's request time at or past its budget. That is budget_hit's rule, but read after the
/// reply is serialised, and each frame counts once per stream however many of its calls ran in it. Keyed by
/// CallContext, so a resume token's streams keep their counts across a reconnect. The lane scheduler records each call
/// it runs and passes on every stream end (stream_end, an owner's end); a world change leaves the counts, which are
/// about streams rather than the world. Main thread only. A stream's entry is allocated at its first call; every later
/// call is a dictionary lookup and three additions.
/// </summary>
internal sealed class StreamCosts : IStreamStore
{
    private readonly Dictionary<CallContext, Counters> _byStream = new Dictionary<CallContext, Counters>();
    private readonly List<CallContext> _ending = new List<CallContext>();

    /// <summary>Streams with counters.</summary>
    internal int Count => _byStream.Count;

    /// <summary>
    /// One call of the context ran for ms on the main thread, ending in frame; overBudget when the frame's request time
    /// had reached its budget by then. A call with no context (None) is not counted.
    /// </summary>
    internal void Record(CallContext context, double ms, bool overBudget, long frame)
    {
        if (context.IsNone)
        {
            return;
        }

        if (!_byStream.TryGetValue(context, out Counters? counters) || counters == null)
        {
            counters = new Counters();
            _byStream.Add(context, counters);
        }

        counters.Calls++;
        counters.MainThreadMs += ms;
        if (overBudget && counters.LastOverBudgetFrame != frame)
        {
            counters.OverBudgetFrames++;
            counters.LastOverBudgetFrame = frame;
        }
    }

    /// <summary>The stream's counts; all 0 for a stream with no call counted, or one that has ended.</summary>
    internal StreamCost Of(CallContext context) =>
        _byStream.TryGetValue(context, out Counters? counters) && counters != null
            ? new StreamCost(context.Stream, counters.Calls, counters.MainThreadMs, counters.OverBudgetFrames)
            : new StreamCost(context.Stream, 0, 0.0, 0);

    /// <summary>Every stream of the owner with counters, in stream order.</summary>
    internal void Of(string owner, List<StreamCost> into)
    {
        foreach (KeyValuePair<CallContext, Counters> entry in _byStream)
        {
            if (string.Equals(entry.Key.Owner, owner, StringComparison.Ordinal))
            {
                Counters counters = entry.Value;
                into.Add(new StreamCost(entry.Key.Stream, counters.Calls, counters.MainThreadMs, counters.OverBudgetFrames));
            }
        }

        into.Sort(static (left, right) => left.Stream.CompareTo(right.Stream));
    }

    /// <summary>The streams the end covers lose their counts; a world change keeps them.</summary>
    public void Ended(StreamEnd end)
    {
        if (end.IsWorld)
        {
            return;
        }

        foreach (CallContext context in _byStream.Keys)
        {
            if (end.Covers(context))
            {
                _ending.Add(context);
            }
        }

        foreach (CallContext context in _ending)
        {
            _byStream.Remove(context);
        }

        _ending.Clear();
    }

    private sealed class Counters
    {
        internal long Calls { get; set; }

        internal double MainThreadMs { get; set; }

        internal long OverBudgetFrames { get; set; }

        internal long LastOverBudgetFrame { get; set; } = -1;
    }
}

/// <summary>One stream's counts as stream_costs reports them.</summary>
internal readonly struct StreamCost
{
    internal StreamCost(long stream, long calls, double mainThreadMs, long overBudgetFrames)
    {
        Stream = stream;
        Calls = calls;
        MainThreadMs = mainThreadMs;
        OverBudgetFrames = overBudgetFrames;
    }

    internal long Stream { get; }

    internal long Calls { get; }

    internal double MainThreadMs { get; }

    internal long OverBudgetFrames { get; }
}
