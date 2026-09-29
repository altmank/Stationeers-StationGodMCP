#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Where a queued transfer stands: waiting (still queued or being applied right now), done with its outcome, or not
/// known (never issued, or older than the kept outcomes). A closed set.
/// </summary>
internal abstract class TransferState<TOutcome> where TOutcome : class
{
    private TransferState()
    {
    }

    internal sealed class Waiting : TransferState<TOutcome>
    {
        internal static readonly Waiting Instance = new Waiting();

        private Waiting()
        {
        }
    }

    internal sealed class Done : TransferState<TOutcome>
    {
        internal Done(TOutcome outcome)
        {
            Outcome = outcome;
        }

        internal TOutcome Outcome { get; }
    }

    internal sealed class Unknown : TransferState<TOutcome>
    {
        internal static readonly Unknown Instance = new Unknown();

        private Unknown()
        {
        }
    }
}

/// <summary>
/// A queue of transfers applied one at a time on another thread, and their recent outcomes. A transfer taken off the
/// queue stays waiting until its outcome is recorded, so a poll never finds a known id in neither place.
/// </summary>
internal sealed class TransferLedger<TMove, TOutcome> where TMove : class where TOutcome : class
{
    private readonly object _gate = new object();
    private readonly Queue<(long Id, TMove Move)> _pending = new Queue<(long Id, TMove Move)>();
    private readonly Dictionary<long, TMove> _applying = new Dictionary<long, TMove>();
    private readonly Dictionary<long, TOutcome> _outcomes = new Dictionary<long, TOutcome>();
    private readonly Queue<long> _outcomeOrder = new Queue<long>();
    private long _nextId;

    internal TransferLedger(int maximumPending, int keptOutcomes)
    {
        MaximumPending = maximumPending;
        KeptOutcomes = keptOutcomes;
    }

    internal int MaximumPending { get; }

    internal int KeptOutcomes { get; }

    /// <summary>Queues a new transfer under a fresh id; null when MaximumPending are already waiting.</summary>
    internal long? TryEnqueue(Func<long, TMove> create)
    {
        lock (_gate)
        {
            if (_pending.Count >= MaximumPending)
            {
                return null;
            }

            long id = ++_nextId;
            _pending.Enqueue((id, create(id)));
            return id;
        }
    }

    /// <summary>Takes the oldest waiting transfer to apply; it reads as waiting until Complete records it.</summary>
    internal bool TryBegin(out long id, out TMove? move)
    {
        lock (_gate)
        {
            if (_pending.Count > 0)
            {
                (id, move) = _pending.Dequeue();
                _applying.Add(id, move);
                return true;
            }
        }

        id = 0;
        move = null;
        return false;
    }

    internal void Complete(long id, TOutcome outcome)
    {
        lock (_gate)
        {
            _outcomes[id] = outcome;
            _outcomeOrder.Enqueue(id);
            _applying.Remove(id);
            while (_outcomeOrder.Count > KeptOutcomes)
            {
                _outcomes.Remove(_outcomeOrder.Dequeue());
            }
        }
    }

    /// <summary>Whether any transfer still waiting (queued, or being applied right now) matches.</summary>
    internal bool AnyWaiting(Func<TMove, bool> matches)
    {
        lock (_gate)
        {
            foreach ((long Id, TMove Move) pending in _pending)
            {
                if (matches(pending.Move))
                {
                    return true;
                }
            }

            foreach (TMove applying in _applying.Values)
            {
                if (matches(applying))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal TransferState<TOutcome> Find(long id)
    {
        lock (_gate)
        {
            if (_outcomes.TryGetValue(id, out TOutcome? outcome))
            {
                return new TransferState<TOutcome>.Done(outcome);
            }

            if (_applying.ContainsKey(id))
            {
                return TransferState<TOutcome>.Waiting.Instance;
            }

            foreach ((long Id, TMove Move) pending in _pending)
            {
                if (pending.Id == id)
                {
                    return TransferState<TOutcome>.Waiting.Instance;
                }
            }

            return TransferState<TOutcome>.Unknown.Instance;
        }
    }
}
