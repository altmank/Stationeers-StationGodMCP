#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A running total, mean and maximum of one measure (milliseconds or bytes). Immutable: each sample makes a new one.
/// </summary>
internal readonly struct Tally
{
    private Tally(long count, double total, double maximum)
    {
        Count = count;
        Total = total;
        Maximum = maximum;
    }

    internal long Count { get; }

    internal double Total { get; }

    internal double Maximum { get; }

    /// <summary>Null before the first sample.</summary>
    internal double? Mean => Count > 0 ? Total / Count : null;

    internal Tally With(double sample) =>
        new Tally(Count + 1, Total + sample, Count == 0 ? sample : Math.Max(Maximum, sample));
}

/// <summary>One method's counters as read at one moment: calls, errors and the time and size of its replies.</summary>
internal sealed class MethodTiming
{
    internal MethodTiming(string method, long calls, long errors, Tally handler, Tally serialize, Tally queueWait,
        Tally replyBytes)
    {
        Method = method;
        Calls = calls;
        Errors = errors;
        Handler = handler;
        Serialize = serialize;
        QueueWait = queueWait;
        ReplyBytes = replyBytes;
    }

    internal string Method { get; }

    internal long Calls { get; }

    internal long Errors { get; }

    /// <summary>Main thread: parsing, the argument check and the tool, up to the reply object.</summary>
    internal Tally Handler { get; }

    /// <summary>Main thread: the reply object to its JSON text.</summary>
    internal Tally Serialize { get; }

    /// <summary>From the listener queueing the request to the main thread starting it (frame wait included).</summary>
    internal Tally QueueWait { get; }

    /// <summary>The reply line's UTF-8 size, counted on the listener thread that writes it.</summary>
    internal Tally ReplyBytes { get; }

    /// <summary>What the method costs the main thread: handler and serialisation.</summary>
    internal double MainThreadMs => Handler.Total + Serialize.Total;

    internal static MethodTiming None(string method) =>
        new MethodTiming(method, 0, 0, default, default, default, default);
}

/// <summary>
/// Per-method counters since the mod loaded. The main thread records each request's handler, serialisation and queue
/// wait; the listener thread that writes the reply records its size afterwards, so every change is taken under one
/// lock. Only methods the caller names as known are counted (a client sending made-up names cannot grow the table).
/// </summary>
internal sealed class MethodTimings
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, MethodTiming> _byMethod = new Dictionary<string, MethodTiming>(StringComparer.Ordinal);

    internal void Record(string method, bool ok, double handlerMs, double serializeMs, double queueWaitMs)
    {
        lock (_gate)
        {
            MethodTiming now = Of(method);
            _byMethod[method] = new MethodTiming(method, now.Calls + 1, now.Errors + (ok ? 0 : 1),
                now.Handler.With(handlerMs), now.Serialize.With(serializeMs), now.QueueWait.With(queueWaitMs),
                now.ReplyBytes);
        }
    }

    internal void RecordReply(string method, long bytes)
    {
        lock (_gate)
        {
            MethodTiming now = Of(method);
            _byMethod[method] = new MethodTiming(method, now.Calls, now.Errors, now.Handler, now.Serialize,
                now.QueueWait, now.ReplyBytes.With(bytes));
        }
    }

    internal MethodTiming Snapshot(string method)
    {
        lock (_gate)
        {
            return Of(method);
        }
    }

    /// <summary>Every method called at least once, the most main-thread time first.</summary>
    internal List<MethodTiming> Called()
    {
        List<MethodTiming> called;
        lock (_gate)
        {
            called = new List<MethodTiming>(_byMethod.Values);
        }

        called.Sort(static (a, b) =>
        {
            int byCost = b.MainThreadMs.CompareTo(a.MainThreadMs);
            return byCost != 0 ? byCost : string.CompareOrdinal(a.Method, b.Method);
        });
        return called;
    }

    // Under _gate.
    private MethodTiming Of(string method) =>
        _byMethod.TryGetValue(method, out MethodTiming timing) ? timing : MethodTiming.None(method);
}
