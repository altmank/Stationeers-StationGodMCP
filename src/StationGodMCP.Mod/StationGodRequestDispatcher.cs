#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;

namespace StationGodMCP;

/// <summary>
/// Hands calls from the connections to the main thread and their answers back. Connections (and the TCP and old pipe
/// listener threads, through Dispatch) queue a call; StationGodMod.Update drains the queue on the main thread, where the
/// game's objects may be used (ApiHost.Handle, ApiHost.HandleCall), in the order the scheduler gives (ICallScheduler:
/// each connection in turn, keeping each connection's order rule), within the frame's request budget (FrameBudget: the
/// first request of a frame always runs). Each answer goes straight to its call's Deliver: no thread waits on the main
/// thread. A call the main thread does not reach before its deadline is answered game_timeout by the DeadlineWatch and
/// skipped here unrun; a call it has started is always answered with its result.
/// </summary>
internal sealed class StationGodRequestDispatcher : ICallQueue, ICallRunner
{
    /// <summary>At most this many requests per frame, so a flood cannot stall the game.</summary>
    private const int RequestsPerFrame = 64;

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

    public void Submit(QueuedCall call)
    {
        _deadlines.Watch(call);
        _requests.Add(call);
    }

    internal void ProcessPendingRequests(FrameBudget budget)
    {
        long frameStarted = Stopwatch.GetTimestamp();
        int taken = 0;
        int served = 0;
        bool budgetStopped = false;
        while (taken < RequestsPerFrame)
        {
            if (!budget.MayServeAnother(served, MillisecondsSince(frameStarted)))
            {
                budgetStopped = !_requests.IsEmpty;
                break;
            }

            if (!_requests.TryTake(out QueuedCall? next) || next == null)
            {
                break;
            }

            QueuedCall call = next;

            taken++;
            if (!call.State.TryStart())
            {
                // Already answered (its deadline passed): not run, and not charged to the budget.
                Stats.Expired();
                continue;
            }

            CallOutcome outcome;
            try
            {
                outcome = call.Run(this, MillisecondsSince(call.ReceivedAt));
            }
            catch (Exception exception)
            {
                // ApiHost.Handle answers every tool and serializer error itself; this is the error reply failing too.
                outcome = new CallOutcome(ApiHost.Serialize(
                    new ErrorReplyView(null, new ErrorView("internal_error", exception.Message), null)), null);
            }

            served++;
            if (call.State.TryFinish())
            {
                call.Deliver(outcome.Reply, outcome.Method);
            }
        }

        Stats.Frame(served, MillisecondsSince(frameStarted), budgetStopped);
    }

    public CallOutcome RunLine(string requestJson, double queueWaitMs)
    {
        HandledRequest handled = ApiHost.Handle(requestJson, queueWaitMs);
        return new CallOutcome(handled.Json, handled.Method);
    }

    public CallOutcome RunCall(CallRequest call, double queueWaitMs)
    {
        HandledRequest handled = ApiHost.HandleCall(call, queueWaitMs, UnityEngine.Time.frameCount);
        return new CallOutcome(handled.Json, handled.Method);
    }

    /// <summary>
    /// One version-1 request from a thread that waits for its answer (the TCP listener, the synchronous pipe): queued
    /// as any call, then waited for.
    /// </summary>
    internal string Dispatch(string requestJson, int timeoutMilliseconds)
    {
        WaitedAnswer answer = new WaitedAnswer();
        LineCall call = new LineCall(requestJson, timeoutMilliseconds, null, answer.Set);
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

    private static double MillisecondsSince(long timestamp) =>
        (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

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
