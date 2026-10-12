#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;
using Xunit;
using Connection = StationGodMCP.Pure.Scheduling.FrameScheduler<StationGodMCP.Tests.Scheduling.FakeCall>.Connection;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// Streams in the frame scheduler (PLAN 2.1, ARCH-4-03): the order rule per (connection, stream), rounds by client and
/// then by (connection, stream) in both lanes, in-flight limits per stream and per connection, and ending a stream.
/// </summary>
public sealed class FrameSchedulerStreamTests
{
    // ---- order per stream ----

    [Fact]
    public void AWriteOnOneStreamHoldsItsOwnLaterCallsButNotAnotherStreamsOfTheSameConnection()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        rig.AddOn(a, 1, "build", SchedulerRig.Write("build_structure", CostClass.Job));
        rig.LightOn(a, 1, "read1");
        rig.LightOn(a, 2, "read2");

        rig.Frame();
        List<string> afterFirst = rig.Runner.RanNames();
        rig.Frame();

        Assert.Equal(new[] { "read2", "build" }, afterFirst);
        Assert.Equal(new[] { "read2", "build", "read1" }, rig.Runner.RanNames());
    }

    [Fact]
    public void AWriteWaitsOnlyForTheEarlierCallsOfItsOwnStream()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open("agent");
        rig.AddOn(a, 1, "survey", SchedulerRig.Heavy());
        rig.AddOn(a, 2, "write", SchedulerRig.Write());

        rig.Frame();

        Assert.Equal(new[] { "write", "survey" }, rig.Runner.RanNames());
    }

    [Fact]
    public void StreamZeroIsTheConnectionsDefaultStream()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open("agent");
        rig.Add(a, "survey", SchedulerRig.Heavy());
        rig.AddOn(a, 0, "write", SchedulerRig.Write());

        rig.Frame();
        List<string> afterFirst = rig.Runner.RanNames();
        rig.Frame();

        Assert.Equal(new[] { "survey" }, afterFirst);
        Assert.Equal(new[] { "survey", "write" }, rig.Runner.RanNames());
    }

    // ---- rounds: by client, then by (connection, stream) ----

    [Fact]
    public void AClientWithTwoConnectionsGetsOneTurnARoundLikeAClientWithOne()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a1 = rig.Scheduler.Open("dashboard");
        Connection a2 = rig.Scheduler.Open("dashboard");
        Connection b = rig.Scheduler.Open("agent");
        for (int index = 1; index <= 3; index++)
        {
            rig.Light(a1, $"a1-{index}");
            rig.Light(a2, $"a2-{index}");
        }

        rig.Light(b, "b-1");
        rig.Light(b, "b-2");

        rig.Frame();

        Assert.Equal(new[] { "a1-1", "b-1", "a2-1", "b-2", "a1-2", "a2-2", "a1-3", "a2-3" }, rig.Runner.RanNames());
    }

    [Fact]
    public void AOneCallClientIsServedInTheFirstRoundBesideAClientWithTwoBusyConnections()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a1 = rig.Scheduler.Open("dashboard");
        Connection a2 = rig.Scheduler.Open("dashboard");
        Connection b = rig.Scheduler.Open("agent");
        for (int index = 1; index <= 4; index++)
        {
            rig.Light(a1, $"a1-{index}");
            rig.Light(a2, $"a2-{index}");
        }

        rig.Light(b, "b-1");

        rig.Frame();

        Assert.Equal(new[] { "a1-1", "b-1", "a2-1" }, rig.Runner.RanNames().GetRange(0, 3));
    }

    [Fact]
    public void WithinAClientItsStreamsTakeTurns()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection hub = rig.Scheduler.Open("hub");
        for (int index = 1; index <= 2; index++)
        {
            rig.LightOn(hub, 0, $"s0-{index}");
            rig.LightOn(hub, 7, $"s7-{index}");
            rig.LightOn(hub, 9, $"s9-{index}");
        }

        rig.Frame();

        Assert.Equal(new[] { "s0-1", "s7-1", "s9-1", "s0-2", "s7-2", "s9-2" }, rig.Runner.RanNames());
    }

    [Fact]
    public void AHubsManyStreamsGetOneTurnARoundBesideAnotherClient()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection hub = rig.Scheduler.Open("hub");
        Connection dashboard = rig.Scheduler.Open("dashboard");
        for (long stream = 1; stream <= 3; stream++)
        {
            rig.LightOn(hub, stream, $"h{stream}");
        }

        rig.Light(dashboard, "d1");
        rig.Light(dashboard, "d2");

        rig.Frame();

        Assert.Equal(new[] { "h1", "d1", "h2", "d2", "h3" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ABlockedStreamDoesNotCostItsClientItsTurn()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection hub = rig.Scheduler.Open("hub");
        Connection other = rig.Scheduler.Open("other");
        rig.AddOn(hub, 1, "survey", SchedulerRig.Heavy());
        rig.AddOn(hub, 1, "write", SchedulerRig.Write());
        rig.LightOn(hub, 2, "h2-1");
        rig.LightOn(hub, 2, "h2-2");
        rig.Light(other, "o1");
        rig.Light(other, "o2");

        rig.Frame();

        Assert.Equal(new[] { "h2-1", "o1", "h2-2", "o2", "survey" }, rig.Runner.RanNames());
    }

    [Fact]
    public void OneCallClientsAreServedFirstComeFirstServedWhateverTheirStreams()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open("a");
        Connection b = rig.Scheduler.Open("b");
        Connection c = rig.Scheduler.Open("c");

        rig.LightOn(c, 4, "c1");
        rig.LightOn(a, 0, "a1");
        rig.LightOn(b, 2, "b1");
        rig.Frame();
        rig.LightOn(b, 3, "b2");
        rig.LightOn(c, 5, "c2");
        rig.LightOn(a, 1, "a2");
        rig.Frame();

        Assert.Equal(new[] { "c1", "a1", "b1", "b2", "c2", "a2" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ConnectionsWithoutANameAreEachTheirOwnClient()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a = rig.Scheduler.Open();
        Connection b = rig.Scheduler.Open();
        rig.Light(a, "a1");
        rig.Light(a, "a2");
        rig.Light(b, "b1");
        rig.Light(b, "b2");

        rig.Frame();

        Assert.Equal(new[] { "a1", "b1", "a2", "b2" }, rig.Runner.RanNames());
    }

    [Fact]
    public void HeavyCallsTakeTurnsByClientThenByStream()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a1 = rig.Scheduler.Open("dashboard");
        Connection a2 = rig.Scheduler.Open("dashboard");
        Connection b = rig.Scheduler.Open("agent");
        rig.Add(a1, "a1-1", SchedulerRig.Heavy());
        rig.Add(a1, "a1-2", SchedulerRig.Heavy());
        rig.Add(a2, "a2-1", SchedulerRig.Heavy());
        rig.Add(a2, "a2-2", SchedulerRig.Heavy());
        rig.AddOn(b, 3, "b3-1", SchedulerRig.Heavy());
        rig.AddOn(b, 4, "b4-1", SchedulerRig.Heavy());

        for (int frame = 0; frame < 6; frame++)
        {
            rig.Frame();
        }

        Assert.Equal(new[] { "a1-1", "b3-1", "a2-1", "b4-1", "a1-2", "a2-2" }, rig.Runner.RanNames());
    }

    [Fact]
    public void TheHeavyLanesWaitingBoundStillRunsTheOldestCallWhateverItsClient()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection busy = rig.Scheduler.Open("busy");
        Connection hub = rig.Scheduler.Open("hub");
        rig.AddOn(hub, 5, "h0", SchedulerRig.Heavy());
        rig.AddOn(hub, 6, "h1", SchedulerRig.Heavy());

        List<long> heavyFrames = new List<long>();
        for (int frame = 1; frame <= 14; frame++)
        {
            rig.Busy(busy, frame, 5.0);
            if (rig.Frame().HeavyCalls == 1)
            {
                heavyFrames.Add(frame);
            }
        }

        Assert.Equal(new long[] { 11, 12 }, heavyFrames);
    }

    // ---- in flight ----

    [Fact]
    public void TheSeventeenthWaitingCallOnAStreamIsRefusedWhileAnotherStreamIsAdmitted()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection hub = rig.Scheduler.Open("hub");
        for (int index = 0; index < SchedulerSettings.DefaultMaxInFlight; index++)
        {
            Assert.True(rig.LightOn(hub, 1, $"s1-{index}").IsQueued);
        }

        Admission refused = rig.LightOn(hub, 1, "s1-16");
        Admission other = rig.LightOn(hub, 2, "s2-0");

        Assert.Equal(AdmissionRefusal.TooManyInFlight, refused.Refusal);
        Assert.False(refused.IsQueued);
        Assert.True(other.IsQueued);
    }

    [Fact]
    public void AConnectionHoldsAtMostItsTotalAcrossItsStreams()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(4.0, maxInFlight: 4, maxInFlightPerConnection: 10));
        Connection hub = rig.Scheduler.Open("hub");
        Connection other = rig.Scheduler.Open("other");
        for (int index = 0; index < 10; index++)
        {
            Assert.True(rig.LightOn(hub, index / 4 + 1, $"h{index}").IsQueued);
        }

        Admission refused = rig.LightOn(hub, 9, "h10");
        Admission elsewhere = rig.Light(other, "o0");

        Assert.Equal(AdmissionRefusal.TooManyInFlightOnConnection, refused.Refusal);
        Assert.True(elsewhere.IsQueued);
        Assert.Equal(10, hub.WaitingCalls);
    }

    [Fact]
    public void TheDefaultConnectionTotalCoversSixteenFullStreams()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection hub = rig.Scheduler.Open("hub");
        for (long stream = 0; stream < 16; stream++)
        {
            for (int index = 0; index < SchedulerSettings.DefaultMaxInFlight; index++)
            {
                Assert.True(rig.LightOn(hub, stream, $"s{stream}-{index}").IsQueued);
            }
        }

        Assert.Equal(AdmissionRefusal.TooManyInFlightOnConnection, rig.LightOn(hub, 99, "over").Refusal);
        Assert.Equal(256, SchedulerSettings.DefaultMaxInFlightPerConnection);
    }

    // ---- ending a stream, cancelling, closing ----

    [Fact]
    public void EndingAStreamDropsItsWaitingCallsUnrunAndNoOthers()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection hub = rig.Scheduler.Open("hub");
        Connection other = rig.Scheduler.Open("other");
        rig.LightOn(hub, 1, "s1-a");
        rig.AddOn(hub, 1, "s1-b", SchedulerRig.Heavy());
        rig.LightOn(hub, 2, "s2-a");
        rig.LightOn(other, 1, "o1-a");

        int dropped = rig.Scheduler.EndStream(hub, 1);
        rig.Frame();

        Assert.Equal(2, dropped);
        Assert.Equal(new[] { "s2-a", "o1-a" }, rig.Runner.RanNames());
        Assert.Empty(rig.Runner.Expired);
        Assert.Equal(0, hub.WaitingCalls);
    }

    [Fact]
    public void EndingAStreamReleasesItsShareOfTheConnectionTotal()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(4.0, maxInFlight: 4, maxInFlightPerConnection: 4));
        Connection hub = rig.Scheduler.Open("hub");
        for (int index = 0; index < 4; index++)
        {
            rig.LightOn(hub, 1, $"s1-{index}");
        }

        rig.Scheduler.EndStream(hub, 1);

        Assert.True(rig.LightOn(hub, 2, "s2-0").IsQueued);
        Assert.Equal(0, rig.Scheduler.EndStream(hub, 77));
    }

    [Fact]
    public void ACancelledCallOnAStreamNeverRuns()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection hub = rig.Scheduler.Open("hub");
        FakeCall call = new FakeCall("cancelled", 0.1) { Stream = 3 };
        rig.Scheduler.Enqueue(hub, 3, call, SchedulerRig.Read(), double.PositiveInfinity);
        rig.LightOn(hub, 3, "kept");

        Assert.False(rig.Scheduler.Cancel(hub, 4, call));
        Assert.True(rig.Scheduler.Cancel(hub, 3, call));
        rig.Frame();

        Assert.Equal(new[] { "kept" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ClosingAConnectionDropsTheCallsOfEveryStreamAndLeavesItsClientsOtherConnections()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection a1 = rig.Scheduler.Open("dashboard");
        Connection a2 = rig.Scheduler.Open("dashboard");
        rig.LightOn(a1, 0, "a1-0");
        rig.LightOn(a1, 5, "a1-5");
        rig.AddOn(a1, 6, "a1-6", SchedulerRig.Heavy());
        rig.Light(a2, "a2-0");

        rig.Scheduler.Close(a1);
        Admission after = rig.LightOn(a1, 7, "a1-7");
        rig.Frame();
        rig.Light(a2, "a2-1");
        rig.Frame();

        Assert.Equal(AdmissionRefusal.ConnectionClosed, after.Refusal);
        Assert.Equal(new[] { "a2-0", "a2-1" }, rig.Runner.RanNames());
        Assert.Equal(0, a1.WaitingCalls);
    }

    [Fact]
    public void AClientOpenedAgainAfterAllItsConnectionsClosedIsServed()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(0.0));
        Connection first = rig.Scheduler.Open("dashboard");
        rig.Light(first, "f1");
        rig.Scheduler.Close(first);
        rig.Frame();
        Connection second = rig.Scheduler.Open("dashboard");
        rig.Light(second, "s1");
        Connection third = rig.Scheduler.Open("dashboard");
        rig.Light(third, "t1");
        rig.Light(second, "s2");

        rig.Frame();

        Assert.Equal(new[] { "s1", "t1", "s2" }, rig.Runner.RanNames());
    }
}
