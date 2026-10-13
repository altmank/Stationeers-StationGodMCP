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

    /// <summary>
    /// The params as protocol.md defines them: topic and interval_s, and for devices read logic's options (id, logic,
    /// reads, gateway, include). Unknown keys are refused; a world or jobs subscription takes topic alone. The item and
    /// value bounds of one read are a subscription limit here, not an invalid argument.
    /// </summary>
    internal static SubscribeRequest Of(JObject? parameters)
    {
        JObject given = parameters ?? new JObject();
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
            return new Invalid("Argument 'topic' must be \"devices\", \"world\" or \"jobs\".");
        }

        if (topic != null && Is(topic, WorldTopic))
        {
            return TopicAlone(given, WorldTopic, World.Instance);
        }

        return topic != null && Is(topic, JobsTopic) ? TopicAlone(given, JobsTopic, Jobs.Instance) : OfDevices(given);
    }

    private const string WorldTopic = "world";

    private const string JobsTopic = "jobs";

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
