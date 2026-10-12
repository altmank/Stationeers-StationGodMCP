#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>Each what with several handlers picks the one its when names (LegacyVerbMap, one method per what).</summary>
public sealed class HandlerSelectorTests
{
    [Fact]
    public void TheWhatsWithSeveralHandlersAreTheTwelveSelected()
    {
        List<string> several = VerbCatalogue.Embedded.Verbs
            .SelectMany(verb => verb.WhatNames.Select(name => verb.Whats[name]))
            .Where(what => what.Handlers.Count > 1)
            .Select(what => what.Key)
            .ToList();

        Assert.Equal(new[]
        {
            "find.item", "inspect.chip", "inspect.job", "read.logic", "write.logic", "write.chip", "plan.route",
            "plan.aim", "swap.cable", "swap.pipe", "move.item", "undo"
        }, several);
    }

    [Fact]
    public void FindItem()
    {
        Assert.Equal("item_totals", Sent("find", "item", """{"prefab":"ItemIronIngot","group_by":"place","holders":3}""").Method);
        Assert.Equal("find_items", Sent("find", "item", """{"prefab":"ItemIronIngot","group_by":"holder"}""").Method);
        Assert.Equal("find_items", Sent("find", "item", "{}").Method);
        Same("""{"prefab":"ItemIronIngot","holders_limit":3}""",
            Sent("find", "item", """{"prefab":"ItemIronIngot","group_by":"place","holders":3}""").Params());
        Assert.Equal("offset: not taken with group_by place (it goes with list or group_by holder/prefab)",
            Assert.Single(new FakeGameLink().Pipeline().Call("find", "item", """{"group_by":"place","offset":4}""")
                .Failed().Problems()));
    }

    [Fact]
    public void InspectChip()
    {
        FakeGameLink game = new();
        game.Answer = call => FakeGameLink.Ok(call.Method == "get_ic_status" ? """{"state":"running"}""" : """{"source":"yield"}""");

        JsonNode? one = game.Pipeline().Call("inspect", "chip", """{"id":"7","count":4}""").Done();
        JsonNode? two = game.Pipeline().Call("inspect", "chip", """{"id":"7","parts":["status","source"]}""").Done();

        Assert.Equal("""{"state":"running"}""", one!.ToJsonString());
        Assert.Equal("""{"status":{"state":"running"},"source":{"source":"yield"}}""", two!.ToJsonString());
        Same("""{"reference_id":"7","stack_count":4}""", game.CallsTo("get_ic_status")[0].Params());
        Same("""{"reference_id":"7"}""", game.Single("get_ic_source").Params());
        Assert.Equal("resolve_ic_selectors",
            Sent("inspect", "chip", """{"id":"7","parts":["selectors"],"targets":["8"]}""").Method);
    }

    [Fact]
    public void InspectJob()
    {
        GameCall build = Sent("inspect", "job", """{"id":"place-41","verbose":true}""");
        Assert.Equal("place_structure", build.Method);
        Same("""{"job_id":"place-41","verbose":true}""", build.Params());
        Assert.Equal("place_cables", Sent("inspect", "job", """{"id":"run-4"}""").Method);
        Assert.Equal("replace_walls", Sent("inspect", "job", """{"id":"replace-2"}""").Method);
        Assert.Equal("upgrade_cables", Sent("inspect", "job", """{"id":"upgrade-7"}""").Method);
        Same("""{"status":true}""", Sent("inspect", "job", """{"id":"paste"}""").Params());
        Same("""{"transfer_id":"gas-3"}""", Sent("inspect", "job", """{"id":"gas-3"}""").Params());

        VerbReply.Failed script = new FakeGameLink().Pipeline().Call("inspect", "job", """{"id":"script-14"}""").Failed();
        Assert.Equal("not_available", script.Code);
        Assert.Contains("runs piece", script.Message);
    }

    [Fact]
    public void ReadLogic()
    {
        Same("""{"reference_id":"1","logic_type":"On"}""",
            Sent("read", "logic", """{"id":"1","logic":"On"}""", "read_logic").Params());
        Same("""{"reads":[{"reference_id":"1","logic_type":"On"},{"reference_id":"1","logic_type":"Power"}]}""",
            Sent("read", "logic", """{"id":"1","logic":["On","Power"]}""", "read_logic_many").Params());
        Same("""{"reads":[{"reference_id":"1","logic_type":"On"}]}""",
            Sent("read", "logic", """{"reads":[{"id":"1","logic":"On"}]}""", "read_logic_many").Params());
        Same("""{"items":[{"reference_id":"1","logic":["On","Power"]}]}""",
            Sent("read", "logic", """{"reads":[{"id":"1","logic":["On","Power"]}]}""", "read_devices").Params());
        Same("""{"include":["clock"],"items":[{"reference_id":"1","logic":["On"]}]}""",
            Sent("read", "logic", """{"id":"1","logic":"On","include":["clock"]}""", "read_devices").Params());
    }

    [Fact]
    public void WriteLogic()
    {
        // Typed ids act: the gate's Act sends dry_run: false.
        Same("""{"reference_id":"1","logic_type":"On","value":1,"dry_run":false}""",
            Sent("write", "logic", """{"id":"1","logic":"On","value":1}""", "write_logic").Params());
        Same(
            """{"writes":[{"reference_id":"1","logic_type":"On","value":0},{"reference_id":"2","logic_type":"On","value":0}],"dry_run":false}""",
            Sent("write", "logic", """{"id":["1","2"],"logic":"On","value":0}""", "write_logic_many").Params());
        Same("""{"gateway_id":"9","writes":[{"reference_id":"1","logic_type":"On","value":1}],"dry_run":false}""",
            Sent("write", "logic", """{"gateway":"9","writes":[{"id":"1","logic":"On","value":1}]}""", "write_logic_many")
                .Params());
    }

    [Fact]
    public void WriteChip()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "set_ic_pins"
            ? FakeGameLink.Refused("not_on_data_network", "Device 9 is not on the housing's data network.")
            : FakeGameLink.Ok("""{"ok":1}""");

        JsonNode? stopped = game.Pipeline().Call("write", "chip", """{"id":"7","code":"yield","pins":{"d0":"9"},"run":"resume"}""").Done();

        Same("""{"reference_id":"7","source":"yield"}""", game.Single("set_ic_source").Params());
        Same("""{"reference_id":"7","pins":{"d0":"9"}}""", game.Single("set_ic_pins").Params());
        Assert.Empty(game.CallsTo("control_ic_execution"));
        Assert.Equal("""{"ok":1}""", stopped!["code"]!.ToJsonString());
        Assert.Equal("not_on_data_network", (string)stopped["pins"]!["code"]!);
        Assert.Equal("""["run"]""", stopped["not_run"]!.ToJsonString());

        Same("""{"reference_id":"7","action":"pause"}""",
            Sent("write", "chip", """{"id":"7","run":"pause"}""", "control_ic_execution").Params());
        Assert.Equal("code: code, pins or run is required",
            Assert.Single(new FakeGameLink().Pipeline().Call("write", "chip", """{"id":"7"}""").Failed().Problems()));
    }

    [Fact]
    public void PlanRoute()
    {
        Assert.Equal("plan_cable_route", Sent("plan", "route", """{"kind":"cable","start":{"at":[1,2,3]}}""").Method);
        Assert.Equal("plan_pipe_route", Sent("plan", "route", """{"kind":"pipe","grade":"gas"}""").Method);
        Assert.Equal("plan_chute_route", Sent("plan", "route", """{"kind":"chute"}""").Method);
        Assert.Equal("kind: required", Assert.Single(new FakeGameLink().Pipeline().Call("plan", "route", """{"grade":"gas"}""")
            .Failed().Problems()));
    }

    [Fact]
    public void PlanAim()
    {
        Same("""{"dish_id":"5","contact_id":"8"}""",
            Sent("plan", "aim", """{"id":"5","contact":"8"}""", "dish_aim").Params());
        Same("""{"reference_id":"5"}""", Sent("plan", "aim", """{"id":"5"}""", "solar_aim").Params());

        FakeGameLink game = new();
        game.Answer = call => call.Method == "solar_aim"
            ? FakeGameLink.Refused("not_solar_panel", "5 is not a solar panel.")
            : FakeGameLink.Ok("""{"horizontal":1}""");
        JsonNode? dish = game.Pipeline().Call("plan", "aim", """{"id":"5"}""").Done();

        Same("""{"dish_id":"5"}""", game.Single("dish_aim").Params());
        Assert.Equal(1, (int)dish!["horizontal"]!);
    }

    [Fact]
    public void SwapCable()
    {
        Assert.Equal("upgrade_cables", Sent("swap", "cable", """{"network":"5","into":"heavy"}""").Method);
        Same("""{"dry_run":true,"network_id":"5","wait":true,"operations":["simplify_junctions"]}""",
            Sent("swap", "cable", """{"network":"5","tidy":true}""", "clean_cables").Params());
        Same("""["remove_loops"]""",
            Sent("swap", "cable", """{"network":"5","tidy":["remove_loops"]}""", "clean_cables").Params()["operations"]!
                );
        VerbPipeline pipeline = new FakeGameLink().Pipeline();
        Assert.Equal("into: into or tidy is required", Assert.Single(pipeline.Call("swap", "cable", """{"network":"5"}""").Failed().Problems()));
        Assert.Equal("tidy: give into or tidy, not both",
            Assert.Single(pipeline.Call("swap", "cable", """{"network":"5","into":"heavy","tidy":true}""").Failed().Problems()));
    }

    [Fact]
    public void SwapPipe()
    {
        Same("""{"dry_run":true,"network_id":"5","wait":true,"to":"repair"}""",
            Sent("swap", "pipe", """{"network":"5","into":"repair"}""", "upgrade_pipes").Params());
        Assert.Equal("clean_pipes", Sent("swap", "pipe", """{"network":"5","tidy":["remove_dead_ends"],"spare":["3"]}""").Method);
        Assert.Equal("upgrade_pipes", Sent("swap", "pipe", """{"network":"5","into":"insulated","tidy":false}""").Method);
    }

    [Fact]
    public void MoveItem()
    {
        // to: "player" is the player's id (player_vitals answers 77 in the fake); typed ids act.
        Same("""{"reference_id":"1","to_id":"77","to_slot":"auto","dry_run":false}""",
            Sent("move", "item", """{"id":"1","to":"player"}""", "move_item").Params());
        Same("""{"reference_id":"1","to_id":"9","to_slot":2,"dry_run":false}""",
            Sent("move", "item", """{"id":"1","to":"9","slot":2}""", "move_item").Params());
        Same("""{"dry_run":false,"moves":[{"reference_id":"1","to_id":"9","to_slot":0,"allow_in_use":true}]}""",
            Sent("move", "item", """{"moves":[{"id":"1","to":"9","slot":0,"allow":["in_use"]}]}""").Params());

        Assert.Equal("vault_deposit", Store("""{"to":"20","prefab":"*Ore*"}""", ("20", "StructureIngotVault")));
        Assert.Equal("silo_deposit", Store("""{"to":"21","location":"player"}""", ("21", "StructureSDBSilo")));
        Assert.Equal("move_item", Store("""{"id":"1","to":"22"}""", ("22", "StructureLocker")));
        Assert.Equal("vault_withdraw", Store("""{"source":"20","prefab":"ItemIronIngot","quantity":5}""", ("20", "StructureIngotVault")));
        Assert.Equal("silo_withdraw", Store("""{"source":"21","prefab":"ItemIronOre","to":"player"}""", ("21", "StructureSDBSilo")));
        Assert.Equal("vault_transfer", Store("""{"source":"20","to":"23","items":"all"}""",
            ("20", "StructureIngotVault"), ("23", "StructureRemoteVault")));
    }

    [Fact]
    public void Undo()
    {
        Same("""{"dry_run":true,"job_id":"place-41"}""", Sent("undo", null, """{"id":"place-41"}""", "undo_job").Params());
        Same("""{"dry_run":false,"job_id":"place-41","confirm":true}""",
            Sent("undo", null, """{"id":"place-41","confirm":true}""", "undo_job").Params());
        Same("""{"undo":true}""", Sent("undo", null, """{"id":"paste","confirm":true}""", "paste_blueprint").Params());
        Assert.Equal("not_available", new FakeGameLink().Pipeline().Call("undo", null, """{"id":"script-3"}""").Failed().Code);
        Assert.Equal("job_not_found", new FakeGameLink().Pipeline().Call("undo", null, """{"id":"write-3"}""").Failed().Code);
    }

    // The store move a call picks when the game says each id's prefab.
    private static string Store(string options, params (string Id, string Prefab)[] things)
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "thing_health" && call.Params()["reference_id"] is { } asked
            ? FakeGameLink.Ok($$"""{"prefab_name":"{{things.Single(thing => thing.Id == (string)asked!).Prefab}}"}""")
            : FakeGameLink.Ok("{}");
        game.Pipeline().Call("move", "item", options).Done();
        return Assert.Single(game.Calls, call => call.Method != "thing_health").Method;
    }

    internal static readonly string[] HubReads = { "thing_health", "player_vitals" };

    internal static GameCall Sent(string verb, string? what, string options, string? handler = null,
        Face face = Face.Direct, IWriteGate? gate = null)
    {
        FakeGameLink game = new();
        game.Pipeline(gate).Call(verb, what, options, face).Done();
        // The hub's own reads around a write (the names a report shows) are not the call under test.
        List<GameCall> calls = game.Calls.Where(call => !HubReads.Contains(call.Method)).ToList();
        GameCall sent = Assert.Single(calls.Count > 0 ? calls : game.Calls.ToList());
        if (handler != null)
        {
            Assert.Equal(handler, sent.Method);
        }

        return sent;
    }
}
