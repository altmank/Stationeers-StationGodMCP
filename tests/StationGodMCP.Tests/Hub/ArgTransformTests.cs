#nullable enable

using System.Text.Json.Nodes;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;
using static StationGodMCP.Tests.Hub.HandlerSelectorTests;

namespace StationGodMCP.Tests.Hub;

/// <summary>Each merged and moved transform of the argument tables, as its note says.</summary>
public sealed class ArgTransformTests
{
    private static JsonObject Params(string verb, string? what, string options) => Sent(verb, what, options).Params();

    [Fact]
    public void BoxBecomesMinAndMax()
    {
        Same("""{"min":[1,2,3],"max":[4,5,6]}""", Params("find", "thing", """{"box":[[1,2,3],[4,5,6]]}"""));
    }

    [Fact]
    public void APrefabListBecomesPrefabsAndAPartBecomesPrefabContains()
    {
        Same("""{"prefabs":["StructureTankBig","StructureTankSmall"]}""",
            Params("find", "thing", """{"prefab":["StructureTankBig","StructureTankSmall"]}"""));
        Same("""{"prefab_contains":"Battery"}""", Params("find", "thing", """{"prefab":"*Battery*"}"""));
        Same("""{"prefab":"StructureTankBig"}""", Params("find", "thing", """{"prefab":["StructureTankBig"]}"""));
        Assert.Equal("prefab: takes one part with * here ('*Battery*')", Assert.Single(new FakeGameLink().Pipeline()
            .Call("find", "thing", """{"prefab":"*Tank*Insulated*"}""").Failed().Problems()));
    }

    [Fact]
    public void APrefabNameIsResolvedToItsHashWhereTheHandlerTakesOnlyAHash()
    {
        Same($$"""{"prefab_hash":{{LintChipPrograms.HashOf("StructureBatteryLarge")}}}""",
            Params("read", "snapshot", """{"prefab":"StructureBatteryLarge"}"""));
        Same("""{"prefab_hash":-400115994}""", Params("read", "snapshot", """{"prefab":-400115994}"""));
        Assert.Equal("prefab: a hash is not taken here; give the prefab name",
            Assert.Single(new FakeGameLink().Pipeline().Call("find", "thing", """{"prefab":5}""").Failed().Problems()));
    }

    [Fact]
    public void FindDevicePagesAndFiltersPartsOnTheWholeList()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""
            {"devices":[{"reference_id":"1","prefab_name":"StructureGasSensor"},{"reference_id":"2","prefab_name":"StructureVent"},
            {"reference_id":"3","prefab_name":"StructureGasSensorLarge"},{"reference_id":"4","prefab_name":"StructureGasSensor"}],"count":4}
            """);

        JsonNode? page = game.Pipeline().Call("find", "device", """{"prefab":"*gassensor*","offset":1,"limit":1}""").Done();

        Assert.Equal("{}", game.Single("list_devices").Params().ToJsonString());
        Assert.Equal("""[{"reference_id":"3","prefab_name":"StructureGasSensorLarge"}]""", page!["devices"]!.ToJsonString());
        Assert.Equal(3, (int)page["total"]!);
        Assert.True((bool)page["has_more"]!);
    }

    [Fact]
    public void AStorePrefabNameOrHashFoldsIntoPrefab()
    {
        FakeGameLink game = new();
        game.Answer = call => FakeGameLink.Ok(call.Method == "thing_health" ? """{"prefab_name":"StructureIngotVault"}""" : "{}");

        game.Pipeline().Call("move", "item", """{"source":"20","prefab":"ItemIronIngot","quantity":5}""").Done();
        game.Pipeline().Call("move", "item", """{"source":"20","prefab":-1301215609,"quantity":5}""").Done();

        Assert.Equal("ItemIronIngot", (string)game.CallsTo("vault_withdraw")[0].Params()["prefab_name"]!);
        Assert.Equal(-1301215609, (int)game.CallsTo("vault_withdraw")[1].Params()["prefab_hash"]!);
    }

    [Fact]
    public void NearPlayerWithRadiusBecomesNearPlayerM()
    {
        Same("""{"near_player_m":30}""", Params("find", "thing", """{"near":"player","radius":30}"""));
        Same("""{"near":[1,2,3],"radius_m":5}""", Params("find", "thing", """{"near":[1,2,3],"radius":5}"""));
        Same("""{"near_player_m":20}""", Params("find", "frame", """{"near":"player","radius":20}"""));
        Assert.Equal("near: takes only \"player\" (with radius) here", Assert.Single(new FakeGameLink().Pipeline()
            .Call("find", "frame", """{"near":[1,2,3],"radius":5}""").Failed().Problems()));
        Assert.Equal("radius: required with near: \"player\"", Assert.Single(new FakeGameLink().Pipeline()
            .Call("find", "frame", """{"near":"player"}""").Failed().Problems()));
    }

    [Fact]
    public void IncludeAllowAndAvoidWordsSetTheHandlersSwitches()
    {
        Same("""{"include_inner":true}""", Params("find", "frame", """{"include":["inner"]}"""));
        Same("{}", Params("find", "frame", """{"include":[]}"""));
        Same("""{"dry_run":true,"from":"5","force":true}""", Params("move", "gas", """{"source":"5","allow":["burst"]}"""));
        Same("""{"genes":{"Growth":2},"force":true,"dry_run":true,"reference_id":"3"}""",
            Params("write", "genes", """{"id":"3","genes":{"Growth":2},"allow":["out_of_range"]}"""));
        Same("""{"avoid_walkways":true,"allow_split":true,"include_notes":true}""",
            Params("plan", "route", """{"kind":"cable","avoid":["walkways"],"allow":["split"],"include":["notes"]}"""));
        Same("""{"reference_id":"4","image":true,"include_style":true}""",
            Params("inspect", "screen", """{"id":"4","include":["image","style"]}"""));
    }

    [Fact]
    public void WhatIfKeysBecomeTheirOwnArguments()
    {
        Same("""{"rocket_id":"4","fuel_mol":120,"throttle":0.5}""",
            Params("plan", "flight", """{"id":"4","what_if":{"fuel_mol":120,"throttle":0.5}}"""));
    }

    [Fact]
    public void ConfirmIsTheInverseOfDryRunAndQueueFillsWait()
    {
        Same("""{"dry_run":true,"reference_ids":["5"],"wait":true}""", Params("remove", "cable", """{"id":["5"]}"""));
        Same("""{"dry_run":false,"reference_ids":["5"],"wait":false,"confirm":true}""",
            Params("remove", "cable", """{"id":["5"],"confirm":true,"queue":false}"""));
        Same("""{"dry_run":true,"near_player":"xceled"}""", Params("move", "player", """{"to_player":"xceled"}"""));
    }

    [Fact]
    public void RefundToCarriesRefundAndNoneIsSentAsGiven()
    {
        Same("""{"dry_run":true,"reference_ids":["5"],"wait":true,"refund_to":"none"}""",
            Params("remove", "cable", """{"id":["5"],"refund_to":"none"}"""));
    }

    [Fact]
    public void MovedOptionsGoToTheirArguments()
    {
        Same("""{"min_damage_ratio":0.25,"structures_only":true}""",
            Params("find", "damaged", """{"min_damage":0.25,"structures_only":true}"""));
        Same("""{"network_id":"5","prefab_contains":"Vent","open_ends_only":true,"min":[0,0,0],"max":[1,1,1]}""",
            Params("survey", "network", """{"network":"5","prefab":"*Vent*","open_ends":true,"box":[[0,0,0],[1,1,1]]}"""));
        Same("""{"action":"start","interval_s":2,"rocket_id":"Tanker"}""",
            Params("write", "flight_log", """{"action":"start","id":"Tanker","interval_s":2}"""));
    }

    [Fact]
    public void KeysInsideObjectsAndListEntriesTakeTheHandlersNames()
    {
        Same("""{"labels":[{"reference_id":"1","name":"Tank"}]}""",
            Params("write", "label", """{"labels":[{"id":"1","label":"Tank"}]}"""));
        Same("""{"targets":[{"reference_ids":["1","2"],"color":"Red"},{"network_id":"9"}]}""",
            Params("show", "highlight", """{"targets":[{"id":["1","2"],"color":"Red"},{"network":"9"}]}"""));
        Same("""{"from":{"reference_id":"7","port":1},"to":{"at":[1,2,3]}}""",
            Params("plan", "route", """{"kind":"cable","start":{"id":"7","port":1},"end":{"at":[1,2,3]}}"""));
        Same("""{"reference_id":"1","pins":{"d0":"9","d1":null}}""",
            Params("write", "chip", """{"id":"1","pins":{"d0":"9","d1":null}}"""));
    }

    [Fact]
    public void ADroppedOldNameIsNeverSent()
    {
        Same("""{"min_mol":5}""", Params("find", "water", """{"min_mol":5}"""));
    }

    [Fact]
    public void OneIdGoesToTheOneIdArgumentAndAListToTheListArgument()
    {
        Same("""{"reference_id":"5"}""", Params("inspect", "health", """{"id":"5"}"""));
        Same("""{"reference_ids":["5","6"]}""", Params("inspect", "health", """{"id":["5","6"]}"""));
        Same("""{"reference_ids":["5"],"color":"Red"}""", Params("write", "paint", """{"id":"5","color":"Red"}"""));
    }
}
