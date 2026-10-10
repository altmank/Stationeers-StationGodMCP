#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>subscribe's outcome: the subscription and its first reading, or the limit it would pass.</summary>
internal abstract class SubscribeOutcome<TReading> where TReading : class
{
    private SubscribeOutcome()
    {
    }

    internal sealed class Subscribed : SubscribeOutcome<TReading>
    {
        internal Subscribed(SubscriptionId subscription, SamplingInterval interval, int values, long frame,
            TReading? reading)
        {
            Subscription = subscription;
            Interval = interval;
            Values = values;
            Frame = frame;
            Reading = reading;
        }

        internal SubscriptionId Subscription { get; }

        internal SamplingInterval Interval { get; }

        /// <summary>0 for the world topic.</summary>
        internal int Values { get; }

        internal long Frame { get; }

        /// <summary>The first reading; null for the world topic.</summary>
        internal TReading? Reading { get; }
    }

    internal sealed class Refused : SubscribeOutcome<TReading>
    {
        internal Refused(SubscriptionRefusal refusal)
        {
            Refusal = refusal;
        }

        internal SubscriptionRefusal Refusal { get; }
    }
}

/// <summary>
/// The subscriptions of every connection, sampled on the main thread. Pure: the device reads, the comparison and the
/// transport are injected, and time comes in with each call.
/// <para>
/// A devices subscription reads its items in one frame each interval of game time, in the first frame on or after
/// it is due, and offers the reading to its UpdateSlot only when a value other than the clock differs from the last
/// reading offered (or a resync asked for the next reading whatever it holds). Due samples are taken round-robin
/// across connections, the oldest due first within one, while the lane's budget allows; the connection the budget
/// stopped at goes first next frame. Admission keeps one subscription to one read_devices call, a connection to
/// MaxSubscriptions and MaxConnectionValues, and the projected load of all of them to MaxProjectedMsPerSecond.
/// </para>
/// </summary>
internal sealed class SubscriptionEngine<TReading> where TReading : class
{
    private readonly IDeviceReader<TReading> _reader;
    private readonly IReadingComparer<TReading> _comparer;
    private readonly Dictionary<ConnectionId, ConnectionSubscriptions> _byConnection =
        new Dictionary<ConnectionId, ConnectionSubscriptions>();

    private readonly List<ConnectionSubscriptions> _connections = new List<ConnectionSubscriptions>();
    private long _lastId;
    private int _cursor;
    private string? _gameState;

    internal SubscriptionEngine(IDeviceReader<TReading> reader, IReadingComparer<TReading> comparer,
        SubscriptionLimits limits)
    {
        _reader = reader;
        _comparer = comparer;
        Limits = limits;
    }

    internal SubscriptionLimits Limits { get; private set; }

    /// <summary>Subscriptions held on the connection, both topics.</summary>
    internal int CountOf(ConnectionId connection) =>
        _byConnection.TryGetValue(connection, out ConnectionSubscriptions? held) ? held.Count : 0;

    /// <summary>Values the connection's devices subscriptions read per sample.</summary>
    internal int ValuesOf(ConnectionId connection) =>
        _byConnection.TryGetValue(connection, out ConnectionSubscriptions? held) ? held.Values : 0;

    /// <summary>The projected sampling load of every devices subscription, in ms per game second.</summary>
    internal double ProjectedMsPerSecond()
    {
        double total = 0.0;
        foreach (ConnectionSubscriptions held in _connections)
        {
            foreach (DeviceSubscription subscription in held.Devices)
            {
                total += subscription.ProjectedMsPerSecond;
            }
        }

        return total;
    }

    /// <summary>
    /// A devices subscription: admitted, then read once now for the reply's first reading, which is also the
    /// baseline later readings are compared with. A read that throws leaves no subscription behind; the call fails
    /// as read_devices would.
    /// </summary>
    internal SubscribeOutcome<TReading> Subscribe(ConnectionId connection, DeviceSubscriptionQuery query,
        SamplingInterval interval, SamplingTick tick)
    {
        SubscriptionPlan plan = SubscriptionPlan.Of(query.Request);
        if (Admit(connection, plan, interval) is SubscriptionRefusal refusal)
        {
            return new SubscribeOutcome<TReading>.Refused(refusal);
        }

        TReading first = _reader.Read(query);
        SubscriptionId id = new SubscriptionId(++_lastId);
        DeviceSubscription subscription = new DeviceSubscription(id, query, plan, interval,
            new UpdateSlot<TReading>(connection, id), first, tick.GameTimeS + interval.Seconds);
        Held(connection).Add(subscription);
        return new SubscribeOutcome<TReading>.Subscribed(id, interval, plan.Values, tick.Frame, first);
    }

    /// <summary>A world-topic subscription: world_changed and game_state events, nothing sampled.</summary>
    internal SubscribeOutcome<TReading> SubscribeWorld(ConnectionId connection, SamplingTick tick)
    {
        int held = CountOf(connection);
        if (held + 1 > Limits.MaxSubscriptions)
        {
            return new SubscribeOutcome<TReading>.Refused(new SubscriptionRefusal(
                SubscriptionLimitKind.SubscriptionsPerConnection, Limits.MaxSubscriptions, held + 1,
                ProjectedMsPerSecond()));
        }

        SubscriptionId id = new SubscriptionId(++_lastId);
        Held(connection).World.Add(id);
        return new SubscribeOutcome<TReading>.Subscribed(id, SamplingInterval.Default, 0, tick.Frame, null);
    }

    /// <summary>Ends the connection's subscription; false when it holds no such subscription (unknown_subscription).</summary>
    internal bool Unsubscribe(ConnectionId connection, SubscriptionId subscription)
    {
        if (!_byConnection.TryGetValue(connection, out ConnectionSubscriptions? held) || !held.Remove(subscription))
        {
            return false;
        }

        PruneIfEmpty(held);
        return true;
    }

    /// <summary>
    /// The next sample of the subscription is taken as soon as possible and sent whether or not it changed: for a
    /// client that lost track of the subscription's state. False when the connection holds no such devices
    /// subscription. The update keeps the slot's seq rules, so a resync never makes a gap.
    /// </summary>
    internal bool Resync(ConnectionId connection, SubscriptionId subscription)
    {
        if (!_byConnection.TryGetValue(connection, out ConnectionSubscriptions? held) ||
            !(held.Find(subscription) is DeviceSubscription found))
        {
            return false;
        }

        found.ForceNext = true;
        found.NextDueS = double.NegativeInfinity;
        return true;
    }

    /// <summary>The connection closed: its subscriptions end without an event.</summary>
    internal void DropConnection(ConnectionId connection)
    {
        if (_byConnection.TryGetValue(connection, out ConnectionSubscriptions? held))
        {
            held.CloseAll();
            Forget(held);
        }
    }

    /// <summary>
    /// A world finished loading: every devices subscription ends with world_changed, since its reference ids may name
    /// other things now, and every world-topic subscription is told the new id.
    /// </summary>
    internal void WorldChanged(WorldId world, ISubscriptionEvents<TReading> events)
    {
        for (int index = _connections.Count - 1; index >= 0; index--)
        {
            ConnectionSubscriptions held = _connections[index];
            held.EndDevices(SubscriptionEnd.WorldChanged, events);
            foreach (SubscriptionId subscription in held.World)
            {
                events.WorldChanged(held.Connection, subscription, world);
            }

            PruneIfEmpty(held);
        }
    }

    /// <summary>Each frame or on change: world-topic subscriptions hear of a game state other than the last one seen.</summary>
    internal void ObserveGameState(string state, ISubscriptionEvents<TReading> events)
    {
        if (string.Equals(state, _gameState, StringComparison.Ordinal))
        {
            return;
        }

        _gameState = state;
        foreach (ConnectionSubscriptions held in _connections)
        {
            foreach (SubscriptionId subscription in held.World)
            {
                events.GameStateChanged(held.Connection, subscription, state);
            }
        }
    }

    /// <summary>
    /// The owner changed the limits: subscriptions now past them end with subscription_ended {reason: limit}, newest
    /// first, until each connection and the whole mod fit again.
    /// </summary>
    internal void ApplyLimits(SubscriptionLimits limits, ISubscriptionEvents<TReading> events)
    {
        Limits = limits;
        for (int index = _connections.Count - 1; index >= 0; index--)
        {
            ConnectionSubscriptions held = _connections[index];
            if (!limits.Enabled)
            {
                held.EndDevices(SubscriptionEnd.Limit, events);
            }

            while (held.Count > limits.MaxSubscriptions || held.Values > limits.MaxConnectionValues)
            {
                if (!held.EndNewest(SubscriptionEnd.Limit, events))
                {
                    break;
                }
            }
        }

        while (ProjectedMsPerSecond() > limits.MaxProjectedMsPerSecond && EndNewestAnywhere(events))
        {
        }

        for (int index = _connections.Count - 1; index >= 0; index--)
        {
            PruneIfEmpty(_connections[index]);
        }
    }

    /// <summary>
    /// One frame of sampling: due devices subscriptions, round-robin across connections, while the budget allows.
    /// Returns the samples taken. No allocation beyond the reads themselves and nothing serialised here.
    /// </summary>
    internal int Poll(SamplingTick tick, ISamplingBudget budget, ISubscriptionEvents<TReading> events)
    {
        int count = _connections.Count;
        if (count == 0)
        {
            return 0;
        }

        int taken = 0;
        int start = _cursor % count;
        bool tookInRound = true;
        while (tookInRound)
        {
            tookInRound = false;
            for (int step = 0; step < count; step++)
            {
                int index = (start + step) % count;
                ConnectionSubscriptions held = _connections[index];
                DeviceSubscription? due = held.OldestDue(tick);
                if (due == null)
                {
                    continue;
                }

                if (!budget.MayTakeAnother())
                {
                    _cursor = index;
                    PruneEmpty();
                    return taken;
                }

                Sample(held, due, tick, events);
                taken++;
                tookInRound = true;
            }
        }

        _cursor = (start + 1) % count;
        PruneEmpty();
        return taken;
    }

    private void Sample(ConnectionSubscriptions held, DeviceSubscription subscription, SamplingTick tick,
        ISubscriptionEvents<TReading> events)
    {
        double dueS = subscription.NextDueS;
        subscription.LastSampledFrame = tick.Frame;
        TReading reading;
        try
        {
            reading = _reader.Read(subscription.Query);
        }
        catch (Exception failure)
        {
            // The game tick's boundary: a read that throws ends its own subscription, never the frame.
            held.End(subscription, new SubscriptionEnd.ReadFailed(failure), events);
            return;
        }

        subscription.NextDueS = NextDue(dueS, subscription.Interval.Seconds, tick.GameTimeS);
        bool changed = subscription.ForceNext || !_comparer.SameValues(subscription.Baseline, reading);
        if (!changed)
        {
            return;
        }

        subscription.ForceNext = false;
        subscription.Baseline = reading;
        double lateMs = double.IsNegativeInfinity(dueS) ? 0.0 : Math.Max(0.0, (tick.GameTimeS - dueS) * 1000.0);
        if (subscription.Slot.Offer(reading, tick.Frame, tick.GameTimeS, lateMs))
        {
            events.Enqueue(subscription.Slot);
        }
    }

    /// <summary>
    /// The first point of the subscription's grid (due + k * interval) after now, so a late sample does not make the
    /// next ones late too, and a frame that missed several points takes one sample, not a burst.
    /// </summary>
    internal static double NextDue(double dueS, double intervalS, double nowS)
    {
        if (double.IsNegativeInfinity(dueS) || dueS + intervalS > nowS)
        {
            return double.IsNegativeInfinity(dueS) ? nowS + intervalS : dueS + intervalS;
        }

        double missed = Math.Floor((nowS - dueS) / intervalS) + 1.0;
        double next = dueS + missed * intervalS;
        return next > nowS ? next : next + intervalS;
    }

    private SubscriptionRefusal? Admit(ConnectionId connection, SubscriptionPlan plan, SamplingInterval interval)
    {
        double projected = ProjectedMsPerSecond() + plan.ProjectedMsPerSecond(interval);
        if (!Limits.Enabled)
        {
            return new SubscriptionRefusal(SubscriptionLimitKind.SubscriptionsOff, 0, 1, projected);
        }

        if (plan.Items > Limits.MaxItemsPerSubscription)
        {
            return new SubscriptionRefusal(SubscriptionLimitKind.ItemsPerSubscription,
                Limits.MaxItemsPerSubscription, plan.Items, projected);
        }

        if (plan.Values > Limits.MaxValuesPerSubscription)
        {
            return new SubscriptionRefusal(SubscriptionLimitKind.ValuesPerSubscription,
                Limits.MaxValuesPerSubscription, plan.Values, projected);
        }

        int count = CountOf(connection) + 1;
        if (count > Limits.MaxSubscriptions)
        {
            return new SubscriptionRefusal(SubscriptionLimitKind.SubscriptionsPerConnection, Limits.MaxSubscriptions,
                count, projected);
        }

        int values = ValuesOf(connection) + plan.Values;
        if (values > Limits.MaxConnectionValues)
        {
            return new SubscriptionRefusal(SubscriptionLimitKind.ValuesPerConnection, Limits.MaxConnectionValues,
                values, projected);
        }

        return projected > Limits.MaxProjectedMsPerSecond
            ? new SubscriptionRefusal(SubscriptionLimitKind.ProjectedLoad, Limits.MaxProjectedMsPerSecond, projected,
                projected)
            : null;
    }

    private bool EndNewestAnywhere(ISubscriptionEvents<TReading> events)
    {
        ConnectionSubscriptions? newestHolder = null;
        long newest = 0;
        foreach (ConnectionSubscriptions held in _connections)
        {
            foreach (DeviceSubscription subscription in held.Devices)
            {
                if (subscription.Id.Number > newest)
                {
                    newest = subscription.Id.Number;
                    newestHolder = held;
                }
            }
        }

        return newestHolder != null && newestHolder.EndNewestDevice(SubscriptionEnd.Limit, events);
    }

    private ConnectionSubscriptions Held(ConnectionId connection)
    {
        if (!_byConnection.TryGetValue(connection, out ConnectionSubscriptions? held))
        {
            held = new ConnectionSubscriptions(connection);
            _byConnection.Add(connection, held);
            _connections.Add(held);
        }

        return held;
    }

    private void PruneIfEmpty(ConnectionSubscriptions held)
    {
        if (held.Count == 0)
        {
            Forget(held);
        }
    }

    private void PruneEmpty()
    {
        for (int index = _connections.Count - 1; index >= 0; index--)
        {
            PruneIfEmpty(_connections[index]);
        }
    }

    private void Forget(ConnectionSubscriptions held)
    {
        _byConnection.Remove(held.Connection);
        _connections.Remove(held);
    }

    /// <summary>One devices subscription's state between samples.</summary>
    private sealed class DeviceSubscription
    {
        internal DeviceSubscription(SubscriptionId id, DeviceSubscriptionQuery query, SubscriptionPlan plan,
            SamplingInterval interval, UpdateSlot<TReading> slot, TReading baseline, double nextDueS)
        {
            Id = id;
            Query = query;
            Plan = plan;
            Interval = interval;
            Slot = slot;
            Baseline = baseline;
            NextDueS = nextDueS;
            LastSampledFrame = long.MinValue;
        }

        internal SubscriptionId Id { get; }

        internal DeviceSubscriptionQuery Query { get; }

        internal SubscriptionPlan Plan { get; }

        internal SamplingInterval Interval { get; }

        internal UpdateSlot<TReading> Slot { get; }

        /// <summary>The last reading offered to the client (the subscribe reply's first one to begin with).</summary>
        internal TReading Baseline { get; set; }

        internal double NextDueS { get; set; }

        internal bool ForceNext { get; set; }

        internal long LastSampledFrame { get; set; }

        internal double ProjectedMsPerSecond => Plan.ProjectedMsPerSecond(Interval);
    }

    /// <summary>One connection's subscriptions, in the order they were made.</summary>
    private sealed class ConnectionSubscriptions
    {
        internal ConnectionSubscriptions(ConnectionId connection)
        {
            Connection = connection;
        }

        internal ConnectionId Connection { get; }

        internal List<DeviceSubscription> Devices { get; } = new List<DeviceSubscription>();

        internal List<SubscriptionId> World { get; } = new List<SubscriptionId>();

        internal int Values { get; private set; }

        internal int Count => Devices.Count + World.Count;

        internal void Add(DeviceSubscription subscription)
        {
            Devices.Add(subscription);
            Values += subscription.Plan.Values;
        }

        internal DeviceSubscription? Find(SubscriptionId id)
        {
            foreach (DeviceSubscription subscription in Devices)
            {
                if (subscription.Id.Equals(id))
                {
                    return subscription;
                }
            }

            return null;
        }

        /// <summary>The due subscription with the oldest due time, at most one sample per frame each.</summary>
        internal DeviceSubscription? OldestDue(SamplingTick tick)
        {
            DeviceSubscription? oldest = null;
            foreach (DeviceSubscription subscription in Devices)
            {
                if (subscription.NextDueS <= tick.GameTimeS && subscription.LastSampledFrame != tick.Frame &&
                    (oldest == null || subscription.NextDueS < oldest.NextDueS))
                {
                    oldest = subscription;
                }
            }

            return oldest;
        }

        internal bool Remove(SubscriptionId id)
        {
            if (Find(id) is DeviceSubscription found)
            {
                Close(found);
                return true;
            }

            return World.Remove(id);
        }

        internal void End(DeviceSubscription subscription, SubscriptionEnd end, ISubscriptionEvents<TReading> events)
        {
            Close(subscription);
            events.Ended(Connection, subscription.Id, end);
        }

        internal void EndDevices(SubscriptionEnd end, ISubscriptionEvents<TReading> events)
        {
            while (Devices.Count > 0)
            {
                End(Devices[0], end, events);
            }
        }

        /// <summary>Ends the newest subscription of either topic; false when none is held.</summary>
        internal bool EndNewest(SubscriptionEnd end, ISubscriptionEvents<TReading> events)
        {
            long newestDevice = Devices.Count > 0 ? Devices[Devices.Count - 1].Id.Number : 0;
            long newestWorld = World.Count > 0 ? World[World.Count - 1].Number : 0;
            if (newestDevice == 0 && newestWorld == 0)
            {
                return false;
            }

            if (newestDevice > newestWorld)
            {
                return EndNewestDevice(end, events);
            }

            SubscriptionId id = World[World.Count - 1];
            World.RemoveAt(World.Count - 1);
            events.Ended(Connection, id, end);
            return true;
        }

        internal bool EndNewestDevice(SubscriptionEnd end, ISubscriptionEvents<TReading> events)
        {
            if (Devices.Count == 0)
            {
                return false;
            }

            End(Devices[Devices.Count - 1], end, events);
            return true;
        }

        internal void CloseAll()
        {
            while (Devices.Count > 0)
            {
                Close(Devices[0]);
            }

            World.Clear();
        }

        private void Close(DeviceSubscription subscription)
        {
            subscription.Slot.Close();
            Devices.Remove(subscription);
            Values -= subscription.Plan.Values;
        }
    }
}
