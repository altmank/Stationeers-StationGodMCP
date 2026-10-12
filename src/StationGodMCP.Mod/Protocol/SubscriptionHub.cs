#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Subscriptions;

namespace StationGodMCP.Protocol;

/// <summary>
/// The subscription lane's work on the wire (protocol.md, Subscriptions; sample_logic): subscribe and unsubscribe as
/// calls run on the main thread, the engine's samples, and its events into each connection's outbound queue,
/// where an update waits as its subscription's UpdateSlot and is written by the connection's writer thread; and
/// sample_logic, whose call starts a LogicSampler run and is answered when the run's last sample is taken. The lane
/// takes sample_logic samples first, then subscription samples, one at a time. Subscriptions and sample_logic runs
/// belong to the stream of the call that made them: a stream's end (stream_end, a slow client, a closed connection)
/// ends them at the start of the next frame. Main thread only, apart from Closed and StreamEnded, which a
/// connection's reader or writer thread calls.
/// </summary>
internal sealed class SubscriptionHub : ISubscriptionEvents<ReadDevicesView>, ISampleLogicEvents<BatchItemView>, ISampleLane
{
    internal const string SubscribeMethod = "subscribe";
    internal const string UnsubscribeMethod = "unsubscribe";
    internal const string SampleLogicMethod = "sample_logic";
    internal const string UnknownSubscriptionCode = "unknown_subscription";

    /// <summary>The connection id the synchronous pipe's sample_logic runs (no Connection) share.</summary>
    private static readonly ConnectionId NoConnection = new ConnectionId("pipe");

    private readonly SubscriptionEngine<ReadDevicesView> _engine;
    private readonly Dictionary<ConnectionId, Connection> _connections = new Dictionary<ConnectionId, Connection>();
    private readonly ConcurrentQueue<Ending> _ended = new ConcurrentQueue<Ending>();
    private readonly OneSample _one = new OneSample();
    private readonly LogicSampler<BatchItemView> _sampler;
    private readonly Dictionary<SampleLogicRun<BatchItemView>, SampleLogicCall> _runs =
        new Dictionary<SampleLogicRun<BatchItemView>, SampleLogicCall>();
    private SamplingTick _tick;
    private RealTimeTick _real;

    internal SubscriptionHub(IDeviceReader<ReadDevicesView> reader, ILogicSampleReader<BatchItemView> logicReader,
        SubscriptionLimits limits)
    {
        _engine = new SubscriptionEngine<ReadDevicesView>(reader, ReadDevicesComparer.Instance, limits);
        _sampler = new LogicSampler<BatchItemView>(logicReader, LogicResultComparer.Instance);
    }

    /// <summary>sample_logic runs in progress.</summary>
    internal int SampleLogicRuns => _sampler.Active;

    internal SubscriptionLimits Limits => _engine.Limits;

    /// <summary>Subscriptions the connection holds (mod_info, the console).</summary>
    internal int CountOf(string clientId) => _engine.CountOf(new ConnectionId(clientId));

    /// <summary>Whether the method is one of the hub's.</summary>
    internal static bool Handles(string method) => method == SubscribeMethod || method == UnsubscribeMethod;

    /// <summary>
    /// The start of a frame: streams and connections that ended since the last one drop their subscriptions silently
    /// and their sample_logic runs (a run of one ended stream answers its call cancelled; a closed connection's runs
    /// answer nothing, there is no one to tell), and the frame's ticks (game time for subscriptions, real time for
    /// sample_logic) are what samples and replies are stamped with.
    /// </summary>
    internal void BeginFrame(SamplingTick tick, RealTimeTick real)
    {
        _tick = tick;
        _real = real;
        while (_ended.TryDequeue(out Ending ending))
        {
            ConnectionId id = new ConnectionId(ending.Connection.ClientId);
            if (ending.End.IsWholeConnection)
            {
                _engine.DropConnection(id);
                _connections.Remove(id);
                _sampler.CancelConnection(id);
                foreach (SampleLogicRun<BatchItemView> run in RunsOf(id, null))
                {
                    _runs.Remove(run);
                }

                continue;
            }

            _engine.DropStream(id, ending.End.Stream);
            foreach (SampleLogicRun<BatchItemView> run in RunsOf(id, ending.End.Stream))
            {
                SampleLogicCall pending = _runs[run];
                _runs.Remove(run);
                _sampler.Cancel(run);
                pending.Call.Finish(ProtocolCall.StreamEndedRunReply(pending.Id), SampleLogicMethod);
            }
        }
    }

    /// <summary>A connection ended (any thread): its subscriptions end without an event at the next frame.</summary>
    internal void Closed(object source)
    {
        if (source is Connection connection)
        {
            _ended.Enqueue(new Ending(connection, StreamEnd.AllOf(connection.ClientId)));
        }
    }

    /// <summary>
    /// Streams of a connection ended (any thread): their subscriptions end without an event and their sample_logic
    /// runs end at the next frame.
    /// </summary>
    internal void StreamEnded(object source, StreamEnd end)
    {
        if (source is Connection connection && !end.IsWorld)
        {
            _ended.Enqueue(new Ending(connection, end));
        }
    }

    // The runs of the connection, of one stream or (stream null) of all; a list, since the caller removes them.
    private List<SampleLogicRun<BatchItemView>> RunsOf(ConnectionId connection, long? stream)
    {
        List<SampleLogicRun<BatchItemView>> found = new List<SampleLogicRun<BatchItemView>>();
        foreach (SampleLogicRun<BatchItemView> run in _runs.Keys)
        {
            if (run.Connection.Equals(connection) && (stream == null || run.Stream == stream.Value))
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
                ? Subscribe((Connection)call.Source!, request.Params, call.Context.Stream)
                : Unsubscribe((Connection)call.Source!, request.Params);
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

    /// <summary>
    /// sample_logic: invalid arguments are answered at once; otherwise a run starts, its first sample is
    /// due now, and the call is answered when the run ends.
    /// </summary>
    internal CallOutcome StartSampleLogic(ProtocolCall call, double queueWaitMs)
    {
        CallRequest request = call.Request;
        return Start(call, request.Params,
            new SampleLogicCall(call, request.Id, request.Shape, Math.Round(queueWaitMs, 2)));
    }

    void ISampleLogicEvents<BatchItemView>.Finished(SampleLogicRun<BatchItemView> run,
        SampleLogicOutcome<BatchItemView> outcome)
    {
        if (!_runs.TryGetValue(run, out SampleLogicCall? pending))
        {
            return;
        }

        _runs.Remove(run);
        string reply = outcome switch
        {
            SampleLogicOutcome<BatchItemView>.Completed completed => pending.Reply(completed.Result, null, _tick.Frame),
            SampleLogicOutcome<BatchItemView>.Failed failed =>
                pending.Reply(null, new ErrorView(failed.Code, failed.Message), _tick.Frame),
            _ => pending.Reply(null, new ErrorView("internal_error", "Unknown sample_logic outcome."), _tick.Frame)
        };
        pending.Call.Finish(reply, SampleLogicMethod);
    }

    private CallOutcome Start(QueuedCall call, JObject? parameters, SampleLogicCall pending)
    {
        switch (SampleLogicArguments.Of(parameters))
        {
            case SampleLogicParse.Parsed parsed:
                ConnectionId connection = call.Source is Connection open ? new ConnectionId(open.ClientId) : NoConnection;
                _runs.Add(_sampler.Start(connection, parsed.Arguments, _real.RealTimeS, call.Context.Stream), pending);
                return CallOutcome.Deferred;
            case SampleLogicParse.Invalid invalid:
                return new CallOutcome(pending.Reply(null, new ErrorView(ApiErrors.InvalidArgumentCode, invalid.Message),
                    _tick.Frame), SampleLogicMethod);
            default:
                throw new InvalidOperationException("Unknown sample_logic arguments.");
        }
    }

    void ISubscriptionEvents<ReadDevicesView>.Enqueue(UpdateSlot<ReadDevicesView> slot)
    {
        if (_connections.TryGetValue(slot.Connection, out Connection? connection))
        {
            connection.Send(new UpdateLine(slot));
        }
    }

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

    /// <summary>A subscription event's line: the view's keys after the envelope's type.</summary>
    internal static string EventLine(object view)
    {
        string keys = ApiJson.WriteFresh(view);
        return "{\"type\":\"event\"," + keys.Substring(1);
    }

    private object Subscribe(Connection connection, JObject? parameters, long stream)
    {
        ConnectionId id = new ConnectionId(connection.ClientId);
        SubscribeOutcome<ReadDevicesView> outcome = SubscribeRequest.Of(parameters) switch
        {
            SubscribeRequest.Devices devices => _engine.Subscribe(id, devices.Query, devices.Interval, _tick, stream),
            SubscribeRequest.World _ => _engine.SubscribeWorld(id, _tick, stream),
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

    private object Unsubscribe(Connection connection, JObject? parameters)
    {
        if (!UnsubscribeRequest.TryOf(parameters, out SubscriptionId subscription, out string problem))
        {
            throw ApiErrors.InvalidArgument(problem);
        }

        if (!_engine.Unsubscribe(new ConnectionId(connection.ClientId), subscription))
        {
            throw new ApiException(UnknownSubscriptionCode,
                $"This connection holds no subscription {subscription}; it may have ended already.");
        }

        return new UnsubscribeReplyView(subscription);
    }

    private void SendEvent(ConnectionId connection, object view)
    {
        if (_connections.TryGetValue(connection, out Connection? open))
        {
            open.Send(EventLine(view));
        }
    }

    private static double Elapsed(Stopwatch watch) => Math.Round(watch.Elapsed.TotalMilliseconds, 2);

    /// <summary>The reply's budget_hit: whether the frame the reply is built in has reached its request budget.</summary>
    private static bool BudgetHit() => CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);

    /// <summary>A sample_logic call waiting for its run to end, and how its reply is written.</summary>
    private sealed class SampleLogicCall
    {
        private readonly string? _id;
        private readonly ShapeRequest? _shape;
        private readonly double _queueMs;
        private readonly Stopwatch _since = Stopwatch.StartNew();

        internal SampleLogicCall(QueuedCall call, string? id, ShapeRequest? shape, double queueMs)
        {
            Call = call;
            _id = id;
            _shape = shape;
            _queueMs = queueMs;
        }

        internal QueuedCall Call { get; }

        internal string? Id => _id;

        /// <summary>
        /// The reply: the result shaped as the call asked, or the error. elapsed_ms is the run's whole real time, from
        /// its start to its last sample; budget_hit is for the frame this reply is built in, not the one the run began in.
        /// </summary>
        internal string Reply(object? result, ErrorView? error, long frame)
        {
            double elapsedMs = Elapsed(_since);
            bool budgetHit = BudgetHit();
            return result == null
                ? ApiJson.WriteShared(CallReplyView.Failed(_id, error!, elapsedMs, _queueMs, frame, budgetHit))
                : ApiJson.WriteShaped(CallReplyView.Of(_id, result, _shape != null, elapsedMs, _queueMs, frame, budgetHit),
                    _shape ?? ShapeRequest.None).Json;
        }
    }

    /// <summary>A connection's streams that ended, waiting for the start of the next frame.</summary>
    private readonly struct Ending
    {
        internal Ending(Connection connection, StreamEnd end)
        {
            Connection = connection;
            End = end;
        }

        internal Connection Connection { get; }

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

    /// <summary>An update in the outbound queue: written as the slot holds it when the writer reaches it.</summary>
    private sealed class UpdateLine : IOutboundLine
    {
        private readonly UpdateSlot<ReadDevicesView> _slot;

        internal UpdateLine(UpdateSlot<ReadDevicesView> slot) => _slot = slot;

        public bool TryTake(out string line)
        {
            if (_slot.TryTake(out SubscriptionUpdate<ReadDevicesView> update))
            {
                line = EventLine(UpdateEventView.Of(update));
                return true;
            }

            line = string.Empty;
            return false;
        }
    }
}
