#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Multiplayer (BRIEF-multiplayer): every verb takes as_player, the player the call speaks for, carried in the call's
/// context so its later steps keep it; with several players in play and none named, show draws for each of them, a
/// player whose screen cannot be drawn on being a note (LU: "it can't just draw for both?").
/// </summary>
public sealed class MultiplayerTests
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

    [Fact]
    public void SeveralPlayersInPlayIsAmbiguousAndSaysToNameOneWithAsPlayer()
    {
        PlayerChoice<Body>.Missing missing = Assert.IsType<PlayerChoice<Body>.Missing>(
            PlayerRule.Choose(null, new List<PlayerCandidate<Body>> { Human("xceled"), Human("<MW> Flying Ferret") }));

        Assert.Equal("There is no local player (a dedicated server) and 2 connected, living players (xceled, <MW> " +
                     "Flying Ferret), so which one is the player is ambiguous: name one with as_player (as_player: " +
                     "\"xceled\").", missing.Reason);
    }

    [Fact]
    public void ShowWithNoPlayerNamedDrawsForEveryPlayerInPlay()
    {
        List<PlayerCandidate<Body>> humans = new List<PlayerCandidate<Body>>
        {
            Human("xceled"), Human("Left", connected: false), Human("Ferret"), Human("Dead", alive: false)
        };

        List<Body> audience = PlayerRule.Audience<Body>(null, "ambiguous", named: false, humans, out string? absence);

        Assert.Equal(new[] { "xceled", "Ferret" }, audience.ConvertAll(body => body.Name));
        Assert.Null(absence);
    }

    [Fact]
    public void ShowForTheOnePlayerOrTheNamedOneDrawsForThemAlone()
    {
        Body named = new Body("Ferret");
        List<PlayerCandidate<Body>> humans = new List<PlayerCandidate<Body>> { Human("xceled"), Human("Ferret") };

        Assert.Same(named, Assert.Single(PlayerRule.Audience(named, null, named: true, humans, out string? absence)));
        Assert.Null(absence);
    }

    [Fact]
    public void ShowForANameThatMatchesNobodyDrawsForNoOneAndSaysWhy()
    {
        List<PlayerCandidate<Body>> humans = new List<PlayerCandidate<Body>> { Human("xceled"), Human("Ferret") };

        Assert.Empty(PlayerRule.Audience<Body>(null, "as_player: no such player", named: true, humans,
            out string? absence));
        Assert.Equal("as_player: no such player", absence);
    }

    [Fact]
    public void ShowWithNoPlayerInPlaySaysWhy()
    {
        Assert.Empty(PlayerRule.Audience<Body>(null, "no player", named: false,
            new List<PlayerCandidate<Body>> { Human("Left", connected: false) }, out string? absence));
        Assert.Equal("no player", absence);
    }

    [Fact]
    public void AsPlayerFindsAPlayerAsMovePlayerDoes()
    {
        List<NamedPlayer<Body>> players = new List<NamedPlayer<Body>>
        {
            new NamedPlayer<Body>(new Body("xceled"), 101, "xceled"),
            new NamedPlayer<Body>(new Body("<MW> Flying Ferret"), 202, "<MW> Flying Ferret")
        };

        Assert.Equal("xceled", Assert.IsType<PlayerFound<Body>.One>(PlayerMatch.Find(players, "XCELED")).Player.Name);
        Assert.Equal(202, Assert.IsType<PlayerFound<Body>.One>(PlayerMatch.Find(players, "ferret")).Player.ReferenceId);
        Assert.Equal(202, Assert.IsType<PlayerFound<Body>.One>(PlayerMatch.Find(players, "202")).Player.ReferenceId);
        Assert.IsType<PlayerFound<Body>.None>(PlayerMatch.Find(players, "nobody"));
    }

    [Fact]
    public void TheCallsPlayerRidesInItsContextButIsNoPartOfAStoreKey()
    {
        CallContext named = new CallContext("c1", 4, "xceled");

        Assert.Equal("xceled", named.Player);
        Assert.Equal(new CallContext("c1", 4), named);
        Assert.Equal(new CallContext("c1", 4).GetHashCode(), named.GetHashCode());
        Assert.Null(new CallContext("c1", 4, "").Player);
        using (CallContext.Enter(named))
        {
            Assert.Equal("xceled", CallContext.Current.Player);
        }

        Assert.Null(CallContext.Current.Player);
    }

    [Fact]
    public void EveryVerbTakesAsPlayerAsText()
    {
        Assert.Null(Check("inspect", """{"what": "looking_at", "as_player": "xceled"}"""));
        Assert.Null(Check("show", """{"what": "highlight", "targets": [{"at": [0, 0, 0]}], "as_player": "202"}"""));
        Assert.Null(Check("find", """{"what": "thing", "near": "player", "radius": 10, "as_player": "xceled"}"""));
        Assert.Null(Check("build", """{"what": "structure", "prefab": "StructureFrame", "at": [0, 0, 0], "materials": ["player"], "as_player": "xceled"}"""));
        Assert.Null(Check("write", """{"what": "screen", "id": "5", "element": "b", "as_player": "Ann"}"""));
        Assert.Equal("invalid_argument", Check("inspect", """{"what": "looking_at", "as_player": 7}""")!.Code);
    }

    [Fact]
    public void FindThingFiltersByColourAndPaint()
    {
        Assert.Null(Check("find", """{"what": "thing", "color": "Orange", "painted": true}"""));
        Assert.Equal("invalid_argument", Check("find", """{"what": "thing", "painted": "yes"}""")!.Code);
    }

    [Fact]
    public void SeveralScreensDrawAndOneThatCannotBeReachedIsANote()
    {
        List<string> notes = new List<string>();
        List<string> drawnOn = new List<string>();
        List<ScreenRound.Target<string>> screens = new List<ScreenRound.Target<string>>
        {
            new ScreenRound.Target<string>("a", "xceled", "xceled"),
            new ScreenRound.Target<string>("b", "Ferret", "Ferret"),
            new ScreenRound.Target<string>("c", "Host", null)
        };

        int cleared = ScreenRound.Run(screens, screen => screen == "b"
            ? throw ApiErrors.Refused("no_view", "Ferret's game could not be reached to draw on (they left).")
            : 2, notes, drawnOn);

        Assert.Equal(4, cleared);
        Assert.Equal(new[] { "xceled" }, drawnOn);
        Assert.Equal("Not drawn for Ferret: Ferret's game could not be reached to draw on (they left).",
            Assert.Single(notes));
    }

    [Fact]
    public void ScreensThatAllRefuseRefuseTheCallAndOneScreenRefusesAsAlone()
    {
        List<ScreenRound.Target<string>> two = new List<ScreenRound.Target<string>>
        {
            new ScreenRound.Target<string>("a", "xceled", "xceled"),
            new ScreenRound.Target<string>("b", "Ferret", "Ferret")
        };
        Func<string, int> refuse = screen => throw ApiErrors.Refused("no_view", screen + " unreachable");

        ApiException all = Assert.Throws<ApiException>(() =>
            ScreenRound.Run(two, refuse, new List<string>(), new List<string>()));
        Assert.Equal("b unreachable", all.Message);

        List<string> notes = new List<string>();
        Assert.Throws<ApiException>(() => ScreenRound.Run(two.GetRange(0, 1), refuse, notes, new List<string>()));
        Assert.Empty(notes);
    }

    [Fact]
    public async Task AVerbCallsAsPlayerReachesTheMainThreadInItsContext()
    {
        string? seen = "unset";
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false));
        rig.Mod.CallHook = (call, _) =>
        {
            if (call.Request.Method != "inspect")
            {
                return null;
            }

            seen = CallContext.Current.Player;
            return new CallOutcome(new JObject
            {
                ["type"] = "reply", ["id"] = call.Request.Id, ["ok"] = true, ["result"] = new JObject()
            }.ToString(Formatting.None), call.Request.Method);
        };
        using V2Client client = await V2Client.Connect(rig);

        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = "c1", ["method"] = "inspect",
            ["params"] = new JObject { ["what"] = "looking_at", ["as_player"] = "xceled" }
        }.ToString(Formatting.None));
        while ((string?)client.Next()["id"] != "c1")
        {
        }

        Assert.Equal("xceled", seen);
    }

    private static VerbError? Check(string verb, string options) =>
        OptionCheck.Check(VerbSurface.Verb(verb)!, JObject.Parse(options), out _);
}
