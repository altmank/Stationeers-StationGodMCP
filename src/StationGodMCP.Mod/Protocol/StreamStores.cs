#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Jobs;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Protocol;

/// <summary>
/// The one place a per-stream store hears that streams ended (protocol.md, Streams): a stream_end, an owner whose state
/// ended (every stream of it: a connection without a resume token that closed or was too slow, a resume token's window
/// that passed, a bye), and a world change (every stream of every owner: the stores clear entirely). The lane scheduler tells it at the start of the frame after the end, WorldStores on the frame after a
/// world was left; cursor_close reaches the cursor stores the same way, release the pin stores (job pins), and so
/// does an owner's abrupt session end, which the session-end stores (held restores, job pins) act on before its
/// streams end. Main thread only. A store that throws is logged
/// and the others still hear.
/// </summary>
internal sealed class StreamStores
{
    private readonly List<IStreamStore> _stores = new List<IStreamStore>();

    /// <summary>The mod's stores: the walk cursors and the kept jobs' pins.</summary>
    internal static StreamStores Main { get; } = WithModStores();

    internal void Register(IStreamStore store) => _stores.Add(store ?? throw new ArgumentNullException(nameof(store)));

    internal void End(StreamEnd end)
    {
        foreach (IStreamStore store in _stores)
        {
            try
            {
                store.Ended(end);
            }
            catch (Exception exception)
            {
                // A store's own bug: it may keep stale entries, but the scheduler's frame and the other stores go on.
                ProtocolLog.Warning($"A stream store failed to drop what it kept: {exception.Message}");
            }
        }
    }

    /// <summary>cursor_close from a connection of that owner: every cursor store frees the cursor if the owner holds it.</summary>
    internal void CloseCursor(string owner, string cursorId)
    {
        foreach (IStreamStore store in _stores)
        {
            if (!(store is ICursorStore cursors))
            {
                continue;
            }

            try
            {
                cursors.Close(owner, cursorId);
            }
            catch (Exception exception)
            {
                // A store's own bug: the cursor may stay until its bound or its stream's end frees it.
                ProtocolLog.Warning($"A cursor store failed to close a cursor: {exception.Message}");
            }
        }
    }

    /// <summary>release from a connection of that owner: every pin store drops the stream's job pins.</summary>
    internal void ReleasePins(string owner, long stream)
    {
        foreach (IStreamStore store in _stores)
        {
            if (!(store is IPinStore pins))
            {
                continue;
            }

            try
            {
                pins.Release(owner, stream);
            }
            catch (Exception exception)
            {
                // A store's own bug: the pins may stay until the owner's end or a world change drops them.
                ProtocolLog.Warning($"A pin store failed to release a stream's pins: {exception.Message}");
            }
        }
    }

    /// <summary>The owner's session ended abruptly: every session-end store hears it (held restores apply).</summary>
    internal void SessionEnded(string owner)
    {
        foreach (IStreamStore store in _stores)
        {
            if (!(store is ISessionEndStore ending))
            {
                continue;
            }

            try
            {
                ending.SessionEnded(owner);
            }
            catch (Exception exception)
            {
                // A store's own bug: what it held for the owner may not have been acted on; the frame goes on.
                ProtocolLog.Warning($"A store failed at an owner's session end: {exception.Message}");
            }
        }
    }

    private static StreamStores WithModStores()
    {
        StreamStores stores = new StreamStores();
        stores.Register(WalkCursors.Main);
        stores.Register(KeptJobs.Main);
        return stores;
    }

    /// <summary>A world was left: every store clears.</summary>
    internal void WorldChanged() => End(StreamEnd.World);
}
