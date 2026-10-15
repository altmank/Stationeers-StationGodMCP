#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>
/// Where a body of work (Work) puts its answer: an iterator has no out parameters, so a body is handed one of these and
/// sets it before it ends. A nested body gets its own, and its caller reads it once the nested body's steps are taken.
/// </summary>
internal sealed class Answer<T>
{
    private T _value = default!;

    internal bool IsSet { get; private set; }

    internal T Value
    {
        get => IsSet ? _value : throw new InvalidOperationException("The body ended without its answer.");
        set
        {
            _value = value;
            IsSet = true;
        }
    }
}

/// <summary>
/// The stop of the call whose work is being stepped (scheduling.md, Work over frames): cancel requested or its deadline
/// passed. DeferredCalls sets it around each slice; a Work step meets it at its next yield and returns, and the slice
/// lane then answers the call. Thread-static, main thread only; nothing is allocated per check.
/// </summary>
internal static class WorkStop
{
    [ThreadStatic]
    private static Func<bool>? _current;

    /// <summary>Whether the work being stepped must stop now; false outside a slice that set a stop.</summary>
    internal static bool Requested => _current != null && _current();

    /// <summary>Sets the stop for the slice; dispose to clear it (it puts back what was set before).</summary>
    internal static Scope Enter(Func<bool>? stop)
    {
        Func<bool>? before = _current;
        _current = stop;
        return new Scope(before);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Func<bool>? _before;

        internal Scope(Func<bool>? before) => _before = before;

        public void Dispose() => _current = _before;
    }
}

/// <summary>
/// The one form of work spread over frames (A3; scheduling.md, Work over frames): a body is an iterator that does a
/// bounded step of its work and yields (one candidate, one batch of a search's expansions, one piece of an apply), and
/// sets its Answer before it ends. Nested bodies are chained by yielding each of their steps
/// (foreach (object? step in Sub(answer)) yield return step;). Step takes the body's steps while a step as long as
/// the last one would still end within the slice, at least one, and stops early at a yield where the running call's
/// stop says so (WorkStop), so a slice overruns its end only by a step longer than the one before it. Main thread
/// only; a body that throws throws out of Step.
/// </summary>
internal sealed class Work<T>
{
    private readonly Answer<T> _answer = new Answer<T>();
    private IEnumerator<object?>? _steps;

    internal Work(Func<Answer<T>, IEnumerable<object?>> body)
    {
        if (body == null)
        {
            throw new ArgumentNullException(nameof(body));
        }

        _steps = body(_answer).GetEnumerator();
    }

    /// <summary>Whether the body has ended and its answer is ready.</summary>
    internal bool Done => _steps == null;

    /// <summary>The steps taken so far.</summary>
    internal long Steps { get; private set; }

    /// <summary>The body's answer, once Done.</summary>
    internal T Value => Done ? _answer.Value : throw new InvalidOperationException("The work has not ended yet.");

    /// <summary>
    /// Takes steps while the last step's length still fits before untilMs, at least one, or until the stop is met;
    /// true once Done.
    /// </summary>
    internal bool Step(IMonotonicClock clock, double untilMs)
    {
        IEnumerator<object?>? steps = _steps;
        if (steps == null)
        {
            return true;
        }

        WorkSlice.Scope slice = WorkSlice.Enter(clock, untilMs);
        try
        {
            return StepWithin(steps, clock, untilMs);
        }
        finally
        {
            slice.Dispose();
        }
    }

    private bool StepWithin(IEnumerator<object?> steps, IMonotonicClock clock, double untilMs)
    {
        double stepStarted = clock.NowMs;
        while (true)
        {
            long began = WorkSteps.Long != null ? Stopwatch.GetTimestamp() : 0;
            if (!steps.MoveNext())
            {
                steps.Dispose();
                _steps = null;
                return true;
            }

            Steps++;
            if (began != 0)
            {
                WorkSteps.Measured(steps, began);
            }

            // The next step is taken only while one as long as this one still ends within the slice.
            double now = clock.NowMs;
            double lastStepMs = now - stepStarted;
            stepStarted = now;
            if (now >= untilMs || now + lastStepMs > untilMs || WorkStop.Requested)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// The slice the running Work is stepped to (Work.Step: its clock and its end; the earliest end when steps nest): a
/// body whose own batch can outlast its slice (a route search's expansions) asks Over whether its step has run long
/// enough, so the slice ends on time (review D, R-D-12). Outside a slice the clock is the stopwatch and the end
/// PositiveInfinity. Thread-static; nothing is allocated.
/// </summary>
internal static class WorkSlice
{
    [ThreadStatic]
    private static IMonotonicClock? _clock;

    [ThreadStatic]
    private static double _endMs;

    /// <summary>The slice's end on its clock; PositiveInfinity outside a slice.</summary>
    internal static double EndMs => _clock != null ? _endMs : double.PositiveInfinity;

    /// <summary>Now, on the slice's clock (the stopwatch outside a slice).</summary>
    internal static double NowMs => (_clock ?? StopwatchClock.Instance).NowMs;

    /// <summary>
    /// Whether a step a body began at startedMs (NowMs then) should yield now: it has run maxStepMs, or it began before
    /// the slice's end and the end has passed (a step begun after the end is the one step a slice always takes).
    /// </summary>
    internal static bool Over(double startedMs, double maxStepMs)
    {
        double now = NowMs;
        double end = EndMs;
        return now - startedMs >= maxStepMs || (startedMs < end && now >= end);
    }

    internal static Scope Enter(IMonotonicClock clock, double endMs)
    {
        Scope before = new Scope(_clock, _endMs);
        if (_clock == null || endMs < _endMs)
        {
            _endMs = endMs;
        }

        _clock = clock;
        return before;
    }

    /// <summary>The slice before Enter, put back when disposed.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly IMonotonicClock? _clockBefore;
        private readonly double _endBefore;

        internal Scope(IMonotonicClock? clockBefore, double endBefore)
        {
            _clockBefore = clockBefore;
            _endBefore = endBefore;
        }

        public void Dispose()
        {
            _clock = _clockBefore;
            _endMs = _endBefore;
        }
    }
}

/// <summary>
/// A step of Work that took longer than LongStepMs breaks the A3 rule (a step is bounded; a frame overruns its budget by
/// one step at most): the mod is told the body (its iterator's declaring type and method) and the step's milliseconds,
/// and logs it rate-limited. Null keeps nothing and times nothing.
/// </summary>
internal static class WorkSteps
{
    internal const double LongStepMs = 1.0;

    internal static Action<string, double>? Long { get; set; }

    internal static void Measured(object steps, long began)
    {
        double ms = (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
        if (ms > LongStepMs)
        {
            Type body = steps.GetType();
            Long?.Invoke((body.DeclaringType?.Name ?? "?") + "." + body.Name, ms);
        }
    }
}
