#nullable enable

using System.Collections.Generic;
using System.Text;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;
using Xunit;
using Connection = StationGodMCP.Pure.Scheduling.FrameScheduler<StationGodMCP.Tests.Scheduling.FakeCall>.Connection;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// The reply's budget_hit: CurrentFrame as the scheduler publishes it for the length of a frame, read as each call
/// finishes, and the envelope's bytes with and without it.
/// </summary>
public sealed class BudgetHitTests
{
    private const string Envelope =
        "{\"type\":\"reply\",\"id\":\"7\",\"ok\":true,\"shaped\":false,\"result\":{\"member_count\":0}," +
        "\"elapsed_ms\":1.25,\"queue_ms\":0.5,\"frame\":9";

    [Fact]
    public void ACallThatFinishesUnderBudgetSaysNothingAndOneThatReachesItSaysSo()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "r1", costMs: 1.0);
        rig.Light(a, "r2", costMs: 2.0);
        rig.Light(a, "r3", costMs: 1.5);

        rig.Frame();

        Assert.Equal(new[] { false, false, true }, Hits(rig.Runner.Ran));
    }

    [Fact]
    public void ACallThatEndsExactlyOnTheBudgetHasReachedIt()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "r1", costMs: 3.0);
        rig.Light(a, "r2", costMs: 1.0);

        rig.Frame();

        Assert.Equal(new[] { false, true }, Hits(rig.Runner.Ran));
    }

    [Fact]
    public void AHeavyCallsOwnOverrunCounts()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "light", costMs: 0.5);
        rig.Add(a, "survey", SchedulerRig.Heavy(), costMs: 12.0);

        rig.Frame();

        Assert.Equal(new[] { "light", "survey" }, rig.Runner.RanNames());
        Assert.Equal(new[] { false, true }, Hits(rig.Runner.Ran));
    }

    [Fact]
    public void AnUnlimitedBudgetIsNeverReached()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        for (int index = 0; index < 5; index++)
        {
            rig.Light(a, $"r{index}", costMs: 50.0);
        }

        rig.Frame();

        Assert.Equal(5, rig.Runner.Ran.Count);
        Assert.DoesNotContain(true, Hits(rig.Runner.Ran));
    }

    [Fact]
    public void WhileAJobHoldsTheTickTheCappedBudgetIsTheOneReached()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "r1", costMs: 1.5);
        rig.Light(a, "r2", costMs: 1.0);

        rig.Frame(jobHoldsTick: true);

        Assert.Equal(new[] { false, true }, Hits(rig.Runner.Ran));
    }

    [Fact]
    public void OutsideAFrameNothingIsReached()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open();
        rig.Light(a, "slow", costMs: 10.0);

        rig.Frame();

        Assert.True(rig.Runner.Ran[0].BudgetHit);
        Assert.False(CurrentFrame.Running);
        Assert.False(CurrentFrame.BudgetReached(rig.Clock.NowMs + 1_000.0));
    }

    [Fact]
    public void TheFlagIsAbsentFromTheEnvelopeUnlessTrue()
    {
        Assert.Equal(Envelope + "}", ApiJson.WriteShared(Reply(false)));
        Assert.Equal(Envelope + ",\"budget_hit\":true}", ApiJson.WriteShared(Reply(true)));
        Assert.EndsWith("},\"elapsed_ms\":0.1,\"queue_ms\":0.5,\"frame\":9,\"budget_hit\":true}",
            ApiJson.WriteShared(CallReplyView.Failed("7", new ErrorView("thing_not_found", "m"), 0.1, 0.5, 9, true)));
    }

    [Fact]
    public void TheFlagCostsNoBytesWhenFalseAndEighteenWhenTrue()
    {
        int without = Bytes(Reply(false), shaped: false);
        int withFlag = Bytes(Reply(true), shaped: false);
        int shapedWithout = Bytes(Reply(false), shaped: true);
        int shapedWith = Bytes(Reply(true), shaped: true);

        Assert.Equal(Encoding.UTF8.GetByteCount(Envelope + "}"), without);
        Assert.Equal(18, withFlag - without);
        Assert.Equal(18, shapedWith - shapedWithout);
    }

    [Fact]
    public void ARefusedReplyNeverCarriesIt() =>
        Assert.DoesNotContain("budget_hit",
            ApiJson.WriteShared(CallReplyView.Refused("7", new ErrorView("too_many_in_flight", "m"))));

    private static CallReplyView Reply(bool budgetHit) =>
        CallReplyView.Of("7", new ChuteSummaryView(0), false, 1.25, 0.5, 9, budgetHit);

    // The bytes ApiHost sends: WriteShared for errors, the shaping writer (WriteReply) for results.
    private static int Bytes(CallReplyView reply, bool shaped)
    {
        string json = shaped
            ? ApiJson.WriteReply(reply, ShapeRequest.None, new List<Truncation>()).Json
            : ApiJson.WriteShared(reply);
        return Encoding.UTF8.GetByteCount(json);
    }

    private static bool[] Hits(List<FakeCall> ran)
    {
        bool[] hits = new bool[ran.Count];
        for (int index = 0; index < ran.Count; index++)
        {
            hits[index] = ran[index].BudgetHit;
        }

        return hits;
    }
}
