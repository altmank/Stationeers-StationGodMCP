#nullable enable

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>One update event as the writer takes it from the outbound queue.</summary>
internal readonly struct SubscriptionUpdate<TReading> where TReading : class
{
    internal SubscriptionUpdate(SubscriptionId subscription, long seq, TReading reading, long frame, double gameTimeS,
        double lateMs)
    {
        Subscription = subscription;
        Seq = seq;
        Reading = reading;
        Frame = frame;
        GameTimeS = gameTimeS;
        LateMs = lateMs;
    }

    internal SubscriptionId Subscription { get; }

    /// <summary>1 for a subscription's first event, the last sent plus 1 after that: never a gap.</summary>
    internal long Seq { get; }

    internal TReading Reading { get; }

    internal long Frame { get; }

    internal double GameTimeS { get; }

    internal double LateMs { get; }
}

/// <summary>
/// A subscription's place in its connection's outbound queue: at most one update waits per subscription. The main
/// thread offers each changed reading; when no update of this subscription is waiting, the offer takes the next seq
/// and the caller enqueues the slot; when one is waiting, the newer reading replaces its reading, frame, game time and
/// lateness and keeps its seq. The writer thread takes the update when it reaches the slot in the queue, which frees
/// the slot for the next seq. A slow client therefore sees fewer, fresher updates and never a seq gap. Closed when the
/// subscription ends: a waiting update is then dropped.
/// </summary>
internal sealed class UpdateSlot<TReading> where TReading : class
{
    private readonly object _gate = new object();
    private bool _waiting;
    private bool _closed;
    private long _seq;
    private TReading? _reading;
    private long _frame;
    private double _gameTimeS;
    private double _lateMs;

    internal UpdateSlot(ConnectionId connection, SubscriptionId subscription)
    {
        Connection = connection;
        Subscription = subscription;
    }

    internal ConnectionId Connection { get; }

    internal SubscriptionId Subscription { get; }

    /// <summary>The seq of the newest update offered (0 before the first).</summary>
    internal long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>
    /// Main thread. True when the update is new and the slot must be put in the outbound queue; false when it merged
    /// into the update already waiting there, or the subscription has ended.
    /// </summary>
    internal bool Offer(TReading reading, long frame, double gameTimeS, double lateMs)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            _reading = reading;
            _frame = frame;
            _gameTimeS = gameTimeS;
            _lateMs = lateMs;
            if (_waiting)
            {
                return false;
            }

            _waiting = true;
            _seq++;
            return true;
        }
    }

    /// <summary>Writer thread, when the slot reaches the front of the queue. False when nothing is to be written.</summary>
    internal bool TryTake(out SubscriptionUpdate<TReading> update)
    {
        lock (_gate)
        {
            if (!_waiting || _closed || _reading == null)
            {
                update = default;
                return false;
            }

            update = new SubscriptionUpdate<TReading>(Subscription, _seq, _reading, _frame, _gameTimeS, _lateMs);
            _waiting = false;
            return true;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _waiting = false;
            _reading = null;
        }
    }
}
