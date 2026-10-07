#nullable enable

using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Pure;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The settle gate (skip only when the game provably has nothing queued; unreadable queues settle), a connection's
/// per-method counts, the slow water_sources warning, and the three methods costed for the heavy lane.
/// </summary>
public sealed class PerfFollowUpTests
{
    [Fact]
    public void ASettleIsSkippedOnlyWhenNothingIsQueued()
    {
        Assert.True(SettleGate.MaySkip(QueuedGas.Of(0, 0, 0, false)));
        Assert.False(SettleGate.MaySkip(QueuedGas.Of(1, 0, 0, false)));
        Assert.False(SettleGate.MaySkip(QueuedGas.Of(0, 2, 0, false)));
        Assert.False(SettleGate.MaySkip(QueuedGas.Of(0, 0, 1, false)));
        Assert.False(SettleGate.MaySkip(QueuedGas.Of(0, 0, 0, true)));
    }

    [Fact]
    public void UnreadableQueuesAlwaysSettleAndAreCounted()
    {
        long run = SettleGate.Run;
        long skipped = SettleGate.Skipped;
        long unchecked_ = SettleGate.Unchecked;

        Assert.False(SettleGate.MaySkip(QueuedGas.Unreadable));
        Assert.False(SettleGate.Skip(QueuedGas.Unreadable));
        Assert.False(SettleGate.Skip(QueuedGas.Of(1, 0, 0, false)));
        Assert.True(SettleGate.Skip(QueuedGas.Of(0, 0, 0, false)));

        Assert.Equal(run + 2, SettleGate.Run);
        Assert.Equal(skipped + 1, SettleGate.Skipped);
        Assert.Equal(unchecked_ + 1, SettleGate.Unchecked);
    }

    [Fact]
    public void AConnectionListsItsMostCalledMethodsFirst()
    {
        ConnectionCalls calls = new ConnectionCalls();
        for (int index = 0; index < 3; index++)
        {
            calls.Record("game_clock", true, 0.5);
        }

        calls.Record("find_things", false, 40.0);
        calls.Record("find_things", true, 10.0);
        calls.Record("dish_aim", true, 2.0);
        calls.Record("atmosphere_contents", true, 1.0);

        MethodCallCount[] top = calls.Top(3).ToArray();

        Assert.Equal(4, calls.MethodCount);
        Assert.Equal(new[] { "game_clock", "find_things", "atmosphere_contents" }, top.Select(count => count.Method));
        Assert.Equal(3, top[0].Calls);
        Assert.Equal(1.5, top[0].TotalMs);
        Assert.Equal(1, top[1].Errors);
        Assert.Equal(50.0, top[1].TotalMs);
    }

    [Fact]
    public void AConnectionViewCarriesItsMethodsAndSubscriptions()
    {
        ConnectionCalls calls = new ConnectionCalls();
        calls.Record("read_devices", true, 1.234);
        calls.Record("thing_health", false, 2.0);

        string json = StationGodMCP.Api.Shared.ApiJson.WriteFresh(new StationGodMCP.Api.Views.ConnectionView("c2",
            "dash", "stationgod-py", "tcp", 2, 1, 2, 300, calls, 4));

        Assert.EndsWith(
            "\"subscriptions\":4,\"methods\":[{\"method\":\"read_devices\",\"calls\":1,\"errors\":0,\"total_ms\":1.23}," +
            "{\"method\":\"thing_health\",\"calls\":1,\"errors\":1,\"total_ms\":2.0}],\"method_count\":2}", json);
    }

    [Fact]
    public void ASlowWaterSourcesCallIsWarnedAboutOncePerMinuteWithItsSplit()
    {
        WaterSourcesWarning warning = new WaterSourcesWarning();
        WaterSourcesTiming quick = new WaterSourcesTiming(30, 1, 2, 0, 1, 20, 5, 900, 12, 0);
        WaterSourcesTiming slow = new WaterSourcesTiming(7328.5, 7300.25, 7301, 0.5, 2, 20, 5.5, 900, 12, 1);

        Assert.Null(warning.Check(10, in quick));
        Assert.Equal(
            "water_sources held the main thread 7328.5 ms: atmosphere list 7301.0 ms (lock wait 7300.3 ms, 900 " +
            "atmospheres), pipe network list 2.0 ms (lock wait 0.5 ms), scan 20.0 ms, rows 5.5 ms (12 sources), 1 " +
            "garbage collection(s) during it.", warning.Check(11, in slow));
        Assert.Null(warning.Check(30, in slow));
        Assert.EndsWith("; 1 slow call(s) not logged before it.", warning.Check(80, in slow));
    }

    [Theory]
    [InlineData("run_console_command", "world")]
    [InlineData("rocket_status", "world")]
    [InlineData("dish_aim", "plan")]
    public void SlowMethodsAreCostedForTheHeavyLane(string method, string cost)
    {
        JsonObject entry = CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath)["methods"]!.AsArray()
            .Select(node => node!.AsObject()).Single(node => (string?)node["name"] == method);

        Assert.Equal(cost, (string?)entry["cost"]);
        Assert.True(StationGodMCP.Pure.Scheduling.CostPredictor.IsHeavyByClass(
            cost == "world" ? StationGodMCP.Pure.Catalogue.CostClass.World : StationGodMCP.Pure.Catalogue.CostClass.Plan));
    }
}
