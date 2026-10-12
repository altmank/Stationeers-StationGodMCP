#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// The order in which the main thread runs queued calls. Add, Withdraw, EndStreams, CloseCursor, ReleasePins, EndSession
/// and Close run on any thread and only post to an inbox; RunFrame, on the main thread, takes the inbox in and runs one
/// frame of calls, each to its answer (or, for a deferred call, to its start) before the next.
/// </summary>
internal interface ICallScheduler
{
    void Add(QueuedCall call);

    /// <summary>A call answered without running (cancelled) leaves its lane.</summary>
    void Withdraw(QueuedCall call);

    /// <summary>
    /// Streams of an owner ended: the waiting calls of the source connection on them are dropped unrun (whoever ended
    /// them answered them), and the per-stream stores hear of it. A source that is no connection (a resume window that
    /// passed) reaches the stores only.
    /// </summary>
    void EndStreams(object source, StreamEnd end);

    /// <summary>cursor_close: the cursor stores hear it at the start of the next frame, before that frame's calls.</summary>
    void CloseCursor(string owner, string cursorId);

    /// <summary>release: the pin stores hear it at the start of the next frame, before that frame's calls.</summary>
    void ReleasePins(string owner, long stream);

    /// <summary>
    /// The owner's session ended abruptly: the session-end stores (held restores) hear it at the start of the next frame,
    /// in order with the streams that end.
    /// </summary>
    void EndSession(string owner);

    /// <summary>The connection ended: its waiting calls are dropped unrun and it takes no more.</summary>
    void Close(object source);

    /// <summary>One frame: the subscription lane's samples, then light calls, then at most one heavy call.</summary>
    FrameOutcome RunFrame(bool jobHoldsTick, ISampleLane? samples, ICallRunner runner);

    /// <summary>What each (owner, stream) has cost the main thread (stream_costs); main thread only.</summary>
    StreamCosts Costs { get; }
}

/// <summary>
/// The lane scheduler (scheduling.md): listener threads post calls, cancels, ended streams and closed connections to a
/// concurrent inbox; each frame the main thread takes the inbox in order into FrameScheduler and runs the frame. A
/// connection is its Connection object (calls without one share one), opened under the client name its first call
/// carries; each call waits on its context's stream. A call the scheduler refuses beyond the in-flight limits is
/// answered too_many_in_flight unrun, with the limit and its scope. Ended streams and closed connections reach the
/// per-stream stores here, at the start of the frame.
/// <para>
/// A call that goes on with work over frames (CallOutcome.Continued) joins Deferred, the frame's slice lane, until it
/// answers; ended streams, closed connections and the calls' own cancels, deadlines and worlds stop it there. Every
/// deferred call (sample_logic's too) holds its stream in FrameScheduler until its state is answered, by whoever
/// answers it; its stream's end or its connection's close lets go of it unanswered.
/// </para>
/// <para>
/// Each call it runs is timed on the scheduler's clock from its start to its serialised reply (the span the
/// dispatcher's RunCall covers) and counted for its context in Costs, with whether the frame's request budget had been
/// reached by then; stream ends reach Costs with the stores. Each later slice of a deferred call adds its time (and its
/// frame, if over budget by its end) to the same stream in Costs and to the call's method in its connection's counts,
/// without counting the call again.
/// </para>
/// </summary>
internal sealed class LaneScheduler : ICallScheduler, ICallRunner<QueuedCall>
{
    private static readonly object NoSource = new object();

    private readonly ConcurrentQueue<Posted> _inbox = new ConcurrentQueue<Posted>();
    private readonly Dictionary<object, FrameScheduler<QueuedCall>.Connection> _connections =
        new Dictionary<object, FrameScheduler<QueuedCall>.Connection>();
    private readonly FrameScheduler<QueuedCall> _scheduler;
    private ICallRunner? _runner;

    private readonly StreamStores _stores;
    private readonly IMonotonicClock _clock;
    private readonly Func<WorldFacts> _world;

    /// <summary>
    /// world: the running world as welcome names it (WorldFacts.None while none runs), read on the main thread as a call
    /// that names a world starts; ServerFacts.Current's when not given.
    /// </summary>
    internal LaneScheduler(SchedulerSettings settings, StreamStores? stores = null, IMonotonicClock? clock = null,
        Func<WorldFacts>? world = null)
    {
        Settings = settings;
        _world = world ?? CurrentWorld;
        _stores = stores ?? StreamStores.Main;
        _clock = clock ?? StopwatchClock.Instance;
        _scheduler = new FrameScheduler<QueuedCall>(settings, this, _clock, new CostPredictor());
        Deferred = new DeferredCalls(_clock, _world, SliceRan);
    }

    /// <summary>The started calls going on with work over frames.</summary>
    internal DeferredCalls Deferred { get; }

    internal SchedulerSettings Settings { get; }

    /// <summary>Started calls still unanswered, each holding its stream.</summary>
    internal int Holding => _scheduler.Holding;

    public StreamCosts Costs { get; } = new StreamCosts();

    public void Add(QueuedCall call) => _inbox.Enqueue(new Posted(PostKind.Add, call, call.Source ?? NoSource));

    public void Withdraw(QueuedCall call) => _inbox.Enqueue(new Posted(PostKind.Withdraw, call, call.Source ?? NoSource));

    public void EndStreams(object source, StreamEnd end) => _inbox.Enqueue(new Posted(PostKind.EndStreams, null, source, end));

    public void Close(object source) => _inbox.Enqueue(new Posted(PostKind.Close, null, source));

    public void CloseCursor(string owner, string cursorId) =>
        _inbox.Enqueue(new Posted(PostKind.CloseCursor, null, NoSource, default, owner, cursorId));

    public void ReleasePins(string owner, long stream) =>
        _inbox.Enqueue(new Posted(PostKind.ReleasePins, null, NoSource, default, owner, null, stream));

    public void EndSession(string owner) =>
        _inbox.Enqueue(new Posted(PostKind.EndSession, null, NoSource, default, owner));

    public FrameOutcome RunFrame(bool jobHoldsTick, ISampleLane? samples, ICallRunner runner)
    {
        _runner = runner;
        TakeInbox();
        return _scheduler.RunFrame(jobHoldsTick, samples, Deferred);
    }

    void ICallRunner<QueuedCall>.Run(QueuedCall call)
    {
        if (!call.State.TryStart())
        {
            // Answered while it waited (its deadline, or the connection going): not run.
            return;
        }

        if (call.World != null)
        {
            WorldFacts world = _world();
            if (!string.Equals(world.Id, call.World, StringComparison.Ordinal))
            {
                call.Finish(WorldChangedReply(call, world), null);
                return;
            }
        }

        double began = _clock.NowMs;
        CallOutcome outcome;
        using (CallContext.Enter(call.Context))
        {
            outcome = call.Run(_runner!, QueuedCall.MillisecondsSince(call.ReceivedAt));
        }

        double ended = _clock.NowMs;
        Costs.Record(call.Context, ended - began, CurrentFrame.BudgetReached(ended), _scheduler.Frame);
        if (!outcome.IsDeferred)
        {
            call.Finish(outcome.Reply, outcome.Method);
        }
        else if (outcome.Work != null)
        {
            Deferred.Add(call, outcome.Work, outcome.Method);
        }
    }

    void ICallRunner<QueuedCall>.Expire(QueuedCall call) => call.Drop(call.TimeoutReply());

    private void SliceRan(QueuedCall call, double ms)
    {
        Costs.RecordSlice(call.Context, ms, CurrentFrame.BudgetReached(_clock.NowMs), _scheduler.Frame);
        call.CountSliceOnConnection(ms);
    }

    bool ICallRunner<QueuedCall>.IsAnswered(QueuedCall call) => call.State.IsAnswered;

    private static WorldFacts CurrentWorld() => ServerFacts.Current.World;

    // A call that names a world other than the current one (a load since it was sent): refused unrun, with the world now.
    private static string WorldChangedReply(QueuedCall call, WorldFacts world) =>
        call.RefusalReply(ProtocolCall.WorldChangedCode,
            world.Id.Length == 0
                ? $"The call is for world {call.World}, but no world is running now. Nothing ran."
                : $"The call is for world {call.World}, but world {world.Id} has loaded since, and its reference ids " +
                  "may name other things. Nothing ran.",
            new Dictionary<string, WorldFacts> { ["world"] = world });

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
                        _scheduler.Cancel(holder, posted.Call!.Context.Stream, posted.Call);
                    }

                    break;
                case PostKind.EndStreams:
                    EndStreamsNow(posted.Source, posted.End);
                    break;
                case PostKind.CloseCursor:
                    _stores.CloseCursor(posted.Owner!, posted.CursorId!);
                    break;
                case PostKind.ReleasePins:
                    _stores.ReleasePins(posted.Owner!, posted.Stream);
                    break;
                case PostKind.EndSession:
                    _stores.SessionEnded(posted.Owner!);
                    Deferred.Ended(StreamEnd.AllOf(posted.Owner!));
                    break;
                case PostKind.Close:
                    if (_connections.TryGetValue(posted.Source, out FrameScheduler<QueuedCall>.Connection? closing))
                    {
                        _scheduler.Close(closing);
                        _connections.Remove(posted.Source);
                    }

                    Deferred.Closed(posted.Source);

                    if (posted.Source is Connection connection)
                    {
                        StreamEnd closed = StreamEnd.AllOf(connection.ClientId);
                        _stores.End(closed);
                        Costs.Ended(closed);
                    }

                    break;
            }
        }
    }

    private void Enqueue(QueuedCall call, object source)
    {
        if (!_connections.TryGetValue(source, out FrameScheduler<QueuedCall>.Connection? connection))
        {
            connection = _scheduler.Open(call.ClientName);
            _connections.Add(source, connection);
        }

        Admission admission = _scheduler.Enqueue(connection, call.Context.Stream, call, call.Profile,
            ClockMs(call.DeadlineAt));
        if (admission.IsQueued)
        {
            return;
        }

        if (admission.Refusal == AdmissionRefusal.TooManyInFlight)
        {
            call.Drop(call.RefusalReply(InFlightLimits.Code, InFlightLimits.StreamMessage(Settings.MaxInFlight),
                InFlightLimits.Data(Settings.MaxInFlight, InFlightLimits.StreamScope)));
        }
        else if (admission.Refusal == AdmissionRefusal.TooManyInFlightOnConnection)
        {
            call.Drop(call.RefusalReply(InFlightLimits.Code,
                InFlightLimits.ConnectionMessage(Settings.MaxInFlightPerConnection),
                InFlightLimits.Data(Settings.MaxInFlightPerConnection, InFlightLimits.ConnectionScope)));
        }
    }

    private void EndStreamsNow(object source, StreamEnd end)
    {
        if (_connections.TryGetValue(source, out FrameScheduler<QueuedCall>.Connection? connection))
        {
            if (end.IsWholeConnection)
            {
                List<FrameScheduler<QueuedCall>.StreamQueue> live = connection.Live;
                while (live.Count > 0)
                {
                    _scheduler.EndStream(connection, live[live.Count - 1].Id);
                }
            }
            else
            {
                _scheduler.EndStream(connection, end.Stream);
            }
        }

        _stores.End(end);
        Deferred.Ended(end);
        Costs.Ended(end);
    }

    private enum PostKind
    {
        Add,
        Withdraw,
        EndStreams,
        CloseCursor,
        ReleasePins,
        EndSession,
        Close
    }

    private readonly struct Posted
    {
        internal Posted(PostKind kind, QueuedCall? call, object source, StreamEnd end = default,
            string? owner = null, string? cursorId = null, long stream = 0)
        {
            Stream = stream;
            Kind = kind;
            Call = call;
            Source = source;
            End = end;
            Owner = owner;
            CursorId = cursorId;
        }

        internal PostKind Kind { get; }

        internal QueuedCall? Call { get; }

        internal object Source { get; }

        internal StreamEnd End { get; }

        /// <summary>
        /// cursor_close, release and an ended session: the connection's owner (CallContext.Owner); the cursor id;
        /// release's stream.
        /// </summary>
        internal string? Owner { get; }

        internal string? CursorId { get; }

        internal long Stream { get; }
    }
}

/// <summary>
/// too_many_in_flight's words and data, the same from the reader thread's check and the scheduler's: the limit passed
/// and whether it was the stream's or the connection's (protocol.md, Order on one stream).
/// </summary>
internal static class InFlightLimits
{
    internal const string Code = "too_many_in_flight";
    internal const string StreamScope = "stream";
    internal const string ConnectionScope = "connection";

    internal static string StreamMessage(int limit) => $"At most {limit} calls may be in flight on one stream.";

    internal static string ConnectionMessage(int limit) =>
        $"At most {limit} calls may be in flight on a connection, all its streams together.";

    internal static Dictionary<string, object> Data(int limit, string scope) =>
        new Dictionary<string, object> { ["limit"] = limit, ["scope"] = scope };
}

/// <summary>
/// A call's profile from the catalogue, worked out on the thread that received it. A method the catalogue does not
/// know (answered method_not_found on the main thread) is an instant read.
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
}
