#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Protocol;

/// <summary>The protocol layer's limits and switches, read from the mod's config at load.</summary>
internal sealed class ProtocolSettings
{
    internal const int DefaultFirstLineTimeoutMilliseconds = 10000;
    internal const int DefaultMaxPipeConnections = 32;
    internal const int MinimumPipeConnections = 1;
    internal const int DefaultMaxReplyBytes = 16777216;
    internal const int DefaultPingAfterMilliseconds = 30000;
    internal const int DefaultSlowClientMilliseconds = 35000;

    /// <summary>The most instances Windows allows a pipe (PIPE_UNLIMITED_INSTANCES is 255).</summary>
    internal const int MaximumPipeConnections = 254;

    internal ProtocolSettings(int maxPipeConnections, int firstLineTimeoutMilliseconds = DefaultFirstLineTimeoutMilliseconds,
        int maxReplyBytes = DefaultMaxReplyBytes, int pingAfterMilliseconds = DefaultPingAfterMilliseconds,
        int slowClientMilliseconds = DefaultSlowClientMilliseconds, bool strictArguments = true)
    {
        StrictArguments = strictArguments;
        PingAfterMilliseconds = pingAfterMilliseconds;
        SlowClientMilliseconds = slowClientMilliseconds;
        MaxPipeConnections = maxPipeConnections;
        FirstLineTimeoutMilliseconds = firstLineTimeoutMilliseconds;
        MaxReplyBytes = maxReplyBytes;
    }

    internal int MaxPipeConnections { get; }

    /// <summary>A connection that has sent no complete line by then is closed.</summary>
    internal int FirstLineTimeoutMilliseconds { get; }

    /// <summary>The largest reply sent (limits.max_reply_bytes).</summary>
    internal int MaxReplyBytes { get; }

    /// <summary>A connection that has been sent nothing for this long gets a ping event.</summary>
    internal int PingAfterMilliseconds { get; }

    /// <summary>A client that takes no line for this long is closed (goodbye slow_client).</summary>
    internal int SlowClientMilliseconds { get; }

    /// <summary>
    /// Whether calls are checked against the catalogue in full ([Server] StrictArguments): every argument's name at any
    /// depth, type, range, enum, pattern and the required ones, and the shape.
    /// </summary>
    internal bool StrictArguments { get; }
}

/// <summary>
/// What every connection shares: the settings, the queue to the main thread, the deadline watch, the catalogue, the
/// resume tokens, the session for a new connection, and the list of open connections.
/// </summary>
internal sealed class ProtocolHost
{
    private readonly ICallQueue _calls;
    private readonly ConcurrentDictionary<Connection, byte> _open = new ConcurrentDictionary<Connection, byte>();

    internal ProtocolHost(ProtocolSettings settings, ICallQueue calls, DeadlineWatch deadlines, CatalogueFile? catalogue,
        string? legacySecret = null, SubscriptionHub? subscriptions = null, ResumeHolds? holds = null,
        HeldRestores? restores = null)
    {
        Restores = restores;
        Subscriptions = subscriptions;
        Holds = holds ?? new ResumeHolds(0, calls);
        Settings = settings;
        _calls = calls;
        Deadlines = deadlines;
        Catalogue = catalogue;
        LegacySecret = string.IsNullOrEmpty(legacySecret) ? null : legacySecret;
    }

    /// <summary>
    /// The resume tokens ([Server] ResumeWindowSeconds; welcome lists the resume feature when on), shared by the pipe's
    /// and TCP's hosts so a token resumes over either. Off unless given.
    /// </summary>
    internal ResumeHolds Holds { get; }

    /// <summary>
    /// Held restores (protocol.md, Held restores), for welcome.restores on a resume token: what the mod applied,
    /// skipped and dropped for the token since its last welcome. Null when the host keeps none.
    /// </summary>
    internal HeldRestores? Restores { get; }

    /// <summary>Subscriptions and their events (welcome lists the subscriptions feature); null when off.</summary>
    internal SubscriptionHub? Subscriptions { get; }

    /// <summary>The TCP sign-in's shared secret ([Remote MCP] Secret); null when it is not set.</summary>
    internal string? LegacySecret { get; }

    internal ProtocolSettings Settings { get; }

    /// <summary>The mod's one deadline timer, shared by the pipe and TCP.</summary>
    internal DeadlineWatch Deadlines { get; }

    /// <summary>The embedded catalogue; null when it did not load (every call is then answered internal_error).</summary>
    internal CatalogueFile? Catalogue { get; }

    /// <summary>Raised (on the connection's reader thread) when a connection has ended and freed its transport.</summary>
    internal event Action<Connection>? ConnectionEnded;

    internal ICollection<Connection> Open => _open.Keys;

    internal void Opened(Connection connection) => _open.TryAdd(connection, 0);

    /// <summary>
    /// The connection ended. A connection without a resume token that did not say bye ended abruptly: its session ends
    /// (its held restores apply) before its streams do. A token's connection leaves that to ResumeHolds.
    /// </summary>
    internal void Ended(Connection connection)
    {
        _open.TryRemove(connection, out _);
        Holds.Release(connection);
        if (!connection.IsResumable && !connection.SaidBye)
        {
            _calls.SessionEnded(connection.Owner);
        }

        _calls.Closed(connection);
        ConnectionEnded?.Invoke(connection);
    }

    /// <summary>A call for the main thread: watched for its deadline, then queued.</summary>
    internal void Submit(QueuedCall call)
    {
        Deadlines.Watch(call);
        _calls.Submit(call);
    }

    /// <summary>A queued call answered without running (cancelled) stops holding its place.</summary>
    internal void Withdraw(QueuedCall call) => _calls.Withdraw(call);

    /// <summary>Streams of an owner ended (stream_end, a slow client): the main thread ends what they had going.</summary>
    internal void StreamEnded(Connection connection, StreamEnd end) => _calls.StreamEnded(connection, end);

    /// <summary>The owner's session ended abruptly (session_end, a slow client): its held restores apply at the next frame.</summary>
    internal void SessionEnded(string owner) => _calls.SessionEnded(owner);

    /// <summary>cursor_close from a connection of that owner: the main thread frees the cursor if the owner holds it.</summary>
    internal void CursorClosed(string owner, string cursorId) => _calls.CursorClosed(owner, cursorId);

    /// <summary>A new connection's session: over TCP the shared secret comes first; then the protocol, hello first.</summary>
    internal Session SessionFor(Connection connection) =>
        connection.Transport == "pipe" ? AfterSignIn(connection) : new SecretGateSession(connection, this);

    /// <summary>The protocol after any sign-in.</summary>
    internal Session AfterSignIn(Connection connection) => new CallSession(connection, this);

    /// <summary>The listener stopped.</summary>
    internal void Retire()
    {
    }

    /// <summary>A world finished loading: every connection is told.</summary>
    internal void WorldChanged(WorldFacts world)
    {
        foreach (Connection connection in _open.Keys)
        {
            connection.Session?.OnWorldChanged(world);
        }
    }

    /// <summary>
    /// Ends every connection: each gets its unstarted calls answered shutting_down and a goodbye with the reason; then
    /// waits up to the given time for them to end, and closes any still open.
    /// </summary>
    internal void CloseAll(string reason, int waitMilliseconds)
    {
        List<Connection> open = new List<Connection>(_open.Keys);
        foreach (Connection connection in open)
        {
            Session? session = connection.Session;
            if (session != null)
            {
                session.ShutDown(reason);
            }
            else
            {
                connection.Close();
            }
        }

        DateTime until = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
        foreach (Connection connection in open)
        {
            TimeSpan left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !connection.Stopped.WaitOne(left))
            {
                connection.Close();
            }
        }
    }
}
