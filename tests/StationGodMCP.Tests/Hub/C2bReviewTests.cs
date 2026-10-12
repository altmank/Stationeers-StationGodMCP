#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>The C2b review's findings, each held by the params sent or the reply.</summary>
public sealed class C2bReviewTests
{
    private static readonly string[] Reads = { "thing_health", "player_vitals", "read_logic_many", "connections", "inspect_controllers" };

    private static Task<VerbReply> Run(VerbPipeline pipeline, string verb, string? what, string options, Face face,
        IWriteGate? gate = null, long stream = 0) =>
        pipeline.CallAsync(new VerbCall(verb, what, Options(options), face, stream) { Gate = gate }, CancellationToken.None);

    // ---- B1: under a report nothing leaves that could act ------------------------------------------------------------

    [Theory]
    [InlineData("""{"id":["paste"]}""", Face.Direct)]
    [InlineData("""{"id":"paste"}""", Face.Program)]
    [InlineData("""{"id":["place-3","paste"]}""", Face.Direct)]
    public async Task UndoPasteInAListOrOnAnotherFaceReportsFromItsStatus(string options, Face face)
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"file":"airlock.bp","entries":4}""");

        (await Run(game.Pipeline(), "undo", null, options, face)).Done();

        Assert.All(game.CallsTo("paste_blueprint"), call => Same("""{"status":true}""", call.Params()));
        Assert.All(game.CallsTo("undo_job"), call => Assert.True((bool)call.Params()["dry_run"]!));
    }

    public static IEnumerable<object[]> WriteWhats() => WriteRuleTests.WriteWhats();

    [Theory]
    [MemberData(nameof(WriteWhats))]
    public async Task UnderAReportEveryCallIsAReportOrARead(string key)
    {
        WhatEntry what = VerbExampleTests.WhatOf(key);
        JsonObject example = (JsonObject)(what.Example ?? new JsonObject()).DeepClone();
        example.Remove("code_file");
        example.Remove("confirm");
        List<JsonObject> forms = [example];
        if (what.Options.ContainsKey("id") && what.ListForm != "one")
        {
            JsonObject listed = (JsonObject)example.DeepClone();
            listed["id"] = new JsonArray("5", "paste", "write-9");
            forms.Add(listed);
        }

        foreach (JsonObject form in forms)
        {
            FakeGameLink game = new();
            await Run(game.Pipeline(), what.Verb, what.Name == "-" ? null : what.Name, form.ToJsonString(), Face.Direct,
                new ScriptedGate(new WriteDecision.Report()));
            foreach (GameCall call in game.Calls)
            {
                JsonObject sent = call.Params();
                bool read = Reads.Contains(call.Method) || (bool?)sent["status"] == true;
                bool report = (bool?)sent["dry_run"] == true && sent["confirm"] == null;
                Assert.True(read || report, $"{key} sent {call.Method} {sent.ToJsonString()} under a report");
            }
        }
    }

    // ---- B2 and M7: the gate's decision, not the caller's confirm --------------------------------------------------

    [Fact]
    public async Task TheGateDecidesUndoAndAScriptRunsActActs()
    {
        FakeGameLink game = new();
        VerbPipeline pipeline = game.Pipeline();

        await Run(pipeline, "undo", null, """{"id":"place-41"}""", Face.Script, new ScriptedGate(new WriteDecision.Act()));
        await Run(pipeline, "undo", null, """{"id":"place-42","confirm":true}""", Face.Direct, new ScriptedGate(new WriteDecision.Report()));
        await Run(pipeline, "build", "structure", """{"prefab":"StructureWallLight","at":[1,2,3]}""", Face.Script,
            new ScriptedGate(new WriteDecision.Act()));

        List<GameCall> undos = game.CallsTo("undo_job").ToList();
        Same("""{"job_id":"place-41","dry_run":false,"confirm":true}""", undos[0].Params());
        Same("""{"job_id":"place-42","dry_run":true}""", undos[1].Params());
        Assert.False((bool)game.Single("place_structure").Params()["dry_run"]!);
        Assert.True((bool)game.Single("place_structure").Params()["confirm"]!);
    }

    // ---- M1: write-N undo reads first, skips drift, resumes ------------------------------------------------------

    private static (FakeGameLink Game, VerbPipeline Pipeline) BulkLogic(Func<string, double> now)
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "write_logic_many" when call.Params()["writes"]!.AsArray().Count == 3 => FakeGameLink.Ok("""
                {"results":[
                {"index":0,"ok":true,"reference_id":"1","logic_type":{"id":28,"name":"On"},"requested_value":1,"previous_value":0,"current_value":1},
                {"index":1,"ok":true,"reference_id":"2","logic_type":{"id":28,"name":"On"},"requested_value":1,"previous_value":0,"current_value":1},
                {"index":2,"ok":true,"reference_id":"3","logic_type":{"id":0,"name":"Activate"},"requested_value":1,"previous_value":0,"current_value":1}],
                "count":3,"success_count":3,"error_count":0}
                """),
            "read_logic_many" => FakeGameLink.Ok($$"""{"results":[{"index":0,"ok":true,"value":{{now((string)call.Params()["reads"]![0]!["reference_id"]!)}}}]}"""),
            _ => FakeGameLink.Ok("{}")
        };
        VerbPipeline pipeline = game.Pipeline();
        pipeline.Call("write", "logic", """{"id":["1","2","3"],"logic":"On","value":1}""").Done();
        return (game, pipeline);
    }

    [Fact]
    public void AnUndoLeavesATargetThatChangedSinceAndOverwriteWritesItBack()
    {
        (FakeGameLink game, VerbPipeline pipeline) = BulkLogic(id => id == "1" ? 5 : 1);

        JsonNode first = pipeline.Call("undo", null, """{"id":"write-1","confirm":true}""").Done()!;
        JsonNode second = pipeline.Call("undo", null, """{"id":"write-1","confirm":true,"allow":["overwrite"]}""").Done()!;

        List<GameCall> backs = game.CallsTo("write_logic_many").Skip(1).ToList();
        Same("""{"writes":[{"reference_id":"2","logic_type":"On","value":0}]}""", backs[0].Params());
        Same("""{"writes":[{"reference_id":"1","logic_type":"On","value":0}]}""", backs[1].Params());
        // Steps go newest first: 1 is id 3 (an action), 2 is id 2, 3 is id 1.
        Assert.Equal("[3]", first["drifted"]!.ToJsonString());
        Assert.Equal(1, (int)first["undone"]!);
        Assert.Equal(1, (int)second["undone"]!);
        Assert.Equal("undone", (string)second["status"]!);
        Assert.Equal("already_undone", pipeline.Call("undo", null, """{"id":"write-1","confirm":true}""").Failed().Code);
    }

    [Fact]
    public void FromStepGoesOnFromThatStepAndAResumedUndoDoesNotResend()
    {
        (FakeGameLink game, VerbPipeline pipeline) = BulkLogic(_ => 1);

        pipeline.Call("undo", null, """{"id":"write-1","confirm":true,"from_step":3}""").Done();
        pipeline.Call("undo", null, """{"id":"write-1","confirm":true}""").Done();

        List<string> written = game.CallsTo("write_logic_many").Skip(1)
            .Select(call => (string)call.Params()["writes"]![0]!["reference_id"]!).ToList();
        Assert.Equal(new[] { "1", "2" }, written);
    }

    [Fact]
    public void AWriteNReportListsEveryStepWithItsOwnMark()
    {
        (_, VerbPipeline pipeline) = BulkLogic(_ => 1);

        JsonNode report = pipeline.Call("undo", null, """{"id":"write-1"}""").Done()!;

        Assert.Equal(new[] { "[cannot undo: action]", "[undo]", "[undo]" },
            report["targets"]!.AsArray().Select(row => (string)row!["undo"]!));
        Assert.Equal(2, (int)report["undoable"]!);
    }

    [Fact]
    public void OneUndoAtATime()
    {
        BulkWrite write = new BulkWrites().Mint(VerbExampleTests.WhatOf("write.logic"), JsonNode.Parse(
            """{"results":[{"index":0,"ok":true,"reference_id":"1","logic_type":{"name":"On"},"previous_value":0,"current_value":1}]}"""));

        Assert.True(write.BeginUndo());
        Assert.False(write.BeginUndo());
        write.EndUndo();
        Assert.True(write.BeginUndo());
    }

    // ---- M2: write-N in lists, newest first -------------------------------------------------------------------------

    [Fact]
    public async Task WriteNInAListIsFoundAndUndoneNewestFirst()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "label" when call.Params()["labels"]!.AsArray().Count == 2 => FakeGameLink.Ok(Labelled(call)),
            "thing_health" => FakeGameLink.Ok("""{"results":[{"reference_id":"1","custom_name":"new"},{"reference_id":"2","custom_name":"new"},{"reference_id":"3","custom_name":"new"},{"reference_id":"4","custom_name":"new"}]}"""),
            _ => FakeGameLink.Ok("{}")
        };
        VerbPipeline pipeline = game.Pipeline();
        pipeline.Call("write", "label", """{"id":["1","2"],"label":"new"}""").Done();
        pipeline.Call("write", "label", """{"id":["3","4"],"label":"new"}""").Done();

        JsonNode view = (await Run(pipeline, "inspect", "job", """{"id":"write-2"}""", Face.Script)).Done()!;
        JsonNode undone = pipeline.Call("undo", null, """{"id":["write-1","write-2"],"confirm":true}""").Done()!;

        Assert.Equal("write-2", (string)view["results"]![0]!["payload"]!["job_id"]!);
        List<string> order = game.CallsTo("label").Skip(2).Select(call => (string)call.Params()["labels"]![0]!["reference_id"]!).ToList();
        Assert.Equal(new[] { "4", "3", "2", "1" }, order);
        Assert.Equal(2, (int)undone["success_count"]!);
    }

    // label's rows for the two labels it was sent: each one's name was "old" and is "new".
    private static string Labelled(GameCall call)
    {
        JsonArray rows = [];
        foreach (JsonNode? label in call.Params()["labels"]!.AsArray())
        {
            rows.Add(new JsonObject
            {
                ["index"] = rows.Count,
                ["ok"] = true,
                ["reference_id"] = label!["reference_id"]!.DeepClone(),
                ["previous"] = new JsonObject { ["custom_name"] = "old" },
                ["current"] = new JsonObject { ["custom_name"] = "new" }
            });
        }

        return new JsonObject { ["results"] = rows }.ToJsonString();
    }

    // ---- M3: a hub report of a spread lists every target -----------------------------------------------------------

    [Fact]
    public void PaintOfANetworkListsEveryPieceAndActsOnThem()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "connections"
            ? FakeGameLink.Ok("""{"members":[{"reference_id":"11","member":"structure","color":{"name":"Blue"}},{"reference_id":"12","member":"structure","color":{"name":"Red"}},{"reference_id":"13","member":"device","color":{"name":"Blue"}}],"has_more":false}""")
            : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline();

        JsonNode report = pipeline.Call("write", "paint", """{"network":"9","kind":"pipe","color":"Green"}""").Done()!;
        JsonNode withDevices = pipeline.Call("write", "paint", """{"network":"9","include":["devices"],"color":"Green"}""").Done()!;
        pipeline.Call("write", "paint", """{"network":"9","kind":"pipe","color":"Green","confirm":true}""").Done();

        Same("""{"network_id":"9","offset":0,"limit":500,"kind":"pipe"}""", game.CallsTo("connections")[0].Params());
        Assert.Equal(new[] { "[11, ?, Blue -> Green] [undo]", "[12, ?, Red -> Green] [undo]" },
            report["targets"]!.AsArray().Select(row => (string)row!["text"]!));
        Assert.Equal(3, withDevices["targets"]!.AsArray().Count);
        Same("""{"reference_ids":["11","12"],"color":"Green"}""", game.Single("paint").Params());
    }

    // ---- M4: repeat with confirm acts on what the report listed ------------------------------------------------------

    [Fact]
    public void RepeatingAReportWithConfirmKeepsEachPathAndItsRole()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "find_things"
            ? FakeGameLink.Ok("""{"things":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}]}""")
            : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline();
        string things = (string)pipeline.Call("find", "thing", "{}").Done()!["ref"]!;

        string report = (string)pipeline.Call("move", "item", $$"""{"id":"{{things}}","to":"9"}""").Done()!["ref"]!;
        pipeline.Call("move", null, $$"""{"repeat":"{{report}}","confirm":true}""").Done();

        Same("""{"moves":[{"reference_id":"1","to_id":"9","to_slot":"auto"},{"reference_id":"2","to_id":"9","to_slot":"auto"},{"reference_id":"3","to_id":"9","to_slot":"auto"}],"dry_run":false}""",
            game.CallsTo("move_item").Last().Params());
    }

    [Fact]
    public void RepeatingASpreadReportSendsTheCallAgainAndSaysSo()
    {
        FakeGameLink game = new();
        VerbPipeline pipeline = game.Pipeline();
        string report = (string)pipeline.Call("swap", "wall", """{"room":"17","into":"StructureCompositeWall"}""").Done()!["ref"]!;

        JsonNode acted = pipeline.Call("swap", null, $$"""{"repeat":"{{report}}","confirm":true}""").Done()!;

        JsonObject sent = game.CallsTo("replace_walls").Last().Params();
        Assert.Equal("17", (string)sent["room_id"]!);
        Assert.False((bool)sent["dry_run"]!);
        Assert.Contains("selection may differ", (string)acted["note"]!);
    }

    [Fact]
    public void RepeatingAnExpandedSpreadActsOnItsIdsAndAWriteNUndoReportOnTheUndo()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "connections" => FakeGameLink.Ok("""{"members":[{"reference_id":"11","member":"structure"}],"has_more":false}"""),
            "label" => FakeGameLink.Ok("""{"results":[{"index":0,"ok":true,"reference_id":"1","previous":{"custom_name":"a"},"current":{"custom_name":"b"}},{"index":1,"ok":true,"reference_id":"2","previous":{"custom_name":"a"},"current":{"custom_name":"b"}}]}"""),
            "thing_health" => FakeGameLink.Ok("""{"results":[{"reference_id":"1","custom_name":"b"},{"reference_id":"2","custom_name":"b"}]}"""),
            _ => FakeGameLink.Ok("{}")
        };
        VerbPipeline pipeline = game.Pipeline();

        string paint = (string)pipeline.Call("write", "paint", """{"network":"9","color":"Red"}""").Done()!["ref"]!;
        pipeline.Call("write", null, $$"""{"repeat":"{{paint}}","confirm":true}""").Done();
        pipeline.Call("write", "label", """{"id":["1","2"],"label":"b"}""").Done();
        string undo = (string)pipeline.Call("undo", null, """{"id":"write-2"}""").Done()!["ref"]!;
        pipeline.Call("undo", null, $$"""{"repeat":"{{undo}}","confirm":true}""").Done();

        Assert.Single(game.CallsTo("connections"));
        Same("""{"reference_ids":["11"],"color":"Red"}""", game.Single("paint").Params());
        Assert.Equal(3, game.CallsTo("label").Count);
    }

    // ---- M5: store moves cannot be undone ---------------------------------------------------------------------------

    [Fact]
    public void StoreMovesAreMarkedAndTheirWriteNCannotBeUndone()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "thing_health" when call.Params()["reference_id"] != null => FakeGameLink.Ok("""{"prefab_name":"StructureIngotVault"}"""),
            "vault_deposit" => FakeGameLink.Ok("""{"dry_run":false,"vault":{"reference_id":"20"},"items":{"results":[{"index":0,"ok":true,"reference_id":"1","display_name":"Iron Ore"}],"count":1,"success_count":1,"error_count":0}}"""),
            _ => FakeGameLink.Ok("{}")
        };
        VerbPipeline pipeline = game.Pipeline();

        JsonNode moved = pipeline.Call("move", "item", """{"id":["1"],"to":"20"}""").Done()!;
        JsonNode report = pipeline.Call("undo", null, """{"id":"write-1"}""").Done()!;
        VerbReply.Failed undo = pipeline.Call("undo", null, """{"id":"write-1","confirm":true}""").Failed();

        Assert.Equal("[cannot undo: store move]", (string)moved["items"]!["results"]![0]!["undo"]!);
        Assert.Contains("cannot be undone", (string)report["note"]!);
        Assert.Equal("cannot_undo", undo.Code);
    }

    // ---- M6: the undo of a write-N honours the gate -----------------------------------------------------------------

    [Fact]
    public async Task AWriteNUndoHonoursRefuseAndStop()
    {
        (FakeGameLink game, VerbPipeline pipeline) = BulkLogic(_ => 1);
        JsonObject row = new() { ["error"] = new JsonObject { ["code"] = "after_stop" } };

        JsonNode refused = (await Run(pipeline, "undo", null, """{"id":"write-1"}""", Face.Script,
            new ScriptedGate(new WriteDecision.Refuse(row)))).Done()!;
        VerbReply.Failed stopped = (await Run(pipeline, "undo", null, """{"id":"write-1"}""", Face.Script,
            new ScriptedGate(new WriteDecision.Stop(new JsonArray(), "undo script-3 first", "world_changed", "The world changed.")))).Failed();

        Assert.Equal(1, (int)refused["error_count"]!);
        Assert.Equal(("world_changed", "The world changed.", true), (stopped.Code, stopped.Message, stopped.Stopped));
        Assert.Single(game.CallsTo("write_logic_many"));
    }

    // ---- minors ------------------------------------------------------------------------------------------------------

    [Fact]
    public void RefsKeepTheirSourcePerIdAndOneIdPathsTakeAOneIdRef()
    {
        ScriptedGate gate = new(new WriteDecision.Act());
        FakeGameLink game = new();
        game.Answer = call => call.Method == "find_things"
            ? FakeGameLink.Ok((string?)call.Params()["name_contains"] == "one"
                ? """{"things":[{"reference_id":"9"}],"has_more":true}"""
                : """{"things":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}]}""")
            : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline(gate);
        string three = (string)pipeline.Call("find", "thing", "{}").Done()!["ref"]!;
        string other = (string)pipeline.Call("find", "thing", "{}").Done()!["ref"]!;
        string one = (string)pipeline.Call("find", "thing", """{"name":"one"}""").Done()!["ref"]!;

        pipeline.Call("move", "item", $$"""{"id":["{{three}}","77","{{other}}"],"to":"{{one}}"}""").Done();
        VerbReply.Failed many = pipeline.Call("move", "item", $$"""{"id":"5","to":"{{three}}"}""").Failed();
        VerbReply.Failed nested = pipeline.Call("write", "logic", $$"""{"writes":[{"id":"{{three}}","logic":"On","value":1}]}""").Failed();

        List<WriteTarget> targets = gate.Asked[0].Targets.Targets.ToList();
        Assert.Equal(new[] { (TargetSource.Ref, three), (TargetSource.Ref, other), (TargetSource.Typed, (string?)null) },
            targets.Where(target => target.Path == "id").Select(target => (target.Source, target.From)));
        Assert.Equal(new[] { "77" }, targets.Single(target => target.Path == "id" && target.Source == TargetSource.Typed).Ids);
        Assert.Equal("9", (string)game.CallsTo("move_item")[0].Params()["moves"]![0]!["to_id"]!);
        Assert.Equal("to: takes one id; the ref shows 3", Assert.Single(many.Problems()));
        Assert.Equal("writes[].id: a ref is not taken inside entries; give id: \"rN\" for its ids", Assert.Single(nested.Problems()));
    }

    [Fact]
    public void ARefusalAnswersARowPerTarget()
    {
        JsonNode refused = new FakeGameLink().Pipeline(new ScriptedGate(new WriteDecision.Refuse(new JsonObject { ["error"] = "no" })))
            .Call("write", "logic", """{"id":["1","2","3"],"logic":"On","value":1}""").Done()!;

        Assert.Equal(3, (int)refused["count"]!);
        Assert.Equal(new[] { "1", "2", "3" }, refused["results"]!.AsArray().Select(row => (string)row!["reference_id"]!));
    }

    [Fact]
    public void TheCutStaysWithin4KbWhateverListIsLarge()
    {
        FakeGameLink game = new();
        string notes = string.Join(",", Enumerable.Range(0, 400).Select(n => $"\"note {n} {new string('n', 40)}\""));
        string huge = new('x', 6000);
        game.Answer = call => FakeGameLink.Ok((string?)call.Params()["name_contains"] == "huge"
            ? $$"""{"things":[{"reference_id":"1","prefab_name":"{{huge}}","parts":[1,2,3]},{"reference_id":"2","prefab_name":"{{huge}}"},{"reference_id":"3","prefab_name":"{{huge}}"}]}"""
            : $$"""{"things":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}],"notes":[{{notes}}]}""");
        VerbPipeline pipeline = game.Pipeline();

        JsonNode notesCut = pipeline.Call("find", "thing", "{}").Done()!;
        JsonNode shallow = pipeline.Call("find", "thing", """{"name":"huge"}""").Done()!;

        Assert.True(ReplyCap.Bytes(notesCut) <= ReplyCap.Target, ReplyCap.Bytes(notesCut).ToString());
        Assert.Equal(3, notesCut["things"]!.AsArray().Count);
        Assert.Equal(400, (int)notesCut["cut"]!["lists"]!["notes"]!["total"]!);
        Assert.True(ReplyCap.Bytes(shallow) <= ReplyCap.Target);
        Assert.True((bool)shallow["cut"]!["shallow"]!);
        Assert.Equal("1", (string)shallow["things"]![0]!["reference_id"]!);
    }

    [Fact]
    public void AfterWholeFromTheStoreTheRefIsTheWholeReply()
    {
        FakeGameLink game = new();
        string things = string.Join(",", Enumerable.Range(0, 200).Select(n => $$"""{"reference_id":"{{n}}","prefab_name":"{{new string('p', 60)}}"}"""));
        game.Answer = call => FakeGameLink.Ok(call.Method == "find_things" ? $$"""{"things":[{{things}}]}""" : "{}");
        VerbPipeline pipeline = game.Pipeline();
        string reference = (string)pipeline.Call("find", "thing", "{}").Done()!["ref"]!;

        pipeline.Call("find", null, $$"""{"repeat":"{{reference}}","whole":true}""").Done();
        pipeline.Call("remove", "cable", $$"""{"id":"{{reference}}"}""").Done();

        Assert.Equal(200, game.Single("remove_cables").Params()["reference_ids"]!.AsArray().Count);
    }

    [Fact]
    public async Task MorePagesKeepTheShapeAndAListWithNoPagesSaysSo()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "list_devices"
            ? FakeGameLink.Ok("""{"devices":[{"reference_id":"1"}],"has_more":true}""")
            : FakeGameLink.Ok((string?)call.Params()["cursor_id"] == null
                ? """{"things":[{"reference_id":"1"}],"has_more":true,"cursor_id":"k1"}"""
                : """{"things":[{"reference_id":"2"}],"has_more":false}""");
        VerbPipeline pipeline = game.Pipeline();

        string cursor = (string)pipeline.Call("find", "thing", """{"fields":["reference_id"]}""", Face.Script).Done()!["cursor"]!;
        (await pipeline.MoreAsync(0, cursor, null, CancellationToken.None)).Done();
        JsonNode devices = pipeline.Call("find", "device", "{}", Face.Script).Done()!;

        Assert.Equal("""{"fields":["reference_id"]}""", game.CallsTo("find_things")[1].Shape!.Value.GetRawText());
        Assert.Contains("takes no offset", (string)devices["rest_unreachable"]!);
        Assert.Null(devices["cursor"]);
    }

    [Fact]
    public void AReportTooLargeForTheClientSaysHowLargeAndHowToNarrow()
    {
        string ids = string.Join(",", Enumerable.Range(100000, 4000).Select(n => $"\"{n}\""));

        VerbReply.Failed failed = new FakeGameLink().Pipeline()
            .Call("write", "logic", $$"""{"id":[{{ids}}],"logic":"On","value":1,"confirm":false}""").Failed();

        Assert.Equal("report_too_large", failed.Code);
        Assert.Equal(4000, (int)failed.Data!["targets"]!);
    }

    // ---- bulk splitting by handler list limits ---------------------------------------------------------------------

    [Fact]
    public void AListLongerThanTheHandlerTakesIsSplitIntoPartsReindexedIntoOneReply()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "paint"
            ? FakeGameLink.Ok("{\"results\":[" + string.Join(",", Enumerable.Range(0, call.Params()["reference_ids"]!.AsArray().Count)
                .Select(index => $$"""{"index":{{index}},"ok":true}""")) + "],\"count\":0}")
            : FakeGameLink.Ok("{}");
        string ids = string.Join(",", Enumerable.Range(1, 300).Select(n => $"\"{n}\""));

        JsonNode reply = game.Pipeline().Call("write", "paint", $$"""{"id":[{{ids}}],"color":"Red"}""").Done()!;

        List<GameCall> parts = game.CallsTo("paint").ToList();
        Assert.Equal(new[] { 256, 44 }, parts.Select(call => call.Params()["reference_ids"]!.AsArray().Count));
        Assert.Equal(300, (int)reply["count"]!);
        Assert.Equal(299, (int)reply["results"]![299]!["index"]!);
        Assert.Equal("write-1", (string)reply["job_id"]!);
    }

    [Fact]
    public void ARefusedPartStopsTheRest()
    {
        FakeGameLink game = new();
        int calls = 0;
        game.Answer = call => call.Method != "paint" ? FakeGameLink.Ok("{}")
            : ++calls == 1 ? FakeGameLink.Ok("""{"results":[{"index":0,"ok":true}]}""")
            : FakeGameLink.Refused("paint_failed", "No.");
        string ids = string.Join(",", Enumerable.Range(1, 600).Select(n => $"\"{n}\""));

        JsonNode reply = game.Pipeline().Call("write", "paint", $$"""{"id":[{{ids}}],"color":"Red"}""").Done()!;

        Assert.Equal("[256,512]", reply["not_run"]!.ToJsonString());
        Assert.Equal(2, game.CallsTo("paint").Count);
    }
}
