#nullable enable

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>One find_update as the writer takes it: a change against what this subscriber was sent, or the whole result.</summary>
internal readonly struct FindUpdate
{
    internal FindUpdate(SubscriptionId subscription, long seq, FindResult result, FindChange? change)
    {
        Subscription = subscription;
        Seq = seq;
        Result = result;
        Change = change;
    }

    internal SubscriptionId Subscription { get; }

    /// <summary>1 for a subscription's first update, the last sent plus 1 after that: never a gap.</summary>
    internal long Seq { get; }

    /// <summary>The evaluation the update brings the subscriber to.</summary>
    internal FindResult Result { get; }

    /// <summary>What changed since the last result sent; null for a whole update (after a resume).</summary>
    internal FindChange? Change { get; }

    internal bool IsWhole => Change == null;
}

/// <summary>
/// A find subscription's place in its connection's outbound queue, as UpdateSlot is a devices subscription's: at most one
/// update waits. The main thread offers each new evaluation of the subscription's shared query; the writer thread, when
/// it reaches the slot, compares the newest offered with the last one it sent this subscriber (their text forms,
/// FindResult.Rendered) and writes only what changed, taking the next seq; an evaluation that changed nothing writes
/// nothing and takes no seq. So a slow client sees fewer, larger changes, never a gap, and never the same entry twice.
/// The subscribe reply's result is the first one sent. Requeued when the subscription resumes on another connection: the
/// old queue's entry writes nothing and the next update is whole.
/// </summary>
internal sealed class FindSlot
{
    private readonly object _gate = new object();
    private bool _waiting;
    private bool _closed;
    private bool _whole;
    private int _epoch;
    private long _seq;
    private FindResult? _offered;
    private FindResult _sent;

    internal FindSlot(ConnectionId connection, SubscriptionId subscription, long stream, FindResult first)
    {
        Connection = connection;
        Subscription = subscription;
        Stream = stream;
        _sent = first;
    }

    internal ConnectionId Connection { get; }

    internal SubscriptionId Subscription { get; }

    internal long Stream { get; }

    /// <summary>Main thread: which queue entry may take the slot's update; read when the entry is made.</summary>
    internal int Epoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    /// <summary>
    /// Main thread. True when no update of this subscription waits and the slot must be put in the outbound queue;
    /// false when the newer evaluation replaced the one already waiting, or the subscription ended.
    /// </summary>
    internal bool Offer(FindResult result)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            _offered = result;
            if (_waiting)
            {
                return false;
            }

            _waiting = true;
            return true;
        }
    }

    /// <summary>
    /// Writer thread: the update for the queue entry made at epoch. False when nothing is to be written: nothing waits,
    /// the slot was requeued since, the subscription ended, or the evaluation changed nothing the subscriber was sent.
    /// The comparison runs outside the lock, so the main thread's offers never wait on it.
    /// </summary>
    internal bool TryTake(int epoch, out FindUpdate update)
    {
        FindResult offered;
        FindResult sent;
        bool whole;
        lock (_gate)
        {
            if (!_waiting || _closed || _offered == null || epoch != _epoch)
            {
                update = default;
                return false;
            }

            _waiting = false;
            offered = _offered;
            sent = _sent;
            whole = _whole;
        }

        FindChange? change = whole ? null : FindChange.Between(sent.Rendered, offered.Rendered);
        lock (_gate)
        {
            if (_closed || epoch != _epoch || (!whole && change == null))
            {
                update = default;
                return false;
            }

            _sent = offered;
            _whole = false;
            _seq++;
            update = new FindUpdate(Subscription, _seq, offered, change);
            return true;
        }
    }

    /// <summary>
    /// Main thread: the queue the waiting update sat in is gone (the subscription resumes on another connection). The
    /// next offer is queued anew and sent whole, since what the client last received is not known.
    /// </summary>
    internal void Requeue()
    {
        lock (_gate)
        {
            _epoch++;
            _waiting = false;
            _whole = true;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _waiting = false;
            _offered = null;
        }
    }
}
