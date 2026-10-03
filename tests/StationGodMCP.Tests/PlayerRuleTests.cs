#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Who "the player" is: the local player whenever there is one, else on a dedicated server the one connected, living
/// human; none or several is no player, with the reason naming which.
/// </summary>
public sealed class PlayerRuleTests
{
    private sealed class Body
    {
        internal Body(string name)
        {
            Name = name;
        }

        internal string Name { get; }
    }

    private static PlayerCandidate<Body> Human(string name, bool connected = true, bool alive = true) =>
        new PlayerCandidate<Body>(new Body(name), name, connected, alive);

    private static Body FoundPlayer(PlayerChoice<Body> choice, bool isLocal)
    {
        PlayerChoice<Body>.Found found = Assert.IsType<PlayerChoice<Body>.Found>(choice);
        Assert.Equal(isLocal, found.IsLocal);
        return found.Player;
    }

    [Fact]
    public void TheLocalPlayerIsThePlayer()
    {
        Body local = new Body("Host");

        Assert.Same(local, FoundPlayer(PlayerRule.Choose(local, new List<PlayerCandidate<Body>>()), isLocal: true));
    }

    [Fact]
    public void TheLocalPlayerWinsOverOtherConnectedPlayers()
    {
        // A hosted game with guests: exactly as before, the host's own player.
        Body local = new Body("Host");
        List<PlayerCandidate<Body>> humans = new List<PlayerCandidate<Body>> { Human("Guest"), Human("Other") };

        Assert.Same(local, FoundPlayer(PlayerRule.Choose(local, humans), isLocal: true));
    }

    [Fact]
    public void WithoutALocalPlayerTheOneConnectedPlayerIsThePlayer()
    {
        PlayerCandidate<Body> lu = Human("LU");

        Body player = FoundPlayer(PlayerRule.Choose(null, new List<PlayerCandidate<Body>> { lu }), isLocal: false);

        Assert.Same(lu.Body, player);
    }

    [Fact]
    public void DisconnectedAndDeadBodiesDoNotCount()
    {
        // A sleeping body of a client who left, and a corpse, stay in the world; neither is in play.
        List<PlayerCandidate<Body>> humans = new List<PlayerCandidate<Body>>
        {
            Human("Left", connected: false), Human("Corpse", alive: false), Human("LU")
        };

        Assert.Equal("LU", FoundPlayer(PlayerRule.Choose(null, humans), isLocal: false).Name);
    }

    [Fact]
    public void NoneInPlayIsNoPlayerAndSaysSo()
    {
        List<PlayerCandidate<Body>> humans = new List<PlayerCandidate<Body>> { Human("Left", connected: false) };

        PlayerChoice<Body>.Missing missing = Assert.IsType<PlayerChoice<Body>.Missing>(PlayerRule.Choose(null, humans));

        Assert.Empty(missing.InPlay);
        Assert.Equal("There is no local player (a dedicated server) and no connected, living player in the world.",
            missing.Reason);
    }

    [Fact]
    public void AnEmptyWorldIsNoPlayer() =>
        Assert.IsType<PlayerChoice<Body>.Missing>(PlayerRule.Choose(null, new List<PlayerCandidate<Body>>()));

    [Fact]
    public void TwoInPlayIsNoPlayerAndNamesThem()
    {
        List<PlayerCandidate<Body>> humans = new List<PlayerCandidate<Body>>
        {
            Human("LU"), Human("Left", connected: false), Human("Friend")
        };

        PlayerChoice<Body>.Missing missing = Assert.IsType<PlayerChoice<Body>.Missing>(PlayerRule.Choose(null, humans));

        Assert.Equal(new[] { "LU", "Friend" }, missing.InPlay);
        Assert.Equal("There is no local player (a dedicated server) and 2 connected, living players (LU, Friend), so " +
                     "which one is the player is ambiguous.", missing.Reason);
    }
}
