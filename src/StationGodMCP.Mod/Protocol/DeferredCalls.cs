#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// The calls that go on over frames with work of their own (CallOutcome.Continued: a walk's build), the scheduler's
/// slice lane: each frame, after the frame's calls, one of them in turn runs a slice in what is left of the budget,
/// under its call's context, and is answered when its work is ready. A call keeps its in-flight place until then.
/// Before each slice every call is checked: a cancel that came after it started answers it cancelled, its deadline
/// passing game_timeout, another world loading world_changed; its stream's end (stream_end, a whole connection's end)
/// answers it cancelled and its connection's close drops it; its owner's session_end answers it cancelled too, and a
/// call a shutdown answered (shutting_down, from the connection's thread) is dropped. A stopped call's work is dropped
/// and holds nothing.
/// Each slice is timed on the clock, from its first step to its serialised reply when it answers, and handed to
/// sliceRan with its call: a call's cost is its run and every slice.
/// Main thread only; a frame allocates nothing here beyond what the work does.
/// </summary>
internal sealed class DeferredCalls : ISliceLane
{
    private readonly List<Entry> _entries = new List<Entry>();
    private readonly IMonotonicClock _clock;
    private readonly Func<WorldFacts> _world;
    private readonly Action<QueuedCall, double>? _sliceRan;
    private int _next;

    internal DeferredCalls(IMonotonicClock clock, Func<WorldFacts> world, Action<QueuedCall, double>? sliceRan = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _sliceRan = sliceRan;
    }

    internal int Count => _entries.Count;

    public bool HasWork => _entries.Count > 0;

    /// <summary>A started call goes on with work: it is answered when the work is ready, or when it is stopped.</summary>
    internal void Add(QueuedCall call, IDeferredWork work, string? method)
    {
        call.MarkDeferred();
        _entries.Add(new Entry(call, work, method, _world().Id));
    }

    public void RunSlice(double untilMs)
    {
        Sweep();
        if (_entries.Count == 0)
        {
            return;
        }

        if (_next >= _entries.Count)
        {
            _next = 0;
        }

        Entry entry = _entries[_next];
        string? reply = null;
        double began = _clock.NowMs;
        try
        {
            using (CallContext.Enter(entry.Call.Context))
            {
                if (entry.Work.Step(untilMs))
                {
                    reply = entry.Work.Finish();
                }
            }
        }
        catch (Exception exception)
        {
            // Work that throws past its own reply building: the call is answered, and the frame and the others go on.
            ProtocolLog.Warning($"A deferred call's work failed: {exception}");
            _sliceRan?.Invoke(entry.Call, _clock.NowMs - began);
            Stop(_next, "internal_error", exception.Message);
            return;
        }

        _sliceRan?.Invoke(entry.Call, _clock.NowMs - began);
        if (reply == null)
        {
            _next++;
            return;
        }

        _entries.RemoveAt(_next);
        entry.Call.Finish(reply, entry.Method);
    }

    /// <summary>Streams ended: their calls are answered cancelled and their work dropped.</summary>
    internal void Ended(StreamEnd end)
    {
        for (int index = _entries.Count - 1; index >= 0; index--)
        {
            Entry entry = _entries[index];
            if (end.IsWorld || end.Covers(entry.Call.Context))
            {
                Stop(index, ProtocolCall.CancelledCode,
                    "The call's stream ended while its work went on over frames; nothing is kept.");
            }
        }
    }

    /// <summary>A connection closed: its calls' work is dropped; there is no one to answer.</summary>
    internal void Closed(object source)
    {
        for (int index = _entries.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_entries[index].Call.Source, source))
            {
                RemoveAt(index);
            }
        }
    }

    // Answers and drops every call that must stop before the next slice.
    private void Sweep()
    {
        double now = _clock.NowMs;
        WorldFacts? world = null;
        for (int index = _entries.Count - 1; index >= 0; index--)
        {
            Entry entry = _entries[index];
            if (entry.Call.State.IsAnswered)
            {
                RemoveAt(index);
            }
            else if (entry.Call.CancelRequested)
            {
                Stop(index, ProtocolCall.CancelledCode, "Cancelled while its work went on over frames; nothing is kept.");
            }
            else if (now >= entry.Call.DeadlineAtMs)
            {
                Stop(index, ProtocolCall.TimeoutCode,
                    "The call's deadline passed while its work went on over frames; nothing is kept.");
            }
            else if (!string.Equals((world ??= _world()).Id, entry.World, StringComparison.Ordinal))
            {
                Stop(index, ProtocolCall.WorldChangedCode,
                    "Another world loaded while the call's work went on over frames; nothing is kept.",
                    new Dictionary<string, WorldFacts> { ["world"] = world });
            }
        }
    }

    private void Stop(int index, string code, string message, object? data = null)
    {
        Entry entry = _entries[index];
        RemoveAt(index);
        entry.Call.Finish(entry.Call.RefusalReply(code, message, data), entry.Method);
    }

    private void RemoveAt(int index)
    {
        _entries.RemoveAt(index);
        if (index < _next)
        {
            _next--;
        }
    }

    private sealed class Entry
    {
        internal Entry(QueuedCall call, IDeferredWork work, string? method, string world)
        {
            Call = call;
            Work = work;
            Method = method;
            World = world;
        }

        internal QueuedCall Call { get; }

        internal IDeferredWork Work { get; }

        internal string? Method { get; }

        /// <summary>The world the call started in.</summary>
        internal string World { get; }
    }
}
