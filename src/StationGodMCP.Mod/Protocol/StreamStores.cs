#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Protocol;

/// <summary>
/// The one place a per-stream store hears that streams ended (protocol.md, Streams): a stream_end, a connection that
/// closed or was too slow (every stream of it), and a world change (every stream of every connection: the stores clear
/// entirely). The lane scheduler tells it at the start of the frame after the end, WorldStores on the frame after a
/// world was left. Main thread only. A store that throws is logged and the others still hear.
/// </summary>
internal sealed class StreamStores
{
    private readonly List<IStreamStore> _stores = new List<IStreamStore>();

    /// <summary>The mod's stores.</summary>
    internal static StreamStores Main { get; } = new StreamStores();

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

    /// <summary>A world was left: every store clears.</summary>
    internal void WorldChanged() => End(StreamEnd.World);
}
