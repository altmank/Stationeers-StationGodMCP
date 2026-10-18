using System;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// The sidecar's player (BRIEF-multiplayer): --as-player (else STATIONGODMCP_PLAYER) names the player this agent acts
/// for, and the hub adds it as as_player to every verb call that names none, on every face; a call's own wins.
/// </summary>
public sealed class AsPlayerTests
{
    [Fact]
    public void TheSidecarTakesItsPlayerFromTheCommandLineElseTheEnvironment()
    {
        Assert.Equal("xceled", Options(["--as-player", "xceled"], name => name == "STATIONGODMCP_PLAYER" ? "other" : null).AsPlayer);
        Assert.Equal("<MW> Flying Ferret",
            Options([], name => name == "STATIONGODMCP_PLAYER" ? " <MW> Flying Ferret " : null).AsPlayer);
        Assert.Null(Options([], _ => null).AsPlayer);
    }

    [Fact]
    public void EveryVerbCallThatNamesNoPlayerGetsTheSidecarsOne()
    {
        FakeGameLink game = new();
        VerbPipeline pipeline = new(game) { PollInterval = TimeSpan.Zero, AsPlayer = "xceled" };

        pipeline.Call("inspect", "looking_at").Done();
        pipeline.Call("find", "thing", """{"near": "player", "radius": 10}""", Face.Script).Done();

        foreach (GameCall call in game.Calls)
        {
            Assert.Equal("xceled", (string?)call.Params()["as_player"]);
        }

        Assert.Equal(2, game.Calls.Count);
    }

    [Fact]
    public void ACallsOwnPlayerWinsAndWithoutASidecarPlayerNoneIsAdded()
    {
        FakeGameLink named = new();
        new VerbPipeline(named) { PollInterval = TimeSpan.Zero, AsPlayer = "xceled" }
            .Call("inspect", "looking_at", """{"as_player": "Ferret"}""").Done();
        Assert.Equal("Ferret", (string?)Assert.Single(named.Calls).Params()["as_player"]);

        FakeGameLink plain = new();
        plain.Pipeline().Call("inspect", "looking_at").Done();
        Assert.Null(Assert.Single(plain.Calls).Params()["as_player"]);
    }

    [Fact]
    public void TheHubsOptionCheckTakesAsPlayerOnEveryVerb()
    {
        FakeGameLink game = new();
        game.Pipeline().Call("show", "highlight", """{"targets": [{"at": [0, 0, 0]}], "as_player": "xceled"}""").Done();

        Assert.Equal("xceled", (string?)Assert.Single(game.Calls).Params()["as_player"]);
    }

    private static SidecarOptions Options(string[] args, Func<string, string?> environment) =>
        Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse(args, environment)).Options;
}
