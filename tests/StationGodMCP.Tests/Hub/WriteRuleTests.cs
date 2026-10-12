#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// The direct write rule (PLAN v9 2.0) per verb, the report forms by class, move item acting by typed ids, bulk writes
/// (write-N) and their undo, and the gate seam a run's gate builds on.
/// </summary>
public sealed class WriteRuleTests
{
    // ---- the direct rule -------------------------------------------------------------------------------------------

    // Typed ids without confirm: these act; every other write what reports.
    private static readonly string[] Acting =
    {
        "write.logic", "write.chip", "write.screen", "write.label", "write.paint", "write.flight_log", "write.uplink",
        "move.item", "server.profiling", "server.stop"
    };

    public static IEnumerable<object[]> WriteWhats() =>
        VerbCatalogue.Embedded.Verbs.SelectMany(verb => verb.WhatNames.Select(name => verb.Whats[name]))
            .Where(what => what.Write != null)
            .Select(what => new object[] { what.Key });

    [Theory]
    [MemberData(nameof(WriteWhats))]
    public void TheDirectRuleActsOnTypedIdsOrReportsByVerb(string key)
    {
        WhatEntry what = VerbExampleTests.WhatOf(key);
        DirectWriteGate gate = new(VerbCatalogue.Embedded);
        JsonObject typed = new() { ["id"] = "5" };

        WriteDecision plain = gate.Decide(Call(what, typed), WriteTargetPaths.Resolve(what, typed));
        JsonObject confirmed = new() { ["id"] = "5", ["confirm"] = true };
        WriteDecision withConfirm = gate.Decide(Call(what, confirmed), WriteTargetPaths.Resolve(what, confirmed));

        Assert.IsType(Acting.Contains(key) ? typeof(WriteDecision.Act) : typeof(WriteDecision.Report), plain);
        Assert.IsType<WriteDecision.Act>(withConfirm);
    }

    [Fact]
    public void RefsSpreadsCheatsAndConfirmFalseReport()
    {
        DirectWriteGate gate = new(VerbCatalogue.Embedded);
        WhatEntry logic = VerbExampleTests.WhatOf("write.logic");
        WhatEntry move = VerbExampleTests.WhatOf("move.item");
        WhatEntry paint = VerbExampleTests.WhatOf("write.paint");

        WriteTargets fromRef = WriteTargetPaths.Resolve(logic, Options("""{"id":["1","2"]}"""),
            new List<RefUse> { new("id", "r4", new List<string> { "1", "2" }) });
        JsonObject filtered = Options("""{"to":"20","prefab":"*Ore*"}""");
        JsonObject network = Options("""{"network":"9","color":"Red"}""");
        JsonObject forced = Options("""{"id":"1","to":"9","slot":0,"force":true}""");
        JsonObject no = Options("""{"id":"1","logic":"On","value":1,"confirm":false}""");

        Assert.IsType<WriteDecision.Report>(gate.Decide(Call(logic, Options("""{"id":["1","2"]}""")), fromRef));
        Assert.IsType<WriteDecision.Report>(gate.Decide(Call(move, filtered), WriteTargetPaths.Resolve(move, filtered)));
        Assert.IsType<WriteDecision.Report>(gate.Decide(Call(paint, network), WriteTargetPaths.Resolve(paint, network)));
        Assert.IsType<WriteDecision.Report>(gate.Decide(Call(move, forced), WriteTargetPaths.Resolve(move, forced)));
        Assert.IsType<WriteDecision.Report>(gate.Decide(Call(logic, no), WriteTargetPaths.Resolve(logic, no)));
    }

    [Fact]
    public void ConfirmFalseSendsTheHandlersReportAndTypedIdsAct()
    {
        FakeGameLink game = new();

        game.Pipeline().Call("write", "logic", """{"id":"1","logic":"On","value":1,"confirm":false}""").Done();
        game.Pipeline().Call("write", "logic", """{"id":"1","logic":"On","value":1}""").Done();

        // Report class hub: the report sends no write and reads the current value; the second call acts.
        Same("""{"reference_id":"1","logic_type":"On","value":1,"dry_run":false}""", game.Single("write_logic").Params());
        Same("""{"reads":[{"reference_id":"1","logic_type":"On"}]}""", game.Single("read_logic_many").Params());
    }

    [Fact]
    public async System.Threading.Tasks.Task AScriptCallActsWhenItsRunsGateActsAndReportsWithoutOne()
    {
        FakeGameLink game = new();
        VerbPipeline pipeline = game.Pipeline();

        (await pipeline.CallAsync(new VerbCall("remove", "cable", Options("""{"id":["5"]}"""), Face.Script)
        {
            Gate = new ScriptedGate(new WriteDecision.Act())
        }, default)).Done();
        JsonNode? report = pipeline.Call("remove", "pipe", """{"id":["6"]}""", Face.Script).Done();

        Assert.False((bool)game.Single("remove_cables").Params()["dry_run"]!);
        Assert.True((bool)game.Single("remove_cables").Params()["confirm"]!);
        Assert.True((bool)game.Single("remove_pipes").Params()["dry_run"]!);
        Assert.Null(game.Single("remove_pipes").Params()["confirm"]);
        Assert.True((bool)report!["report_only"]!);
    }

    // ---- report forms ----------------------------------------------------------------------------------------------

    [Fact]
    public void ADryRunClassReportSendsTheHandlersDryRunAndNamesEachTarget()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "thing_health"
            ? FakeGameLink.Ok("""{"results":[{"reference_id":"5","display_name":"Cable (heavy)"}]}""")
            : FakeGameLink.Ok("""{"status":"dry_run","ready":true,"to_remove":1}""");

        JsonNode? report = game.Pipeline().Call("remove", "cable", """{"id":["5"]}""").Done();

        Same("""{"dry_run":true,"reference_ids":["5"],"wait":true}""", game.Single("remove_cables").Params());
        Assert.True((bool)report!["report_only"]!);
        Assert.Equal(1, (int)report["to_remove"]!);
        JsonNode row = report["targets"]![0]!;
        Assert.Equal("5", (string)row["id"]!);
        Assert.Equal("Cable (heavy)", (string)row["name"]!);
        Assert.Equal("[undo]", (string)row["undo"]!);
        Assert.Equal("[5, Cable (heavy), ? -> ?] [undo]", (string)row["text"]!);
    }

    [Fact]
    public void AModClassReportSendsTheHandlersOwnReport()
    {
        FakeGameLink game = new();

        JsonNode? report = game.Pipeline().Call("write", "genes", """{"id":"3","genes":{"Growth":1}}""").Done();

        Same("""{"genes":{"Growth":1},"reference_id":"3","dry_run":true}""", game.Single("plant_genes").Params());
        Assert.True((bool)report!["report_only"]!);
    }

    [Fact]
    public void AHubClassReportReadsCurrentValuesAndSendsNoWrite()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "read_logic_many" => FakeGameLink.Ok("""{"results":[{"index":0,"ok":true,"reference_id":"1","value":0},{"index":1,"ok":true,"reference_id":"2","value":1}]}"""),
            "thing_health" => FakeGameLink.Ok("""{"results":[{"reference_id":"1","display_name":"Pump A"},{"reference_id":"2","display_name":"Pump B"}]}"""),
            _ => FakeGameLink.Ok("{}")
        };

        JsonNode? report = game.Pipeline().Call("write", "logic", """{"id":["1","2"],"logic":"On","value":1,"confirm":false}""")
            .Done();

        Assert.Empty(game.CallsTo("write_logic_many"));
        // Each row its own mark: On holds a setting.
        Assert.Equal("[1, Pump A, 0 -> 1] [undo]",
            (string)report!["targets"]![0]!["text"]!);
        Assert.Equal("Pump B", (string)report["targets"]![1]!["name"]!);
        Assert.Equal(1, (int)report["targets"]![1]!["current"]!);
    }

    [Fact]
    public void ANoneClassReportSendsNothing()
    {
        FakeGameLink game = new();

        JsonNode? report = game.Pipeline().Call("server", "profiling", """{"action":"report"}""", Face.Script).Done();

        Assert.Empty(game.Calls);
        Assert.True((bool)report!["report_only"]!);
    }

    [Fact]
    public void ABuildReportCarriesPlacedAndItsPlacementsByCell()
    {
        FakeGameLink game = new();

        JsonNode? report = game.Pipeline().Call("build", "structure", """{"prefab":"StructureWallLight","at":[1,2,3]}""").Done();

        Assert.Equal("[]", report!["placed"]!.ToJsonString());
        Assert.Equal("[[1,2,3], StructureWallLight, ? -> placed] [undo]", (string)report["targets"]![0]!["text"]!);
        Assert.True((bool)game.Single("place_structure").Params()["dry_run"]!);
    }

    [Fact]
    public void UndoPasteReportsFromThePasteStatusAndActsOnlyWithConfirm()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"known":true,"file":"airlock.bp","entries":42}""");

        JsonNode? report = game.Pipeline().Call("undo", null, """{"id":"paste"}""").Done();
        game.Pipeline().Call("undo", null, """{"id":"paste","confirm":true}""").Done();

        List<GameCall> pastes = game.CallsTo("paste_blueprint").ToList();
        Same("""{"status":true}""", pastes[0].Params());
        Same("""{"undo":true}""", pastes[1].Params());
        Assert.Equal("would undo the newest paste (airlock.bp, 42 entries)", (string)report!["targets"]![0]!["text"]!);
    }

    // ---- move item and bulk writes ---------------------------------------------------------------------------------

    [Fact]
    public void MoveItemActsOnTypedIdsWithNamesAndMarksMergesCannotUndo()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "thing_health" => FakeGameLink.Ok("""{"results":[{"reference_id":"1","display_name":"Iron Ingot"},{"reference_id":"2","display_name":"Gold Ingot"}]}"""),
            "move_item" => FakeGameLink.Ok("""{"results":[{"index":0,"ok":true,"reference_id":"1","from":{"id":"40","slot":2},"to":{"id":"9","slot":0},"merged_into":null},{"index":1,"ok":true,"reference_id":"2","from":{"id":"40","slot":3},"to":{"id":"9","slot":1},"merged_into":"55"}],"count":2,"success_count":2,"error_count":0}"""),
            _ => FakeGameLink.Ok("{}")
        };

        JsonNode? reply = game.Pipeline().Call("move", "item", """{"id":["1","2"],"to":"9"}""").Done();

        Same("""{"dry_run":false,"moves":[{"reference_id":"1","to_id":"9","to_slot":"auto"},{"reference_id":"2","to_id":"9","to_slot":"auto"}]}""",
            game.Single("move_item").Params());
        JsonArray rows = reply!["results"]!.AsArray();
        Assert.Equal("Iron Ingot", (string)rows[0]!["display_name"]!);
        Assert.Equal("[undo]", (string)rows[0]!["undo"]!);
        Assert.Equal("[cannot undo: merged into 55]", (string)rows[1]!["undo"]!);
        Assert.Equal("write-1", (string)reply["job_id"]!);
    }

    [Fact]
    public void ABulkWriteIsInspectedAndUndoneNewestFirstLeavingMergesOut()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "write_logic_many" && call.Params()["writes"]!.AsArray().Count == 2
            ? FakeGameLink.Ok("""{"results":[{"index":0,"ok":true,"reference_id":"1","logic_type":{"id":28,"name":"On"},"requested_value":1,"previous_value":0,"current_value":1},{"index":1,"ok":true,"reference_id":"2","logic_type":{"id":28,"name":"On"},"requested_value":1,"previous_value":1,"current_value":1}],"count":2,"success_count":2,"error_count":0}""")
            : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline();

        JsonNode? acted = pipeline.Call("write", "logic", """{"id":["1","2"],"logic":"On","value":1}""").Done();
        JsonNode? view = pipeline.Call("inspect", "job", """{"id":"write-1"}""").Done();
        JsonNode? report = pipeline.Call("undo", null, """{"id":"write-1"}""").Done();
        JsonNode? undone = pipeline.Call("undo", null, """{"id":"write-1","confirm":true}""").Done();

        Assert.Equal("write-1", (string)acted!["job_id"]!);
        Assert.Equal("applied", (string)view!["status"]!);
        Assert.True((bool)report!["report_only"]!);
        Assert.Equal("2", (string)report["targets"]![0]!["id"]!);
        List<GameCall> backs = game.CallsTo("write_logic_many").Skip(1).ToList();
        Same("""{"writes":[{"reference_id":"2","logic_type":"On","value":1}]}""", backs[0].Params());
        Same("""{"writes":[{"reference_id":"1","logic_type":"On","value":0}]}""", backs[1].Params());
        Assert.Equal(2, (int)undone!["undone"]!);
        Assert.Equal("undone", (string)pipeline.Call("inspect", "job", """{"id":"write-1"}""").Done()!["status"]!);
    }

    [Fact]
    public void OneTypedIdIsNoBulkWrite()
    {
        JsonNode? reply = new FakeGameLink().Pipeline().Call("write", "logic", """{"id":"1","logic":"On","value":1}""").Done();

        Assert.Null(reply!["job_id"]);
    }

    // ---- the gate seam -----------------------------------------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task TheGateSeesFaceStreamLineAndEachTargetsSourceRoleAndSelector()
    {
        ScriptedGate gate = new(new WriteDecision.Act());
        FakeGameLink game = new();
        game.Answer = call => call.Method == "thing_health" && call.Params()["reference_id"] != null
            ? FakeGameLink.Ok("""{"prefab_name":"StructureIngotVault"}""")
            : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline();

        (await pipeline.CallAsync(new VerbCall("move", "item", Options("""{"to":"20","prefab":"*Ore*","near":"player","radius":5}"""),
            Face.Script, stream: 4, line: 12) { Gate = gate }, default)).Done();

        (VerbCall call, WriteTargets targets) = Assert.Single(gate.Asked);
        Assert.Equal((Face.Script, 4L, (int?)12, "move", "item"), (call.Face, call.Stream, call.Line, call.Verb, call.What));
        WriteTarget to = targets.Targets.Single(target => target.Path == "to");
        Assert.Equal((TargetSource.Typed, "acts-on", "20"), (to.Source, to.Role, to.Ids[0]));
        WriteTarget prefab = targets.Targets.Single(target => target.Path == "prefab");
        Assert.Equal((TargetSource.Selector, "spread", "\"*Ore*\""), (prefab.Source, prefab.Role, prefab.Selector!.ToJsonString()));
        Assert.Contains(targets.Targets, target => target.Path == "near" && target.Source == TargetSource.Selector);
    }

    [Fact]
    public void ARefusedWriteAnswersItsRowWithoutRaisingAndAReportAnswersRows()
    {
        FakeGameLink game = new();
        JsonObject row = new() { ["id"] = "1", ["ok"] = false, ["error"] = new JsonObject { ["code"] = "cleanup" } };

        JsonNode? refused = game.Pipeline(new ScriptedGate(new WriteDecision.Refuse(row)))
            .Call("write", "logic", """{"id":"1","logic":"On","value":1}""", Face.Script).Done();
        JsonNode? report = game.Pipeline(new ScriptedGate(new WriteDecision.Report()))
            .Call("write", "logic", """{"id":"1","logic":"On","value":1}""", Face.Script).Done();

        Assert.Equal(1, (int)refused!["error_count"]!);
        Assert.Equal("cleanup", (string)refused["results"]![0]!["error"]!["code"]!);
        Assert.True((bool)report!["report_only"]!);
        Assert.Empty(game.CallsTo("write_logic_many"));
    }

    [Fact]
    public void ARefTargetIsMarkedWithItsRef()
    {
        ScriptedGate gate = new(new WriteDecision.Act());
        FakeGameLink game = new();
        game.Answer = call => call.Method == "find_things"
            ? FakeGameLink.Ok("""{"things":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}],"has_more":false}""")
            : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline(gate);

        string reference = (string)pipeline.Call("find", "thing", """{"name":"Light"}""").Done()!["ref"]!;
        pipeline.Call("write", "logic", $$"""{"id":"{{reference}}","logic":"On","value":1}""").Done();

        WriteTarget target = Assert.Single(gate.Asked).Targets.Targets.Single();
        Assert.Equal((TargetSource.Ref, reference), (target.Source, target.From));
        Assert.Equal(new[] { "1", "2", "3" }, target.Ids);
    }

    private static VerbCall Call(WhatEntry what, JsonObject options) =>
        new(what.Verb, what.Name == "-" ? null : what.Name, options, Face.Direct);
}

/// <summary>A run gate double: answers one decision and keeps what it was asked.</summary>
internal sealed class ScriptedGate(WriteDecision decision) : IWriteGate
{
    public List<(VerbCall Call, WriteTargets Targets)> Asked { get; } = new();

    public WriteDecision Decide(VerbCall call, WriteTargets targets)
    {
        Asked.Add((call, targets));
        return decision;
    }
}
