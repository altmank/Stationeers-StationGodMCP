#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>A clock the fakes move: a call or sample costs exactly what it says.</summary>
internal sealed class FakeClock : IMonotonicClock
{
    public double NowMs { get; private set; }

    internal void Advance(double ms) => NowMs += ms;
}

/// <summary>A call with a name, a main-thread cost and the connection it came on.</summary>
internal sealed class FakeCall
{
    internal FakeCall(string name, double costMs, int connection = 0, bool ordered = false)
    {
        Name = name;
        CostMs = costMs;
        Connection = connection;
        Ordered = ordered;
    }

    internal string Name { get; }

    internal double CostMs { get; }

    internal int Connection { get; }

    internal bool Ordered { get; }

    /// <summary>Its arrival position on its connection.</summary>
    internal int Position { get; set; }

    internal long RanInFrame { get; set; } = -1;

    /// <summary>The clock when it started running.</summary>
    internal double StartedAtMs { get; set; }

    /// <summary>FrameScheduler.Frame when it was queued.</summary>
    internal long ArrivedAfterFrame { get; set; }
}

/// <summary>Runs a call by advancing the clock by its cost, and records what ran and what expired, in order.</summary>
internal sealed class FakeRunner : ICallRunner<FakeCall>
{
    private readonly FakeClock _clock;

    internal FakeRunner(FakeClock clock)
    {
        _clock = clock;
    }

    internal List<FakeCall> Ran { get; } = new List<FakeCall>(4096);

    internal List<FakeCall> Expired { get; } = new List<FakeCall>(4096);

    /// <summary>The frame number stamped on calls as they run.</summary>
    internal long Frame { get; set; }

    public void Run(FakeCall call)
    {
        call.RanInFrame = Frame;
        call.StartedAtMs = _clock.NowMs;
        Ran.Add(call);
        _clock.Advance(call.CostMs);
    }

    public void Expire(FakeCall call) => Expired.Add(call);

    internal List<string> RanNames()
    {
        List<string> names = new List<string>(Ran.Count);
        foreach (FakeCall call in Ran)
        {
            names.Add(call.Name);
        }

        return names;
    }
}

/// <summary>A subscription lane with a number of due samples, each costing the same.</summary>
internal sealed class FakeSamples : ISampleLane
{
    private readonly FakeClock _clock;
    private readonly double _costMs;

    internal FakeSamples(FakeClock clock, double costMs, int due)
    {
        _clock = clock;
        _costMs = costMs;
        Due = due;
    }

    internal int Due { get; set; }

    public bool RunNextDueSample()
    {
        if (Due == 0)
        {
            return false;
        }

        Due--;
        _clock.Advance(_costMs);
        return true;
    }
}

/// <summary>A scheduler over fakes, with helpers that keep the tests to their point.</summary>
internal sealed class SchedulerRig
{
    internal SchedulerRig(SchedulerSettings? settings = null, CostPredictor? predictor = null)
    {
        Settings = settings ?? SchedulerSettings.Default;
        Predictor = predictor ?? new CostPredictor();
        Runner = new FakeRunner(Clock);
        Scheduler = new FrameScheduler<FakeCall>(Settings, Runner, Clock, Predictor);
    }

    internal FakeClock Clock { get; } = new FakeClock();

    internal FakeRunner Runner { get; }

    internal SchedulerSettings Settings { get; }

    internal CostPredictor Predictor { get; }

    internal FrameScheduler<FakeCall> Scheduler { get; }

    internal static SchedulerSettings WithBudget(double requestBudgetMs, double subscriptionBudgetMs = 1.5,
        int maxInFlight = SchedulerSettings.DefaultMaxInFlight) =>
        new SchedulerSettings(requestBudgetMs, subscriptionBudgetMs, SchedulerSettings.DefaultHeavyThresholdMs,
            SchedulerSettings.DefaultHeavyMaxWaitFrames, maxInFlight);

    internal static CallProfile Read(string method = "read_logic", CostClass cost = CostClass.Instant, int items = 1) =>
        new CallProfile(method, MethodClass.Read, new CallCost(cost, items));

    internal static CallProfile Write(string method = "write_logic", CostClass cost = CostClass.Instant) =>
        new CallProfile(method, MethodClass.Write, new CallCost(cost, 1));

    internal static CallProfile Heavy(string method = "grid_survey") => Read(method, CostClass.World);

    internal Admission Light(FrameScheduler<FakeCall>.Connection connection, string name, double costMs = 0.1) =>
        Add(connection, name, Read(), costMs);

    /// <summary>
    /// A slow light call under a method of its own, so the predictor has no history that would move it to the heavy
    /// lane: load that keeps the light lane over budget.
    /// </summary>
    internal Admission Busy(FrameScheduler<FakeCall>.Connection connection, int frame, double costMs = 5.0) =>
        Add(connection, $"busy{frame}", Read($"busy_method_{frame}"), costMs);

    internal Admission Add(FrameScheduler<FakeCall>.Connection connection, string name, CallProfile profile,
        double costMs = 0.1, double deadlineMs = double.PositiveInfinity) =>
        Scheduler.Enqueue(connection, new FakeCall(name, costMs, ordered: profile.IsOrdered), profile, deadlineMs);

    internal FrameOutcome Frame(bool jobHoldsTick = false, ISampleLane? samples = null)
    {
        Runner.Frame = Scheduler.Frame + 1;
        return Scheduler.RunFrame(jobHoldsTick, samples);
    }
}
