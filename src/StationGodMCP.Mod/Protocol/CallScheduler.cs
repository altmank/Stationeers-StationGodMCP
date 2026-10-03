#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// The order in which the main thread runs queued calls. Add, Withdraw and Close run on any thread and only post to
/// an inbox; RunFrame, on the main thread, takes the inbox in and runs one frame of calls, each to its answer (or, for
/// a deferred call, to its start) before the next.
/// </summary>
internal interface ICallScheduler
{
    void Add(QueuedCall call);

    /// <summary>A call answered without running (cancelled) leaves its lane.</summary>
    void Withdraw(QueuedCall call);

    /// <summary>The connection ended: its waiting calls are dropped unrun and it takes no more.</summary>
    void Close(object source);

    /// <summary>One frame: the subscription lane's samples, then light calls, then at most one heavy call.</summary>
    FrameOutcome RunFrame(bool jobHoldsTick, ISampleLane? samples, ICallRunner runner);
}

/// <summary>
/// The lane scheduler (scheduling.md): listener threads post calls to a concurrent inbox; each frame the main thread
/// sorts what arrived into FrameScheduler's lanes and runs the frame. A connection's place in the rounds is its
/// Connection object; calls without one (the synchronous pipe) share one place. A call the scheduler refuses beyond
/// max_in_flight is answered too_many_in_flight unrun.
/// </summary>
internal sealed class LaneScheduler : ICallScheduler, ICallRunner<QueuedCall>
{
    private static readonly object NoSource = new object();

    private readonly ConcurrentQueue<Posted> _inbox = new ConcurrentQueue<Posted>();
    private readonly Dictionary<object, FrameScheduler<QueuedCall>.Connection> _connections =
        new Dictionary<object, FrameScheduler<QueuedCall>.Connection>();
    private readonly FrameScheduler<QueuedCall> _scheduler;
    private ICallRunner? _runner;

    internal LaneScheduler(SchedulerSettings settings)
    {
        Settings = settings;
        _scheduler = new FrameScheduler<QueuedCall>(settings, this, StopwatchClock.Instance, new CostPredictor());
    }

    internal SchedulerSettings Settings { get; }

    public void Add(QueuedCall call) => _inbox.Enqueue(new Posted(PostKind.Add, call, call.Source ?? NoSource));

    public void Withdraw(QueuedCall call) => _inbox.Enqueue(new Posted(PostKind.Withdraw, call, call.Source ?? NoSource));

    public void Close(object source) => _inbox.Enqueue(new Posted(PostKind.Close, null, source));

    public FrameOutcome RunFrame(bool jobHoldsTick, ISampleLane? samples, ICallRunner runner)
    {
        _runner = runner;
        TakeInbox();
        return _scheduler.RunFrame(jobHoldsTick, samples);
    }

    void ICallRunner<QueuedCall>.Run(QueuedCall call)
    {
        if (!call.State.TryStart())
        {
            // Answered while it waited (its deadline, or the connection going): not run.
            return;
        }

        CallOutcome outcome = call.Run(_runner!, QueuedCall.MillisecondsSince(call.ReceivedAt));
        if (!outcome.IsDeferred)
        {
            call.Finish(outcome.Reply, outcome.Method);
        }
    }

    void ICallRunner<QueuedCall>.Expire(QueuedCall call) => call.Drop(call.TimeoutReply());

    /// <summary>Stopwatch.GetTimestamp as StopwatchClock milliseconds.</summary>
    internal static double ClockMs(long timestamp) => timestamp * 1000.0 / Stopwatch.Frequency;

    private void TakeInbox()
    {
        while (_inbox.TryDequeue(out Posted posted))
        {
            switch (posted.Kind)
            {
                case PostKind.Add:
                    Enqueue(posted.Call!, posted.Source);
                    break;
                case PostKind.Withdraw:
                    if (_connections.TryGetValue(posted.Source, out FrameScheduler<QueuedCall>.Connection? holder))
                    {
                        _scheduler.Cancel(holder, posted.Call!);
                    }

                    break;
                case PostKind.Close:
                    if (_connections.TryGetValue(posted.Source, out FrameScheduler<QueuedCall>.Connection? closing))
                    {
                        _scheduler.Close(closing);
                        _connections.Remove(posted.Source);
                    }

                    break;
            }
        }
    }

    private void Enqueue(QueuedCall call, object source)
    {
        if (!_connections.TryGetValue(source, out FrameScheduler<QueuedCall>.Connection? connection))
        {
            connection = _scheduler.Open();
            _connections.Add(source, connection);
        }

        Admission admission = _scheduler.Enqueue(connection, call, call.Profile, ClockMs(call.DeadlineAt));
        if (!admission.IsQueued && admission.Refusal == AdmissionRefusal.TooManyInFlight)
        {
            call.Drop(call.RefusalReply("too_many_in_flight",
                $"At most {Settings.MaxInFlight} calls may be in flight on a connection."));
        }
    }

    private enum PostKind
    {
        Add,
        Withdraw,
        Close
    }

    private readonly struct Posted
    {
        internal Posted(PostKind kind, QueuedCall? call, object source)
        {
            Kind = kind;
            Call = call;
            Source = source;
        }

        internal PostKind Kind { get; }

        internal QueuedCall? Call { get; }

        internal object Source { get; }
    }
}

/// <summary>
/// A call's profile from the catalogue, worked out on the thread that received it. A method the catalogue does not
/// know (answered method_not_found on the main thread) is an instant read. Version-1 lines keep today's order: each
/// one is ordered like a write, whatever its class, so the lines of one connection run one after another.
/// </summary>
internal static class CallProfiles
{
    internal static CallProfile Of(CatalogueFile? catalogue, string method, JObject? parameters)
    {
        CatalogueMethod? entry = null;
        return catalogue?.Catalogue.TryGet(method, out entry) == true && entry != null
            ? CallProfile.Of(entry, parameters)
            : new CallProfile(method, MethodClass.Read, new CallCost(CostClass.Instant, 1));
    }

    internal static CallProfile OfLine(CatalogueFile? catalogue, string line)
    {
        string method = string.Empty;
        JObject? parameters = null;
        try
        {
            JObject request = JObject.Parse(line);
            method = request["method"]?.Type == JTokenType.String ? (string)request["method"]! : string.Empty;
            parameters = request["params"] as JObject;
        }
        catch (JsonReaderException)
        {
            // A line that is not JSON: the main thread answers it, as an instant call.
        }

        CallProfile profile = Of(catalogue, method, parameters);
        return new CallProfile(profile.Method, MethodClass.Write, new CallCost(profile.Cost, profile.Items));
    }
}
