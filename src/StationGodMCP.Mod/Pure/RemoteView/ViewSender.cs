#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// Whose view a message carries: the human of the client it came from. The game's own link (Client.RegisteredHuman)
/// counts while that human is still the client's; it is set only when a human's OwnerClientId changes while its client
/// is connected (Thing.OwnerClientId), so a player who rejoins an existing character (a loaded save, a reconnect) has
/// none. Then it is the human in play (PlayerRule's test: connected, not dead or decaying) whose OwnerClientId is the
/// client's. A client without an id (0, no Steam id yet) has no human.
/// </summary>
internal static class ViewSender
{
    internal static T? HumanOf<T>(ulong clientId, ClientHuman<T>? registered, IEnumerable<ClientHuman<T>> humans)
        where T : class
    {
        if (clientId == 0UL)
        {
            return null;
        }

        if (registered is { } known && known.OwnerClientId == clientId)
        {
            return known.Body;
        }

        foreach (ClientHuman<T> human in humans)
        {
            if (human.OwnerClientId == clientId && human.InPlay)
            {
                return human.Body;
            }
        }

        return null;
    }
}

/// <summary>A human in the world as the view sender rule sees it.</summary>
internal readonly struct ClientHuman<T> where T : class
{
    internal ClientHuman(T body, ulong ownerClientId, bool inPlay)
    {
        Body = body;
        OwnerClientId = ownerClientId;
        InPlay = inPlay;
    }

    internal T Body { get; }

    /// <summary>The client the game says owns it (Thing.OwnerClientId).</summary>
    internal ulong OwnerClientId { get; }

    /// <summary>Its brain belongs to a connected client and it is not dead or decaying.</summary>
    internal bool InPlay { get; }
}
