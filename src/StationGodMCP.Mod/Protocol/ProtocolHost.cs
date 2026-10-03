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
    internal const int DefaultLineTimeoutMilliseconds = 30000;
    internal const int DefaultMaxPipeConnections = 32;
    internal const int MinimumPipeConnections = 1;
    internal const int DefaultMaxReplyBytes = 16777216;
    internal const int DefaultPingAfterMilliseconds = 30000;
    internal const int DefaultSlowClientMilliseconds = 35000;

    /// <summary>The most instances Windows allows a pipe (PIPE_UNLIMITED_INSTANCES is 255).</summary>
    internal const int MaximumPipeConnections = 254;

    internal ProtocolSettings(int maxPipeConnections, int firstLineTimeoutMilliseconds = DefaultFirstLineTimeoutMilliseconds,
        int lineTimeoutMilliseconds = DefaultLineTimeoutMilliseconds, bool protocol2 = true,
        int maxReplyBytes = DefaultMaxReplyBytes, int pingAfterMilliseconds = DefaultPingAfterMilliseconds,
        int slowClientMilliseconds = DefaultSlowClientMilliseconds, bool strictArguments = true)
    {
        StrictArguments = strictArguments;
        PingAfterMilliseconds = pingAfterMilliseconds;
        SlowClientMilliseconds = slowClientMilliseconds;
        MaxPipeConnections = maxPipeConnections;
        FirstLineTimeoutMilliseconds = firstLineTimeoutMilliseconds;
        LineTimeoutMilliseconds = lineTimeoutMilliseconds;
        Protocol2 = protocol2;
        MaxReplyBytes = maxReplyBytes;
    }

    internal int MaxPipeConnections { get; }

    /// <summary>A connection that has sent no complete line by then is closed.</summary>
    internal int FirstLineTimeoutMilliseconds { get; }

    /// <summary>A version-1 request not started by then is answered game_timeout.</summary>
    internal int LineTimeoutMilliseconds { get; }

    /// <summary>Whether a first line that is a hello starts version 2 ([Server] Protocol2); false serves version 1 only.</summary>
    internal bool Protocol2 { get; }

    /// <summary>The largest reply version 2 sends (limits.max_reply_bytes).</summary>
    internal int MaxReplyBytes { get; }

    /// <summary>A version-2 connection that has been sent nothing for this long gets a ping event.</summary>
    internal int PingAfterMilliseconds { get; }

    /// <summary>A version-2 client that takes no line for this long is closed (goodbye slow_client).</summary>
    internal int SlowClientMilliseconds { get; }

    /// <summary>
    /// Whether version-2 calls are checked against the catalogue in full ([Server] StrictArguments): every argument's
    /// name at any depth, type, range, enum, pattern and the required ones, and the shape. Version 1 is never.
    /// </summary>
    internal bool StrictArguments { get; }
}

/// <summary>
/// What every connection shares: the settings, the queue to the main thread, the deadline watch, the catalogue, the
/// choice of protocol from a connection's first line, and the list of open connections.
/// </summary>
internal sealed class ProtocolHost
{
    private readonly ICallQueue _calls;
    private readonly ConcurrentDictionary<Connection, byte> _open = new ConcurrentDictionary<Connection, byte>();

    internal ProtocolHost(ProtocolSettings settings, ICallQueue calls, DeadlineWatch deadlines, CatalogueFile? catalogue,
        AccessControl access, string? legacySecret = null)
    {
        Settings = settings;
        _calls = calls;
        Deadlines = deadlines;
        Catalogue = catalogue;
        Access = access;
        LegacySecret = string.IsNullOrEmpty(legacySecret) ? null : legacySecret;
        access.Register(this);
    }

    /// <summary>Keys, levels and the owner's approvals, shared by every listener.</summary>
    internal AccessControl Access { get; }

    /// <summary>The old TCP sign-in's shared secret ([Remote MCP] Secret); null when it is not set.</summary>
    internal string? LegacySecret { get; }

    internal ProtocolSettings Settings { get; }

    /// <summary>The mod's one deadline timer, shared with the TCP and synchronous pipe paths.</summary>
    internal DeadlineWatch Deadlines { get; }

    /// <summary>The embedded catalogue; null when it did not load (every call is then answered internal_error).</summary>
    internal CatalogueFile? Catalogue { get; }

    /// <summary>Raised (on the connection's reader thread) when a connection has ended and freed its transport.</summary>
    internal event Action<Connection>? ConnectionEnded;

    internal ICollection<Connection> Open => _open.Keys;

    internal void Opened(Connection connection) => _open.TryAdd(connection, 0);

    internal void Ended(Connection connection)
    {
        _open.TryRemove(connection, out _);
        ConnectionEnded?.Invoke(connection);
    }

    /// <summary>A call for the main thread: watched for its deadline, then queued.</summary>
    internal void Submit(QueuedCall call)
    {
        Deadlines.Watch(call);
        _calls.Submit(call);
    }

    /// <summary>The protocol a connection speaks, from its first line: a hello starts version 2, anything else version 1.</summary>
    internal Session SessionFor(Connection connection, string firstLine)
    {
        if (Settings.Protocol2 && FirstLine.StartsVersion2(firstLine))
        {
            return new CallSession(connection, this);
        }

        return connection.Transport == "pipe"
            ? new LineSession(connection, this, Access.Settings.LegacyPipe)
            : new LegacySignInSession(connection, this);
    }

    /// <summary>The listener stopped: this host's connections no longer count for the console and mod_info.</summary>
    internal void Retire() => Access.Unregister(this);

    /// <summary>A world finished loading: every connection is told.</summary>
    internal void WorldChanged(WorldFacts world)
    {
        foreach (Connection connection in _open.Keys)
        {
            connection.Session?.OnWorldChanged(world);
        }
    }

    /// <summary>
    /// Ends every connection: version 2 ones get their unstarted calls answered shutting_down and a goodbye with the
    /// reason; then waits up to the given time for them to end, and closes any still open.
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
