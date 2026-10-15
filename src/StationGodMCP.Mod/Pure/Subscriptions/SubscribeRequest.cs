#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.DeviceReads;

using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>What a devices subscription reads each sample: read logic's request as items, and the gateway it reads through.</summary>
internal sealed class DeviceSubscriptionQuery
{
    internal DeviceSubscriptionQuery(DeviceReadRequest request, string? gatewayId)
    {
        Request = request;
        GatewayId = gatewayId;
    }

    internal DeviceReadRequest Request { get; }

    internal string? GatewayId { get; }
}

/// <summary>
/// subscribe's params, read: a devices subscription, the world or jobs topic, an invalid_argument, or a request well formed
/// but larger than one subscription may read (subscription_limit, so the client polls instead).
/// </summary>
internal abstract class SubscribeRequest
{
    private SubscribeRequest()
    {
    }

    internal sealed class Devices : SubscribeRequest
    {
        internal Devices(DeviceSubscriptionQuery query, SamplingInterval interval)
        {
            Query = query;
            Interval = interval;
        }

        internal DeviceSubscriptionQuery Query { get; }

        internal SamplingInterval Interval { get; }
    }

    /// <summary>A find subscription: the query (its what and options) and how often its live facts are read again.</summary>
    internal sealed class Find : SubscribeRequest
    {
        internal Find(FindQuery query, SamplingInterval interval)
        {
            Query = query;
            Interval = interval;
        }

        internal FindQuery Query { get; }

        internal SamplingInterval Interval { get; }
    }

    internal sealed class World : SubscribeRequest
    {
        internal static readonly World Instance = new World();

        private World()
        {
        }
    }

    internal sealed class Jobs : SubscribeRequest
    {
        internal static readonly Jobs Instance = new Jobs();

        private Jobs()
        {
        }
    }

    internal sealed class Invalid : SubscribeRequest
    {
        internal Invalid(string message)
        {
            Message = message;
        }

        internal string Message { get; }
    }

    internal sealed class OverLimit : SubscribeRequest
    {
        internal OverLimit(SubscriptionRefusal refusal)
        {
            Refusal = refusal;
        }

        internal SubscriptionRefusal Refusal { get; }
    }

    private static readonly string[] OwnKeys = { "topic", "interval_s" };

    /// <summary>A find subscription's interval when none is given, in game seconds.</summary>
    internal const double FindDefaultIntervalS = 10.0;

    /// <summary>A find subscription's shortest interval: an evaluation is a find's whole walk.</summary>
    internal const double FindMinimumIntervalS = 1.0;

    // A find subscription holds the whole list: the paging options have nothing to say.
    private static readonly string[] FindPagingKeys = { "limit", "offset", "cursor", "order" };

    /// <summary>
    /// The params as protocol.md defines them: topic and interval_s, and for devices read logic's options (id, logic,
    /// reads, gateway, include), for find the find's what and its options but paging. Unknown keys are refused; a world
    /// or jobs subscription takes topic alone. The item and value bounds of one read are a subscription limit here, not
    /// an invalid argument.
    /// </summary>
    internal static SubscribeRequest Of(JObject? parameters)
    {
        JObject given = parameters ?? new JObject();
        if (Present(given["topic"]) is JToken named && named.Type == JTokenType.String && Is(named, FindTopicName))
        {
            return OfFind(given);
        }

        foreach (JProperty property in given.Properties())
        {
            if (Array.IndexOf(OwnKeys, property.Name) < 0 && !ReadLogicOptions.Spec.Takes(property.Name))
            {
                return new Invalid(
                    $"subscribe has no option '{property.Name}'; it takes {string.Join(", ", OwnKeys)} and read " +
                    $"logic's {string.Join(", ", ReadLogicOptions.Spec.OptionNames)}.");
            }
        }

        JToken? topic = Present(given["topic"]);
        if (topic != null && (topic.Type != JTokenType.String ||
                              !(Is(topic, "devices") || Is(topic, WorldTopic) || Is(topic, JobsTopic))))
        {
            return new Invalid("Argument 'topic' must be \"devices\", \"world\", \"jobs\" or \"find\".");
        }

        if (topic != null && Is(topic, WorldTopic))
        {
            return TopicAlone(given, WorldTopic, World.Instance);
        }

        return topic != null && Is(topic, JobsTopic) ? TopicAlone(given, JobsTopic, Jobs.Instance) : OfDevices(given);
    }

    private const string WorldTopic = "world";

    private const string JobsTopic = "jobs";

    private const string FindTopicName = "find";

    // topic "find": what (a FindTopic), interval_s (1 to 3600, 10 by default), and the find's own options, checked as a
    // find call's are; paging refused.
    private static SubscribeRequest OfFind(JObject given)
    {
        JToken? whatToken = Present(given["what"]);
        FindTopic? topic = whatToken?.Type == JTokenType.String ? FindTopic.Of(whatToken.Value<string>()?.Trim()) : null;
        if (topic == null)
        {
            return new Invalid($"The find topic takes what: one of {string.Join(", ", FindTopic.Whats)}.");
        }

        JToken? intervalToken = Present(given["interval_s"]);
        SamplingInterval interval = SamplingInterval.Default;
        bool intervalOk = intervalToken == null
            ? SamplingInterval.TryOf(FindDefaultIntervalS, out interval)
            : (intervalToken.Type == JTokenType.Integer || intervalToken.Type == JTokenType.Float) &&
              intervalToken.Value<double>() >= FindMinimumIntervalS &&
              SamplingInterval.TryOf(intervalToken.Value<double>(), out interval);
        if (!intervalOk)
        {
            return new Invalid(
                $"interval_s must be a number from {FindMinimumIntervalS} to {SamplingInterval.MaximumSeconds} " +
                "(game seconds) for the find topic.");
        }

        JObject call = new JObject();
        JObject options = new JObject();
        foreach (JProperty property in given.Properties())
        {
            if (Array.IndexOf(OwnKeys, property.Name) >= 0)
            {
                continue;
            }

            if (Array.IndexOf(FindPagingKeys, property.Name) >= 0)
            {
                return new Invalid(
                    $"A find subscription holds the whole list: it takes no '{property.Name}' " +
                    $"({string.Join(", ", FindPagingKeys)} page a find call).");
            }

            if (VerbSurface.ShapingOption(property.Name) != null)
            {
                return new Invalid(
                    $"A find subscription sends whole entries and only what changed: it takes no '{property.Name}'.");
            }

            call[property.Name] = property.Value.DeepClone();
            if (property.Name != "what")
            {
                options[property.Name] = property.Value.DeepClone();
            }
        }

        call["what"] = topic.What;
        if (OptionCheck.Check(VerbSurface.Verb(FindTopicName)!, call, out _) is { } refused)
        {
            return new Invalid(refused.Message);
        }

        return new Find(new FindQuery(topic, options), interval);
    }

    // The world and jobs topics take topic alone.
    private static SubscribeRequest TopicAlone(JObject given, string topic, SubscribeRequest request)
    {
        foreach (JProperty property in given.Properties())
        {
            if (property.Name != "topic" && Present(property.Value) != null)
            {
                return new Invalid($"The {topic} topic takes no '{property.Name}'.");
            }
        }

        return request;
    }

    private static SubscribeRequest OfDevices(JObject given)
    {
        if (!TryInterval(Present(given["interval_s"]), out SamplingInterval interval))
        {
            return new Invalid(
                $"interval_s must be a number from {SamplingInterval.MinimumSeconds} to " +
                $"{SamplingInterval.MaximumSeconds} (game seconds).");
        }

        JObject read = new JObject();
        foreach (JProperty property in given.Properties())
        {
            if (Array.IndexOf(OwnKeys, property.Name) < 0)
            {
                read[property.Name] = property.Value.DeepClone();
            }
        }

        List<OptionProblem> problems = new List<OptionProblem>();
        if (OptionCheck.Check(VerbSurface.Verb("read")!, With(read), out _) is { } refused)
        {
            return new Invalid(refused.Message);
        }

        OptionReader reader = new OptionReader(ReadLogicOptions.Spec, read);
        ReadLogicOptions options = ReadLogicOptions.Bind(reader);
        problems.AddRange(reader.Problems);
        if (problems.Count > 0 || LogicReadRequest.Of(options, problems, bounded: false) is not { } request)
        {
            return new Invalid(OptionProblem.Refusal("subscribe", problems).Message);
        }

        DeviceReadRequest items = request.AsItems();
        return OverOneRead(items) is { } limit
            ? new OverLimit(limit)
            : new Devices(new DeviceSubscriptionQuery(items, options.Gateway), interval);
    }

    // read logic's options with its what, as the option check takes a call's params.
    private static JObject With(JObject read)
    {
        JObject call = (JObject)read.DeepClone();
        call["what"] = "logic";
        return call;
    }

    // A read larger than one read logic call may make is a subscription limit, which tells the client to poll.
    private static SubscriptionRefusal? OverOneRead(DeviceReadRequest request)
    {
        if (request.Items.Count > DeviceReadBounds.MaximumItems)
        {
            return new SubscriptionRefusal(SubscriptionLimitKind.ItemsPerSubscription, DeviceReadBounds.MaximumItems,
                request.Items.Count, 0.0);
        }

        int values = 0;
        foreach (DeviceReadItem item in request.Items)
        {
            values += item.Weight;
        }

        return values > DeviceReadBounds.MaximumValues
            ? new SubscriptionRefusal(SubscriptionLimitKind.ValuesPerSubscription, DeviceReadBounds.MaximumValues,
                values, 0.0)
            : null;
    }

    private static bool TryInterval(JToken? token, out SamplingInterval interval)
    {
        if (token == null)
        {
            interval = SamplingInterval.Default;
            return true;
        }

        interval = SamplingInterval.Default;
        return (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) &&
               SamplingInterval.TryOf(token.Value<double>(), out interval);
    }

    private static bool Is(JToken token, string value) =>
        string.Equals(token.Value<string>()?.Trim(), value, StringComparison.OrdinalIgnoreCase);

    // JSON null is an absent key, as every argument reads it.
    private static JToken? Present(JToken? token) => token == null || token.Type == JTokenType.Null ? null : token;
}

/// <summary>unsubscribe's params: {subscription}.</summary>
internal static class UnsubscribeRequest
{
    /// <summary>The subscription named, or an invalid_argument message.</summary>
    internal static bool TryOf(JObject? parameters, out SubscriptionId subscription, out string problem)
    {
        subscription = default;
        problem = string.Empty;
        JObject given = parameters ?? new JObject();
        foreach (JProperty property in given.Properties())
        {
            if (property.Name != "subscription")
            {
                problem = $"unsubscribe has no argument '{property.Name}'; it takes subscription.";
                return false;
            }
        }

        JToken? token = given["subscription"];
        if (token == null || token.Type != JTokenType.String ||
            !SubscriptionId.TryParse(token.Value<string>(), out subscription))
        {
            problem = "Argument 'subscription' must be a subscription id such as \"s3\".";
            return false;
        }

        return true;
    }
}
