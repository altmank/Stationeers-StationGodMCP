#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// Resume tokens (protocol.md, Resuming). A connection whose hello carries resume: "token" keeps its per-stream state
/// under the token's owner key (CallContext.Owner) instead of its client_id: the streams it ended, its walk cursors, its
/// subscriptions, its stream cost counters and its held restores. When the connection closes for any reason but bye,
/// that state is held for the window: a connection that presents the token within it picks the state up, and one that presents a token
/// another connection still holds takes it over (the old connection is told goodbye resumed_elsewhere and ended, since
/// over a VPN a broken connection can look open for up to the slow-client timeout). A window that passes, a bye, or a
/// world change ends the token's state as a stream_end ends each of its streams; a window that passes first ends the
/// token's session (ICallQueue.SessionEnded), so its held restores apply. Calls are never resumed: a closed
/// connection's unstarted calls are dropped unanswered and its sample_logic runs end unanswered.
/// <para>
/// Claim, Goodbye and Release run on connection threads; Tick and WorldChanged on the main thread. One lock covers the
/// tables, and every notice to the main thread (ICallQueue) is posted under it, so the main thread hears a token's
/// holds, resumes and ends in the order they happened. Owner keys are "r" and a number, never a client_id, and a token
/// whose state ended gets a new key when it is presented again, so a late notice for the old state never touches the
/// new. A token whose owner sent session_end is over: presented again, it starts afresh (resumed false), the old state
/// ends, and a connection still holding it is taken over. Tick costs one volatile read while nothing is held and allocates nothing.
/// </para>
/// </summary>
internal sealed class ResumeHolds
{
    internal const string Feature = "resume";
    internal const string ResumedElsewhere = "resumed_elsewhere";
    internal const int DefaultWindowSeconds = 60;
    internal const int MaximumWindowSeconds = 3600;

    private const string OwnerPrefix = "r";
    private static long _lastOwner;

    private readonly object _sync = new object();
    private readonly Dictionary<string, Hold> _byToken = new Dictionary<string, Hold>(StringComparer.Ordinal);
    private readonly Dictionary<Connection, Hold> _byConnection = new Dictionary<Connection, Hold>();
    private readonly List<Hold> _ending = new List<Hold>();
    private readonly ICallQueue _calls;
    private readonly IMonotonicClock _clock;
    private int _held;
    private double _nextExpiryMs = double.PositiveInfinity;

    /// <summary>windowMilliseconds 0 turns resuming off: tokens are then not taken and every connection is its own owner.</summary>
    internal ResumeHolds(int windowMilliseconds, ICallQueue calls, IMonotonicClock? clock = null)
    {
        WindowMilliseconds = windowMilliseconds >= 0
            ? windowMilliseconds
            : throw new ArgumentOutOfRangeException(nameof(windowMilliseconds), windowMilliseconds, "A window is 0 or more.");
        _calls = calls ?? throw new ArgumentNullException(nameof(calls));
        _clock = clock ?? StopwatchClock.Instance;
    }

    internal int WindowMilliseconds { get; }

    internal bool Enabled => WindowMilliseconds > 0;

    /// <summary>welcome.resume.window_s.</summary>
    internal double WindowSeconds => WindowMilliseconds / 1000.0;

    /// <summary>Tokens whose state is held with no connection.</summary>
    internal int Held => Volatile.Read(ref _held);

    /// <summary>
    /// hello with a resume token, on the connection's reader thread: the connection becomes the token's (its Owner is
    /// the token's owner key) and gets the token's ended streams. resumed is true when state was there to pick up, held
    /// or taken over from a live connection; false for a new token, and for one whose owner sent session_end, which
    /// starts afresh.
    /// </summary>
    internal EndedStreams Claim(string token, Connection connection, out bool resumed)
    {
        Connection? displaced = null;
        EndedStreams ended;
        lock (_sync)
        {
            _byToken.TryGetValue(token, out Hold? hold);
            if (hold != null)
            {
                if (hold.Live != null)
                {
                    displaced = hold.Live;
                    _byConnection.Remove(displaced);
                }
                else
                {
                    _held--;
                }

                if (Ended(hold))
                {
                    // Its owner sent session_end: that session is over, so the token starts afresh, and the old state
                    // ends as a stream_end ends each of its streams.
                    _byToken.Remove(token);
                    _calls.StreamEnded(this, StreamEnd.AllOf(hold.Owner));
                    hold = null;
                }
            }

            resumed = hold != null;
            if (hold == null)
            {
                hold = new Hold(token, OwnerPrefix + Interlocked.Increment(ref _lastOwner));
                _byToken.Add(token, hold);
            }

            hold.Live = connection;
            hold.SaidBye = false;
            _byConnection[connection] = hold;
            connection.ResumeAs(hold.Owner, token);
            if (resumed)
            {
                _calls.OwnerResumed(hold.Owner, connection);
            }

            ended = hold.Ended;
        }

        displaced?.Supersede(ResumedElsewhere);
        return ended;
    }

    /// <summary>bye on the connection: its token's state ends when it closes, rather than being held.</summary>
    internal void Goodbye(Connection connection)
    {
        lock (_sync)
        {
            if (_byConnection.TryGetValue(connection, out Hold? hold) && hold != null)
            {
                hold.SaidBye = true;
            }
        }
    }

    /// <summary>
    /// The connection ended. A connection holding a token leaves its state held for the window, or ended now after a
    /// bye; any other connection (no token, or one another connection took over) is left to ICallQueue.Closed.
    /// </summary>
    internal void Release(Connection connection)
    {
        lock (_sync)
        {
            if (!_byConnection.TryGetValue(connection, out Hold? hold) || hold == null)
            {
                return;
            }

            _byConnection.Remove(connection);
            hold.Live = null;
            if (hold.SaidBye)
            {
                _byToken.Remove(hold.Token);
                _calls.StreamEnded(connection, StreamEnd.AllOf(hold.Owner));
                return;
            }

            hold.ExpiresAtMs = _clock.NowMs + WindowMilliseconds;
            _nextExpiryMs = Math.Min(_nextExpiryMs, hold.ExpiresAtMs);
            _held++;
            _calls.OwnerHeld(hold.Owner);
        }
    }

    /// <summary>Main thread, once a frame: the state of every token whose window has passed ends.</summary>
    internal void Tick()
    {
        if (Volatile.Read(ref _held) == 0)
        {
            return;
        }

        double now = _clock.NowMs;
        lock (_sync)
        {
            if (now < _nextExpiryMs)
            {
                return;
            }

            double next = double.PositiveInfinity;
            foreach (Hold hold in _byToken.Values)
            {
                if (hold.Live != null)
                {
                    continue;
                }

                if (hold.ExpiresAtMs <= now)
                {
                    _ending.Add(hold);
                }
                else if (hold.ExpiresAtMs < next)
                {
                    next = hold.ExpiresAtMs;
                }
            }

            _nextExpiryMs = next;
            EndHeld(true);
        }
    }

    /// <summary>
    /// Main thread, a world was left: every held token's state ends at once, since its reference ids may name other
    /// things now. A live connection keeps its token; the world change clears its stores as it clears everyone's.
    /// </summary>
    internal void WorldChanged()
    {
        lock (_sync)
        {
            foreach (Hold hold in _byToken.Values)
            {
                if (hold.Live == null)
                {
                    _ending.Add(hold);
                }
            }

            _nextExpiryMs = double.PositiveInfinity;
            EndHeld(false);
        }
    }

    // Under the lock: the held tokens in _ending end as a stream_end ends each of their streams. A window that passed
    // is an abrupt end of the token's session, so its held restores apply first; a world change has dropped them.
    private void EndHeld(bool sessionEnded)
    {
        foreach (Hold hold in _ending)
        {
            _byToken.Remove(hold.Token);
            _held--;
            if (sessionEnded)
            {
                _calls.SessionEnded(hold.Owner);
            }

            _calls.StreamEnded(this, StreamEnd.AllOf(hold.Owner));
        }

        _ending.Clear();
    }

    // Whether the token's owner sent session_end.
    private static bool Ended(Hold hold)
    {
        lock (hold.Ended)
        {
            return hold.Ended.SessionEnded;
        }
    }

    /// <summary>One token's state: its owner key, the streams it ended, and its connection or the end of its window.</summary>
    private sealed class Hold
    {
        internal Hold(string token, string owner)
        {
            Token = token;
            Owner = owner;
        }

        internal string Token { get; }

        internal string Owner { get; }

        internal EndedStreams Ended { get; } = new EndedStreams();

        /// <summary>The connection holding the token; null while held.</summary>
        internal Connection? Live { get; set; }

        internal bool SaidBye { get; set; }

        /// <summary>When the window ends, on the clock; meaningful while held.</summary>
        internal double ExpiresAtMs { get; set; }
    }
}
