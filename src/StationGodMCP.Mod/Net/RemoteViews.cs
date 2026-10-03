#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;

namespace StationGodMCP.Net;

/// <summary>
/// The server's side: the latest view each remote player's StationGod sent, keyed by that player's human (the
/// message's connection's Client.RegisteredHuman), with the connection it came on. A view of another protocol or one
/// that does not read is ignored, logged once per connection, and remembered as the reason that player has no view.
/// Views go when their player's client leaves or the human is no longer theirs (checked every second), and with the
/// world (WorldStores).
/// </summary>
internal static class RemoteViews
{
    private const float PruneEveryS = 1f;

    private static readonly ViewBook<long> Book = new ViewBook<long>();
    private static readonly Dictionary<long, long> ConnectionOf = new Dictionary<long, long>();
    private static readonly Dictionary<long, string> Unreadable = new Dictionary<long, string>();
    private static float _nextPrune;

    internal static float Now => Time.realtimeSinceStartup;

    internal static void Receive(long connectionId, byte[] payload)
    {
        switch (ViewWire.Decode(payload))
        {
            case WireRead<ViewReport>.Read read:
                Human? human = Client.Find(connectionId)?.RegisteredHuman;
                if (human == null)
                {
                    return;
                }

                if (Book.Offer(human.ReferenceId, read.Value, Now))
                {
                    ConnectionOf[human.ReferenceId] = connectionId;
                }

                Unreadable.Remove(connectionId);
                return;
            case WireRead<ViewReport>.OtherProtocol other:
                Ignore(connectionId, $"its StationGod speaks view protocol {other.Protocol} and this server's " +
                                     $"StationGod {StationGodMod.Version} speaks {ViewProtocol.Current}");
                return;
            case WireRead<ViewReport>.Malformed malformed:
                Ignore(connectionId, $"its view did not read ({malformed.Reason})");
                return;
        }
    }

    /// <summary>The view held for a player, and the connection it came on; null on none.</summary>
    internal static (ReceivedView View, long ConnectionId)? Find(Human human)
    {
        ReceivedView? view = Book.Find(human.ReferenceId);
        return view != null && ConnectionOf.TryGetValue(human.ReferenceId, out long connection)
            ? (view, connection)
            : null;
    }

    /// <summary>Why the views of a player's connection are ignored; null when none were.</summary>
    internal static string? IgnoredFor(Human human)
    {
        foreach (Client client in NetworkBase.Clients)
        {
            if (client.RegisteredHuman == human && Unreadable.TryGetValue(client.connectionId, out string? reason))
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>Every frame on the server: drops, once a second, the views of players who left.</summary>
    internal static void Tick()
    {
        if (Book.Count == 0 && Unreadable.Count == 0)
        {
            return;
        }

        float now = Now;
        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now + PruneEveryS;
        foreach (long human in new List<long>(Book.Keys))
        {
            Client? client = ConnectionOf.TryGetValue(human, out long connection) ? Client.Find(connection) : null;
            if (client == null || client.state == ClientState.Disconnected || client.RegisteredHuman == null ||
                client.RegisteredHuman.ReferenceId != human)
            {
                Book.Remove(human);
                ConnectionOf.Remove(human);
            }
        }

        foreach (long connection in new List<long>(Unreadable.Keys))
        {
            if (Client.Find(connection) == null)
            {
                Unreadable.Remove(connection);
            }
        }

        RemoteDrawings.Prune();
    }

    internal static void Clear()
    {
        Book.Clear();
        ConnectionOf.Clear();
        Unreadable.Clear();
        RemoteDrawings.Clear();
    }

    private static void Ignore(long connectionId, string reason)
    {
        string name = Client.Find(connectionId)?.name ?? $"connection {connectionId}";
        if (!Unreadable.ContainsKey(connectionId))
        {
            StationGodMod.LogWarning($"Ignoring {name}'s views: {reason}. Run the same StationGod version on both.");
        }

        Unreadable[connectionId] = reason;
    }
}
