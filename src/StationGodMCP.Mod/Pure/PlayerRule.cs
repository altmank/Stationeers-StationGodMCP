#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Who "the player" is. The local player whenever the game has one (single player, or the host of a listen server).
/// Without one (a dedicated server), the one human in the world who is in play: its brain belongs to a connected
/// client (Brain.IsOnline, the game's own test in CleanupPlayersCommand "disconnected") and it is not dead or
/// decaying. With none in play, or several, there is no player, and the reason says which.
/// </summary>
internal static class PlayerRule
{
    internal static PlayerChoice<T> Choose<T>(T? local, IReadOnlyList<PlayerCandidate<T>> humans) where T : class
    {
        if (local != null)
        {
            return new PlayerChoice<T>.Found(local, isLocal: true);
        }

        List<PlayerCandidate<T>> inPlay = new List<PlayerCandidate<T>>(humans.Count);
        foreach (PlayerCandidate<T> human in humans)
        {
            if (human.InPlay)
            {
                inPlay.Add(human);
            }
        }

        return inPlay.Count switch
        {
            1 => new PlayerChoice<T>.Found(inPlay[0].Body, isLocal: false),
            0 => new PlayerChoice<T>.Missing(
                "There is no local player (a dedicated server) and no connected, living player in the world.",
                new List<string>()),
            _ => Several(inPlay)
        };
    }

    private static PlayerChoice<T> Several<T>(List<PlayerCandidate<T>> inPlay) where T : class
    {
        List<string> names = new List<string>(inPlay.Count);
        inPlay.ForEach(human => names.Add(human.Name));
        return new PlayerChoice<T>.Missing(
            $"There is no local player (a dedicated server) and {names.Count} connected, living players " +
            $"({string.Join(", ", names)}), so which one is the player is ambiguous.", names);
    }
}

/// <summary>A human in the world as the player rule sees it.</summary>
internal readonly struct PlayerCandidate<T> where T : class
{
    internal PlayerCandidate(T body, string name, bool connected, bool alive)
    {
        Body = body;
        Name = name;
        Connected = connected;
        Alive = alive;
    }

    internal T Body { get; }

    /// <summary>The name players see (Human.DisplayName), for the refusal that lists several.</summary>
    internal string Name { get; }

    /// <summary>Its brain belongs to a client connected now.</summary>
    internal bool Connected { get; }

    /// <summary>Not dead and not decaying (EntityState Dead, Decay).</summary>
    internal bool Alive { get; }

    internal bool InPlay => Connected && Alive;
}

/// <summary>The player, or why there is none.</summary>
internal abstract class PlayerChoice<T> where T : class
{
    private PlayerChoice()
    {
    }

    internal sealed class Found : PlayerChoice<T>
    {
        internal Found(T player, bool isLocal)
        {
            Player = player;
            IsLocal = isLocal;
        }

        internal T Player { get; }

        /// <summary>The local player; false for the one connected player of a dedicated server.</summary>
        internal bool IsLocal { get; }
    }

    internal sealed class Missing : PlayerChoice<T>
    {
        internal Missing(string reason, IReadOnlyList<string> inPlay)
        {
            Reason = reason;
            InPlay = inPlay;
        }

        /// <summary>A whole sentence: no local player, and none or how many in play (named).</summary>
        internal string Reason { get; }

        /// <summary>The names of the players in play when there are several; empty when there are none.</summary>
        internal IReadOnlyList<string> InPlay { get; }
    }
}
