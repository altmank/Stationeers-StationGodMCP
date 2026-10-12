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
    private bool _closed;

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

    /// <summary>Fails every waiting call and every later one: the link has ended its session or is closed.</summary>
    internal void Close()
    {
        List<Waiter> failed = [];
        lock (_gate)
        {
            _closed = true;
            Drain(_zero, failed);
            foreach (Lane lane in _lanes.Values)
            {
                Drain(lane, failed);
            }
        }

        foreach (Waiter waiter in failed)
        {
            waiter.Fail();
        }
    }

    /// <summary>
    /// True once the call holds a slot on its stream; false when the timeout passed first or admission is closed.
    /// Throws when cancelled. Every true is paired with one Leave.
    /// </summary>
    internal ValueTask<bool> EnterAsync(long stream, TimeSpan timeout, CancellationToken cancellation)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (_closed)
            {
                return new ValueTask<bool>(false);
            }

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
            return await waiter.Admitted.WaitAsync(timeout, cancellation).ConfigureAwait(false);
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
                return waiter.Admitted.Result;
            }

            waiter.Abandoned = true;
            Pump();
            return false;
        }
    }

    // Hands free slots out in sweeps until a sweep admits nothing or the connection is full. A sweep is as many steps
    // as there are lanes in turn, plus one, counted when it starts. No lane joins while the lock is held, and a round
    // restarts with stream 0 only after every lane left in it had its step, so a sweep reaches every lane however many
    // it drops on the way (a lane whose waiters all gave up).
    private void Pump()
    {
        bool admitted = true;
        while (admitted && _inFlight < _perConnection)
        {
            admitted = false;
            int steps = _turns.Count + 1;
            for (int step = 0; step < steps && _inFlight < _perConnection; step++)
            {
                admitted |= Step();
            }
        }
    }

    // One lane's turn: stream 0 at the start of each round, then each lane that was in turn when the round began.
    private bool Step()
    {
        if (_roundLeft == 0)
        {
            _roundLeft = _turns.Count;
            _zeroDue = true;
        }

        if (_zeroDue)
        {
            _zeroDue = false;
            return TryAdmitOne(_zero);
        }

        Lane lane = _turns.Dequeue();
        _roundLeft--;
        bool admitted = TryAdmitOne(lane);
        ReturnToTurns(lane);
        return admitted;
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

    private static void Drain(Lane lane, List<Waiter> failed)
    {
        while (lane.Waiting.Count > 0)
        {
            failed.Add(lane.Waiting.Dequeue());
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
        private readonly TaskCompletionSource<bool> _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True when admitted, false when admission closed first.</summary>
        internal Task<bool> Admitted => _admitted.Task;

        internal bool Abandoned { get; set; }

        internal void Admit() => _admitted.TrySetResult(true);

        internal void Fail() => _admitted.TrySetResult(false);
    }
}
