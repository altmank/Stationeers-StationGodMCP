#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Protocol;

/// <summary>
/// The subscription lane's work on the wire (protocol.md, Subscriptions; read sample): subscribe and unsubscribe as
/// calls run on the main thread, the engine's samples, and its events into each connection's outbound queue,
/// where an update waits as its subscription's UpdateSlot and is written by the connection's writer thread; and
/// read sample runs (StartSample), each a LogicSampler run whose call waits in the slice lane (SampleWatch) until the
/// run's last sample is taken. The lane takes read sample samples first, then subscription samples, one at a time.
/// Subscriptions and sample runs belong to the (owner, stream) of the call that made them (CallContext: the
/// connection, or its resume token): a stream's end (stream_end, a slow client, a closed connection, a resume window
/// that passed) ends them at the start of the next frame, and the slice lane answers a waiting call cancelled. A
/// sample run also ends when its owner's connection closes, since its reply cannot be delivered. While a resume token is held its subscriptions take no samples; when a connection resumes it,
/// their events go to that connection and each devices subscription's next sample is sent whole. Main thread only,
/// apart from Closed, StreamEnded, Held and Resumed, which connection threads call.
/// </summary>
internal sealed class SubscriptionHub : ISubscriptionEvents<ReadDevicesView>, ISampleEvents<BatchItemView>, ISampleLane
{
    internal const string SubscribeMethod = "subscribe";
    internal const string UnsubscribeMethod = "unsubscribe";
    internal const string UnknownSubscriptionCode = "unknown_subscription";

    /// <summary>The owner sample runs of calls without a context share.</summary>
    private static readonly ConnectionId NoConnection = new ConnectionId("pipe");

    private readonly SubscriptionEngine<ReadDevicesView> _engine;

    // Each owner's connection, where its events go; an owner held for a resume has none.
    private readonly Dictionary<ConnectionId, Connection> _connections = new Dictionary<ConnectionId, Connection>();
    private readonly ConcurrentQueue<Ending> _ended = new ConcurrentQueue<Ending>();
    private readonly OneSample _one = new OneSample();
    private readonly LogicSampler<BatchItemView> _sampler;
    private readonly Dictionary<SampleRun<BatchItemView>, SampleWatch> _runs =
        new Dictionary<SampleRun<BatchItemView>, SampleWatch>();

    // Where each stream's samples and update events are counted (stream_costs' samples/sample_ms and
    // updates/update_ms); null when nobody asks.
    private readonly StreamCosts? _costs;
    private readonly IMonotonicClock _clock;
    private SamplingTick _tick;
    private RealTimeTick _real;

    internal SubscriptionHub(IDeviceReader<ReadDevicesView> reader, ILogicSampleReader<BatchItemView> logicReader,
        SubscriptionLimits limits, StreamCosts? costs = null, IMonotonicClock? clock = null)
    {
        _clock = clock ?? StopwatchClock.Instance;
        _engine = new SubscriptionEngine<ReadDevicesView>(reader, ReadDevicesComparer.Instance, limits,
            () => _clock.NowMs);
        _sampler = new LogicSampler<BatchItemView>(logicReader, LogicResultComparer.Instance);
        _costs = costs;
    }

    /// <summary>Sample runs in progress.</summary>
    internal int SampleRuns => _sampler.Active;

    internal SubscriptionLimits Limits => _engine.Limits;

    /// <summary>Subscriptions the owner holds (server info, the console: a connection's Owner).</summary>
    internal int CountOf(string owner) => _engine.CountOf(new ConnectionId(owner));

    /// <summary>Whether the method is one of the hub's.</summary>
    internal static bool Handles(string method) => method == SubscribeMethod || method == UnsubscribeMethod;

    /// <summary>
    /// The start of a frame: what connection threads posted since the last one, in order (streams and owners that
    /// ended drop their subscriptions silently and their sample runs, whose waiting calls the slice lane answers
    /// cancelled or drops with their connection; held owners pause, resumed ones go on), and the frame's ticks (game
    /// time for subscriptions, real time for sample runs) are what samples and replies are stamped with.
    /// </summary>
    internal void BeginFrame(SamplingTick tick, RealTimeTick real)
    {
        _tick = tick;
        _real = real;
        while (_ended.TryDequeue(out Ending ending))
        {
            switch (ending.Kind)
            {
                case EndingKind.Closed:
                    ConnectionClosed(ending.Connection!);
                    break;
                case EndingKind.Held:
                    _engine.Pause(new ConnectionId(ending.Owner!));
                    break;
                case EndingKind.Resumed:
                    ConnectionId resumed = new ConnectionId(ending.Owner!);
                    _connections[resumed] = ending.Connection!;
                    _engine.Resume(resumed);
                    break;
                default:
                    StreamsEnded(ending.End);
                    break;
            }
        }
    }

    /// <summary>
    /// A connection ended (any thread): at the next frame its owner's sample runs end, and without a resume
    /// token its subscriptions end without an event (a token's are held or ended through ResumeHolds).
    /// </summary>
    internal void Closed(object source)
    {
        if (source is Connection connection)
        {
            _ended.Enqueue(new Ending(EndingKind.Closed, connection, null, default));
        }
    }

    /// <summary>
    /// Streams of an owner ended (any thread): their subscriptions end without an event and their sample runs
    /// end at the next frame.
    /// </summary>
    internal void StreamEnded(object source, StreamEnd end)
    {
        if (!end.IsWorld && end.Owner != null)
        {
            _ended.Enqueue(new Ending(EndingKind.Ended, source as Connection, null, end));
        }
    }

    /// <summary>A resume token's state is held (any thread): its subscriptions take no samples from the next frame.</summary>
    internal void Held(string owner) => _ended.Enqueue(new Ending(EndingKind.Held, null, owner, default));

    /// <summary>
    /// A connection resumed the owner's token (any thread): from the next frame its subscriptions' events go to that
    /// connection, and each devices subscription's next sample is sent whole.
    /// </summary>
    internal void Resumed(string owner, object source)
    {
        if (source is Connection connection)
        {
            _ended.Enqueue(new Ending(EndingKind.Resumed, connection, owner, default));
        }
    }

    private void ConnectionClosed(Connection connection)
    {
        foreach (SampleRun<BatchItemView> run in RunsOf(new ConnectionId(connection.Owner), null))
        {
            _runs.Remove(run);
            _sampler.Cancel(run);
        }

        ConnectionId owner = new ConnectionId(connection.Owner);
        if (!connection.IsResumable)
        {
            DropOwner(owner);
        }
        else if (_connections.TryGetValue(owner, out Connection? open) && open == connection)
        {
            _connections.Remove(owner);
        }
    }

    private void StreamsEnded(StreamEnd end)
    {
        ConnectionId owner = new ConnectionId(end.Owner!);
        if (end.IsWholeConnection)
        {
            DropOwner(owner);
            return;
        }

        _engine.DropStream(owner, end.Stream);
        foreach (SampleRun<BatchItemView> run in RunsOf(owner, end.Stream))
        {
            _runs.Remove(run);
            _sampler.Cancel(run);
        }
    }

    // Every subscription and sample run of the owner ends without a word.
    private void DropOwner(ConnectionId owner)
    {
        _engine.DropConnection(owner);
        _connections.Remove(owner);
        _sampler.CancelConnection(owner);
        foreach (SampleRun<BatchItemView> run in RunsOf(owner, null))
        {
            _runs.Remove(run);
        }
    }

    // The runs of the owner, of one stream or (stream null) of all; a list, since the caller removes them.
    private List<SampleRun<BatchItemView>> RunsOf(ConnectionId owner, long? stream)
    {
        List<SampleRun<BatchItemView>> found = new List<SampleRun<BatchItemView>>();
        foreach (SampleRun<BatchItemView> run in _runs.Keys)
        {
            if (run.Connection.Equals(owner) && (stream == null || run.Stream == stream.Value))
            {
                found.Add(run);
            }
        }

        return found;
    }

    /// <summary>A world finished loading: devices subscriptions end, world-topic ones hear the new id.</summary>
    internal void WorldChanged(string worldId) => _engine.WorldChanged(WorldId.Of(worldId), this);

    /// <summary>Each frame: world-topic subscriptions hear of a game state other than the last one.</summary>
    internal void ObserveGameState(string state) => _engine.ObserveGameState(state, this);

    /// <summary>
    /// A held-tick job reached its final status (finished, failed, or dropped before it started), on the main thread
    /// where it ended: every jobs-topic subscription of every connection gets a job_ended event with the job's facts
    /// (JobEndFacts) taken from finalView, the view a poll of the job answers from then on. The facts are read out of
    /// the view by each connection's writer as it writes the event (JobEndedFacts), off the game's thread, and only
    /// when someone subscribes. Each event is a line of its own in the connection's outbound queue, never merged with
    /// another; a client too slow to take its lines is closed as for any line (slow_client).
    /// </summary>
    [Profiled]
    internal void JobEnded(object finalView, long frame)
    {
        if (!_engine.HasJobSubscriptions)
        {
            return;
        }

        _engine.JobEnded(new JobEndedFacts(finalView), frame, this);
    }

    /// <summary>
    /// subscribe or unsubscribe, run on the main thread: the reply message. Each method answers its result, or an
    /// ErrorView for an error that carries data (subscription_limit); a read that is refused throws ApiException.
    /// </summary>
    internal string Run(ProtocolCall call, double queueWaitMs)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CallRequest request = call.Request;
        double queueMs = Math.Round(queueWaitMs, 2);
        object result;
        try
        {
            result = request.Method == SubscribeMethod
                ? Subscribe((Connection)call.Source!, request.Params, call.Context)
                : Unsubscribe(call.Context, request.Params);
        }
        catch (ApiException refused)
        {
            result = new ErrorView(refused.Code, refused.Message);
        }
        catch (Exception exception)
        {
            // The request boundary: a bug in the read path answers this call, never the frame.
            return ApiJson.WriteShared(CallReplyView.Failed(request.Id, new ErrorView("internal_error", exception.Message),
                Elapsed(watch), queueMs, _tick.Frame, BudgetHit()));
        }

        bool budgetHit = BudgetHit();
        return ApiJson.WriteShared(result is ErrorView error
            ? CallReplyView.Failed(request.Id, error, Elapsed(watch), queueMs, _tick.Frame, budgetHit)
            : CallReplyView.Of(request.Id, result, false, Elapsed(watch), queueMs, _tick.Frame, budgetHit));
    }

    public bool RunNextDueSample()
    {
        _one.Reset();
        if (_sampler.Active > 0 && _sampler.Poll(_real, _one, this) > 0)
        {
            return true;
        }

        _one.Reset();
        return _engine.Poll(_tick, _one, this) > 0;
    }

    /// <summary>read sample's targets read once, before its run starts: the misses refuse the call.</summary>
    internal LogicSampleRead CheckSample(SampleRequest request) => _sampler.Check(request);

    /// <summary>
    /// read sample: a run for the call on its (owner, stream), its first sample due now; the returned watch is the
    /// call's pending result, ready once the run's last sample is taken or a sample's read failed.
    /// </summary>
    internal SampleWatch StartSample(SampleRequest request, CallContext context)
    {
        ConnectionId connection = context.IsNone ? NoConnection : new ConnectionId(context.Owner!);
        SampleWatch watch = new SampleWatch();
        _runs.Add(_sampler.Start(connection, request, _real.RealTimeS, context.Stream), watch);
        return watch;
    }

    void ISampleEvents<BatchItemView>.Finished(SampleRun<BatchItemView> run,
        SampleOutcome<BatchItemView> outcome)
    {
        if (_runs.TryGetValue(run, out SampleWatch? watch))
        {
            _runs.Remove(run);
            watch.End(outcome);
        }
    }

    void ISubscriptionEvents<ReadDevicesView>.Enqueue(UpdateSlot<ReadDevicesView> slot)
    {
        if (_connections.TryGetValue(slot.Connection, out Connection? connection))
        {
            connection.Send(new UpdateLine(slot, slot.Epoch, _costs, _clock));
        }
    }

    // On the main thread where the sample was taken, so it is stored with the call counters.
    void ISubscriptionEvents<ReadDevicesView>.Sampled(ConnectionId connection, long stream, double ms) =>
        _costs?.RecordSample(new CallContext(connection.Value, stream), ms);

    void ISubscriptionEvents<ReadDevicesView>.Ended(ConnectionId connection, SubscriptionId subscription, SubscriptionEnd end)
    {
        if (end is SubscriptionEnd.ReadFailed failed)
        {
            ProtocolLog.Warning($"Subscription {subscription} of {connection} ended: its read failed ({failed.Failure.Message}).");
        }

        SendEvent(connection, new SubscriptionEndedEventView(subscription, end));
    }

    void ISubscriptionEvents<ReadDevicesView>.WorldChanged(ConnectionId connection, SubscriptionId subscription, WorldId world) =>
        SendEvent(connection, new WorldChangedEventView(subscription, world));

    void ISubscriptionEvents<ReadDevicesView>.GameStateChanged(ConnectionId connection, SubscriptionId subscription,
        string state) => SendEvent(connection, new GameStateEventView(subscription, state));

    void ISubscriptionEvents<ReadDevicesView>.JobEnded(ConnectionId connection, SubscriptionId subscription, long seq,
        long frame, object job) => SendEvent(connection, new JobEndedEventView(subscription, seq, frame, job));

    /// <summary>A subscription event's line: the view's keys after the envelope's type.</summary>
    internal static string EventLine(object view)
    {
        string keys = ApiJson.WriteShared(view);
        return "{\"type\":\"event\"," + keys.Substring(1);
    }

    private object Subscribe(Connection connection, JObject? parameters, CallContext context)
    {
        ConnectionId id = new ConnectionId(context.Owner!);
        long stream = context.Stream;
        SubscribeOutcome<ReadDevicesView> outcome = SubscribeRequest.Of(parameters) switch
        {
            SubscribeRequest.Devices devices => _engine.Subscribe(id, devices.Query, devices.Interval, _tick, stream),
            SubscribeRequest.World _ => _engine.SubscribeWorld(id, _tick, stream),
            SubscribeRequest.Jobs _ => _engine.SubscribeJobs(id, _tick, stream),
            SubscribeRequest.OverLimit over => new SubscribeOutcome<ReadDevicesView>.Refused(over.Refusal),
            SubscribeRequest.Invalid invalid => throw ApiErrors.InvalidArgument(invalid.Message),
            _ => throw ApiErrors.InvalidArgument("Unknown subscribe request.")
        };
        switch (outcome)
        {
            case SubscribeOutcome<ReadDevicesView>.Subscribed subscribed:
                _connections[id] = connection;
                return SubscribeReplyView.Of(subscribed);
            case SubscribeOutcome<ReadDevicesView>.Refused refused:
                return new ErrorView(SubscriptionRefusal.ErrorCode, refused.Refusal.Message, refused.Refusal.Data);
            default:
                throw new InvalidOperationException("Unknown subscribe outcome.");
        }
    }

    private object Unsubscribe(CallContext context, JObject? parameters)
    {
        if (!UnsubscribeRequest.TryOf(parameters, out SubscriptionId subscription, out string problem))
        {
            throw ApiErrors.InvalidArgument(problem);
        }

        if (!_engine.Unsubscribe(new ConnectionId(context.Owner!), subscription))
        {
            throw new ApiException(UnknownSubscriptionCode,
                $"This connection holds no subscription {subscription}; it may have ended already.");
        }

        return new UnsubscribeReplyView(subscription);
    }

    // The event's line is made by the connection's writer when it reaches it, off the game's thread.
    private void SendEvent(ConnectionId connection, object view)
    {
        if (_connections.TryGetValue(connection, out Connection? open))
        {
            open.Send(new EventOutbound(view));
        }
    }

    private static double Elapsed(Stopwatch watch) => Math.Round(watch.Elapsed.TotalMilliseconds, 2);

    /// <summary>The reply's budget_hit: whether the frame the reply is built in has reached its request budget.</summary>
    private static bool BudgetHit() => CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);

    private enum EndingKind
    {
        Ended,
        Closed,
        Held,
        Resumed
    }

    /// <summary>
    /// What a connection thread posted for the start of the next frame: streams that ended, a connection that closed,
    /// an owner held for a resume, or one a connection resumed.
    /// </summary>
    private readonly struct Ending
    {
        internal Ending(EndingKind kind, Connection? connection, string? owner, StreamEnd end)
        {
            Kind = kind;
            Connection = connection;
            Owner = owner;
            End = end;
        }

        internal EndingKind Kind { get; }

        internal Connection? Connection { get; }

        internal string? Owner { get; }

        internal StreamEnd End { get; }
    }

    /// <summary>The lane asks for one sample at a time: the first offer is taken, the next refused.</summary>
    private sealed class OneSample : ISamplingBudget
    {
        private bool _taken;

        internal void Reset() => _taken = false;

        public bool MayTakeAnother()
        {
            if (_taken)
            {
                return false;
            }

            _taken = true;
            return true;
        }
    }

    /// <summary>An event in the outbound queue, its line made when the writer reaches it.</summary>
    private sealed class EventOutbound : IOutboundLine
    {
        private readonly object _view;

        internal EventOutbound(object view) => _view = view;

        public bool TryTake(out string line)
        {
            try
            {
                line = EventLine(_view);
                return true;
            }
            catch (Exception exception)
            {
                // A view that will not write (a job's final view): its event is lost, never the connection.
                ProtocolLog.Warning($"An event could not be written: {exception.Message}");
                line = string.Empty;
                return false;
            }
        }
    }

    /// <summary>
    /// An update in the outbound queue: written as the slot holds it when the writer reaches it, unless the slot was
    /// requeued on another connection since (a resume), when this entry writes nothing. Writing it is where the
    /// update's serialisation is counted for its stream (stream_costs): one update of the (owner, stream) and the
    /// line's own milliseconds. The sample's own time was counted when it was taken, changed or not.
    /// </summary>
    private sealed class UpdateLine : IOutboundLine
    {
        private readonly UpdateSlot<ReadDevicesView> _slot;
        private readonly int _epoch;
        private readonly StreamCosts? _costs;
        private readonly IMonotonicClock _clock;

        internal UpdateLine(UpdateSlot<ReadDevicesView> slot, int epoch, StreamCosts? costs, IMonotonicClock clock)
        {
            _slot = slot;
            _epoch = epoch;
            _costs = costs;
            _clock = clock;
        }

        public bool TryTake(out string line)
        {
            if (_slot.TryTake(_epoch, out SubscriptionUpdate<ReadDevicesView> update))
            {
                double startedMs = _clock.NowMs;
                line = EventLine(UpdateEventView.Of(update));
                _costs?.RecordUpdate(new CallContext(_slot.Connection.Value, _slot.Stream), _clock.NowMs - startedMs);
                return true;
            }

            line = string.Empty;
            return false;
        }
    }
}
