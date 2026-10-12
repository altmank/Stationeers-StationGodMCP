#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// What ended (protocol.md, Streams): one stream of an owner (stream_end), every stream of an owner (its connection
/// closed or was too slow to take its replies, or a resume token's window passed), or every stream of every owner (a
/// world change). The owner is CallContext's: a connection's client_id, or a resume token's owner key. The default
/// value covers nothing.
/// </summary>
internal readonly struct StreamEnd
{
    /// <summary>The largest stream number a call may give: 2^53, the largest integer every JSON reader keeps exactly.</summary>
    internal const long MaximumStream = 9007199254740992L;

    private readonly Scope _scope;

    private StreamEnd(Scope scope, string? owner, long stream)
    {
        _scope = scope;
        Owner = owner;
        Stream = stream;
    }

    /// <summary>Every stream of every owner: a world change.</summary>
    internal static StreamEnd World => new StreamEnd(Scope.World, null, 0);

    /// <summary>The owner whose streams ended; null for a world change.</summary>
    internal string? Owner { get; }

    /// <summary>The stream that ended; meaningful only for one stream's end.</summary>
    internal long Stream { get; }

    internal bool IsWorld => _scope == Scope.World;

    internal bool IsWholeConnection => _scope == Scope.Connection;

    /// <summary>One stream of the owner.</summary>
    internal static StreamEnd Of(string owner, long stream) => new StreamEnd(Scope.Stream, Named(owner), stream);

    /// <summary>Every stream of the owner.</summary>
    internal static StreamEnd AllOf(string owner) => new StreamEnd(Scope.Connection, Named(owner), 0);

    /// <summary>Whether what a store keeps under this context ends with this end.</summary>
    internal bool Covers(CallContext context) => !context.IsNone && Covers(context.Owner!, context.Stream);

    internal bool Covers(string owner, long stream) => _scope switch
    {
        Scope.World => true,
        Scope.Connection => string.Equals(Owner, owner, StringComparison.Ordinal),
        Scope.Stream => Stream == stream && string.Equals(Owner, owner, StringComparison.Ordinal),
        _ => false
    };

    private static string Named(string owner) =>
        string.IsNullOrEmpty(owner)
            ? throw new ArgumentException("A stream's end names its owner.", nameof(owner))
            : owner;

    private enum Scope : byte
    {
        Nothing,
        Stream,
        Connection,
        World
    }
}

/// <summary>
/// A store that keeps something per stream (CallContext as key: walk cursors, and later build footprints and job
/// pins). Told on the main thread, at the start of a frame, when streams end; it drops what it keeps for every
/// context the end covers. Job undo data, job snapshots and building jobs are not per-stream stores: a stream's end
/// leaves them as they are.
/// </summary>
internal interface IStreamStore
{
    void Ended(StreamEnd end);
}

/// <summary>
/// A per-stream store of walk cursors, which also hears cursor_close (protocol.md, Walks): the owner of the connection
/// that sent it (CallContext.Owner) and the cursor id, told on the main thread at the start of the frame after it
/// arrived. A cursor the owner does not hold, or an unknown id, is left alone.
/// </summary>
internal interface ICursorStore : IStreamStore
{
    void Close(string owner, string cursorId);
}

/// <summary>
/// A per-stream store that also hears an owner's abrupt end (protocol.md, Held restores): session_end from a connection
/// of that owner, a resume token's window that passed without a resume, or a connection without a token that closed
/// without bye. Told on the main thread at the start of the frame after it, before the owner's streams end.
/// </summary>
internal interface ISessionEndStore : IStreamStore
{
    void SessionEnded(string owner);
}

/// <summary>
/// The streams one owner has ended, which are never used again by it (protocol.md, Streams): a connection's, or a
/// resume token's, handed to the connection that resumes it (protocol.md, Resuming). Kept as sorted,
/// disjoint ranges of ended numbers: a hub numbers its runs upward and ends each when it finishes, so the ended numbers
/// merge into a few ranges, one more for each stream still open below the highest ended one. At most MaximumRanges
/// ranges (16 bytes each): past that the two lowest merge, so the streams in the lowest gap count as ended too. Used
/// by connection reader threads under a lock on the instance, since a resumed token's set passes from one connection
/// to the next while the old one may still be reading.
/// </summary>
internal sealed class EndedStreams
{
    internal const int MaximumRanges = 1024;

    private readonly List<long> _low = new List<long>();
    private readonly List<long> _high = new List<long>();

    /// <summary>Ranges held (tests and the bound).</summary>
    internal int Ranges => _low.Count;

    internal bool Contains(long stream)
    {
        int index = Before(stream);
        return index >= 0 && stream <= _high[index];
    }

    /// <summary>Marks the stream ended; false when it already was, or is stream 0, which never ends.</summary>
    internal bool End(long stream)
    {
        if (stream <= 0 || Contains(stream))
        {
            return false;
        }

        int previous = Before(stream);
        int next = previous + 1;
        bool joinsPrevious = previous >= 0 && _high[previous] == stream - 1;
        bool joinsNext = next < _low.Count && _low[next] == stream + 1;
        if (joinsPrevious && joinsNext)
        {
            _high[previous] = _high[next];
            _low.RemoveAt(next);
            _high.RemoveAt(next);
        }
        else if (joinsPrevious)
        {
            _high[previous] = stream;
        }
        else if (joinsNext)
        {
            _low[next] = stream;
        }
        else
        {
            _low.Insert(next, stream);
            _high.Insert(next, stream);
        }

        if (_low.Count > MaximumRanges)
        {
            _high[0] = _high[1];
            _low.RemoveAt(1);
            _high.RemoveAt(1);
        }

        return true;
    }

    /// <summary>The index of the last range starting at or below stream, or -1.</summary>
    private int Before(long stream)
    {
        int found = _low.BinarySearch(stream);
        return found >= 0 ? found : ~found - 1;
    }
}
