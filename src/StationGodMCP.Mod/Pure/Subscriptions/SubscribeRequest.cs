#nullable enable

using System;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.DeviceReads;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>
/// What a devices subscription reads each sample: the parsed request, and read_devices' own arguments (gateway_id,
/// items, include) for the read path that takes them.
/// </summary>
internal sealed class DeviceSubscriptionQuery
{
    internal DeviceSubscriptionQuery(DeviceReadRequest request, string? gatewayId, JObject readArguments)
    {
        Request = request;
        GatewayId = gatewayId;
        ReadArguments = readArguments;
    }

    internal DeviceReadRequest Request { get; }

    internal string? GatewayId { get; }

    internal JObject ReadArguments { get; }
}

/// <summary>
/// subscribe's params, read: a devices subscription, the world topic, an invalid_argument, or a request well formed
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

    private static readonly string[] DeviceKeys = { "topic", "items", "include", "gateway_id", "interval_s" };

    /// <summary>
    /// The params as protocol.md defines them. Unknown keys are refused; a world subscription takes topic alone. The
    /// item and value bounds of one read_devices call are a subscription limit here, not an invalid argument.
    /// </summary>
    internal static SubscribeRequest Of(JObject? parameters)
    {
        JObject given = parameters ?? new JObject();
        foreach (JProperty property in given.Properties())
        {
            if (Array.IndexOf(DeviceKeys, property.Name) < 0)
            {
                return new Invalid(
                    $"subscribe has no argument '{property.Name}'; it takes {string.Join(", ", DeviceKeys)}.");
            }
        }

        JToken? topic = Present(given["topic"]);
        if (topic != null && (topic.Type != JTokenType.String ||
                              !(Is(topic, "devices") || Is(topic, "world"))))
        {
            return new Invalid("Argument 'topic' must be \"devices\" or \"world\".");
        }

        if (topic != null && Is(topic, "world"))
        {
            foreach (JProperty property in given.Properties())
            {
                if (property.Name != "topic" && Present(property.Value) != null)
                {
                    return new Invalid($"The world topic takes no '{property.Name}'.");
                }
            }

            return World.Instance;
        }

        return OfDevices(given);
    }

    private static SubscribeRequest OfDevices(JObject given)
    {
        JToken? gateway = Present(given["gateway_id"]);
        if (gateway != null && gateway.Type != JTokenType.String)
        {
            return new Invalid("Argument 'gateway_id' must be a string.");
        }

        if (!TryInterval(Present(given["interval_s"]), out SamplingInterval interval))
        {
            return new Invalid(
                $"Argument 'interval_s' must be a number from {SamplingInterval.MinimumSeconds} to " +
                $"{SamplingInterval.MaximumSeconds} (game seconds).");
        }

        JToken? items = given["items"];
        JToken? include = given["include"];
        switch (DeviceReadParse.Of(items, include))
        {
            case DeviceReadParse.Parsed parsed:
                string? gatewayId = gateway?.Value<string>();
                JObject readArguments = new JObject { ["items"] = items!.DeepClone() };
                if (Present(include) != null)
                {
                    readArguments["include"] = include!.DeepClone();
                }

                if (gatewayId != null)
                {
                    readArguments["gateway_id"] = gatewayId;
                }

                return new Devices(new DeviceSubscriptionQuery(parsed.Request, gatewayId, readArguments), interval);
            case DeviceReadParse.Refused refused:
                return OverOneCall(items) is SubscriptionRefusal refusal
                    ? new OverLimit(refusal)
                    : new Invalid(refused.Message);
            default:
                throw new InvalidOperationException("Unknown read_devices parse.");
        }
    }

    // A request read_devices refused may be refused only for its size: then it is a subscription limit, which tells
    // the client to poll. Counted leniently, as read_devices counts values.
    private static SubscriptionRefusal? OverOneCall(JToken? items)
    {
        if (!(items is JArray array))
        {
            return null;
        }

        if (array.Count > DeviceReadBounds.MaximumItems)
        {
            return new SubscriptionRefusal(SubscriptionLimitKind.ItemsPerSubscription, DeviceReadBounds.MaximumItems,
                array.Count, 0.0);
        }

        int values = 0;
        foreach (JToken item in array)
        {
            if (!(item is JObject entry))
            {
                continue;
            }

            values += Count(entry["logic"]);
            if (entry["slots"] is JArray slots)
            {
                foreach (JToken slot in slots)
                {
                    values += slot is JObject slotEntry && slotEntry["logic"] is JArray slotLogic
                        ? slotLogic.Count
                        : DeviceReadBounds.AllSlotValuesWeight;
                }
            }
        }

        return values > DeviceReadBounds.MaximumValues
            ? new SubscriptionRefusal(SubscriptionLimitKind.ValuesPerSubscription, DeviceReadBounds.MaximumValues,
                values, 0.0)
            : null;
    }

    private static int Count(JToken? token) => token is JArray array ? array.Count : 0;

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
