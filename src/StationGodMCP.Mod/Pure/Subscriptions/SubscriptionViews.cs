#nullable enable

using System;
using Newtonsoft.Json;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>subscribe's result: {subscription, interval_s, values, frame, result}; result is the first reading.</summary>
internal sealed class SubscribeReplyView
{
    private SubscribeReplyView(SubscriptionId subscription, double intervalS, int values, long frame, object? result)
    {
        Subscription = subscription.ToString();
        IntervalS = intervalS;
        Values = values;
        Frame = frame;
        Result = result;
    }

    internal static SubscribeReplyView Of<TReading>(SubscribeOutcome<TReading>.Subscribed subscribed)
        where TReading : class =>
        new SubscribeReplyView(subscribed.Subscription, subscribed.Interval.Seconds, subscribed.Values,
            subscribed.Frame, subscribed.Reading);

    public string Subscription { get; }

    public double IntervalS { get; }

    public int Values { get; }

    public long Frame { get; }

    /// <summary>A full read_devices result; absent for the world topic.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Result { get; }
}

/// <summary>unsubscribe's result: {subscription, ended: true}.</summary>
internal sealed class UnsubscribeReplyView
{
    internal UnsubscribeReplyView(SubscriptionId subscription)
    {
        Subscription = subscription.ToString();
    }

    public string Subscription { get; }

    public bool Ended => true;
}

/// <summary>
/// An update event's keys after the envelope's type: {event: "update", subscription, seq, frame, game_time_s,
/// late_ms, result}; result is the whole reading in read_devices' reply shape.
/// </summary>
internal sealed class UpdateEventView
{
    private UpdateEventView(string subscription, long seq, long frame, double gameTimeS, double lateMs, object result)
    {
        Subscription = subscription;
        Seq = seq;
        Frame = frame;
        GameTimeS = gameTimeS;
        LateMs = lateMs;
        Result = result;
    }

    internal static UpdateEventView Of<TReading>(SubscriptionUpdate<TReading> update) where TReading : class =>
        new UpdateEventView(update.Subscription.ToString(), update.Seq, update.Frame, update.GameTimeS,
            Math.Round(update.LateMs, 2), update.Reading);

    [JsonProperty(Order = -2)]
    public string Event => "update";

    public string Subscription { get; }

    public long Seq { get; }

    public long Frame { get; }

    public double GameTimeS { get; }

    public double LateMs { get; }

    public object Result { get; }
}

/// <summary>{event: "subscription_ended", subscription, reason}.</summary>
internal sealed class SubscriptionEndedEventView
{
    internal SubscriptionEndedEventView(SubscriptionId subscription, SubscriptionEnd end)
    {
        Subscription = subscription.ToString();
        Reason = end.Reason;
    }

    [JsonProperty(Order = -2)]
    public string Event => "subscription_ended";

    public string Subscription { get; }

    public string Reason { get; }
}

/// <summary>A world-topic event: {event: "world_changed", subscription, world: {id}}.</summary>
internal sealed class WorldChangedEventView
{
    internal WorldChangedEventView(SubscriptionId subscription, WorldId world)
    {
        Subscription = subscription.ToString();
        World = new WorldRef(world);
    }

    [JsonProperty(Order = -2)]
    public string Event => "world_changed";

    public string Subscription { get; }

    public WorldRef World { get; }

    internal sealed class WorldRef
    {
        internal WorldRef(WorldId world)
        {
            Id = world.Value;
        }

        public string Id { get; }
    }
}

/// <summary>A world-topic event: {event: "game_state", subscription, state}.</summary>
internal sealed class GameStateEventView
{
    internal GameStateEventView(SubscriptionId subscription, string state)
    {
        Subscription = subscription.ToString();
        State = state;
    }

    [JsonProperty(Order = -2)]
    public string Event => "game_state";

    public string Subscription { get; }

    public string State { get; }
}
