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
/// (foreach (object? step in Sub(answer)) yield return step;). Step takes the body's steps until the clock reaches the
/// slice's end, at least one, and stops early at a yield where the running call's stop says so (WorkStop), so a slice
/// overruns its end by one step at most. Main thread only; a body that throws throws out of Step.
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

    /// <summary>Takes steps until the clock reaches untilMs, at least one, or the stop is met; true once Done.</summary>
    internal bool Step(IMonotonicClock clock, double untilMs)
    {
        IEnumerator<object?>? steps = _steps;
        if (steps == null)
        {
            return true;
        }

        do
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
        }
        while (clock.NowMs < untilMs && !WorkStop.Requested);

        return false;
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
