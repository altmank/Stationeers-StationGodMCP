#nullable enable

using System;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A handler's answer from work spread over frames (Work): stepped in the call's own frame up to the frame's budget,
/// answered at once if it ends there, else deferred (VerbOutcome.Pending) and stepped in the slice lane until it ends;
/// answer then turns the work's value into the reply (or a VerbOutcome, or throws a refusal).
/// </summary>
internal sealed class WorkResult<T> : IPendingResult
{
    private readonly Work<T> _work;
    private readonly Func<T, object> _answer;

    private WorkResult(Work<T> work, Func<T, object> answer)
    {
        _work = work;
        _answer = answer;
    }

    /// <summary>The call's outcome: answered in this frame when the work fits, else Pending.</summary>
    internal static VerbOutcome Of(Work<T> work, Func<T, object> answer)
    {
        WorkResult<T> result = new WorkResult<T>(work ?? throw new ArgumentNullException(nameof(work)),
            answer ?? throw new ArgumentNullException(nameof(answer)));
        return result.Step(CurrentFrame.EndMs) ? VerbOutcome.Of(result.Result()) : new VerbOutcome.Pending(result);
    }

    /// <summary>The call's outcome from a body: see Of.</summary>
    internal static VerbOutcome Of(Func<Answer<T>, System.Collections.Generic.IEnumerable<object?>> body,
        Func<T, object> answer) =>
        Of(new Work<T>(body), answer);

    public bool Step(double untilMs) => _work.Step(StopwatchClock.Instance, untilMs);

    public object Result() => _answer(_work.Value);
}
