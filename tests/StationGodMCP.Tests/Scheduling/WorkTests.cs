#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// Work over frames (A3): a body takes bounded steps and yields; Step goes on to the slice's end, at least one step,
/// and stops at a yield where the call's stop says so; nested bodies chain; the answer is ready once the body ends.
/// </summary>
public sealed class WorkTests
{
    [Fact]
    public void StepGoesOnUntilTheClockReachesTheSlicesEnd()
    {
        FakeClock clock = new FakeClock();
        Work<int> work = new Work<int>(answer => Counting(clock, 10, 1.0, answer));

        Assert.False(work.Step(clock, 3.0));
        Assert.Equal(3, work.Steps);
        Assert.False(work.Step(clock, 6.0));
        Assert.Equal(6, work.Steps);
        Assert.True(work.Step(clock, 100.0));
        Assert.Equal(10, work.Value);
    }

    [Fact]
    public void ASliceWhoseEndHasPassedStillTakesOneStep()
    {
        FakeClock clock = new FakeClock();
        clock.Advance(50.0);
        Work<int> work = new Work<int>(answer => Counting(clock, 5, 1.0, answer));

        work.Step(clock, 0.0);
        work.Step(clock, 0.0);

        Assert.Equal(2, work.Steps);
        Assert.False(work.Done);
    }

    [Fact]
    public void NestedBodiesAreSteppedThroughAndTheirAnswersRead()
    {
        FakeClock clock = new FakeClock();
        Work<int> work = new Work<int>(answer => Outer(clock, answer));

        int slices = 0;
        while (!work.Step(clock, clock.NowMs + 2.0))
        {
            slices++;
        }

        Assert.Equal(3 + 4, work.Value);
        Assert.True(slices >= 3);
    }

    [Fact]
    public void TheStopIsMetAtTheNextYield()
    {
        FakeClock clock = new FakeClock();
        bool stop = false;
        Work<int> work = new Work<int>(answer => StopAfter(2, () => stop = true, answer));

        using (WorkStop.Enter(() => stop))
        {
            Assert.False(work.Step(clock, 1_000.0));
        }

        Assert.Equal(2, work.Steps);
        Assert.False(WorkStop.Requested);
    }

    [Fact]
    public void AnswerIsNotReadyBeforeTheBodyEnds()
    {
        FakeClock clock = new FakeClock();
        Work<int> work = new Work<int>(answer => Counting(clock, 3, 1.0, answer));
        work.Step(clock, 1.0);

        Assert.Throws<InvalidOperationException>(() => work.Value);
    }

    [Fact]
    public void ABodyThatEndsWithoutItsAnswerIsAFault()
    {
        FakeClock clock = new FakeClock();
        Work<int> work = new Work<int>(_ => Empty());

        Assert.True(work.Step(clock, 1.0));
        Assert.Throws<InvalidOperationException>(() => work.Value);
    }

    [Fact]
    public void ABodysExceptionComesOutOfStep()
    {
        FakeClock clock = new FakeClock();
        Work<int> work = new Work<int>(_ => Throwing());

        Assert.Throws<ArithmeticException>(() => work.Step(clock, 100.0));
    }

    private static IEnumerable<object?> Counting(FakeClock clock, int steps, double msEach, Answer<int> answer)
    {
        int done = 0;
        for (int step = 0; step < steps; step++)
        {
            clock.Advance(msEach);
            done++;
            yield return null;
        }

        answer.Value = done;
    }

    private static IEnumerable<object?> Outer(FakeClock clock, Answer<int> answer)
    {
        Answer<int> first = new Answer<int>();
        foreach (object? step in Counting(clock, 3, 1.0, first))
        {
            yield return step;
        }

        Answer<int> second = new Answer<int>();
        foreach (object? step in Counting(clock, 4, 1.0, second))
        {
            yield return step;
        }

        answer.Value = first.Value + second.Value;
    }

    private static IEnumerable<object?> StopAfter(int steps, Action stop, Answer<int> answer)
    {
        for (int step = 1; step <= 10; step++)
        {
            if (step == steps)
            {
                stop();
            }

            yield return null;
        }

        answer.Value = 10;
    }

    private static IEnumerable<object?> Empty()
    {
        yield break;
    }

    private static IEnumerable<object?> Throwing()
    {
        yield return null;
        throw new ArithmeticException("body failed");
    }
}

/// <summary>
/// The job lane (FrameScheduler.RunFrame's fifth lane): the running job's step gets what the frame's calls and the
/// slice lane left of the budget, and over budget a single step once it has waited HeavyMaxWaitFrames frames.
/// </summary>
public sealed class JobLaneTests
{
    [Fact]
    public void TheJobLaneGetsWhatIsLeftOfTheBudgetAfterTheCalls()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(4.0));
        FrameScheduler<FakeCall>.Connection a = rig.Scheduler.Open("agent");
        rig.Light(a, "read", 1.0);
        RecordingLane jobs = new RecordingLane(rig.Clock);

        rig.Runner.Frame = 1;
        rig.Scheduler.RunFrame(false, null, null, jobs);

        Assert.Single(jobs.Untils);
        Assert.Equal(4.0 - FrameBudget.StepReserveMs, jobs.Untils[0], 3);
        Assert.Equal(1.0, jobs.StartedAt[0], 3);
    }

    [Fact]
    public void OverBudgetTheJobLaneWaitsThenTakesOneStep()
    {
        SchedulerSettings settings = SchedulerRig.WithBudget(4.0);
        SchedulerRig rig = new SchedulerRig(settings);
        FrameScheduler<FakeCall>.Connection a = rig.Scheduler.Open("agent");
        RecordingLane jobs = new RecordingLane(rig.Clock);

        for (int frame = 1; frame <= settings.HeavyMaxWaitFrames + 1; frame++)
        {
            rig.Busy(a, frame, 6.0);
            rig.Runner.Frame = frame;
            rig.Scheduler.RunFrame(false, null, null, jobs);
        }

        Assert.Single(jobs.Untils);
    }

    [Fact]
    public void TheSliceLaneAndTheJobLaneShareTheBudgetInThatOrder()
    {
        SchedulerRig rig = new SchedulerRig(SchedulerRig.WithBudget(4.0));
        List<string> order = new List<string>();
        RecordingLane slices = new RecordingLane(rig.Clock, 3.0, order, "slice");
        RecordingLane jobs = new RecordingLane(rig.Clock, 0.0, order, "job");

        rig.Runner.Frame = 1;
        rig.Scheduler.RunFrame(false, null, slices, jobs);

        Assert.Equal(new[] { "slice", "job" }, order);
        Assert.Equal(3.0, jobs.StartedAt[0], 3);
    }

    private sealed class RecordingLane : ISliceLane
    {
        private readonly FakeClock _clock;
        private readonly double _costMs;
        private readonly List<string>? _order;
        private readonly string _name;

        internal RecordingLane(FakeClock clock, double costMs = 0.0, List<string>? order = null, string name = "")
        {
            _clock = clock;
            _costMs = costMs;
            _order = order;
            _name = name;
        }

        internal List<double> Untils { get; } = new List<double>();

        internal List<double> StartedAt { get; } = new List<double>();

        public bool HasWork => true;

        public void RunSlice(double untilMs)
        {
            Untils.Add(untilMs);
            StartedAt.Add(_clock.NowMs);
            _order?.Add(_name);
            _clock.Advance(_costMs);
        }
    }
}
