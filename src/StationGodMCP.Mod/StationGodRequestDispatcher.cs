#nullable enable

using System;
using System.Diagnostics;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Verbs;

namespace StationGodMCP;

/// <summary>
/// Hands calls from the connections to the main thread and their answers back. Connections post a call to the
/// scheduler's inbox; StationGodMod.Update runs one frame of the lane scheduler on the main thread, where the game's
/// objects may be used (VerbCalls for a verb, the protocol methods' own runners): the subscription lane's samples, light calls in rounds by client and
/// then by stream within the frame's budget, then at most one heavy call (scheduling.md). Each answer goes straight to its
/// call's Deliver: no thread waits on the main thread. A call the main thread does not reach before its deadline is
/// answered game_timeout by the DeadlineWatch (or by the scheduler when it meets it) unrun; a call it has started is
/// always answered with its result.
/// </summary>
internal sealed class StationGodRequestDispatcher : ICallQueue, ICallRunner
{
    private readonly ICallScheduler _requests;
    private readonly DeadlineWatch _deadlines;
    private readonly StreamCostCalls _costs;
    private HeldRestores? _restores;
    private HoldRestoreCalls? _restoreCalls;

    internal StationGodRequestDispatcher(DeadlineWatch deadlines, ICallScheduler scheduler)
    {
        _deadlines = deadlines;
        _requests = scheduler;
        _costs = new StreamCostCalls(scheduler.Costs);
    }

    /// <summary>The per-frame counters mod_info reports.</summary>
    internal static DispatchStats Stats { get; } = new DispatchStats();

    /// <summary>
    /// subscribe, unsubscribe and read sample, and the closing of their connections; null before the mod has loaded.
    /// </summary>
    internal SubscriptionHub? Subscriptions { get; set; }

    /// <summary>The held restores hold_restore changes; null before the mod has loaded.</summary>
    internal HeldRestores? Restores
    {
        get => _restores;
        set
        {
            _restores = value;
            _restoreCalls = value == null ? null : new HoldRestoreCalls(value);
        }
    }

    public void Submit(QueuedCall call)
    {
        _deadlines.Watch(call);
        _requests.Add(call);
    }

    public void Withdraw(QueuedCall call) => _requests.Withdraw(call);

    public void StreamEnded(object source, StreamEnd end)
    {
        _requests.EndStreams(source, end);
        Subscriptions?.StreamEnded(source, end);
    }

    public void CursorClosed(string owner, string cursorId) => _requests.CloseCursor(owner, cursorId);

    public void PinsReleased(string owner, long stream) => _requests.ReleasePins(owner, stream);

    public void SessionEnded(string owner) => _requests.EndSession(owner);

    public void Closed(object source)
    {
        _requests.Close(source);
        Subscriptions?.Closed(source);
    }

    public void OwnerHeld(string owner) => Subscriptions?.Held(owner);

    public void OwnerResumed(string owner, object source) => Subscriptions?.Resumed(owner, source);

    /// <summary>One frame of the scheduler, on the main thread; its outcome goes to mod_info's counters.</summary>
    internal void RunFrame(bool jobHoldsTick, ISampleLane? samples)
    {
        FrameOutcome outcome;
        using (Prof.Scope(ProfId.RunFrame))
        {
            outcome = _requests.RunFrame(jobHoldsTick, samples, this);
        }

        for (int expired = 0; expired < outcome.ExpiredCalls; expired++)
        {
            Stats.Expired();
        }

        Stats.Frame(outcome.Served, outcome.SpentMs, outcome.BudgetStopped);
    }

    private const string UnknownMethod = "unknown";

    private static readonly JobDropCalls Drops = new JobDropCalls(HeldTickJobs.DropWaiting);

    private static readonly VerbRunner Verbs = new VerbRunner(VerbHandlers.Dispatcher, ApiHost.MaxReplyBytes,
        static () => UnityEngine.Time.frameCount, MethodStats.Record);

    // A method name the frame's call list, the slow-frame log and a connection's counts may show: one the mod answers
    // (a verb call by its what, find.thing), else "unknown".
    private static string KnownMethod(ProtocolCall call)
    {
        string method = call.Request.Method;
        if (VerbCalls.Handles(method))
        {
            return VerbHandlers.Dispatcher.AnswersKey(call.Profile.Method) ? call.Profile.Method : method;
        }

        return ProtocolMethods.RunsOnMainThread(method) ? method : MethodStats.Counted(method) ?? UnknownMethod;
    }

    /// <summary>
    /// Runs one call on the main thread, and counts it for its connection (mod_info connections: calls, tool errors
    /// and main-thread ms per known method; subscribe, unsubscribe, read sample, drop_job, stream_costs,
    /// hold_restore and inspect_controllers count as answered). The lane scheduler times the same span for the call's
    /// stream (stream_costs), and adds the slices of a call deferred over frames to both.
    /// </summary>
    public CallOutcome RunCall(ProtocolCall call, double queueWaitMs)
    {
        long began = Stopwatch.GetTimestamp();
        long started = Prof.CallStarted();
        bool ok = true;
        try
        {
            if (Subscriptions != null && SubscriptionHub.Handles(call.Request.Method))
            {
                return new CallOutcome(Subscriptions.Run(call, queueWaitMs), call.Request.Method);
            }

            if (JobDropCalls.Handles(call.Request.Method))
            {
                return new CallOutcome(Drops.Run(call, queueWaitMs, UnityEngine.Time.frameCount), call.Request.Method);
            }

            if (_restoreCalls != null && HoldRestoreCalls.Handles(call.Request.Method))
            {
                return new CallOutcome(_restoreCalls.Run(call, queueWaitMs, UnityEngine.Time.frameCount),
                    call.Request.Method);
            }

            if (StreamCostCalls.Handles(call.Request.Method))
            {
                return new CallOutcome(_costs.Run(call, queueWaitMs, UnityEngine.Time.frameCount), call.Request.Method);
            }

            if (VerbCalls.Handles(call.Request.Method))
            {
                return VerbCalls.Run(Verbs, call, queueWaitMs);
            }

            HandledRequest handled = ApiHost.HandleCall(call.Request, queueWaitMs, UnityEngine.Time.frameCount);
            ok = handled.Ok;
            return new CallOutcome(handled.Json, handled.Method);
        }
        catch (Exception exception)
        {
            ok = false;
            // ApiHost.HandleCall answers every tool and serializer error itself; this is the error reply failing too.
            return new CallOutcome(
                ApiHost.Serialize(CallReplyView.Refused(call.Request.Id, new ErrorView("internal_error", exception.Message))),
                null);
        }
        finally
        {
            string method = KnownMethod(call);
            if (method != UnknownMethod)
            {
                call.CountOnConnection(method, ok, (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency);
            }

            if (started != 0)
            {
                Prof.CallEnded(method, started);
            }
        }
    }
}
