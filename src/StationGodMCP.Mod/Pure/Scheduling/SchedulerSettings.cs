#nullable enable

using System;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>
/// The [Performance] and [Server] values the frame scheduler works from (scheduling.md, Settings), checked once. A
/// value out of range is a caller error: the config binding refuses it before it gets here.
/// </summary>
internal sealed class SchedulerSettings
{
    internal const double DefaultSubscriptionBudgetMs = 1.5;

    internal const double DefaultHeavyThresholdMs = 1.0;

    internal const int DefaultHeavyMaxWaitFrames = 10;

    /// <summary>protocol.md, limits.max_in_flight.</summary>
    internal const int DefaultMaxInFlight = 16;

    /// <summary>At most this many light calls per frame, so an unlimited budget still bounds a frame.</summary>
    internal const int MaxLightCallsPerFrame = 64;

    /// <summary>The frame rate subscription admission assumes when it turns a per-frame share into a per-second load.</summary>
    internal const double AdmissionFramesPerSecond = 30.0;

    private readonly FrameShares _free;
    private readonly FrameShares _jobHeld;

    internal SchedulerSettings(double requestBudgetMs, double subscriptionBudgetMs, double heavyThresholdMs,
        int heavyMaxWaitFrames, int maxInFlight)
    {
        RequestBudgetMs = NonNegative(requestBudgetMs, nameof(requestBudgetMs));
        SubscriptionBudgetMs = NonNegative(subscriptionBudgetMs, nameof(subscriptionBudgetMs));
        HeavyThresholdMs = NonNegative(heavyThresholdMs, nameof(heavyThresholdMs));
        HeavyMaxWaitFrames = heavyMaxWaitFrames >= 0
            ? heavyMaxWaitFrames
            : throw new ArgumentOutOfRangeException(nameof(heavyMaxWaitFrames), heavyMaxWaitFrames, "Must be 0 or more.");
        MaxInFlight = maxInFlight >= 1
            ? maxInFlight
            : throw new ArgumentOutOfRangeException(nameof(maxInFlight), maxInFlight, "Must be 1 or more.");
        _free = FrameShares.For(requestBudgetMs, subscriptionBudgetMs, false);
        _jobHeld = FrameShares.For(requestBudgetMs, subscriptionBudgetMs, true);
    }

    internal static SchedulerSettings Default { get; } = new SchedulerSettings(FrameBudget.DefaultMs,
        DefaultSubscriptionBudgetMs, DefaultHeavyThresholdMs, DefaultHeavyMaxWaitFrames, DefaultMaxInFlight);

    /// <summary>[Performance] RequestBudgetMs; 0 is unlimited.</summary>
    internal double RequestBudgetMs { get; }

    /// <summary>[Performance] SubscriptionBudgetMs; 0 leaves the subscription lane one sample per frame.</summary>
    internal double SubscriptionBudgetMs { get; }

    /// <summary>[Performance] HeavyThresholdMs: a call predicted above it goes to the heavy lane.</summary>
    internal double HeavyThresholdMs { get; }

    /// <summary>[Performance] HeavyMaxWaitFrames: a heavy call passed over this many frames runs even over budget.</summary>
    internal int HeavyMaxWaitFrames { get; }

    /// <summary>Calls one connection may have waiting in the scheduler.</summary>
    internal int MaxInFlight { get; }

    /// <summary>
    /// The projected sampling load every subscription together may reach: half the subscription lane's share outside
    /// a job, at AdmissionFramesPerSecond (0.75 ms a frame, 22.5 ms a second with the defaults).
    /// </summary>
    internal double SubscriptionAdmissionCapMsPerSecond => _free.SubscriptionMs / 2.0 * AdmissionFramesPerSecond;

    /// <summary>The frame's budget and the subscription lane's share of it.</summary>
    internal FrameShares SharesFor(bool jobHoldsTick) => jobHoldsTick ? _jobHeld : _free;

    private static double NonNegative(double value, string name) =>
        value >= 0.0 && !double.IsInfinity(value)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "Must be a finite number, 0 or more.");
}

/// <summary>
/// One frame's limits: the budget for calls (FrameBudget's rule: 0 configured is unlimited, at most JobHeldMs while a
/// job holds the tick) and the subscription lane's share, which is SubscriptionBudgetMs capped at half the budget.
/// </summary>
internal readonly struct FrameShares
{
    private FrameShares(double budgetMs, double subscriptionMs)
    {
        BudgetMs = budgetMs;
        SubscriptionMs = subscriptionMs;
    }

    /// <summary>PositiveInfinity when unlimited.</summary>
    internal double BudgetMs { get; }

    internal double SubscriptionMs { get; }

    internal static FrameShares For(double requestBudgetMs, double subscriptionBudgetMs, bool jobHoldsTick)
    {
        double budget = requestBudgetMs > 0.0 ? requestBudgetMs : double.PositiveInfinity;
        if (jobHoldsTick)
        {
            budget = Math.Min(budget, FrameBudget.JobHeldMs);
        }

        return new FrameShares(budget, Math.Min(subscriptionBudgetMs, budget / 2.0));
    }
}
