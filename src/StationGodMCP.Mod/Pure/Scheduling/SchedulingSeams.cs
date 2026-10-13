#nullable enable

using System.Diagnostics;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>A monotonic clock in milliseconds: frame budgets, call deadlines and call times are all read from it.</summary>
internal interface IMonotonicClock
{
    double NowMs { get; }
}

/// <summary>The process's Stopwatch, in milliseconds.</summary>
internal sealed class StopwatchClock : IMonotonicClock
{
    internal static StopwatchClock Instance { get; } = new StopwatchClock();

    private StopwatchClock()
    {
    }

    public double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}

/// <summary>Runs the calls the scheduler takes, on the main thread.</summary>
internal interface ICallRunner<in TCall>
{
    /// <summary>Handles the call, serialises its reply and hands it to its connection's outbound queue.</summary>
    void Run(TCall call);

    /// <summary>Answers game_timeout for a call whose deadline passed before it started; must not run it.</summary>
    void Expire(TCall call);

    /// <summary>
    /// Whether the call has its answer. A call Run left unanswered goes on over later frames (a find page, read sample)
    /// and holds its stream until this turns true.
    /// </summary>
    bool IsAnswered(TCall call);
}

/// <summary>
/// Work a started call goes on with over later frames (a walk's build, its call deferred meanwhile), run after the
/// frame's calls in what is left of its request budget (FrameScheduler).
/// </summary>
internal interface ISliceLane
{
    bool HasWork { get; }

    /// <summary>One slice of the work, until the clock reaches untilMs: at least one step, even past it.</summary>
    void RunSlice(double untilMs);
}

/// <summary>
/// The subscription lane's source of work: due subscription samples and read sample samples, oldest due first and
/// round-robin across connections (scheduling.md, Subscriptions, Running late).
/// </summary>
internal interface ISampleLane
{
    /// <summary>Takes and runs the next due sample; false when nothing is due.</summary>
    bool RunNextDueSample();
}
