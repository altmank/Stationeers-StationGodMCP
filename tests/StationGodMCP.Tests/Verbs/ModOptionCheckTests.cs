#nullable enable

using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The mod's option check (PLAN 2.0 shared forms; PLAN 2.1 placed it in the hub, decided into the mod): the what and
/// every option against the what's schema, every problem at once, in verb names, with the nearest valid names; the
/// hub's own options never reach the game.
/// </summary>
public sealed class ModOptionCheckTests
{
    private static VerbError? Check(string verb, string options, out WhatSpec? what) =>
        OptionCheck.Check(VerbSurface.Verb(verb)!, JObject.Parse(options), out what);

    private static JArray Problems(VerbError error) => (JArray)error.Data!["problems"]!;

    [Fact]
    public void AGoodCallHasNoProblemAndNamesItsWhat()
    {
        Assert.Null(Check("read", """{"what": "logic", "id": ["1", "2"], "logic": "On"}""", out WhatSpec? what));
        Assert.Equal("read logic", what!.Label);
    }

    [Fact]
    public void AMissingWhatListsTheWhats()
    {
        VerbError error = Check("survey", "{}", out _)!;

        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal("survey: what: required: one of area, wall, network, feeds.", error.Message);
    }

    [Fact]
    public void AnUnknownWhatNamesTheNearest()
    {
        VerbError error = Check("read", """{"what": "logics"}""", out _)!;

        JToken problem = Assert.Single(Problems(error));
        Assert.Equal("what", (string?)problem["path"]);
        Assert.Equal("'logics' is not a what of read", (string?)problem["problem"]);
        Assert.Equal("logic", (string?)problem["nearest"]![0]);
    }

    [Fact]
    public void AWhatlessVerbTakesNoWhat()
    {
        Assert.Null(Check("undo", """{"id": "job-3"}""", out WhatSpec? undo));
        Assert.Equal("undo", undo!.Label);
        Assert.Equal("undo: what: undo takes no what.", Check("undo", """{"what": "job"}""", out _)!.Message);
        Assert.Null(Check("help", """{"verb": "read", "what": "logic"}""", out _));
    }

    [Fact]
    public void EveryProblemComesBackAtOnceWithTheValidNamesNearestFirst()
    {
        VerbError error = Check("find", """{"what": "thing", "limt": 5, "order": "far", "radius": "x", "prefab": [1.5]}""",
            out _)!;

        Assert.Equal(new[] { "limt", "order", "radius", "prefab[0]" },
            Problems(error).Select(problem => (string?)problem["path"]).ToArray());
        Assert.Equal("limit", (string?)Problems(error)[0]["nearest"]![0]);
        Assert.Equal("'far' is not one of nearest, id", (string?)Problems(error)[1]["problem"]);
        Assert.Equal("must be a number", (string?)Problems(error)[2]["problem"]);
        Assert.Equal("must be a string or an integer", (string?)Problems(error)[3]["problem"]);
        Assert.Equal("limit", (string?)error.Data!["valid"]![0]);
        Assert.StartsWith("find thing: limt: not an option of find thing (nearest: limit); order:", error.Message);
    }

    [Fact]
    public void EnumWordsMatchTrimmedInAnyCase()
    {
        Assert.Null(Check("find", """{"what": "thing", "order": " ID "}""", out _));
    }

    [Fact]
    public void ListLengthsAndBoundsAreChecked()
    {
        VerbError error = Check("move", """{"what": "item", "at_most": 0, "moves": [{}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}]}""",
            out _)!;

        Assert.Equal(new[] { "must be at least 1", "takes at most 64 entries; 65 given" },
            Problems(error).Select(problem => (string?)problem["problem"]).ToArray());
    }

    [Fact]
    public void RequiredOptionsAreNamed()
    {
        VerbError error = Check("build", """{"what": "blueprint"}""", out _)!;

        Assert.Equal(new[] { "blueprint", "at" }, Problems(error).Select(problem => (string?)problem["path"]).ToArray());
        Assert.All(Problems(error), problem => Assert.Equal("required", (string?)problem["problem"]));
    }

    [Fact]
    public void AnIdListWhereTheWhatTakesOneIdIsRefusedNamingRunScript()
    {
        VerbError error = Check("write", """{"what": "chip", "id": ["1", "2"], "code": "yield"}""", out _)!;

        JToken problem = Assert.Single(Problems(error));
        Assert.Equal("id", (string?)problem["path"]);
        Assert.StartsWith("takes one id: for several, use run_script", (string?)problem["problem"]);
    }

    [Fact]
    public void TheHubsOwnOptionsAreNotTheGames()
    {
        VerbError error = Check("write", """{"what": "chip", "id": "1", "code_file": "a.ic10", "whole": true}""", out _)!;

        Assert.Equal(new[] { "code_file", "whole" }, Problems(error).Select(problem => (string?)problem["path"]).ToArray());
        Assert.All(Problems(error), problem =>
            Assert.Equal("taken by the MCP server (or sg), not by the game", (string?)problem["problem"]));
        Assert.DoesNotContain("max_wait",
            TestVerbs.Names(Check("build", """{"what": "cable", "colour": 1}""", out _)!.Data!["valid"]));
    }

    [Fact]
    public void FieldsOmitAndListLimitsAreTakenByEveryVerbButHelp()
    {
        Assert.Null(Check("find", """{"what": "thing", "fields": ["things.reference_id"], "omit": ["scanned"], "list_limits": {"things": 5}}""",
            out _));
        Assert.Equal("fields", (string?)Problems(Check("help", """{"fields": ["x"]}""", out _)!)[0]["path"]);
    }

    [Fact]
    public void TheGameTakesTheWireCursorWhereAWhatPagesAndNowhereElse()
    {
        Assert.Null(Check("find", """{"what": "thing", "cursor": "k3"}""", out _));
        Assert.Equal("cursor", (string?)Problems(Check("read", """{"what": "logic", "cursor": "k3"}""", out _)!)[0]["path"]);
        Assert.Equal("must be a string", (string?)Problems(Check("find", """{"what": "thing", "cursor": 3}""", out _)!)[0]["problem"]);
    }

    [Fact]
    public void TheWireCursorIsNoAgentOption()
    {
        Assert.DoesNotContain("cursor", StationGodMCP.Server.VerbCatalogue.Embedded.Verb("find")!.What("thing")!.OptionNames);
        Assert.DoesNotContain(VerbCatalogue.OptionsOf(VerbCatalogue.Verb("find")["whats"]!["thing"]!.AsObject()),
            option => option.Name == "cursor");
    }

    [Fact]
    public void NullIsAnOmittedOption()
    {
        Assert.Null(Check("find", """{"what": "thing", "limit": null, "bogus": null}""", out _));
    }
}
