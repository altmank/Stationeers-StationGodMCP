#nullable enable

using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Sampling;

/// <summary>
/// sample_logic's arguments, with the sidecar loop's defaults, bounds and messages: targets as read_logic_many's reads
/// (1 to 32), duration_seconds 0.1 to 30 (default 5), interval_seconds 0.05 to 5 (default 0.5), and at most
/// MaximumSamples samples, the first at 0 s included.
/// </summary>
internal sealed class SampleLogicArguments
{
    internal const int MaximumTargets = 32;
    internal const int MaximumSamples = 120;
    internal const double MinimumDurationSeconds = 0.1;
    internal const double MaximumDurationSeconds = 30.0;
    internal const double DefaultDurationSeconds = 5.0;
    internal const double MinimumIntervalSeconds = 0.05;
    internal const double MaximumIntervalSeconds = 5.0;
    internal const double DefaultIntervalSeconds = 0.5;

    private SampleLogicArguments(string? gatewayId, JArray targets, double durationSeconds, double intervalSeconds)
    {
        GatewayId = gatewayId;
        Targets = targets;
        DurationSeconds = durationSeconds;
        IntervalSeconds = intervalSeconds;
    }

    /// <summary>Null: the whole world, as read_logic_many reads it without a gateway.</summary>
    internal string? GatewayId { get; }

    /// <summary>The targets as sent, each read_logic_many's reads entry.</summary>
    internal JArray Targets { get; }

    internal double DurationSeconds { get; }

    internal double IntervalSeconds { get; }

    /// <summary>The samples the run takes when every one lands on time: the first at 0 s, then one per interval, the last at the duration.</summary>
    internal int PlannedSamples => Planned(DurationSeconds, IntervalSeconds);

    /// <summary>read_logic_many's arguments for one sample.</summary>
    internal JObject ReadArguments()
    {
        JObject arguments = new JObject { ["reads"] = Targets.DeepClone() };
        if (GatewayId != null)
        {
            arguments["gateway_id"] = GatewayId;
        }

        return arguments;
    }

    /// <summary>The arguments, or the invalid_argument message the sidecar loop gave for them.</summary>
    internal static SampleLogicParse Of(JObject? parameters)
    {
        JObject given = parameters ?? new JObject();
        string? gatewayId = null;
        JToken? gateway = given["gateway_id"];
        if (gateway != null && gateway.Type != JTokenType.Null)
        {
            if (gateway.Type != JTokenType.String)
            {
                return new SampleLogicParse.Invalid("Argument 'gateway_id' must be a string.");
            }

            string text = gateway.Value<string>() ?? string.Empty;
            gatewayId = string.IsNullOrWhiteSpace(text) ? null : text;
        }

        if (!(given["targets"] is JArray targets) || targets.Count == 0 || targets.Count > MaximumTargets)
        {
            return new SampleLogicParse.Invalid($"Argument 'targets' must be an array of 1 to {MaximumTargets} entries.");
        }

        if (!TryNumber(given, "duration_seconds", DefaultDurationSeconds, out double duration, out string problem) ||
            !TryNumber(given, "interval_seconds", DefaultIntervalSeconds, out double interval, out problem))
        {
            return new SampleLogicParse.Invalid(problem);
        }

        if (duration < MinimumDurationSeconds || duration > MaximumDurationSeconds)
        {
            return new SampleLogicParse.Invalid("Argument 'duration_seconds' must be from 0.1 to 30.");
        }

        if (interval < MinimumIntervalSeconds || interval > MaximumIntervalSeconds)
        {
            return new SampleLogicParse.Invalid("Argument 'interval_seconds' must be from 0.05 to 5.");
        }

        int planned = Planned(duration, interval);
        if (planned > MaximumSamples)
        {
            return new SampleLogicParse.Invalid(
                $"duration_seconds {Text(duration)} at interval_seconds {Text(interval)} asks for {planned} samples " +
                $"(the first at 0 s included); at most {MaximumSamples}.");
        }

        return new SampleLogicParse.Parsed(new SampleLogicArguments(gatewayId, (JArray)targets.DeepClone(), duration,
            interval));
    }

    internal static int Planned(double durationSeconds, double intervalSeconds) =>
        (int)Math.Ceiling(durationSeconds / intervalSeconds) + 1;

    // A key that is present must be a finite JSON number; null counts as present, as in the sidecar.
    private static bool TryNumber(JObject given, string name, double fallback, out double value, out string problem)
    {
        problem = string.Empty;
        value = fallback;
        if (!given.TryGetValue(name, StringComparison.Ordinal, out JToken? token))
        {
            return true;
        }

        if ((token.Type == JTokenType.Integer || token.Type == JTokenType.Float) &&
            !double.IsNaN(token.Value<double>()) && !double.IsInfinity(token.Value<double>()))
        {
            value = token.Value<double>();
            return true;
        }

        problem = $"Argument '{name}' must be a finite number.";
        return false;
    }

    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>sample_logic's arguments, read, or why they were refused.</summary>
internal abstract class SampleLogicParse
{
    private SampleLogicParse()
    {
    }

    internal sealed class Parsed : SampleLogicParse
    {
        internal Parsed(SampleLogicArguments arguments)
        {
            Arguments = arguments;
        }

        internal SampleLogicArguments Arguments { get; }
    }

    internal sealed class Invalid : SampleLogicParse
    {
        internal Invalid(string message)
        {
            Message = message;
        }

        internal string Message { get; }
    }
}
