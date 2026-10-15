#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

    /// <summary>A full read logic result; absent for the world topic.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Result { get; }
}

/// <summary>
/// A find subscribe's result: {subscription, topic: "find", what, interval_s, frame, as_of_s, cost_ms, keyed, result}.
/// result is the find's reply with every list whole and no paging keys; keyed names each list whose entries later
/// updates change one by one, with the field that keys them (empty for a list with no entries yet). Made on the
/// connection's writer thread (the result's text form is made there).
/// </summary>
internal sealed class FindSubscribeReplyView
{
    internal FindSubscribeReplyView(FindSubscribeOutcome.Subscribed subscribed, string what)
    {
        RenderedFind rendered = subscribed.Result.Rendered;
        Subscription = subscribed.Subscription.ToString();
        What = what;
        IntervalS = subscribed.Interval.Seconds;
        Frame = subscribed.Frame;
        AsOfS = Math.Round(subscribed.Result.AsOfS, 3);
        CostMs = Math.Round(subscribed.Result.CostMs, 2);
        Keyed = FindUpdateEventView.KeyedOf(rendered);
        Result = rendered.Whole;
    }

    public string Subscription { get; }

    public string Topic => "find";

    public string What { get; }

    public double IntervalS { get; }

    public long Frame { get; }

    public double AsOfS { get; }

    /// <summary>The main-thread milliseconds the evaluation took, every step summed.</summary>
    public double CostMs { get; }

    public Dictionary<string, string> Keyed { get; }

    public JObject Result { get; }
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
/// late_ms, result}; result is the whole reading in read logic' reply shape.
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

/// <summary>
/// A find subscription's event: {event: "find_update", subscription, seq, frame, as_of_s, keyed, lists, rest} with what
/// changed since the result this subscriber was last sent: per keyed list its added and changed entries (whole) and the
/// keys removed; rest (every other key of the reply, whole) only when it changed. A whole update (after a resume)
/// carries {whole: true, result} instead of lists and rest. as_of_s is when the evaluation began (game time).
/// </summary>
internal sealed class FindUpdateEventView
{
    private FindUpdateEventView(FindUpdate update)
    {
        RenderedFind rendered = update.Result.Rendered;
        Subscription = update.Subscription.ToString();
        Seq = update.Seq;
        Frame = update.Result.Frame;
        AsOfS = Math.Round(update.Result.AsOfS, 3);
        Keyed = KeyedOf(rendered);
        if (update.Change is FindChange change)
        {
            Lists = new Dictionary<string, ListChangeView>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, FindListChange> list in change.Lists)
            {
                Lists.Add(list.Key, new ListChangeView(list.Value));
            }

            Rest = change.Rest;
        }
        else
        {
            Whole = true;
            Result = rendered.Whole;
        }
    }

    internal static FindUpdateEventView Of(FindUpdate update) => new FindUpdateEventView(update);

    internal static Dictionary<string, string> KeyedOf(RenderedFind rendered)
    {
        Dictionary<string, string> keyed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, KeyedList> list in rendered.Lists)
        {
            keyed.Add(list.Key, list.Value.Field);
        }

        return keyed;
    }

    [JsonProperty(Order = -2)]
    public string Event => "find_update";

    public string Subscription { get; }

    public long Seq { get; }

    public long Frame { get; }

    public double AsOfS { get; }

    public Dictionary<string, string> Keyed { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, ListChangeView>? Lists { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JObject? Rest { get; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool Whole { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JObject? Result { get; }

    /// <summary>One keyed list's change: {added, changed, removed}.</summary>
    internal sealed class ListChangeView
    {
        internal ListChangeView(FindListChange change)
        {
            Added = change.Added;
            Changed = change.Changed;
            Removed = change.Removed;
        }

        public List<JToken> Added { get; }

        public List<JToken> Changed { get; }

        public List<string> Removed { get; }
    }
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

/// <summary>
/// A jobs-topic event: {event: "job_ended", subscription, seq, frame, job}; job is the ended job's facts
/// (JobEndFacts), seq 1 for the subscription's first event and one more for each after it.
/// </summary>
internal sealed class JobEndedEventView
{
    internal JobEndedEventView(SubscriptionId subscription, long seq, long frame, object job)
    {
        Subscription = subscription.ToString();
        Seq = seq;
        Frame = frame;
        Job = job;
    }

    [JsonProperty(Order = -2)]
    public string Event => "job_ended";

    public string Subscription { get; }

    public long Seq { get; }

    public long Frame { get; }

    public object Job { get; }
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
