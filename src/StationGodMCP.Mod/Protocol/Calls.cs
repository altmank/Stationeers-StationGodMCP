#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Protocol;

/// <summary>
/// One request on its way to the main thread. Its CallState makes sure it is answered once: by the main thread with
/// its result after running it, or without running by the deadline watch, a cancel or a shutdown. The answer goes to
/// Deliver, which hands it to its connection's outbound queue.
/// </summary>
internal abstract class QueuedCall
{
    protected QueuedCall(long deadlineAt, CallProfile profile)
    {
        ReceivedAt = Stopwatch.GetTimestamp();
        DeadlineAt = deadlineAt;
        Profile = profile;
    }

    internal CallState State { get; } = new CallState();

    /// <summary>Stopwatch.GetTimestamp when it was received.</summary>
    internal long ReceivedAt { get; }

    /// <summary>Stopwatch.GetTimestamp after which, still queued, it is answered game_timeout unrun.</summary>
    internal long DeadlineAt { get; }

    /// <summary>The connection it came from, for taking calls from each connection in turn; null for none.</summary>
    internal virtual object? Source => null;

    /// <summary>
    /// Its method, effective class and cost at its arguments, worked out on the thread that received it. The class
    /// decides its order on its connection (a write or cheat call starts only after every earlier call of its
    /// connection is answered, and holds back every later one); the cost decides its lane.
    /// </summary>
    internal CallProfile Profile { get; }

    /// <summary>Whether it is held to the order of a write call.</summary>
    internal bool IsWrite => Profile.IsOrdered;

    /// <summary>Runs the call on the main thread, through the game-side runner.</summary>
    internal abstract CallOutcome Run(ICallRunner runner, double queueWaitMs);

    /// <summary>The game_timeout answer, built on any thread.</summary>
    internal abstract string TimeoutReply();

    /// <summary>A refusal of a call that did not run (too_many_in_flight), built on any thread.</summary>
    internal abstract string RefusalReply(string code, string message);

    /// <summary>Hands an answer on. Called once, by whoever won the call's state.</summary>
    internal abstract void Deliver(string reply, string? method);

    /// <summary>Answers a call that ran with its result; only the run that started it delivers.</summary>
    internal void Finish(string reply, string? method)
    {
        if (State.TryFinish())
        {
            Deliver(reply, method);
        }
    }

    /// <summary>Answers a call that did not run; false when something answered it first.</summary>
    internal bool Drop(string reply)
    {
        if (!State.TryDrop())
        {
            return false;
        }

        Deliver(reply, null);
        return true;
    }

    internal static long DeadlineAfter(int milliseconds) =>
        Stopwatch.GetTimestamp() + (long)(milliseconds * (double)Stopwatch.Frequency / 1000.0);

    internal static double MillisecondsSince(long timestamp) =>
        (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// A call's answer from the main thread: the reply line and the known method it named, if any. Deferred when the call
/// goes on over later frames (sample_logic): its runner answers it with QueuedCall.Finish once it ends.
/// </summary>
internal readonly struct CallOutcome
{
    internal CallOutcome(string reply, string? method)
    {
        Reply = reply;
        Method = method;
    }

    /// <summary>The call is still running: nothing to deliver now.</summary>
    internal static CallOutcome Deferred => default;

    internal bool IsDeferred => Reply == null;

    internal string Reply { get; }

    internal string? Method { get; }
}

/// <summary>Runs calls on the main thread (StationGodRequestDispatcher, through ApiHost).</summary>
internal interface ICallRunner
{
    /// <summary>A call, answered with a reply message.</summary>
    CallOutcome RunCall(ProtocolCall call, double queueWaitMs);
}

/// <summary>A call as the main thread runs it: id, method, params and shape, already read.</summary>
internal sealed class CallRequest
{
    internal CallRequest(string id, string method, JObject? parameters, ShapeRequest? shape, bool argumentNamesChecked)
    {
        Id = id;
        Method = method;
        Params = parameters;
        Shape = shape;
        ArgumentNamesChecked = argumentNamesChecked;
    }

    internal string Id { get; }

    internal string Method { get; }

    internal JObject? Params { get; }

    internal ShapeRequest? Shape { get; }

    /// <summary>
    /// Whether the connection's reader thread already refused every top-level argument name the method does not take
    /// (StrictArguments, a catalogued method whose params are closed), so the main thread need not look again.
    /// </summary>
    internal bool ArgumentNamesChecked { get; }
}

/// <summary>
/// A call in flight on a connection. Answers it never ran for (timeout, cancel, shutdown) are reply messages
/// without elapsed_ms or frame.
/// </summary>
internal sealed class ProtocolCall : QueuedCall
{
    internal const string TimeoutCode = "game_timeout";
    internal const string CancelledCode = "cancelled";
    internal const string ShuttingDownCode = "shutting_down";

    private readonly Action<ProtocolCall, string, string?> _deliver;
    private readonly object _source;

    internal ProtocolCall(CallRequest request, CallProfile profile, int deadlineMilliseconds, object source,
        Action<ProtocolCall, string, string?> deliver) : base(DeadlineAfter(deadlineMilliseconds), profile)
    {
        Request = request;
        _source = source;
        _deliver = deliver;
    }

    internal CallRequest Request { get; }

    internal override object? Source => _source;

    internal override CallOutcome Run(ICallRunner runner, double queueWaitMs) => runner.RunCall(this, queueWaitMs);

    internal override string TimeoutReply() => Wire.Refusal(Request.Id, TimeoutCode,
        "The call was not started before its deadline; it did not run.");

    internal override string RefusalReply(string code, string message) => Wire.Refusal(Request.Id, code, message);

    internal string CancelledReply() => Wire.Refusal(Request.Id, CancelledCode, "Cancelled before it started; it did not run.");

    internal string ShuttingDownReply() => Wire.Refusal(Request.Id, ShuttingDownCode,
        "The mod is stopping or the world is unloading; the call was not started.");

    internal override void Deliver(string reply, string? method) => _deliver(this, reply, method);
}

/// <summary>Where calls wait for the main thread. Every member may be called from any thread.</summary>
internal interface ICallQueue
{
    void Submit(QueuedCall call);

    /// <summary>A queued call was answered without running (cancelled): it stops holding a place.</summary>
    void Withdraw(QueuedCall call);

    /// <summary>The connection ended: its waiting calls are dropped, and what it had going on the main thread ends.</summary>
    void Closed(object source);
}

/// <summary>
/// The one timer for every call in flight: every 100 ms it answers game_timeout, unrun, each call still queued past
/// its deadline. A call the main thread has started is never timed out; it is answered with its result.
/// </summary>
internal sealed class DeadlineWatch : IDisposable
{
    internal const int PeriodMilliseconds = 100;

    private readonly ConcurrentDictionary<QueuedCall, byte> _queued = new ConcurrentDictionary<QueuedCall, byte>();
    private readonly Timer _timer;
    private int _checking;
    private long _timedOut;

    internal DeadlineWatch()
    {
        _timer = new Timer(Check, null, PeriodMilliseconds, PeriodMilliseconds);
    }

    /// <summary>Calls answered game_timeout so far.</summary>
    internal long TimedOut => Interlocked.Read(ref _timedOut);

    internal void Watch(QueuedCall call) => _queued.TryAdd(call, 0);

    /// <summary>Answers every call still queued with what answer gives, unrun (the server is stopping).</summary>
    internal void DropAll(Func<QueuedCall, string> answer)
    {
        foreach (QueuedCall call in _queued.Keys)
        {
            _queued.TryRemove(call, out _);
            if (call.State.TryDrop())
            {
                call.Deliver(answer(call), null);
            }
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Check(object? state)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return;
        }

        try
        {
            long now = Stopwatch.GetTimestamp();
            foreach (QueuedCall call in _queued.Keys)
            {
                if (!call.State.IsQueued)
                {
                    _queued.TryRemove(call, out _);
                    continue;
                }

                if (now < call.DeadlineAt || !call.State.TryDrop())
                {
                    continue;
                }

                _queued.TryRemove(call, out _);
                Interlocked.Increment(ref _timedOut);
                Deliver(call);
            }
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private static void Deliver(QueuedCall call)
    {
        try
        {
            call.Deliver(call.TimeoutReply(), null);
        }
        catch (Exception exception)
        {
            // A timer callback must not throw: that would end the process. The call's client loses only this answer.
            ProtocolLog.Warning($"Could not answer a timed-out call: {exception.Message}");
        }
    }
}
