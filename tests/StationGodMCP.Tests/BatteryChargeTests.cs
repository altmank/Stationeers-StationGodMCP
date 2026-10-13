#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>write battery's goal rule, its reply and its report.</summary>
public sealed class BatteryChargeTests
{
    private const double CellCapacity = 36000.0;

    [Fact]
    public void NoRatioOrJoulesFillsToTheWholeCapacity()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        ChargeGoal goal = ChargeGoal.Of(null, null, problems)!;
        ChargeChange change = goal.Apply(1200.0, CellCapacity);

        Assert.Empty(problems);
        Assert.Equal("full", goal.Kind);
        Assert.Null(goal.Amount);
        Assert.Equal(CellCapacity, change.After);
        Assert.Equal(CellCapacity - 1200.0, change.AddedJoules);
        Assert.False(change.Clamped);
    }

    [Fact]
    public void ARatioIsAShareOfEachCapacityAndCanDrain()
    {
        ChargeGoal goal = ChargeGoal.Of(0.25, null, new List<OptionProblem>())!;

        Assert.Equal(9000.0, goal.Apply(CellCapacity, CellCapacity).After);
        Assert.Equal(900000.0, goal.Apply(0.0, 3600000.0).After);
        Assert.Equal(-27000.0, goal.Apply(CellCapacity, CellCapacity).AddedJoules);
        Assert.Equal(0.0, ChargeGoal.Of(0.0, null, new List<OptionProblem>())!.Apply(500.0, CellCapacity).After);
    }

    [Fact]
    public void JoulesOverACapacityFillItAndSayClamped()
    {
        ChargeGoal goal = ChargeGoal.Of(null, 50000.0, new List<OptionProblem>())!;

        ChargeChange cell = goal.Apply(0.0, CellCapacity);
        Assert.Equal(CellCapacity, cell.After);
        Assert.True(cell.Clamped);

        ChargeChange station = goal.Apply(0.0, 3600000.0);
        Assert.Equal(50000.0, station.After);
        Assert.False(station.Clamped);
    }

    [Theory]
    [InlineData(0.5, 10.0, "goes without ratio")]
    [InlineData(1.5, null, "from 0 to 1")]
    [InlineData(-0.1, null, "from 0 to 1")]
    [InlineData(null, -1.0, "0 or more")]
    public void ImpossibleGoalsAreRefusedWithTheReason(double? ratio, double? joules, string reason)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(ChargeGoal.Of(ratio, joules, problems));
        Assert.Contains(reason, Assert.Single(problems).Problem);
    }

    [Fact]
    public void AZeroCapacityHasNoRatio()
    {
        Assert.Null(ChargeChange.RatioOf(0.0, 0.0));
        Assert.Equal(0.5, ChargeChange.RatioOf(18000.0, CellCapacity));
    }

    [Fact]
    public void TheReplyNamesEachBatteryItsHolderAndItsChargeBeforeAndAfter()
    {
        ChargeGoal goal = ChargeGoal.Of(null, 50000.0, new List<OptionProblem>())!;
        BatchBuilder batch = new BatchBuilder(2);
        ThingView cell = new ThingView(new ThingId(101), "ItemBatteryCell", "Battery Cell (Small)");
        ThingView suit = new ThingView(new ThingId(100), "ItemHardSuit", "Hardsuit");
        batch.Succeeded(new BatteryChargedView(0, cell, PowerStoreKind.BatteryCell, suit,
            goal.Apply(12345.67, CellCapacity)));
        batch.Failed(new BatteryRefusedView(1, new ThingId(102),
            ApiErrors.Refused("not_a_battery", "Wall 102 stores no power.")));

        JObject reply = JObject.Parse(WireCheck.New(new SetBatteryChargeView(goal, null, batch.Build(),
            CellCapacity - 12345.67)));

        Assert.Equal("joules", (string?)reply["goal"]!["kind"]);
        Assert.Equal(50000.0, (double)reply["goal"]!["value"]!);
        Assert.Null(reply["found_in"]);
        Assert.Equal(1, (int)reply["success_count"]!);
        Assert.Equal(1, (int)reply["error_count"]!);
        Assert.Equal(23654.3, (double)reply["added_j"]!);

        JObject done = (JObject)reply["results"]![0]!;
        Assert.Equal(new List<string>
        {
            "index", "ok", "reference_id", "prefab_name", "display_name", "kind", "held_by", "capacity_j", "before_j",
            "after_j", "before_ratio", "after_ratio", "clamped"
        }, Names(done));
        Assert.Equal("battery_cell", (string?)done["kind"]);
        Assert.Equal("100", (string?)done["held_by"]!["reference_id"]);
        Assert.Equal(12345.7, (double)done["before_j"]!);
        Assert.Equal(0.3429, (double)done["before_ratio"]!);
        Assert.Equal(1.0, (double)done["after_ratio"]!);

        JObject refused = (JObject)reply["results"]![1]!;
        Assert.False((bool)refused["ok"]!);
        Assert.Equal("102", (string?)refused["reference_id"]);
        Assert.Equal("not_a_battery", (string?)refused["error"]!["code"]);
    }

    [Fact]
    public void ALooseFullBatteryLeavesOutHolderAndClamped()
    {
        BatchBuilder batch = new BatchBuilder(1);
        batch.Succeeded(new BatteryChargedView(0, new ThingView(new ThingId(7), "StructureBattery", "Station Battery"),
            PowerStoreKind.StationBattery, null, ChargeGoal.Full.Apply(0.0, 3600000.0)));
        ThingView rocket = new ThingView(new ThingId(9), null, "Rocket");

        JObject reply = JObject.Parse(WireCheck.New(new SetBatteryChargeView(ChargeGoal.Full, rocket,
            batch.Build(), 3600000.0)));

        JObject result = (JObject)reply["results"]![0]!;
        Assert.Null(result["held_by"]);
        Assert.Null(result["clamped"]);
        Assert.Equal("station_battery", (string?)result["kind"]);
        Assert.Null(reply["goal"]!["value"]);
        Assert.Equal("9", (string?)reply["found_in"]!["reference_id"]);
    }

    [Fact]
    public void TheReportListsEachBatteryAsATargetAndEachRefusalAsAProblem()
    {
        ThingView cell = new ThingView(new ThingId(101), "ItemBatteryCell", "Battery Cell (Small)");
        List<BatteryResult> results = new List<BatteryResult>
        {
            new BatteryResult.Charged(0, cell, PowerStoreKind.BatteryCell, null,
                ChargeGoal.Full.Apply(12345.67, CellCapacity)),
            new BatteryResult.Refused(1, new ThingId(102), ApiErrors.Refused("not_a_battery", "Wall 102 stores no power."))
        };

        JObject report = BatteryReplies.Reported(ChargeGoal.Full, null, results, "[undo]");

        Assert.Equal(new List<string>
        {
            "goal", "count", "success_count", "error_count", "added_j", "problems", "report_only", "targets"
        }, Names(report));
        Assert.Equal("[101, Battery Cell (Small), 12345.7 -> 36000] [undo]", (string?)report["targets"]![0]!["text"]);
        Assert.Equal(1, (int)report["problems"]![0]!["index"]!);
        Assert.Equal("102", (string?)report["problems"]![0]!["reference_id"]);
        Assert.Equal("not_a_battery", (string?)report["problems"]![0]!["code"]);
    }

    [Fact]
    public void TheActedReplyHasNoReportKeys()
    {
        ThingView cell = new ThingView(new ThingId(101), "ItemBatteryCell", "Battery Cell (Small)");
        JObject reply = BatteryReplies.Acted(ChargeGoal.Full, null, new List<BatteryResult>
        {
            new BatteryResult.Charged(0, cell, PowerStoreKind.BatteryCell, null, ChargeGoal.Full.Apply(0.0, CellCapacity))
        });

        Assert.Equal(new List<string> { "goal", "results", "count", "success_count", "error_count", "added_j" },
            Names(reply));
        Assert.Equal(36000.0, (double)reply["added_j"]!);
    }

    private static List<string> Names(JObject entry)
    {
        List<string> names = new List<string>();
        foreach (JProperty property in entry.Properties())
        {
            names.Add(property.Name);
        }

        return names;
    }
}
