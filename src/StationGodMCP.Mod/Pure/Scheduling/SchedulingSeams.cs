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

    /// <summary>A deadline timeoutMs from now, on this clock.</summary>
    internal static double DeadlineIn(double timeoutMs) => Instance.NowMs + timeoutMs;
}

/// <summary>Runs the calls the scheduler takes, on the main thread.</summary>
internal interface ICallRunner<in TCall>
{
    /// <summary>Handles the call, serialises its reply and hands it to its connection's outbound queue.</summary>
    void Run(TCall call);

    /// <summary>Answers game_timeout for a call whose deadline passed before it started; must not run it.</summary>
    void Expire(TCall call);
}

/// <summary>
/// The subscription lane's source of work: due subscription samples and sample_logic samples, oldest due first and
/// round-robin across connections (scheduling.md, Subscriptions, Running late).
/// </summary>
internal interface ISampleLane
{
    /// <summary>Takes and runs the next due sample; false when nothing is due.</summary>
    bool RunNextDueSample();
}
