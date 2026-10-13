#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Scheduling;
using Xunit;
using Connection = StationGodMCP.Pure.Scheduling.FrameScheduler<StationGodMCP.Tests.Scheduling.FakeCall>.Connection;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// A call that goes on over frames after it starts (a find page, read sample) holds its (connection, stream) until it
/// answers: no later call of that stream starts meanwhile, reads included; other streams and connections go on; ending
/// the stream or closing the connection lets go of it at once.
/// </summary>
public sealed class FrameSchedulerDeferredTests
{
    [Fact]
    public void ADeferredCallHoldsEveryLaterCallOfItsStreamUntilItAnswers()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        FakeCall find = rig.DeferredOn(a, 1, "find");
        rig.AddOn(a, 1, "write", SchedulerRig.Write());
        rig.LightOn(a, 1, "read1");
        rig.LightOn(a, 2, "read2");

        rig.Frame();
        rig.Frame();
        List<string> whileFinding = rig.Runner.RanNames();
        find.Answered = true;
        rig.Frame();

        Assert.Equal(new[] { "find", "read2" }, whileFinding);
        Assert.Equal(new[] { "find", "read2", "write", "read1" }, rig.Runner.RanNames());
        Assert.Equal(0, rig.Scheduler.Holding);
    }

    [Fact]
    public void AReadArrivingWhileItsStreamIsHeldWaitsAndAnotherConnectionsSameStreamDoesNot()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        Connection b = rig.Scheduler.Open("dashboard");
        FakeCall find = rig.DeferredOn(a, 1, "find");
        rig.Frame();

        rig.LightOn(a, 1, "later");
        rig.LightOn(b, 1, "other");
        rig.Frame();
        List<string> held = rig.Runner.RanNames();
        find.Answered = true;
        rig.Frame();

        Assert.Equal(new[] { "find", "other" }, held);
        Assert.Equal(new[] { "find", "other", "later" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ACallAnsweredDuringTheSampleLaneFreesItsStreamForThatFramesCalls()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        FakeCall run = rig.DeferredOn(a, 1, "read.sample", SchedulerRig.Read("read.sample"));
        rig.Frame();
        rig.LightOn(a, 1, "after");

        rig.Frame(samples: new AnsweringSamples(run));

        Assert.Equal(new[] { "read.sample", "after" }, rig.Runner.RanNames());
    }

    [Fact]
    public void ADeferredCallWaitsForAnEarlierDeferredCallOfItsStream()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        FakeCall first = rig.DeferredOn(a, 1, "first", SchedulerRig.Read("read.sample"));
        FakeCall second = rig.DeferredOn(a, 1, "second", SchedulerRig.Read("read.sample"));
        rig.Frame();
        List<string> firstOnly = rig.Runner.RanNames();
        rig.LightOn(a, 1, "after");

        first.Answered = true;
        rig.Frame();
        List<string> oneLeft = rig.Runner.RanNames();
        second.Answered = true;
        rig.Frame();

        Assert.Equal(new[] { "first" }, firstOnly);
        Assert.Equal(new[] { "first", "second" }, oneLeft);
        Assert.Equal(new[] { "first", "second", "after" }, rig.Runner.RanNames());
    }

    [Fact]
    public void EndingTheStreamLetsGoOfItsHoldEvenUnanswered()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        rig.DeferredOn(a, 1, "find");
        rig.Frame();
        rig.LightOn(a, 1, "dropped");

        Assert.Equal(1, rig.Scheduler.EndStream(a, 1));
        rig.LightOn(a, 1, "next");
        rig.Frame();

        Assert.Equal(new[] { "find", "next" }, rig.Runner.RanNames());
        Assert.Equal(0, rig.Scheduler.Holding);
    }

    [Fact]
    public void ClosingTheConnectionLetsGoOfItsHoldsAndKeepsOthers()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        Connection b = rig.Scheduler.Open("agent");
        rig.DeferredOn(a, 1, "findA");
        rig.DeferredOn(a, 2, "findA2");
        FakeCall findB = rig.DeferredOn(b, 1, "findB");
        rig.Frame();
        rig.Frame();
        rig.LightOn(a, 3, "waiting");

        rig.Scheduler.Close(a);
        rig.LightOn(b, 1, "heldB");
        rig.Frame();

        Assert.Equal(1, rig.Scheduler.Holding);
        Assert.True(a.IsClosed);
        Assert.Empty(a.Live);
        findB.Answered = true;
        rig.Frame();
        Assert.Equal(new[] { "findA", "findA2", "findB", "heldB" }, SortedTail(rig.Runner.RanNames()));
        Assert.Equal(0, rig.Scheduler.Holding);
    }

    [Fact]
    public void ACallPastItsDeadlineOnAHeldStreamIsStillAnsweredTimedOut()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection a = rig.Scheduler.Open("agent");
        FakeCall find = rig.DeferredOn(a, 1, "find");
        rig.Frame();
        rig.Scheduler.Enqueue(a, 1, new FakeCall("late", 0.1) { Stream = 1 }, SchedulerRig.Read(), rig.Clock.NowMs);

        rig.Frame();

        Assert.Equal(new[] { "find" }, rig.Runner.RanNames());
        Assert.Single(rig.Runner.Expired);
        find.Answered = true;
    }

    [Fact]
    public void AFrameWithHeldStreamsAllocatesNothingOnceWarm()
    {
        SchedulerRig rig = new SchedulerRig();
        Connection[] connections = { rig.Scheduler.Open("agent"), rig.Scheduler.Open("hub"), rig.Scheduler.Open() };
        FakeCall[] calls = new FakeCall[4000];
        for (int index = 0; index < calls.Length; index++)
        {
            calls[index] = new FakeCall($"c{index}", 0.2) { Defers = index % 4 == 0 };
        }

        int next = 0;
        long run = 10;
        FakeCall?[] previous = new FakeCall?[connections.Length];
        void Cycle()
        {
            run++;
            for (int index = 0; index < connections.Length; index++)
            {
                if (previous[index] != null)
                {
                    previous[index]!.Answered = true;
                }

                FakeCall deferred = calls[next++];
                deferred.Defers = true;
                previous[index] = deferred;
                rig.Scheduler.Enqueue(connections[index], run, deferred, SchedulerRig.Read(), double.PositiveInfinity);
                rig.Scheduler.Enqueue(connections[index], run, calls[next++], SchedulerRig.Write(), double.PositiveInfinity);
                rig.Scheduler.Enqueue(connections[index], 0, calls[next++], SchedulerRig.Read(), double.PositiveInfinity);
                rig.Scheduler.Enqueue(connections[index], run, calls[next++], SchedulerRig.Read(), double.PositiveInfinity);
                rig.Scheduler.EndStream(connections[index], run - 2);
            }

            rig.Scheduler.RunFrame(false, null);
            rig.Scheduler.RunFrame(false, null);
        }

        for (int warm = 0; warm < 30; warm++)
        {
            Cycle();
        }

        long allocated = ThreadAllocations.Of(() =>
        {
            for (int measured = 0; measured < 30; measured++)
            {
                Cycle();
            }
        });

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// Random reads, writes and deferred calls on a few streams of two connections, deferred calls answering after a
    /// random number of frames and streams ending now and then: no call ever starts on a stream while an earlier
    /// deferred call of that stream is unanswered, and every call not on an ended stream runs.
    /// </summary>
    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void RandomWorkloadsNeverStartACallBehindAnUnansweredDeferredOne(int seed)
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(2.0, maxInFlight: 8, maxInFlightPerConnection: 24));
        Random random = new Random(seed);
        Connection[] connections = { rig.Scheduler.Open("agent"), rig.Scheduler.Open("agent") };
        long[] epoch = new long[6];
        Dictionary<(int, long), List<FakeCall>> sent = new Dictionary<(int, long), List<FakeCall>>();
        Dictionary<FakeCall, long> answerAtFrame = new Dictionary<FakeCall, long>();
        Dictionary<FakeCall, long> answeredInFrame = new Dictionary<FakeCall, long>();
        HashSet<FakeCall> dropped = new HashSet<FakeCall>();
        int ranBefore = 0;
        for (int frame = 1; frame <= 800; frame++)
        {
            foreach (KeyValuePair<FakeCall, long> pending in answerAtFrame)
            {
                if (pending.Key.RanInFrame >= 0 && !pending.Key.Answered && pending.Value <= frame)
                {
                    pending.Key.Answered = true;
                    answeredInFrame[pending.Key] = frame;
                }
            }

            for (int index = 0; index < connections.Length && frame <= 600; index++)
            {
                int slot = random.Next(3);
                long stream = slot + 3 * epoch[index * 3 + slot];
                if (random.NextDouble() < 0.01)
                {
                    rig.Scheduler.EndStream(connections[index], stream);
                    foreach (FakeCall call in Sent(sent, index, stream))
                    {
                        if (call.RanInFrame < 0)
                        {
                            dropped.Add(call);
                        }
                    }

                    epoch[index * 3 + slot]++;
                    continue;
                }

                if (random.NextDouble() < 0.6)
                {
                    double pick = random.NextDouble();
                    FakeCall call = new FakeCall($"{index}/{stream}/{frame}", 0.3, index, ordered: pick >= 0.85)
                        { Stream = stream, Defers = pick < 0.2 };
                    CallProfile profile = call.Ordered ? SchedulerRig.Write() : SchedulerRig.Read();
                    if (rig.Scheduler.Enqueue(connections[index], stream, call, profile, double.PositiveInfinity).IsQueued)
                    {
                        Sent(sent, index, stream).Add(call);
                        if (call.Defers)
                        {
                            answerAtFrame[call] = frame + random.Next(1, 6);
                        }
                    }
                }
            }

            rig.Frame();
            List<FakeCall> ran = rig.Runner.Ran;
            for (int index = ranBefore; index < ran.Count; index++)
            {
                FakeCall call = ran[index];
                foreach (FakeCall earlier in Sent(sent, call.Connection, call.Stream))
                {
                    if (ReferenceEquals(earlier, call))
                    {
                        break;
                    }

                    if (earlier.Defers && !dropped.Contains(earlier))
                    {
                        Assert.True(earlier.RanInFrame >= 0 && answeredInFrame.TryGetValue(earlier, out long at) &&
                                    at <= call.RanInFrame,
                            $"{call.Name} started before the deferred {earlier.Name} answered");
                    }
                }
            }

            ranBefore = ran.Count;
        }

        foreach (List<FakeCall> calls in sent.Values)
        {
            foreach (FakeCall call in calls)
            {
                Assert.True(call.RanInFrame >= 0 || dropped.Contains(call), $"{call.Name} never ran");
            }
        }
    }

    private static List<FakeCall> Sent(Dictionary<(int, long), List<FakeCall>> sent, int connection, long stream)
    {
        if (!sent.TryGetValue((connection, stream), out List<FakeCall>? calls))
        {
            calls = new List<FakeCall>();
            sent.Add((connection, stream), calls);
        }

        return calls;
    }

    private static List<string> SortedTail(List<string> names)
    {
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>A sample lane whose one sample answers a deferred call, as read sample's last sample does.</summary>
    private sealed class AnsweringSamples : ISampleLane
    {
        private FakeCall? _call;

        internal AnsweringSamples(FakeCall call)
        {
            _call = call;
        }

        public bool RunNextDueSample()
        {
            if (_call == null)
            {
                return false;
            }

            _call.Answered = true;
            _call = null;
            return true;
        }
    }
}
