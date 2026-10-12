namespace StationGodMCP.Client;

/// <summary>
/// Which calls may be in flight (hub.md, Streams): at most PerStream on each stream and PerConnection in all. A call
/// that finds room and nobody ahead of it on its stream goes in at once, allocating nothing. The others wait in their
/// stream's queue, and freed slots are handed out round robin by stream, stream 0 first in every round.
///
/// Invariant, whenever the lock is free: no waiting call could be admitted. So a call that finds a free connection slot
/// and an empty queue on its stream skips no one: every waiter is held by its own stream's limit.
/// </summary>
internal sealed class Admission
{
    private readonly object _gate = new();
    private readonly Lane _zero = new(0);
    private readonly Dictionary<long, Lane> _lanes = [];

    // Lanes of streams other than 0 that have waiters, in turn order. Stream 0 is never in it: it goes first each round.
    private readonly Queue<Lane> _turns = new();
    private int _roundLeft;
    private bool _zeroDue;
    private int _inFlight;
    private int _perStream;
    private int _perConnection;

    internal Admission(int perStream = 16, int perConnection = 256)
    {
        _perStream = perStream;
        _perConnection = perConnection;
    }

    /// <summary>Calls admitted and not yet left, on all streams.</summary>
    internal int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>The limits of a new welcome; waiters a higher limit makes room for go in now.</summary>
    internal void SetLimits(int perStream, int perConnection)
    {
        lock (_gate)
        {
            _perStream = perStream;
            _perConnection = perConnection;
            Pump();
        }
    }

    /// <summary>
    /// True once the call holds a slot on its stream; false when the timeout passed first. Throws when cancelled.
    /// Every true is paired with one Leave.
    /// </summary>
    internal ValueTask<bool> EnterAsync(long stream, TimeSpan timeout, CancellationToken cancellation)
    {
        Waiter waiter;
        lock (_gate)
        {
            Lane lane = LaneOf(stream);
            if (lane.Waiting.Count == 0 && lane.InFlight < _perStream && _inFlight < _perConnection)
            {
                lane.InFlight++;
                _inFlight++;
                return new ValueTask<bool>(true);
            }

            waiter = new Waiter();
            lane.Waiting.Enqueue(waiter);
            if (lane != _zero && !lane.InTurns)
            {
                lane.InTurns = true;
                _turns.Enqueue(lane);
            }
        }

        return new ValueTask<bool>(WaitAsync(stream, waiter, timeout, cancellation));
    }

    /// <summary>A call admitted on the stream has ended; its slot goes to the next in turn.</summary>
    internal void Leave(long stream)
    {
        lock (_gate)
        {
            Lane lane = LaneOf(stream);
            lane.InFlight--;
            _inFlight--;
            ForgetIfIdle(lane);
            Pump();
        }
    }

    private async Task<bool> WaitAsync(long stream, Waiter waiter, TimeSpan timeout, CancellationToken cancellation)
    {
        try
        {
            await waiter.Admitted.WaitAsync(timeout, cancellation).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return Abandon(waiter);
        }
        catch (OperationCanceledException)
        {
            if (Abandon(waiter))
            {
                Leave(stream);
            }

            throw;
        }
    }

    // True when the waiter was admitted just before it gave up; else it is marked so the pump skips it.
    private bool Abandon(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Admitted.IsCompleted)
            {
                return true;
            }

            waiter.Abandoned = true;
            Pump();
            return false;
        }
    }

    // Hands free slots out, one call per lane per step: stream 0 at the start of each round, then each lane that was in
    // turn when the round began. Stops when a whole round admits nothing or the connection is full.
    private void Pump()
    {
        int idle = 0;
        while (_inFlight < _perConnection && idle <= _turns.Count + 1)
        {
            if (_roundLeft == 0)
            {
                _roundLeft = _turns.Count;
                _zeroDue = true;
            }

            Lane lane;
            if (_zeroDue)
            {
                _zeroDue = false;
                lane = _zero;
            }
            else
            {
                lane = _turns.Dequeue();
                _roundLeft--;
            }

            bool admitted = TryAdmitOne(lane);
            if (lane != _zero)
            {
                ReturnToTurns(lane);
            }

            idle = admitted ? 0 : idle + 1;
        }
    }

    private bool TryAdmitOne(Lane lane)
    {
        while (lane.Waiting.Count > 0)
        {
            Waiter next = lane.Waiting.Peek();
            if (next.Abandoned)
            {
                lane.Waiting.Dequeue();
                continue;
            }

            if (lane.InFlight >= _perStream)
            {
                return false;
            }

            lane.Waiting.Dequeue();
            lane.InFlight++;
            _inFlight++;
            next.Admit();
            return true;
        }

        return false;
    }

    private void ReturnToTurns(Lane lane)
    {
        if (lane.Waiting.Count > 0)
        {
            _turns.Enqueue(lane);
            return;
        }

        lane.InTurns = false;
        ForgetIfIdle(lane);
    }

    private Lane LaneOf(long stream)
    {
        if (stream == 0)
        {
            return _zero;
        }

        if (!_lanes.TryGetValue(stream, out Lane? lane))
        {
            lane = new Lane(stream);
            _lanes[stream] = lane;
        }

        return lane;
    }

    // Streams are never reused, so a lane with nothing in flight or waiting is dropped rather than kept for ever.
    private void ForgetIfIdle(Lane lane)
    {
        if (lane != _zero && lane.InFlight == 0 && lane.Waiting.Count == 0 && !lane.InTurns)
        {
            _lanes.Remove(lane.Stream);
        }
    }

    private sealed class Lane(long stream)
    {
        internal long Stream { get; } = stream;

        internal int InFlight { get; set; }

        internal Queue<Waiter> Waiting { get; } = new();

        internal bool InTurns { get; set; }
    }

    private sealed class Waiter
    {
        private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Admitted => _admitted.Task;

        internal bool Abandoned { get; set; }

        internal void Admit() => _admitted.TrySetResult();
    }
}
