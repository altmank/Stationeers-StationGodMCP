#nullable enable

using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// The world's rules in every report (PLAN): a write logic report is the mod's dry run, so each row carries the
/// device's driven_by, the lint's problems and warnings, and PASSED for lifted guards, on the direct face (confirm:
/// false, a ref) and in a script's report run.
/// </summary>
public sealed class WorldRulesTests
{
    private const string Driven = """{"kind":"chip","reference_id":"2000","prefab_name":"StructureCircuitHousing","logic_type":"Open","via":"d0","line":4}""";

    private static FakeGameLink Game() => new()
    {
        Answer = call => call.Method switch
        {
            "write_logic" => FakeGameLink.Ok($$$"""
                {"report_only":true,"problems":[],"gateway_id":"world","reference_id":"4936423","logic_type":{"id":10,"name":"Open"},
                "requested_value":0,"previous_value":1,"opens":false,
                "warnings":[{"code":"lint_vent_outdoors","message":"opens to outside air"}],"driven_by":[{{{Driven}}}]}
                """),
            "write_logic_many" => FakeGameLink.Ok($$$"""
                {"report_only":true,"problems":[],"warnings":[{"code":"lint_fuel_mix","message":"too cold","index":1}],"passed":["lint"],
                "results":[{"index":0,"ok":true,"reference_id":"4936423","logic_type":{"name":"Open"},"requested_value":0,"previous_value":1,"opens":false,"driven_by":[{{{Driven}}}]},
                {"index":1,"ok":false,"reference_id":"5000","logic_type":{"name":"On"},"error":{"code":"lint_refused","message":"oxidiser vented outdoors"}}]}
                """),
            "thing_health" => FakeGameLink.Ok("""{"results":[{"reference_id":"4936423","display_name":"Hangar Door"},{"reference_id":"5000","display_name":"Vent"}]}"""),
            _ => FakeGameLink.Ok("{}")
        }
    };

    [Theory]
    [InlineData(Face.Direct)]
    [InlineData(Face.Script)]
    public async Task AReportRowCarriesDrivenByAndTheLint(Face face)
    {
        FakeGameLink game = Game();
        string confirm = face == Face.Direct ? ""","confirm":false""" : string.Empty;

        JsonNode report = (await game.Pipeline().CallAsync(new VerbCall("write", "logic",
            Options($$"""{"id":"4936423","logic":"Open","value":0{{confirm}}}"""), face), CancellationToken.None)).Done()!;

        // One id on the script face is a list (rows for every id): write_logic_many's dry run there.
        GameCall sent = Assert.Single(game.Calls, call => call.Method.StartsWith("write_logic", System.StringComparison.Ordinal));
        Assert.True((bool)sent.Params()["dry_run"]!);
        JsonNode row = report["targets"]!.AsArray().First(target => (string?)target!["id"] == "4936423")!;
        Assert.StartsWith("[4936423, Hangar Door, 1 -> 0] [undo] WARNING driven_by 2000", (string)row["text"]!);
        Assert.Equal("2000", (string)row["driven_by"]![0]!["reference_id"]!);
        Assert.Contains(report["targets"]!.AsArray(), target =>
            target!["warnings"]?[0]?["code"] is { } code && ((string)code!).StartsWith("lint_", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ABatchReportPutsEachIssueOnItsRowAndPassedOnEvery()
    {
        FakeGameLink game = Game();
        VerbPipeline pipeline = game.Pipeline();
        string reference = MintRef(game, pipeline);

        JsonNode report = pipeline.Call("write", "logic", $$"""{"id":"{{reference}}","logic":"Open","value":0,"allow":["lint"]}""").Done()!;

        JsonArray rows = report["targets"]!.AsArray();
        // allow: ["lint"] is the handler's allow_lint (a switch the tables leave out).
        Assert.True((bool)game.Single("write_logic_many").Params()["allow_lint"]!);
        Assert.Equal("2000", (string)rows[0]!["driven_by"]![0]!["reference_id"]!);
        Assert.Null(rows[0]!["warnings"]);
        Assert.Equal("lint_refused", (string)rows[1]!["problems"]![0]!["code"]!);
        Assert.Equal("lint_fuel_mix", (string)rows[1]!["warnings"]![0]!["code"]!);
        Assert.All(rows, row => Assert.Equal("""["lint"]""", row!["passed"]!.ToJsonString()));
        Assert.EndsWith("PROBLEM lint_refused WARNING lint_fuel_mix PASSED lint", (string)rows[1]!["text"]!);
    }

    // The ref of a find whose reply shows the door and the vent (a ref write reports until confirm on the direct face).
    private static string MintRef(FakeGameLink game, VerbPipeline pipeline)
    {
        System.Func<GameCall, GameReply> answer = game.Answer;
        game.Answer = call => call.Method == "find_things"
            ? FakeGameLink.Ok("""{"things":[{"reference_id":"4936423"},{"reference_id":"5000"}],"has_more":true}""")
            : answer(call);
        return (string)pipeline.Call("find", "thing", "{}").Done()!["ref"]!;
    }
}
