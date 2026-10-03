#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Subscriptions;

namespace StationGodMCP.Protocol;

/// <summary>
/// Subscriptions on the wire (protocol.md, Subscriptions): subscribe and unsubscribe as version-2 calls run on the main
/// thread, the engine's samples in the scheduler's subscription lane, and its events into each connection's outbound
/// queue, where an update waits as its subscription's UpdateSlot and is written by the connection's writer thread.
/// Main thread only, apart from Closed, which a connection's reader thread calls as it ends.
/// </summary>
internal sealed class SubscriptionHub : ISubscriptionEvents<ReadDevicesView>, ISampleLane
{
    internal const string SubscribeMethod = "subscribe";
    internal const string UnsubscribeMethod = "unsubscribe";
    internal const string UnknownSubscriptionCode = "unknown_subscription";

    private readonly SubscriptionEngine<ReadDevicesView> _engine;
    private readonly Dictionary<ConnectionId, Connection> _connections = new Dictionary<ConnectionId, Connection>();
    private readonly ConcurrentQueue<Connection> _closed = new ConcurrentQueue<Connection>();
    private readonly OneSample _one = new OneSample();
    private SamplingTick _tick;

    internal SubscriptionHub(IDeviceReader<ReadDevicesView> reader, SubscriptionLimits limits)
    {
        _engine = new SubscriptionEngine<ReadDevicesView>(reader, ReadDevicesComparer.Instance, limits);
    }

    internal SubscriptionLimits Limits => _engine.Limits;

    /// <summary>Subscriptions the connection holds (mod_info, the console).</summary>
    internal int CountOf(string clientId) => _engine.CountOf(new ConnectionId(clientId));

    /// <summary>Whether the method is one of the hub's.</summary>
    internal static bool Handles(string method) => method == SubscribeMethod || method == UnsubscribeMethod;

    /// <summary>
    /// The start of a frame: connections that ended since the last one drop their subscriptions silently, and the
    /// frame's tick is what samples and replies are stamped with.
    /// </summary>
    internal void BeginFrame(SamplingTick tick)
    {
        _tick = tick;
        while (_closed.TryDequeue(out Connection connection))
        {
            ConnectionId id = new ConnectionId(connection.ClientId);
            _engine.DropConnection(id);
            _connections.Remove(id);
        }
    }

    /// <summary>A connection ended (any thread): its subscriptions end without an event at the next frame.</summary>
    internal void Closed(object source)
    {
        if (source is Connection connection)
        {
            _closed.Enqueue(connection);
        }
    }

    /// <summary>The connection lost its level: every subscription ends with subscription_ended {reason: revoked}.</summary>
    internal void Revoke(string clientId) => _engine.Revoke(new ConnectionId(clientId), this);

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
                ? Subscribe((Connection)call.Source!, request.Params)
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
                Elapsed(watch), queueMs, _tick.Frame));
        }

        return ApiJson.WriteShared(result is ErrorView error
            ? CallReplyView.Failed(request.Id, error, Elapsed(watch), queueMs, _tick.Frame)
            : CallReplyView.Of(request.Id, result, false, Elapsed(watch), queueMs, _tick.Frame));
    }

    public bool RunNextDueSample()
    {
        _one.Reset();
        return _engine.Poll(_tick, _one, this) > 0;
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

    private object Subscribe(Connection connection, JObject? parameters)
    {
        ConnectionId id = new ConnectionId(connection.ClientId);
        SubscribeOutcome<ReadDevicesView> outcome = SubscribeRequest.Of(parameters) switch
        {
            SubscribeRequest.Devices devices => _engine.Subscribe(id, devices.Query, devices.Interval, _tick),
            SubscribeRequest.World _ => _engine.SubscribeWorld(id, _tick),
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
