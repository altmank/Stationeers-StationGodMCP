#nullable enable

using System;
using System.Text;
using System.Threading;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP;

/// <summary>
/// Hands calls from the connections to the main thread and their answers back. Connections (and the synchronous pipe's
/// listener threads, through Dispatch) post a call to the scheduler's inbox; StationGodMod.Update runs one frame of
/// the lane scheduler on the main thread, where the game's objects may be used (ApiHost.Handle, ApiHost.HandleCall):
/// the subscription lane's samples, light calls round-robin across connections within the frame's budget, then at most
/// one heavy call (scheduling.md). Each answer goes straight to its call's Deliver: no thread waits on the main thread.
/// A call the main thread does not reach before its deadline is answered game_timeout by the DeadlineWatch (or by the
/// scheduler when it meets it) unrun; a call it has started is always answered with its result.
/// </summary>
internal sealed class StationGodRequestDispatcher : ICallQueue, ICallRunner
{
    /// <summary>How much longer than its deadline Dispatch waits for an answer the watch must already have given.</summary>
    private const int DispatchGraceMilliseconds = 5000;

    private readonly ICallScheduler _requests;
    private readonly DeadlineWatch _deadlines;

    internal StationGodRequestDispatcher(DeadlineWatch deadlines, ICallScheduler scheduler)
    {
        _deadlines = deadlines;
        _requests = scheduler;
    }

    /// <summary>The per-frame counters mod_info reports.</summary>
    internal static DispatchStats Stats { get; } = new DispatchStats();

    /// <summary>subscribe and unsubscribe, and the closing of their connections; null before the mod has loaded.</summary>
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

    public CallOutcome RunLine(LineCall call, double queueWaitMs)
    {
        try
        {
            HandledRequest handled = ApiHost.Handle(call.Json, queueWaitMs);
            return new CallOutcome(handled.Json, handled.Method);
        }
        catch (Exception exception)
        {
            // ApiHost.Handle answers every tool and serializer error itself; this is the error reply failing too.
            return Failed(exception);
        }
    }

    public CallOutcome RunCall(ProtocolCall call, double queueWaitMs)
    {
        try
        {
            if (Subscriptions != null && SubscriptionHub.Handles(call.Request.Method))
            {
                return new CallOutcome(Subscriptions.Run(call, queueWaitMs), call.Request.Method);
            }

            HandledRequest handled = ApiHost.HandleCall(call.Request, queueWaitMs, UnityEngine.Time.frameCount);
            return new CallOutcome(handled.Json, handled.Method);
        }
        catch (Exception exception)
        {
            // As in RunLine: the error reply itself failed.
            return Failed(exception);
        }
    }

    /// <summary>
    /// One version-1 request from a thread that waits for its answer (the synchronous pipe): queued as any call, then
    /// waited for.
    /// </summary>
    internal string Dispatch(string requestJson, int timeoutMilliseconds)
    {
        WaitedAnswer answer = new WaitedAnswer();
        LineCall call = new LineCall(requestJson, timeoutMilliseconds, null, answer.Set,
            CallProfiles.OfLine(ApiHost.CatalogueFile, requestJson));
        Submit(call);
        if (!answer.Wait(timeoutMilliseconds + DispatchGraceMilliseconds) && call.State.TryDrop())
        {
            answer.Set(call.TimeoutReply(), null);
        }

        answer.Wait(Timeout.Infinite);
        // Counted here, off the main thread, from the text the listener is about to write.
        MethodStats.RecordReply(answer.Method, Encoding.UTF8.GetByteCount(answer.Reply!));
        return answer.Reply!;
    }

    private static CallOutcome Failed(Exception exception) =>
        new CallOutcome(ApiHost.Serialize(new ErrorReplyView(null, new ErrorView("internal_error", exception.Message), null)),
            null);

    private sealed class WaitedAnswer
    {
        // Not disposed, on purpose: a ManualResetEventSlim makes a kernel event only when its WaitHandle is asked for,
        // and only Wait(int) and Set are used here.
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);

        internal string? Reply { get; private set; }

        internal string? Method { get; private set; }

        internal void Set(string reply, string? method)
        {
            Reply = reply;
            Method = method;
            _done.Set();
        }

        internal bool Wait(int milliseconds) => _done.Wait(milliseconds);
    }
}
