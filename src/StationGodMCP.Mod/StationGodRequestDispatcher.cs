#nullable enable

using System;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP;

/// <summary>
/// Hands calls from the connections to the main thread and their answers back. Connections post a call to the
/// scheduler's inbox; StationGodMod.Update runs one frame of the lane scheduler on the main thread, where the game's
/// objects may be used (ApiHost.HandleCall): the subscription lane's samples, light calls round-robin across
/// connections within the frame's budget, then at most one heavy call (scheduling.md). Each answer goes straight to its
/// call's Deliver: no thread waits on the main thread. A call the main thread does not reach before its deadline is
/// answered game_timeout by the DeadlineWatch (or by the scheduler when it meets it) unrun; a call it has started is
/// always answered with its result.
/// </summary>
internal sealed class StationGodRequestDispatcher : ICallQueue, ICallRunner
{
    private readonly ICallScheduler _requests;
    private readonly DeadlineWatch _deadlines;

    internal StationGodRequestDispatcher(DeadlineWatch deadlines, ICallScheduler scheduler)
    {
        _deadlines = deadlines;
        _requests = scheduler;
    }

    /// <summary>The per-frame counters mod_info reports.</summary>
    internal static DispatchStats Stats { get; } = new DispatchStats();

    /// <summary>
    /// subscribe, unsubscribe and sample_logic, and the closing of their connections; null before the mod has loaded.
    /// </summary>
    internal SubscriptionHub? Subscriptions { get; set; }

    public void Submit(QueuedCall call)
    {
        _deadlines.Watch(call);
        _requests.Add(call);
    }

    public void Withdraw(QueuedCall call) => _requests.Withdraw(call);

    public void Closed(object source)
    {
        _requests.Close(source);
        Subscriptions?.Closed(source);
    }

    /// <summary>One frame of the scheduler, on the main thread; its outcome goes to mod_info's counters.</summary>
    internal void RunFrame(bool jobHoldsTick, ISampleLane? samples)
    {
        FrameOutcome outcome = _requests.RunFrame(jobHoldsTick, samples, this);
        for (int expired = 0; expired < outcome.ExpiredCalls; expired++)
        {
            Stats.Expired();
        }

        Stats.Frame(outcome.Served, outcome.SpentMs, outcome.BudgetStopped);
    }

    public CallOutcome RunCall(ProtocolCall call, double queueWaitMs)
    {
        try
        {
            if (Subscriptions != null && SubscriptionHub.Handles(call.Request.Method))
            {
                return new CallOutcome(Subscriptions.Run(call, queueWaitMs), call.Request.Method);
            }

            if (Subscriptions != null && call.Request.Method == SubscriptionHub.SampleLogicMethod)
            {
                return Subscriptions.StartSampleLogic(call, queueWaitMs);
            }

            HandledRequest handled = ApiHost.HandleCall(call.Request, queueWaitMs, UnityEngine.Time.frameCount);
            return new CallOutcome(handled.Json, handled.Method);
        }
        catch (Exception exception)
        {
            // ApiHost.HandleCall answers every tool and serializer error itself; this is the error reply failing too.
            return new CallOutcome(
                ApiHost.Serialize(CallReplyView.Refused(call.Request.Id, new ErrorView("internal_error", exception.Message))),
                null);
        }
    }
}
