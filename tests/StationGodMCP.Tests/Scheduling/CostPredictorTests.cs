#nullable enable

using System;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>Lane prediction (scheduling.md, The prediction) and the call profile the catalogue gives.</summary>
public sealed class CostPredictorTests
{
    private const double Threshold = SchedulerSettings.DefaultHeavyThresholdMs;

    [Fact]
    public void AMethodWithNoHistoryIsLight()
    {
        CostPredictor predictor = new CostPredictor();

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read.sample", CostClass.Bounded, 128), Threshold));
        Assert.Null(predictor.PredictMs(SchedulerRig.Read("read.sample", CostClass.Bounded, 128)));
    }

    [Fact]
    public void ThePredictionIsTheAverageTimePerItemTimesTheItems()
    {
        CostPredictor predictor = new CostPredictor();
        predictor.Observe(SchedulerRig.Read("read.sample", CostClass.Bounded, 10), 0.3);
        predictor.Observe(SchedulerRig.Read("read.sample", CostClass.Bounded, 30), 0.5);

        double? predicted = predictor.PredictMs(SchedulerRig.Read("read.sample", CostClass.Bounded, 100));

        Assert.Equal(2.0, predicted!.Value, 9);
    }

    [Fact]
    public void AFiveItemReadIsLightAndA128ItemReadWithAHighPerItemAverageIsHeavy()
    {
        CostPredictor predictor = new CostPredictor();
        for (int index = 0; index < CostPredictor.Window; index++)
        {
            predictor.Observe(SchedulerRig.Read("read.sample", CostClass.Bounded, 20), 0.2);
        }

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read.sample", CostClass.Bounded, 5), Threshold));
        Assert.Equal(Lane.Heavy, predictor.LaneFor(SchedulerRig.Read("read.sample", CostClass.Bounded, 128), Threshold));
    }

    [Fact]
    public void APredictionExactlyAtTheThresholdIsLight()
    {
        CostPredictor predictor = new CostPredictor();
        predictor.Observe(SchedulerRig.Read("read.clock"), 1.0);

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read.clock"), 1.0));
    }

    [Fact]
    public void TheAverageForgetsCallsOlderThanTheWindow()
    {
        CostPredictor predictor = new CostPredictor();
        CallProfile call = SchedulerRig.Read("find.device", CostClass.Bounded);
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
            predictor.Observe(SchedulerRig.Read("read.sample", CostClass.Bounded, 5), 0.05);
        }

        predictor.Observe(SchedulerRig.Read("read.sample", CostClass.Bounded, 128), 1.28);

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read.sample", CostClass.Bounded, 5), Threshold));
    }

    [Fact]
    public void MethodsAndCostClassesKeepSeparateHistories()
    {
        CostPredictor predictor = new CostPredictor();
        predictor.Observe(SchedulerRig.Read("inspect.health", CostClass.Bounded, 1), 5.0);

        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("inspect.health", CostClass.Instant), Threshold));
        Assert.Equal(Lane.Light, predictor.LaneFor(SchedulerRig.Read("read.logic", CostClass.Bounded), Threshold));
        Assert.Equal(Lane.Heavy, predictor.LaneFor(SchedulerRig.Read("inspect.health", CostClass.Bounded), Threshold));
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

        predictor.Observe(SchedulerRig.Read("read.clock"), elapsedMs);

        Assert.Null(predictor.PredictMs(SchedulerRig.Read("read.clock")));
    }

    // ---- the profile from the verb model ----

    [Theory]
    [InlineData("find", "thing", "{}", nameof(CostClass.World), 1, false)]
    [InlineData("read", "logic", """{"id": ["1", "2", "3"]}""", nameof(CostClass.Bounded), 3, false)]
    [InlineData("read", "logic", """{"id": "1"}""", nameof(CostClass.Bounded), 1, false)]
    [InlineData("read", "clock", "{}", nameof(CostClass.Instant), 1, false)]
    [InlineData("write", "logic", """{"id": "1", "writes": [{}, {}]}""", nameof(CostClass.Bounded), 2, true)]
    public void TheProfileComesFromTheVerbModelAtTheCallsArguments(string verbName, string whatName, string options,
        string costName, int items, bool ordered)
    {
        CostClass cost = Enum.Parse<CostClass>(costName);
        VerbSpec verb = VerbSurface.Verb(verbName)!;
        WhatSpec what = verb.What(whatName)!;

        CallProfile profile = CallProfile.Of(verb, what, JObject.Parse(options));

        Assert.Equal(what.Key, profile.Method);
        Assert.Equal(cost, profile.Cost);
        Assert.Equal(items, profile.Items);
        Assert.Equal(ordered, profile.IsOrdered);
    }

    [Fact]
    public void AnEmptyPerItemListCountsAsOneItem()
    {
        Assert.Equal(1, new CallProfile("read.logic", MethodClass.Read, new CallCost(CostClass.Bounded, 0)).Items);
    }
}
