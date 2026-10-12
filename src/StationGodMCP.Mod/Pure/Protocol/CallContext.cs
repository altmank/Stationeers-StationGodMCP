#nullable enable

using System;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// Who the main thread is working for: the connection (welcome.client_id, unique while the mod runs) and the stream on
/// it (protocol.md, Streams; 0 for direct calls). Stores that hold something per stream key by it, and StreamEnd says
/// which keys a stream_end, a closed connection or a world change ends. Handlers do not take it as an argument: the
/// lane scheduler enters a call's context around the call's run, and whatever a call starts that goes on over later
/// frames (a building job, a sample_logic run) keeps the context it was started in and enters it around each later
/// step. Outside those it is None.
/// <para>
/// A readonly struct, so making one per call allocates nothing and it compares and hashes by value, which is what a
/// store key needs. Current is thread-static: every setter and reader is on the main thread, where a thread-static
/// costs one thread-local lookup per access (three per call), and a reader or writer thread, or a test running beside
/// another, never sees a context that is not its own.
/// </para>
/// </summary>
internal readonly struct CallContext : IEquatable<CallContext>
{
    [ThreadStatic]
    private static CallContext _current;

    internal CallContext(string connectionId, long stream)
    {
        ConnectionId = string.IsNullOrEmpty(connectionId)
            ? throw new ArgumentException("A call context names its connection.", nameof(connectionId))
            : connectionId;
        Stream = stream >= 0
            ? stream
            : throw new ArgumentOutOfRangeException(nameof(stream), stream, "Streams are numbered from 0.");
    }

    /// <summary>No call: the main thread's own work.</summary>
    internal static CallContext None => default;

    /// <summary>The context of the call or step running on this thread; None outside one.</summary>
    internal static CallContext Current => _current;

    /// <summary>The connection's client_id; null for None.</summary>
    internal string? ConnectionId { get; }

    internal long Stream { get; }

    internal bool IsNone => ConnectionId == null;

    /// <summary>Makes context current until the returned scope is disposed, which restores the one before.</summary>
    internal static Scope Enter(CallContext context)
    {
        CallContext previous = _current;
        _current = context;
        return new Scope(previous);
    }

    public bool Equals(CallContext other) =>
        Stream == other.Stream && string.Equals(ConnectionId, other.ConnectionId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is CallContext other && Equals(other);

    public override int GetHashCode() =>
        unchecked(((ConnectionId == null ? 0 : StringComparer.Ordinal.GetHashCode(ConnectionId)) * 397) ^ Stream.GetHashCode());

    public override string ToString() => IsNone ? "none" : ConnectionId + "/" + Stream;

    /// <summary>Restores the context that was current before Enter; a struct, so a using block allocates nothing.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly CallContext _previous;

        internal Scope(CallContext previous) => _previous = previous;

        public void Dispose() => _current = _previous;
    }
}
