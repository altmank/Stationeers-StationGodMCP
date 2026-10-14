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
/// about streams rather than the world. A call deferred over frames adds each later slice's milliseconds (from its
/// start to its serialised reply, if it answers in it) and the slice's frame if over budget, but is one call. Main
/// thread only. A stream's entry is allocated at its first call; every later call or slice is a dictionary lookup and
/// three additions.
/// <para>
/// A subscription's samples and update events are counted too, and apart from each other. Every due sample is counted
/// where it is paid, on the main thread and in the same lock-free table as the calls: the engine times its read and the
/// transport (SubscriptionHub) records one sample of the (owner, stream) with the read's ms, changed or not. The events
/// a subscription sent are counted in their own table under one small lock, on the connection's writer thread: one
/// update and the update line's serialisation only, the sample's own time being counted when it was taken.
/// </para>
/// </summary>
internal sealed class StreamCosts : IStreamStore
{
    private readonly Dictionary<CallContext, Counters> _byStream = new Dictionary<CallContext, Counters>();
    private readonly List<CallContext> _ending = new List<CallContext>();
    private readonly Dictionary<CallContext, UpdateCounters> _byUpdates = new Dictionary<CallContext, UpdateCounters>();
    private readonly List<CallContext> _endingUpdates = new List<CallContext>();
    private readonly List<CallContext> _ownerStreams = new List<CallContext>();
    private readonly object _updatesGate = new object();

    /// <summary>Streams with counters.</summary>
    internal int Count => _byStream.Count;

    /// <summary>
    /// One call of the context ran for ms on the main thread, ending in frame; overBudget when the frame's request time
    /// had reached its budget by then. A call with no context (None) is not counted.
    /// </summary>
    internal void Record(CallContext context, double ms, bool overBudget, long frame) =>
        Add(context, 1, ms, overBudget, frame);

    /// <summary>
    /// A later slice of a deferred call of the context ran for ms, ending in frame: its time and frame count as Record's,
    /// the call is not counted again.
    /// </summary>
    internal void RecordSlice(CallContext context, double ms, bool overBudget, long frame) =>
        Add(context, 0, ms, overBudget, frame);

    /// <summary>
    /// One due sample of a subscription of the context's stream took ms on the main thread, changed or not. Called on
    /// the main thread, so it is stored with the call counters, lock-free. A call with no context (None) is not counted.
    /// </summary>
    internal void RecordSample(CallContext context, double ms)
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

        counters.Samples++;
        counters.SampleMs += ms;
    }

    /// <summary>
    /// One subscription update was written to the context's client: ms is what the update line took to serialise (the
    /// reading's own sample was counted when it was taken). Called from a connection's writer thread, so it takes the
    /// lock that guards the update counts (a few updates a second per subscription). A call with no context (None) is
    /// not counted.
    /// </summary>
    internal void RecordUpdate(CallContext context, double ms)
    {
        if (context.IsNone)
        {
            return;
        }

        lock (_updatesGate)
        {
            if (!_byUpdates.TryGetValue(context, out UpdateCounters? counters) || counters == null)
            {
                counters = new UpdateCounters();
                _byUpdates.Add(context, counters);
            }

            counters.Updates++;
            counters.UpdateMs += ms;
        }
    }

    private void Add(CallContext context, long calls, double ms, bool overBudget, long frame)
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

        counters.Calls += calls;
        counters.MainThreadMs += ms;
        if (overBudget && counters.LastOverBudgetFrame != frame)
        {
            counters.OverBudgetFrames++;
            counters.LastOverBudgetFrame = frame;
        }
    }

    /// <summary>The stream's counts; all 0 for a stream with no call or sample counted, or one that has ended.</summary>
    internal StreamCost Of(CallContext context)
    {
        _byStream.TryGetValue(context, out Counters? counters);
        UpdateCounters? updates;
        lock (_updatesGate)
        {
            _byUpdates.TryGetValue(context, out updates);
        }

        return new StreamCost(context.Stream, counters?.Calls ?? 0, counters?.MainThreadMs ?? 0.0,
            counters?.OverBudgetFrames ?? 0, counters?.Samples ?? 0, counters?.SampleMs ?? 0.0, updates?.Updates ?? 0,
            updates?.UpdateMs ?? 0.0);
    }

    /// <summary>Every stream of the owner with counters, calls, samples or updates, in stream order.</summary>
    internal void Of(string owner, List<StreamCost> into)
    {
        _ownerStreams.Clear();
        foreach (CallContext context in _byStream.Keys)
        {
            if (string.Equals(context.Owner, owner, StringComparison.Ordinal))
            {
                _ownerStreams.Add(context);
            }
        }

        lock (_updatesGate)
        {
            foreach (CallContext context in _byUpdates.Keys)
            {
                if (string.Equals(context.Owner, owner, StringComparison.Ordinal) && !_ownerStreams.Contains(context))
                {
                    _ownerStreams.Add(context);
                }
            }
        }

        foreach (CallContext context in _ownerStreams)
        {
            into.Add(Of(context));
        }

        _ownerStreams.Clear();
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
        lock (_updatesGate)
        {
            foreach (CallContext context in _byUpdates.Keys)
            {
                if (end.Covers(context))
                {
                    _endingUpdates.Add(context);
                }
            }

            foreach (CallContext context in _endingUpdates)
            {
                _byUpdates.Remove(context);
            }

            _endingUpdates.Clear();
        }
    }

    private sealed class Counters
    {
        internal long Calls { get; set; }

        internal double MainThreadMs { get; set; }

        internal long OverBudgetFrames { get; set; }

        internal long LastOverBudgetFrame { get; set; } = -1;

        internal long Samples { get; set; }

        internal double SampleMs { get; set; }
    }

    private sealed class UpdateCounters
    {
        internal long Updates { get; set; }

        internal double UpdateMs { get; set; }
    }
}

/// <summary>One stream's counts as stream_costs reports them: calls, their main-thread ms, frames over budget, the
/// subscription samples taken with the main-thread ms they cost, and the update events sent with what their
/// serialisation cost.</summary>
internal readonly struct StreamCost
{
    internal StreamCost(long stream, long calls, double mainThreadMs, long overBudgetFrames, long samples,
        double sampleMs, long updates, double updateMs)
    {
        Stream = stream;
        Calls = calls;
        MainThreadMs = mainThreadMs;
        OverBudgetFrames = overBudgetFrames;
        Samples = samples;
        SampleMs = sampleMs;
        Updates = updates;
        UpdateMs = updateMs;
    }

    internal long Stream { get; }

    internal long Calls { get; }

    internal double MainThreadMs { get; }

    internal long OverBudgetFrames { get; }

    internal long Samples { get; }

    internal double SampleMs { get; }

    internal long Updates { get; }

    internal double UpdateMs { get; }
}
