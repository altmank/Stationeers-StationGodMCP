#nullable enable

using System;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>
/// The frame FrameScheduler.RunFrame is running on this thread: its start on the scheduler's clock and its request
/// budget (FrameShares.BudgetMs), set at the top of RunFrame and cleared at its end. Reply builders read it to say
/// whether the frame's request time had reached its budget when their call's handler finished (the reply's
/// budget_hit); they must read the clock the scheduler was given (StopwatchClock in the mod). Thread-static, so a
/// thread outside a frame, or another thread, reads no frame. Nothing is allocated.
/// </summary>
internal static class CurrentFrame
{
    [ThreadStatic]
    private static bool _running;

    [ThreadStatic]
    private static double _startMs;

    [ThreadStatic]
    private static double _budgetMs;

    [ThreadStatic]
    private static double _workEndMs;

    internal static bool Running => _running;

    internal static void Begin(double startMs, double budgetMs, double reserveMs = 0.0)
    {
        _running = true;
        _startMs = startMs;
        _budgetMs = budgetMs;
        _workEndMs = startMs + budgetMs - Math.Min(reserveMs, budgetMs / 2.0);
    }

    internal static void End() => _running = false;

    /// <summary>
    /// Whether, at nowMs on the scheduler's clock, the frame's request time (from its start, samples included) has
    /// reached its budget. False outside a frame and when the budget is unlimited (PositiveInfinity).
    /// </summary>
    internal static bool BudgetReached(double nowMs) =>
        _running && !double.IsPositiveInfinity(_budgetMs) && nowMs - _startMs >= _budgetMs;

    /// <summary>
    /// When, on the scheduler's clock, work sized to the frame (a walk's page, a walk's build, any Work) stops: the
    /// budget's end less the step reserve the scheduler began the frame with (FrameBudget.StepReserveMs);
    /// PositiveInfinity outside a frame and when the budget is unlimited.
    /// </summary>
    internal static double EndMs => _running ? _workEndMs : double.PositiveInfinity;
}
