#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;
using Xunit;
using Connection = StationGodMCP.Pure.Scheduling.FrameScheduler<StationGodMCP.Tests.Scheduling.FakeCall>.Connection;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>The frame scheduler's rules (scheduling.md, One frame, step by step; How it is tested).</summary>
public sealed class FrameSchedulerTests
{
    // ---- the light lane and the budget ----

    [Fact]
    public void FirstLightCallAlwaysRunsEvenWhenItAloneIsOverBudget()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "slow", costMs: 10.0);
        rig.Light(a, "next", costMs: 0.1);

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(new[] { "slow" }, rig.Runner.RanNames());
        Assert.Equal(1, outcome.LightCalls);
        Assert.True(outcome.BudgetStopped);
    }

    [Fact]
    public void LightCallsStopOnceTheFrameHasSpentItsBudget()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < 10; index++)
        {
            rig.Light(a, $"r{index}", costMs: 1.0);
        }

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(4, outcome.LightCalls);
        Assert.Equal(4.0, outcome.SpentMs);
        Assert.True(outcome.BudgetStopped);
    }

    [Fact]
    public void ALightCallStartsWhileTheFrameIsUnderBudgetEvenIfItWillEndOver()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "r1", costMs: 3.9);
        rig.Light(a, "r2", costMs: 0.5);
        rig.Light(a, "r3", costMs: 0.5);

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(new[] { "r1", "r2" }, rig.Runner.RanNames());
        Assert.Equal(4.4, outcome.SpentMs, 9);
    }

    [Fact]
    public void QueueDrainingUnderBudgetIsNotABudgetStop()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "r1");
        rig.Light(a, "r2");

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(2, outcome.LightCalls);
        Assert.False(outcome.BudgetStopped);
    }

    [Fact]
    public void WhileAJobHoldsTheTickTheBudgetIsTwoMilliseconds()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < 10; index++)
        {
            rig.Light(a, $"r{index}", costMs: 0.5);
        }

        FrameOutcome outcome = rig.Frame(jobHoldsTick: true);

        Assert.Equal(4, outcome.LightCalls);
        Assert.Equal(2.0, outcome.SpentMs);
    }

    [Fact]
    public void AnUnlimitedBudgetStillRunsAtMost64LightCallsAFrame()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        for (int index = 0; index < 70; index++)
        {
            rig.Light(rig.Scheduler.Open(), $"r{index}");
        }

        FrameOutcome first = rig.Frame();
        FrameOutcome second = rig.Frame();

        Assert.Equal(SchedulerSettings.MaxLightCallsPerFrame, first.LightCalls);
        Assert.True(first.BudgetStopped);
        Assert.Equal(6, second.LightCalls);
    }

    [Fact]
    public void AnEmptySchedulerRunsNothingAndSpendsNothing()
    {
        SchedulerRig rig = new SchedulerRig();

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(0, outcome.Served);
        Assert.Equal(0.0, outcome.SpentMs);
        Assert.False(outcome.BudgetStopped);
        Assert.Equal(1, rig.Scheduler.Frame);
    }

    // ---- the heavy lane ----

    [Fact]
    public void AtMostOneHeavyCallRunsPerFrameEvenWithAnUnlimitedBudget()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        rig.Add(a, "h1", SchedulerRig.Heavy());
        rig.Add(a, "h2", SchedulerRig.Heavy());
        rig.Add(b, "h3", SchedulerRig.Heavy());

        int[] perFrame = { rig.Frame().HeavyCalls, rig.Frame().HeavyCalls, rig.Frame().HeavyCalls, rig.Frame().HeavyCalls };

        Assert.Equal(new[] { 1, 1, 1, 0 }, perFrame);
    }

    [Fact]
    public void HeavyCallsRunAfterTheLightCallsOfTheirFrame()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Add(a, "survey", SchedulerRig.Heavy(), costMs: 20.0);
        rig.Light(a, "read");

        rig.Frame();

        Assert.Equal(new[] { "read", "survey" }, rig.Runner.RanNames());
    }

    [Fact]
    public void AHeavyCallWaitsWhileTheFrameIsOverBudget()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "busy", costMs: 5.0);
        rig.Add(a, "survey", SchedulerRig.Heavy());

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(0, outcome.HeavyCalls);
        Assert.True(outcome.BudgetStopped);
    }

    [Fact]
    public void AHeavyCallPassedOverForTenFramesRunsInTheEleventhEvenOverBudget()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection busy = rig.Scheduler.Open();
        Connection agent = rig.Scheduler.Open();
        rig.Add(agent, "survey", SchedulerRig.Heavy(), costMs: 30.0);

        for (int frame = 1; frame <= 12; frame++)
        {
            rig.Busy(busy, frame, 5.0);
            rig.Frame();
        }

        Assert.Equal(11, FindRan(rig, "survey").RanInFrame);
    }

    [Fact]
    public void HeavyCallsUnderAlwaysBusyLightWorkRunOldestFirstOnePerFrameFromTheBound()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection busy = rig.Scheduler.Open();
        Connection[] agents = { rig.Scheduler.Open(), rig.Scheduler.Open(), rig.Scheduler.Open() };
        rig.Add(agents[2], "h0", SchedulerRig.Heavy());
        rig.Add(agents[0], "h1", SchedulerRig.Heavy());
        rig.Add(agents[1], "h2", SchedulerRig.Heavy());
        rig.Add(agents[0], "h3", SchedulerRig.Heavy());

        List<long> heavyFrames = new List<long>();
        for (int frame = 1; frame <= 20; frame++)
        {
            rig.Busy(busy, frame, 5.0);
            if (rig.Frame().HeavyCalls == 1)
            {
                heavyFrames.Add(frame);
            }
        }

        Assert.Equal(new long[] { 11, 12, 13, 14 }, heavyFrames);
        Assert.Equal(new[] { "h0", "h1", "h2", "h3" }, HeavyNames(rig));
    }

    [Fact]
    public void NoHeavyCallWaitsMoreThanTenFramesWhenTheyArriveOneAtATime()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection busy = rig.Scheduler.Open();
        Connection agent = rig.Scheduler.Open();
        List<FakeCall> heavy = new List<FakeCall>();
        Dictionary<FakeCall, long> arrived = new Dictionary<FakeCall, long>();

        for (int frame = 1; frame <= 200; frame++)
        {
            if (frame % 5 == 0 && frame <= 150)
            {
                FakeCall call = new FakeCall($"h{frame}", 15.0);
                rig.Scheduler.Enqueue(agent, call, SchedulerRig.Heavy(), double.PositiveInfinity);
                heavy.Add(call);
                arrived[call] = rig.Scheduler.Frame;
            }

            rig.Busy(busy, frame, 6.0);
            rig.Frame();
        }

        foreach (FakeCall call in heavy)
        {
            Assert.True(call.RanInFrame > 0, $"{call.Name} never ran");
            Assert.True(call.RanInFrame - arrived[call] - 1 <= SchedulerSettings.DefaultHeavyMaxWaitFrames,
                $"{call.Name} waited {call.RanInFrame - arrived[call] - 1} frames");
        }
    }

    [Fact]
    public void HeavyCallsTakeTurnsAcrossConnectionsWhenTheFrameHasRoom()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        rig.Add(a, "a1", SchedulerRig.Heavy());
        rig.Add(a, "a2", SchedulerRig.Heavy());
        rig.Add(a, "a3", SchedulerRig.Heavy());
        rig.Add(b, "b1", SchedulerRig.Heavy());

        for (int frame = 0; frame < 4; frame++)
        {
            rig.Frame();
        }

        Assert.Equal(new[] { "a1", "b1", "a2", "a3" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ACheapWorldScanStaysInTheHeavyLane()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < 40; index++)
        {
            rig.Add(a, $"scan{index}", SchedulerRig.Heavy(), costMs: 0.001);
            rig.Frame();
        }

        Assert.Equal(Lane.Heavy, rig.Add(a, "scan", SchedulerRig.Heavy(), costMs: 0.001).Lane);
    }

    [Theory]
    [InlineData(nameof(CostClass.World))]
    [InlineData(nameof(CostClass.Plan))]
    [InlineData(nameof(CostClass.Job))]
    public void WorldPlanAndJobCostsAreHeavyOnArrival(string costName)
    {
        CostClass cost = System.Enum.Parse<CostClass>(costName);
        SchedulerRig rig = new SchedulerRig();

        Admission admission = rig.Add(rig.Scheduler.Open(), "call", SchedulerRig.Read("m", cost));

        Assert.Equal(Lane.Heavy, admission.Lane);
    }

    // ---- round-robin ----

    [Fact]
    public void AConnectionWithOneCallIsServedInTheFirstRoundBesideOneWithSixteen()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.6));
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        for (int index = 1; index <= 16; index++)
        {
            rig.Light(a, $"a{index}", costMs: 0.2);
        }

        rig.Light(b, "b1", costMs: 0.2);

        rig.Frame();

        Assert.Equal(new[] { "a1", "b1", "a2" }, rig.Runner.RanNames());
    }

    [Fact]
    public void TurnsCarryOverFromFrameToFrame()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        for (int index = 1; index <= 3; index++)
        {
            rig.Light(a, $"a{index}", costMs: 5.0);
            rig.Light(b, $"b{index}", costMs: 5.0);
        }

        for (int frame = 0; frame < 6; frame++)
        {
            rig.Frame();
        }

        Assert.Equal(new[] { "a1", "b1", "a2", "b2", "a3", "b3" }, rig.Runner.RanNames());
    }

    [Fact]
    public void WithAnUnlimitedBudgetSingleCallConnectionsAreServedFirstInFirstOut()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        Connection c = rig.Scheduler.Open();

        rig.Light(c, "c1");
        rig.Light(a, "a1");
        rig.Light(b, "b1");
        rig.Frame();
        rig.Light(b, "b2");
        rig.Light(c, "c2");
        rig.Light(a, "a2");
        rig.Frame();

        Assert.Equal(new[] { "c1", "a1", "b1", "b2", "c2", "a2" }, rig.Runner.RanNames());
    }

    // ---- order on one connection ----

    [Fact]
    public void AReadSentAfterAWriteNeverStartsBeforeTheWriteIsAnswered()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        rig.Add(a, "survey", SchedulerRig.Heavy());
        rig.Add(a, "write", SchedulerRig.Write());
        rig.Light(a, "read");

        rig.Frame();
        List<string> afterFirst = rig.Runner.RanNames();
        rig.Frame();

        Assert.Equal(new[] { "survey" }, afterFirst);
        Assert.Equal(new[] { "survey", "write", "read" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ReadsOnOneConnectionMayPassEachOther()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Add(a, "survey", SchedulerRig.Heavy());
        rig.Light(a, "read");

        rig.Frame();

        Assert.Equal(new[] { "read", "survey" }, rig.Runner.RanNames());
    }

    [Fact]
    public void AWriteWaitsForEveryEarlierCallOnItsConnection()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        rig.Add(a, "survey", SchedulerRig.Heavy());
        rig.Light(a, "read");
        rig.Add(a, "write", SchedulerRig.Write());

        rig.Frame();
        rig.Frame();

        Assert.Equal(new[] { "read", "survey", "write" }, rig.Runner.RanNames());
        Assert.Equal(2, FindRan(rig, "write").RanInFrame);
    }

    [Fact]
    public void AHeavyWriteHoldsBackItsConnectionsLaterLightCalls()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        Connection busy = rig.Scheduler.Open();
        rig.Add(a, "build", SchedulerRig.Write("build_structure", CostClass.Job));
        rig.Light(a, "read");

        for (int frame = 1; frame <= 12; frame++)
        {
            rig.Busy(busy, frame, 5.0);
            rig.Frame();
        }

        Assert.Equal(11, FindRan(rig, "build").RanInFrame);
        Assert.Equal(12, FindRan(rig, "read").RanInFrame);
    }

    [Fact]
    public void ACheatCallIsOrderedLikeAWrite()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        CallProfile cheat = new CallProfile("spawn_item", MethodClass.Cheat, new CallCost(CostClass.Instant, 1));
        rig.Add(a, "survey", SchedulerRig.Heavy());
        rig.Add(a, "cheat", cheat);
        rig.Light(a, "read");

        rig.Frame();

        Assert.Equal(new[] { "survey" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ABlockedConnectionDoesNotHoldBackOthers()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        rig.Add(a, "survey", SchedulerRig.Heavy());
        rig.Add(a, "write", SchedulerRig.Write());
        rig.Light(b, "b1");
        rig.Light(b, "b2");

        rig.Frame();

        Assert.Equal(new[] { "b1", "b2", "survey" }, rig.Runner.RanNames());
    }

    // ---- deadlines, cancelling, admission ----

    [Fact]
    public void AnExpiredCallIsAnsweredUnrunAndUncharged()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Clock.Advance(100.0);
        rig.Add(a, "stale", SchedulerRig.Read(), costMs: 50.0, deadlineMs: 90.0);
        rig.Light(a, "fresh", costMs: 1.0);

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(new[] { "fresh" }, rig.Runner.RanNames());
        Assert.Equal("stale", Assert.Single(rig.Runner.Expired).Name);
        Assert.Equal(1, outcome.ExpiredCalls);
        Assert.Equal(1, outcome.LightCalls);
        Assert.Equal(1.0, outcome.SpentMs);
    }

    [Fact]
    public void ACallExpiresWhenItsDeadlinePassesWhileItWaits()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "first", costMs: 5.0);
        rig.Add(a, "late", SchedulerRig.Read(), deadlineMs: 3.0);

        rig.Frame();
        rig.Frame();

        Assert.Equal(new[] { "first" }, rig.Runner.RanNames());
        Assert.Equal("late", Assert.Single(rig.Runner.Expired).Name);
    }

    [Fact]
    public void AnExpiredWriteReleasesTheCallsBehindIt()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Clock.Advance(10.0);
        rig.Add(a, "write", SchedulerRig.Write(), deadlineMs: 5.0);
        rig.Light(a, "read");

        rig.Frame();

        Assert.Equal(new[] { "read" }, rig.Runner.RanNames());
        Assert.Equal("write", Assert.Single(rig.Runner.Expired).Name);
    }

    [Fact]
    public void AnExpiredHeavyCallDoesNotUseTheFramesHeavySlot()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        rig.Clock.Advance(10.0);
        rig.Add(a, "stale", SchedulerRig.Heavy(), deadlineMs: 5.0);
        rig.Add(a, "survey", SchedulerRig.Heavy());

        FrameOutcome outcome = rig.Frame();

        Assert.Equal(new[] { "survey" }, rig.Runner.RanNames());
        Assert.Equal(1, outcome.HeavyCalls);
        Assert.Equal(1, outcome.ExpiredCalls);
    }

    [Fact]
    public void ACancelledCallNeverRuns()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        FakeCall call = new FakeCall("cancelled", 0.1);
        rig.Scheduler.Enqueue(a, call, SchedulerRig.Read(), double.PositiveInfinity);
        rig.Light(a, "kept");

        bool cancelled = rig.Scheduler.Cancel(a, call);
        rig.Frame();

        Assert.True(cancelled);
        Assert.Equal(new[] { "kept" }, rig.Runner.RanNames());
        Assert.Empty(rig.Runner.Expired);
    }

    [Fact]
    public void CancellingACallThatRanIsRefused()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        FakeCall call = new FakeCall("ran", 0.1);
        rig.Scheduler.Enqueue(a, call, SchedulerRig.Read(), double.PositiveInfinity);
        rig.Frame();

        Assert.False(rig.Scheduler.Cancel(a, call));
    }

    [Fact]
    public void CancellingAWriteReleasesTheCallsBehindIt()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Add(a, "survey", SchedulerRig.Heavy(), costMs: 5.0);
        FakeCall write = new FakeCall("write", 0.1, ordered: true);
        rig.Scheduler.Enqueue(a, write, SchedulerRig.Write(), double.PositiveInfinity);
        rig.Light(a, "read");

        rig.Scheduler.Cancel(a, write);
        rig.Frame();

        Assert.Equal(new[] { "read", "survey" }, rig.Runner.RanNames());
    }

    [Fact]
    public void TheSeventeenthWaitingCallOnAConnectionIsRefused()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        for (int index = 0; index < SchedulerSettings.DefaultMaxInFlight; index++)
        {
            Assert.True(rig.Light(a, $"a{index}").IsQueued);
        }

        Admission refused = rig.Light(a, "a16");
        Admission other = rig.Light(b, "b0");

        Assert.False(refused.IsQueued);
        Assert.Equal(AdmissionRefusal.TooManyInFlight, refused.Refusal);
        Assert.True(other.IsQueued);
    }

    [Fact]
    public void ARefusedConnectionIsAdmittedAgainOnceItsCallsRun()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < SchedulerSettings.DefaultMaxInFlight; index++)
        {
            rig.Light(a, $"a{index}");
        }

        rig.Frame();

        Assert.True(rig.Light(a, "again").IsQueued);
    }

    [Fact]
    public void ClosingAConnectionDropsItsCallsUnrunAndRefusesMore()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        rig.Light(a, "a1");
        rig.Add(a, "a2", SchedulerRig.Heavy());
        rig.Light(b, "b1");

        rig.Scheduler.Close(a);
        Admission after = rig.Light(a, "a3");
        rig.Frame();

        Assert.Equal(AdmissionRefusal.ConnectionClosed, after.Refusal);
        Assert.Equal(new[] { "b1" }, rig.Runner.RanNames());
        Assert.Empty(rig.Runner.Expired);
        Assert.Equal(0, a.WaitingCalls);
    }

    // ---- prediction through the scheduler ----

    [Fact]
    public void AMethodThatProvedSlowArrivesInTheHeavyLaneNextTime()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        Admission first = rig.Add(a, "big", SchedulerRig.Read("list_devices", CostClass.Bounded), costMs: 3.0);
        rig.Frame();

        Admission second = rig.Add(a, "big", SchedulerRig.Read("list_devices", CostClass.Bounded), costMs: 3.0);

        Assert.Equal(Lane.Light, first.Lane);
        Assert.Equal(Lane.Heavy, second.Lane);
    }

    [Fact]
    public void ReadDevicesOfFiveItemsIsLightAndOf128IsHeavyAfterTheSameHistory()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < CostPredictor.Window; index++)
        {
            rig.Add(a, $"r{index}", SchedulerRig.Read("read_devices", CostClass.Bounded, 20), costMs: 0.2);
            rig.Frame();
        }

        Admission small = rig.Add(a, "small", SchedulerRig.Read("read_devices", CostClass.Bounded, 5));
        Admission large = rig.Add(a, "large", SchedulerRig.Read("read_devices", CostClass.Bounded, 128));

        Assert.Equal(Lane.Light, small.Lane);
        Assert.Equal(Lane.Heavy, large.Lane);
    }

    // ---- the subscription lane ----

    [Fact]
    public void SubscriptionsTakeAtMostTheirShareAndLightCallsTheRest()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < 16; index++)
        {
            rig.Light(a, $"r{index}", costMs: 0.5);
        }

        FakeSamples samples = new FakeSamples(rig.Clock, 0.5, due: 100);

        FrameOutcome outcome = rig.Frame(samples: samples);

        Assert.Equal(3, outcome.Samples);
        Assert.Equal(5, outcome.LightCalls);
        Assert.Equal(4.0, outcome.SpentMs);
    }

    [Fact]
    public void WhileAJobHoldsTheTickSubscriptionsTakeAtMostHalfOfTwoMilliseconds()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < 16; index++)
        {
            rig.Light(a, $"r{index}", costMs: 0.25);
        }

        FakeSamples samples = new FakeSamples(rig.Clock, 0.25, due: 100);

        FrameOutcome outcome = rig.Frame(jobHoldsTick: true, samples: samples);

        Assert.Equal(4, outcome.Samples);
        Assert.Equal(4, outcome.LightCalls);
    }

    [Fact]
    public void TheFirstDueSampleAlwaysRunsEvenWithNoSubscriptionShare()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(4.0, subscriptionBudgetMs: 0.0));
        FakeSamples samples = new FakeSamples(rig.Clock, 0.01, due: 10);

        FrameOutcome first = rig.Frame(samples: samples);
        FrameOutcome second = rig.Frame(samples: samples);

        Assert.Equal(1, first.Samples);
        Assert.Equal(1, second.Samples);
    }

    [Fact]
    public void TheFirstLightCallRunsEvenWhenSamplesUsedTheWholeBudget()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "r1");
        rig.Light(a, "r2");
        FakeSamples samples = new FakeSamples(rig.Clock, 5.0, due: 3);

        FrameOutcome outcome = rig.Frame(samples: samples);

        Assert.Equal(1, outcome.Samples);
        Assert.Equal(1, outcome.LightCalls);
    }

    [Fact]
    public void NothingDueTakesNoSample()
    {
        SchedulerRig rig = new SchedulerRig();

        FrameOutcome outcome = rig.Frame(samples: new FakeSamples(rig.Clock, 0.1, due: 0));

        Assert.Equal(0, outcome.Samples);
    }

    private static FakeCall FindRan(SchedulerRig rig, string name)
    {
        foreach (FakeCall call in rig.Runner.Ran)
        {
            if (call.Name == name)
            {
                return call;
            }
        }

        throw new Xunit.Sdk.XunitException($"{name} did not run.");
    }

    private static List<string> HeavyNames(SchedulerRig rig)
    {
        List<string> names = new List<string>();
        foreach (FakeCall call in rig.Runner.Ran)
        {
            if (call.Name.StartsWith("h", System.StringComparison.Ordinal))
            {
                names.Add(call.Name);
            }
        }

        return names;
    }
}
