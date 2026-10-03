#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Protocol;

/// <summary>
/// The order in which the main thread takes queued calls. Add runs on connection threads, TryTake on the main thread,
/// one call at a time: a taken call runs to its answer before the next is taken.
/// </summary>
internal interface ICallScheduler
{
    void Add(QueuedCall call);

    /// <summary>The next call the main thread may run, or false when none may run now.</summary>
    bool TryTake(out QueuedCall? call);

    bool IsEmpty { get; }
}

/// <summary>
/// Takes calls from each connection in turn: one call per connection per round, so a connection with sixteen calls
/// waiting gets one turn per round like a connection with one. Within a connection, the earliest call that the order
/// rule lets start (CallOrder; the earliest call always may).
/// </summary>
internal sealed class RoundRobinScheduler : ICallScheduler
{
    private static readonly object NoSource = new object();

    private readonly object _sync = new object();
    private readonly List<SourceCalls> _ring = new List<SourceCalls>();
    private readonly Dictionary<object, SourceCalls> _bySource = new Dictionary<object, SourceCalls>();
    private int _next;
    private int _count;

    public bool IsEmpty
    {
        get
        {
            lock (_sync)
            {
                return _count == 0;
            }
        }
    }

    public void Add(QueuedCall call)
    {
        lock (_sync)
        {
            object source = call.Source ?? NoSource;
            if (!_bySource.TryGetValue(source, out SourceCalls calls))
            {
                calls = new SourceCalls(source);
                _bySource.Add(source, calls);
                _ring.Add(calls);
            }

            calls.Calls.Add(call);
            _count++;
        }
    }

    public bool TryTake(out QueuedCall? call)
    {
        lock (_sync)
        {
            int sources = _ring.Count;
            for (int step = 0; step < sources; step++)
            {
                int index = (_next + step) % sources;
                SourceCalls calls = _ring[index];
                int pick = calls.FirstThatMayStart();
                if (pick < 0)
                {
                    continue;
                }

                call = calls.Calls[pick];
                calls.Calls.RemoveAt(pick);
                _count--;
                if (calls.Calls.Count == 0)
                {
                    _ring.RemoveAt(index);
                    _bySource.Remove(calls.Source);
                    _next = _ring.Count == 0 ? 0 : index % _ring.Count;
                }
                else
                {
                    _next = (index + 1) % sources;
                }

                return true;
            }

            call = null;
            return false;
        }
    }

    /// <summary>One connection's waiting calls, in the order they arrived.</summary>
    private sealed class SourceCalls
    {
        internal SourceCalls(object source) => Source = source;

        internal object Source { get; }

        internal List<QueuedCall> Calls { get; } = new List<QueuedCall>();

        // Answered calls (timed out, cancelled) hold nothing back: the order rule counts only unanswered ones.
        internal int FirstThatMayStart()
        {
            int earlier = 0;
            int earlierWrites = 0;
            for (int index = 0; index < Calls.Count; index++)
            {
                QueuedCall call = Calls[index];
                if (call.State.IsAnswered)
                {
                    return index;
                }

                if (CallOrder.MayStart(call.IsWrite, earlier, earlierWrites))
                {
                    return index;
                }

                earlier++;
                earlierWrites += call.IsWrite ? 1 : 0;
            }

            return -1;
        }
    }
}
