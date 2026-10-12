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

    /// <summary>
    /// The same promises with connections grouped into clients and calls spread over streams that end and are never
    /// used again: order on each (connection, stream), the budget, one heavy call a frame, and every call that was not
    /// on an ended stream served.
    /// </summary>
    [Theory]
    [InlineData(11, 4.0, false)]
    [InlineData(12, 0.0, false)]
    [InlineData(13, 2.5, true)]
    [InlineData(14, 1.0, false)]
    public void MixedWorkloadsOnStreamsKeepEveryRule(int seed, double requestBudgetMs, bool jobHoldsTick)
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(requestBudgetMs, maxInFlight: 6,
            maxInFlightPerConnection: 12));
        FrameShares shares = rig.Settings.SharesFor(jobHoldsTick);
        Random random = new Random(seed);
        string?[] clients = { "dashboard", "dashboard", "hub", null, "agent", "agent" };
        Connection[] connections = new Connection[clients.Length];
        long[][] streams = new long[clients.Length][];
        long[] nextStream = new long[clients.Length];
        Dictionary<(int, long), int> sent = new Dictionary<(int, long), int>();
        Dictionary<(int, long, int), FakeCall> byPosition = new Dictionary<(int, long, int), FakeCall>();
        Dictionary<(int, long), int> endedAt = new Dictionary<(int, long), int>();
        for (int index = 0; index < clients.Length; index++)
        {
            connections[index] = rig.Scheduler.Open(clients[index]);
            streams[index] = new long[] { 0, 1, 2 };
            nextStream[index] = 3;
        }

        int queued = 0;
        int dropped = 0;
        int ranBefore = 0;
        for (int frame = 1; frame <= 1500; frame++)
        {
            if (frame <= 1000)
            {
                for (int index = 0; index < connections.Length; index++)
                {
                    if (random.NextDouble() < 0.02)
                    {
                        int slot = random.Next(1, streams[index].Length);
                        long ending = streams[index][slot];
                        dropped += rig.Scheduler.EndStream(connections[index], ending);
                        endedAt[(index, ending)] = frame;
                        streams[index][slot] = nextStream[index]++;
                    }

                    if (random.NextDouble() < 0.5)
                    {
                        long stream = streams[index][random.Next(streams[index].Length)];
                        sent.TryGetValue((index, stream), out int position);
                        FakeCall? call = ArriveOn(rig, random, connections[index], index, stream, position);
                        if (call != null)
                        {
                            sent[(index, stream)] = position + 1;
                            byPosition[(index, stream, position)] = call;
                            queued++;
                        }
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

        Assert.Equal(queued, rig.Runner.Ran.Count + dropped);
        HashSet<(int, long, int)> answered = new HashSet<(int, long, int)>();
        foreach (FakeCall call in rig.Runner.Ran)
        {
            Assert.True(!endedAt.TryGetValue((call.Connection, call.Stream), out int end) || call.RanInFrame < end,
                $"{call.Name} ran after its stream ended");
            for (int earlier = 0; earlier < call.Position; earlier++)
            {
                FakeCall before = byPosition[(call.Connection, call.Stream, earlier)];
                if (call.Ordered || before.Ordered)
                {
                    Assert.True(answered.Contains((call.Connection, call.Stream, earlier)),
                        $"{call.Name} started before {before.Name} was answered");
                }
            }

            answered.Add((call.Connection, call.Stream, call.Position));
        }
    }

    [Fact]
    public void AFrameAllocatesNothingOnceWarmWithClientsAndStreams()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection[] connections =
        {
            rig.Scheduler.Open("dashboard"), rig.Scheduler.Open("dashboard"), rig.Scheduler.Open("hub"),
            rig.Scheduler.Open(), rig.Scheduler.Open("agent")
        };
        FakeSamples samples = new FakeSamples(rig.Clock, 0.2, 0);
        FakeCall[] calls = new FakeCall[5000];
        for (int index = 0; index < calls.Length; index++)
        {
            calls[index] = new FakeCall($"c{index}", 0.3);
        }

        int next = 0;
        long run = 10;
        void Cycle()
        {
            // Each cycle opens a stream the scheduler has not seen and ends it the cycle after, as a hub's runs do.
            run++;
            for (int index = 0; index < connections.Length; index++)
            {
                rig.Scheduler.Enqueue(connections[index], 0, calls[next++], SchedulerRig.Read(), double.PositiveInfinity);
                rig.Scheduler.Enqueue(connections[index], run, calls[next++], SchedulerRig.Read(), double.PositiveInfinity);
                CallProfile profile = index == 0 ? SchedulerRig.Heavy() : index == 1 ? SchedulerRig.Write()
                    : SchedulerRig.Read("read_devices", CostClass.Bounded, 4);
                rig.Scheduler.Enqueue(connections[index], run, calls[next++], profile,
                    index == 2 ? rig.Clock.NowMs - 1.0 : double.PositiveInfinity);
                rig.Scheduler.Enqueue(connections[index], 1, calls[next++], SchedulerRig.Heavy(), double.PositiveInfinity);
                rig.Scheduler.EndStream(connections[index], run - 1);
            }

            samples.Due = 6;
            rig.Scheduler.RunFrame(false, samples);
            rig.Scheduler.RunFrame(true, samples);
        }

        for (int warm = 0; warm < 30; warm++)
        {
            Cycle();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int measured = 0; measured < 30; measured++)
        {
            Cycle();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    /// <summary>A call on one stream, picked as Arrive picks; null when the scheduler refused it (a limit).</summary>
    private static FakeCall? ArriveOn(SchedulerRig rig, Random random, Connection connection, int index, long stream,
        int position)
    {
        double pick = random.NextDouble();
        CallProfile profile = pick < 0.6 ? SchedulerRig.Read("light_read")
            : pick < 0.75 ? SchedulerRig.Write()
            : pick < 0.9 ? SchedulerRig.Heavy()
            : SchedulerRig.Write("build_structure", CostClass.Job);
        double costMs = pick < 0.6 ? 0.05 + random.NextDouble() * 0.8
            : pick < 0.75 ? 0.1
            : pick < 0.9 ? 2.0 + random.NextDouble() * 18.0
            : 6.0;
        FakeCall call = new FakeCall($"{index}/{stream}:{position}", costMs, index, profile.IsOrdered)
        {
            Stream = stream,
            Position = position,
            ArrivedAfterFrame = rig.Scheduler.Frame
        };
        return rig.Scheduler.Enqueue(connection, stream, call, profile, double.PositiveInfinity).IsQueued ? call : null;
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

    /// <summary>protocol.md, Order on one stream, checked over the order calls were answered in.</summary>
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
