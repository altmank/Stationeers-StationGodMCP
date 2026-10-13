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
/// The direct write rule (PLAN v9 2.0) per verb, reports (the mod's, or the hub's for a what with no confirm), bulk writes
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
    public void MoveItemsPrefabIsASpreadWheneverGiven()
    {
        WhatEntry move = VerbExampleTests.WhatOf("move.item");

        foreach (string options in new[]
                 {
                     """{"to":"5200100","prefab":"*Ore*"}""",
                     """{"source":"5200100","prefab":"ItemIronIngot","quantity":500,"to":"player"}""",
                     """{"id":"7","to":"20","slot":0,"prefab":"*Ore*"}"""
                 })
        {
            WriteTarget spread = Assert.Single(WriteTargetPaths.Resolve(move, Options(options)).Targets,
                target => target.Path == "prefab");
            Assert.Equal("spread", spread.Role);
            Assert.Equal(TargetSource.Selector, spread.Source);
        }
    }

    [Fact]
    public void ConfirmFalseReportsAndTypedIdsAct()
    {
        FakeGameLink game = new();

        game.Pipeline().Call("write", "logic", """{"id":"1","logic":"On","value":1,"confirm":false}""").Done();
        game.Pipeline().Call("write", "logic", """{"id":"1","logic":"On","value":1}""").Done();

        // The report is the mod's (the world's rules are in it); the second call acts.
        List<GameCall> writes = game.CallsTo("write").ToList();
        Same("""{"what":"logic","id":"1","logic":"On","value":1}""", writes[0].Params());
        Same("""{"what":"logic","id":"1","logic":"On","value":1,"confirm":true}""", writes[1].Params());
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

        List<GameCall> removes = game.CallsTo("remove").ToList();
        Same("""{"what":"cable","id":["5"],"confirm":true}""", removes[0].Params());
        Same("""{"what":"pipe","id":["6"]}""", removes[1].Params());
        Assert.True((bool)report!["report_only"]!);
    }

    // ---- reports -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AReportIsTheModsOwnSentWithoutConfirmAndMarkedReportOnly()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"targets":[{"id":"5","text":"[5, Cable (heavy), ? -> ?] [undo]"}],"to_remove":1}""");

        JsonNode? report = game.Pipeline().Call("remove", "cable", """{"id":["5"]}""").Done();

        Same("""{"what":"cable","id":["5"]}""", game.Single("remove").Params());
        Assert.True((bool)report!["report_only"]!);
        Assert.Equal(1, (int)report["to_remove"]!);
        Assert.Equal("[5, Cable (heavy), ? -> ?] [undo]", (string)report["targets"]![0]!["text"]!);
    }

    [Fact]
    public void AWriteWithNoConfirmReportsFromTheHubAndSendsNothing()
    {
        FakeGameLink game = new();

        JsonNode? report = game.Pipeline().Call("server", "profiling", """{"action":"report"}""", Face.Script).Done();
        JsonNode? chip = game.Pipeline().Call("write", "chip", """{"id":"7","run":"pause"}""", Face.Script).Done();

        Assert.Empty(game.Calls);
        Assert.True((bool)report!["report_only"]!);
        Assert.Equal("[7, ?, ? -> ?] [cannot undo: run control]", (string)chip!["targets"]![0]!["text"]!);
    }

    // ---- bulk writes ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AMoveOfAnIdListActsOnTypedIdsAsABulkWrite()
    {
        FakeGameLink game = new();

        JsonNode? reply = game.Pipeline().Call("move", "item", """{"id":["1","2"],"to":"9"}""").Done();

        Same("""{"what":"item","id":["1","2"],"to":"9","confirm":true}""", game.Single("move").Params());
        Assert.Equal("write-1", (string)reply!["job_id"]!);
    }

    [Fact]
    public void ABulkWriteIsInspectedAndUndoneNewestFirstLeavingMergesOut()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "write" && call.Params()["id"] is JsonArray
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
        List<GameCall> backs = game.CallsTo("write").Skip(1).ToList();
        Same("""{"what":"logic","writes":[{"id":"2","logic":"On","value":1}],"confirm":true}""", backs[0].Params());
        Same("""{"what":"logic","writes":[{"id":"1","logic":"On","value":0}],"confirm":true}""", backs[1].Params());
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
        Assert.All(game.CallsTo("write"), call => Assert.Null(call.Params()["confirm"]));
    }

    [Fact]
    public void ARefTargetIsMarkedWithItsRef()
    {
        ScriptedGate gate = new(new WriteDecision.Act());
        FakeGameLink game = new();
        game.Answer = call => call.Method == "find"
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
