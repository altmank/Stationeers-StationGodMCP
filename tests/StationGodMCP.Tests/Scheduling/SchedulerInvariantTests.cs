#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;
using Xunit;
using Connection = StationGodMCP.Pure.Scheduling.FrameScheduler<StationGodMCP.Tests.Scheduling.FakeCall>.Connection;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// The scheduler's promises held over long mixed workloads from fixed seeds: order on each connection, the budget, one
/// heavy call per frame that runs under budget or once it has waited its frames, every call eventually served, and no
/// allocation per frame.
/// </summary>
public sealed class SchedulerInvariantTests
{
    private const int Connections = 6;

    [Theory]
    [InlineData(1, 4.0, false)]
    [InlineData(2, 4.0, true)]
    [InlineData(3, 0.0, false)]
    [InlineData(4, 1.0, false)]
    [InlineData(5, 4.0, false)]
    [InlineData(6, 2.5, true)]
    public void MixedWorkloadsKeepEveryRule(int seed, double requestBudgetMs, bool jobHoldsTick)
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(requestBudgetMs));
        FrameShares shares = rig.Settings.SharesFor(jobHoldsTick);
        Random random = new Random(seed);
        Connection[] connections = new Connection[Connections];
        int[] sent = new int[Connections];
        Dictionary<(int, int), FakeCall> byPosition = new Dictionary<(int, int), FakeCall>();
        for (int index = 0; index < Connections; index++)
        {
            connections[index] = rig.Scheduler.Open();
        }

        int queued = 0;
        int ranBefore = 0;
        for (int frame = 1; frame <= 1500; frame++)
        {
            if (frame <= 1000)
            {
                for (int index = 0; index < Connections; index++)
                {
                    if (random.NextDouble() < 0.35 && connections[index].WaitingCalls < rig.Settings.MaxInFlight)
                    {
                        FakeCall call = Arrive(rig, random, connections[index], index, sent[index]++);
                        byPosition[(index, call.Position)] = call;
                        queued++;
                    }
                }
            }

            double frameStart = rig.Clock.NowMs;
            FakeSamples samples = new FakeSamples(rig.Clock, 0.3, random.Next(0, 8));
            FrameOutcome outcome = rig.Frame(jobHoldsTick, samples);

            Assert.InRange(outcome.HeavyCalls, 0, 1);
            CheckBudget(rig, ranBefore, frameStart, shares.BudgetMs);
            ranBefore = rig.Runner.Ran.Count;
        }

        Assert.Equal(queued, rig.Runner.Ran.Count);
        CheckOrderOnEachConnection(rig.Runner.Ran, byPosition);
    }

    [Fact]
    public void AFrameAllocatesNothingOnceWarm()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection[] connections = { rig.Scheduler.Open(), rig.Scheduler.Open(), rig.Scheduler.Open() };
        FakeSamples samples = new FakeSamples(rig.Clock, 0.2, 0);
        FakeCall[] calls = new FakeCall[600];
        for (int index = 0; index < calls.Length; index++)
        {
            calls[index] = new FakeCall($"c{index}", 0.3);
        }

        int next = 0;
        void Cycle()
        {
            for (int index = 0; index < connections.Length; index++)
            {
                FakeCall light = calls[next++];
                rig.Scheduler.Enqueue(connections[index], light, SchedulerRig.Read(), double.PositiveInfinity);
                FakeCall other = calls[next++];
                CallProfile profile = index == 0 ? SchedulerRig.Heavy() : index == 1 ? SchedulerRig.Write() : SchedulerRig.Read("read_devices", CostClass.Bounded, 4);
                rig.Scheduler.Enqueue(connections[index], other, profile, index == 2 ? rig.Clock.NowMs - 1.0 : double.PositiveInfinity);
            }

            samples.Due = 6;
            rig.Scheduler.RunFrame(false, samples);
            rig.Scheduler.RunFrame(true, samples);
        }

        for (int warm = 0; warm < 20; warm++)
        {
            Cycle();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int measured = 0; measured < 20; measured++)
        {
            Cycle();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static FakeCall Arrive(SchedulerRig rig, Random random, Connection connection, int index, int position)
    {
        double pick = random.NextDouble();
        CallProfile profile;
        double costMs;
        if (pick < 0.6)
        {
            profile = SchedulerRig.Read("light_read");
            costMs = 0.05 + random.NextDouble() * 0.8;
        }
        else if (pick < 0.75)
        {
            profile = SchedulerRig.Write();
            costMs = 0.1;
        }
        else if (pick < 0.9)
        {
            profile = SchedulerRig.Heavy();
            costMs = 2.0 + random.NextDouble() * 18.0;
        }
        else if (pick < 0.95)
        {
            profile = SchedulerRig.Write("build_structure", CostClass.Job);
            costMs = 6.0;
        }
        else
        {
            profile = new CallProfile("spawn_item", MethodClass.Cheat, new CallCost(CostClass.Instant, 1));
            costMs = 0.2;
        }

        FakeCall call = new FakeCall($"{index}:{position}", costMs, index, profile.IsOrdered)
        {
            Position = position,
            ArrivedAfterFrame = rig.Scheduler.Frame
        };
        Assert.True(rig.Scheduler.Enqueue(connection, call, profile, double.PositiveInfinity).IsQueued);
        return call;
    }

    /// <summary>
    /// Within one frame: every light call but the first started under budget, and a heavy call started under budget or
    /// after waiting HeavyMaxWaitFrames.
    /// </summary>
    private static void CheckBudget(SchedulerRig rig, int ranBefore, double frameStart, double budgetMs)
    {
        bool firstLight = true;
        for (int index = ranBefore; index < rig.Runner.Ran.Count; index++)
        {
            FakeCall call = rig.Runner.Ran[index];
            bool heavy = call.CostMs >= 2.0;
            double startedAfter = call.StartedAtMs - frameStart;
            if (heavy)
            {
                long waited = call.RanInFrame - call.ArrivedAfterFrame - 1;
                Assert.True(startedAfter < budgetMs || waited >= SchedulerSettings.DefaultHeavyMaxWaitFrames,
                    $"heavy {call.Name} started {startedAfter} ms in after waiting {waited} frames");
                continue;
            }

            Assert.True(firstLight || startedAfter < budgetMs, $"light {call.Name} started {startedAfter} ms into the frame");
            firstLight = false;
        }
    }

    /// <summary>protocol.md, Order on one connection, checked over the order calls were answered in.</summary>
    private static void CheckOrderOnEachConnection(List<FakeCall> ran, Dictionary<(int, int), FakeCall> byPosition)
    {
        HashSet<(int, int)> answered = new HashSet<(int, int)>();
        foreach (FakeCall call in ran)
        {
            for (int earlier = 0; earlier < call.Position; earlier++)
            {
                FakeCall before = byPosition[(call.Connection, earlier)];
                if (call.Ordered || before.Ordered)
                {
                    Assert.True(answered.Contains((call.Connection, earlier)),
                        $"{call.Name} started before {before.Name} was answered");
                }
            }

            answered.Add((call.Connection, call.Position));
        }
    }
}
