#nullable enable

using System;
using System.IO;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;
using ModCatalogue = StationGodMCP.Pure.Catalogue.Catalogue;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>Lane prediction (scheduling.md, The prediction) and the call profile the catalogue gives.</summary>
public sealed class CostPredictorTests
{
    private const double Threshold = SchedulerSettings.DefaultHeavyThresholdMs;

    [Fact]
    public void AMethodWithNoHistoryIsLight()
    {
        CostPredictor predictor = new CostPredictor();

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read_devices", CostClass.Bounded, 128), Threshold));
        Assert.Null(predictor.PredictMs(SchedulerRig.Read("read_devices", CostClass.Bounded, 128)));
    }

    [Fact]
    public void ThePredictionIsTheAverageTimePerItemTimesTheItems()
    {
        CostPredictor predictor = new CostPredictor();
        predictor.Observe(SchedulerRig.Read("read_devices", CostClass.Bounded, 10), 0.3);
        predictor.Observe(SchedulerRig.Read("read_devices", CostClass.Bounded, 30), 0.5);

        double? predicted = predictor.PredictMs(SchedulerRig.Read("read_devices", CostClass.Bounded, 100));

        Assert.Equal(2.0, predicted!.Value, 9);
    }

    [Fact]
    public void AFiveItemReadIsLightAndA128ItemReadWithAHighPerItemAverageIsHeavy()
    {
        CostPredictor predictor = new CostPredictor();
        for (int index = 0; index < CostPredictor.Window; index++)
        {
            predictor.Observe(SchedulerRig.Read("read_devices", CostClass.Bounded, 20), 0.2);
        }

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read_devices", CostClass.Bounded, 5), Threshold));
        Assert.Equal(Lane.Heavy, predictor.LaneFor(SchedulerRig.Read("read_devices", CostClass.Bounded, 128), Threshold));
    }

    [Fact]
    public void APredictionExactlyAtTheThresholdIsLight()
    {
        CostPredictor predictor = new CostPredictor();
        predictor.Observe(SchedulerRig.Read("game_clock"), 1.0);

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("game_clock"), 1.0));
    }

    [Fact]
    public void TheAverageForgetsCallsOlderThanTheWindow()
    {
        CostPredictor predictor = new CostPredictor();
        CallProfile call = SchedulerRig.Read("list_devices", CostClass.Bounded);
        for (int index = 0; index < CostPredictor.Window; index++)
        {
            predictor.Observe(call, 50.0);
        }

        for (int index = 0; index < CostPredictor.Window; index++)
        {
            predictor.Observe(call, 0.1);
        }

        Assert.Equal(0.1, predictor.PredictMs(call)!.Value, 9);
        Assert.Equal(Lane.Light, predictor.LaneFor(call, Threshold));
    }

    [Fact]
    public void OneLargeCallDoesNotMoveTheWholeMethodToTheHeavyLane()
    {
        CostPredictor predictor = new CostPredictor();
        for (int index = 0; index < CostPredictor.Window - 1; index++)
        {
            predictor.Observe(SchedulerRig.Read("read_devices", CostClass.Bounded, 5), 0.05);
        }

        predictor.Observe(SchedulerRig.Read("read_devices", CostClass.Bounded, 128), 1.28);

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read_devices", CostClass.Bounded, 5), Threshold));
    }

    [Fact]
    public void MethodsAndCostClassesKeepSeparateHistories()
    {
        CostPredictor predictor = new CostPredictor();
        predictor.Observe(SchedulerRig.Read("thing_health", CostClass.Bounded, 1), 5.0);

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("thing_health", CostClass.Instant), Threshold));
        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read_logic", CostClass.Bounded), Threshold));
        Assert.Equal(Lane.Heavy, predictor.LaneFor(SchedulerRig.Read("thing_health", CostClass.Bounded), Threshold));
    }

    [Theory]
    [InlineData(nameof(CostClass.World))]
    [InlineData(nameof(CostClass.Plan))]
    [InlineData(nameof(CostClass.Job))]
    public void HeavyByClassIsHeavyHoweverCheapItWas(string costName)
    {
        CostClass cost = Enum.Parse<CostClass>(costName);
        CostPredictor predictor = new CostPredictor();
        CallProfile call = SchedulerRig.Read("scan", cost);
        for (int index = 0; index < CostPredictor.Window; index++)
        {
            predictor.Observe(call, 0.001);
        }

        Assert.Equal(Lane.Heavy, predictor.LaneFor(call, Threshold));
        Assert.Null(predictor.PredictMs(call));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    public void ATimeThatIsNotATimeIsIgnored(double elapsedMs)
    {
        CostPredictor predictor = new CostPredictor();

        predictor.Observe(SchedulerRig.Read("game_clock"), elapsedMs);

        Assert.Null(predictor.PredictMs(SchedulerRig.Read("game_clock")));
    }

    // ---- the profile from the catalogue ----

    [Theory]
    [InlineData("read_devices", """{"items": [{}, {}, {}, {}, {}]}""", nameof(CostClass.Bounded), 5, false)]
    [InlineData("thing_health", """{"reference_ids": ["1", "2", "3"]}""", nameof(CostClass.Bounded), 3, false)]
    [InlineData("thing_health", """{"reference_id": "1"}""", nameof(CostClass.Instant), 1, false)]
    [InlineData("thing_health", "{}", nameof(CostClass.World), 1, false)]
    [InlineData("grid_survey", "{}", nameof(CostClass.World), 1, false)]
    [InlineData("write_logic", "{}", nameof(CostClass.Instant), 1, true)]
    public void TheProfileComesFromTheCatalogueAtTheCallsArguments(string method, string arguments, string costName,
        int items, bool ordered)
    {
        CostClass cost = Enum.Parse<CostClass>(costName);
        Assert.True(Catalogue.Value.TryGet(method, out CatalogueMethod? found));

        CallProfile profile = CallProfile.Of(found!, JObject.Parse(arguments));

        Assert.Equal(method, profile.Method);
        Assert.Equal(cost, profile.Cost);
        Assert.Equal(items, profile.Items);
        Assert.Equal(ordered, profile.IsOrdered);
    }

    [Fact]
    public void AnEmptyPerItemListCountsAsOneItem()
    {
        Assert.Equal(1, new CallProfile("read_devices", MethodClass.Read, new CallCost(CostClass.Bounded, 0)).Items);
    }

    private static readonly Lazy<ModCatalogue> Catalogue =
        new Lazy<ModCatalogue>(() => ModCatalogue.Load(File.ReadAllText(CatalogueFiles.AssembledPath)));
}
