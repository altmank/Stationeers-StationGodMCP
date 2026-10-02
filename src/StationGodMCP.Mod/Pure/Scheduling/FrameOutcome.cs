#nullable enable

namespace StationGodMCP.Pure.Scheduling;

/// <summary>Why the scheduler refused a call; nothing ran.</summary>
internal enum AdmissionRefusal
{
    /// <summary>The connection already has SchedulerSettings.MaxInFlight calls waiting: answer too_many_in_flight.</summary>
    TooManyInFlight,

    /// <summary>The connection was closed: there is no one to answer.</summary>
    ConnectionClosed
}

/// <summary>The answer to FrameScheduler.Enqueue: queued in a lane, or refused with a reason.</summary>
internal readonly struct Admission
{
    private Admission(bool queued, Lane lane, AdmissionRefusal refusal)
    {
        IsQueued = queued;
        Lane = lane;
        Refusal = refusal;
    }

    internal bool IsQueued { get; }

    /// <summary>The lane the call waits in; meaningful only when IsQueued.</summary>
    internal Lane Lane { get; }

    /// <summary>Why it was refused; meaningful only when not IsQueued.</summary>
    internal AdmissionRefusal Refusal { get; }

    internal static Admission Queued(Lane lane) => new Admission(true, lane, default);

    internal static Admission Refused(AdmissionRefusal refusal) => new Admission(false, default, refusal);
}

/// <summary>What one frame of the scheduler did.</summary>
internal readonly struct FrameOutcome
{
    internal FrameOutcome(int samples, int lightCalls, int heavyCalls, int expiredCalls, double spentMs,
        bool budgetStopped)
    {
        Samples = samples;
        LightCalls = lightCalls;
        HeavyCalls = heavyCalls;
        ExpiredCalls = expiredCalls;
        SpentMs = spentMs;
        BudgetStopped = budgetStopped;
    }

    /// <summary>Subscription and sample_logic samples taken.</summary>
    internal int Samples { get; }

    internal int LightCalls { get; }

    /// <summary>0 or 1.</summary>
    internal int HeavyCalls { get; }

    /// <summary>Calls answered game_timeout unrun.</summary>
    internal int ExpiredCalls { get; }

    /// <summary>Calls run: light and heavy.</summary>
    internal int Served => LightCalls + HeavyCalls;

    /// <summary>Main-thread milliseconds from the frame's start to its end, samples included.</summary>
    internal double SpentMs { get; }

    /// <summary>The budget ended the light lane, or kept a waiting heavy call back, with calls still queued.</summary>
    internal bool BudgetStopped { get; }
}
