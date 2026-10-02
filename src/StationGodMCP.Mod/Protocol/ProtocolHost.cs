#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace StationGodMCP.Protocol;

/// <summary>The protocol layer's limits, read from the mod's config at load.</summary>
internal sealed class ProtocolSettings
{
    internal const int DefaultFirstLineTimeoutMilliseconds = 10000;
    internal const int DefaultLineTimeoutMilliseconds = 30000;
    internal const int DefaultMaxPipeConnections = 32;
    internal const int MinimumPipeConnections = 1;

    /// <summary>The most instances Windows allows a pipe (PIPE_UNLIMITED_INSTANCES is 255).</summary>
    internal const int MaximumPipeConnections = 254;

    internal ProtocolSettings(int maxPipeConnections, int firstLineTimeoutMilliseconds = DefaultFirstLineTimeoutMilliseconds,
        int lineTimeoutMilliseconds = DefaultLineTimeoutMilliseconds)
    {
        MaxPipeConnections = maxPipeConnections;
        FirstLineTimeoutMilliseconds = firstLineTimeoutMilliseconds;
        LineTimeoutMilliseconds = lineTimeoutMilliseconds;
    }

    internal int MaxPipeConnections { get; }

    /// <summary>A connection that has sent no complete line by then is closed.</summary>
    internal int FirstLineTimeoutMilliseconds { get; }

    /// <summary>A version-1 request not started by then is answered game_timeout.</summary>
    internal int LineTimeoutMilliseconds { get; }
}

/// <summary>
/// What every connection shares: the settings, the queue to the main thread, the deadline watch, the choice of
/// protocol from a connection's first line, and the list of open connections.
/// </summary>
internal sealed class ProtocolHost
{
    private readonly ICallQueue _calls;
    private readonly ConcurrentDictionary<Connection, byte> _open = new ConcurrentDictionary<Connection, byte>();

    internal ProtocolHost(ProtocolSettings settings, ICallQueue calls, DeadlineWatch deadlines)
    {
        Settings = settings;
        _calls = calls;
        Deadlines = deadlines;
    }

    internal ProtocolSettings Settings { get; }

    /// <summary>The mod's one deadline timer, shared with the TCP and synchronous pipe paths.</summary>
    internal DeadlineWatch Deadlines { get; }

    /// <summary>Raised (on the connection's reader thread) when a connection has ended and freed its transport.</summary>
    internal event Action<Connection>? ConnectionEnded;

    /// <summary>Open connections, newest last is not guaranteed.</summary>
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

    /// <summary>The protocol a connection speaks, from its first line.</summary>
    internal Session SessionFor(Connection connection, string firstLine) => new LineSession(connection, this);

    /// <summary>Closes every connection and waits up to the given time for them to end.</summary>
    internal void CloseAll(int waitMilliseconds)
    {
        List<WaitHandle> stopped = new List<WaitHandle>();
        foreach (Connection connection in _open.Keys)
        {
            connection.Close();
            stopped.Add(connection.Stopped);
        }

        DateTime until = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
        foreach (WaitHandle handle in stopped)
        {
            TimeSpan left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !handle.WaitOne(left))
            {
                break;
            }
        }
    }
}
