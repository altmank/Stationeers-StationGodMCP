#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure.Sampling;

/// <summary>
/// One value read sample watches: a thing's id (or "looking_at") and a logic type (a name or a number), with the option
/// path that named it (id, id[i] or reads[i]).
/// </summary>
internal sealed record SampleTarget(string Id, JToken Logic, string Path = "id");

/// <summary>
/// read sample's request (catalogue read.json, what sample): the targets (every id with every logic type, or the reads
/// pairs, 1 to 32), the gateway (null: the world), how long to watch (seconds) and the least time between samples
/// (interval), with at most MaximumSamples samples, the first at 0 s included.
/// </summary>
internal sealed class SampleRequest
{
    internal const int MaximumTargets = 32;
    internal const int MaximumSamples = 120;

    internal SampleRequest(string? gateway, IReadOnlyList<SampleTarget> targets, double seconds, double interval)
    {
        Gateway = gateway;
        Targets = targets;
        Seconds = seconds;
        Interval = interval;
    }

    /// <summary>The gateway the reads go through; null for the whole world.</summary>
    internal string? Gateway { get; }

    internal IReadOnlyList<SampleTarget> Targets { get; }

    internal double Seconds { get; }

    internal double Interval { get; }

    /// <summary>The samples a run takes when every one lands on time: the first at 0 s, then one per interval, the last at the end.</summary>
    internal int PlannedSamples => Planned(Seconds, Interval);

    internal static int Planned(double seconds, double interval) => (int)Math.Ceiling(seconds / interval) + 1;

    /// <summary>The request the options give; null with what is wrong with them in problems, in verb names.</summary>
    internal static SampleRequest? Of(ReadSampleOptions options, List<OptionProblem> problems)
    {
        List<SampleTarget> targets = new List<SampleTarget>();
        IReadOnlyList<string> ids = options.Selector.Ids?.Ids ?? Array.Empty<string>();
        if (ids.Count > 0 && options.Reads != null)
        {
            problems.Add(new OptionProblem("reads", "goes without id: give ids with logic, or reads"));
        }
        else if (ids.Count > 0)
        {
            List<JToken> logic = LogicOf(options.Logic);
            if (logic.Count == 0)
            {
                problems.Add(new OptionProblem("logic", "is needed with id: the logic types to watch"));
            }

            bool listed = options.Selector.Ids!.GivenAsList;
            for (int index = 0; index < ids.Count; index++)
            {
                foreach (JToken type in logic)
                {
                    targets.Add(new SampleTarget(ids[index], type, listed ? $"id[{index}]" : "id"));
                }
            }
        }
        else if (options.Reads is JArray reads)
        {
            for (int index = 0; index < reads.Count; index++)
            {
                if (reads[index] is JObject { } read && read["id"] is JValue { Type: JTokenType.String } id &&
                    ((string)id!).Trim().Length > 0 && (read["logic"] ?? options.Logic) is { } type &&
                    type.Type is JTokenType.String or JTokenType.Integer)
                {
                    targets.Add(new SampleTarget(((string)id!).Trim(), type.DeepClone(), $"reads[{index}]"));
                }
                else
                {
                    problems.Add(new OptionProblem($"reads[{index}]", "must be {id, logic}"));
                }
            }
        }
        else
        {
            problems.Add(new OptionProblem("id", "is needed, with logic, or reads: the values to watch"));
        }

        if (targets.Count > MaximumTargets)
        {
            problems.Add(new OptionProblem("id",
                $"names {targets.Count} values to watch (every id with every logic type); at most {MaximumTargets}"));
        }

        double seconds = options.Seconds ?? 0;
        double interval = options.Interval ?? 0;
        int planned = interval > 0 ? Planned(seconds, interval) : int.MaxValue;
        if (planned > MaximumSamples)
        {
            problems.Add(new OptionProblem("interval",
                $"seconds {Text(seconds)} at interval {Text(interval)} asks for {planned} samples (the first at 0 s " +
                $"included); at most {MaximumSamples}"));
        }

        return problems.Count > 0 ? null : new SampleRequest(GatewayOf(options.Gateway), targets, seconds, interval);
    }

    // logic: a name or number, or a list of them.
    private static List<JToken> LogicOf(JToken? logic)
    {
        List<JToken> types = new List<JToken>();
        foreach (JToken type in logic is JArray many ? (IEnumerable<JToken>)many : logic != null ? new[] { logic } : Array.Empty<JToken>())
        {
            if (type.Type is JTokenType.String or JTokenType.Integer)
            {
                types.Add(type.DeepClone());
            }
        }

        return types;
    }

    // world, or left out, is the whole world.
    private static string? GatewayOf(string? gateway) =>
        string.IsNullOrWhiteSpace(gateway) || string.Equals(gateway!.Trim(), "world", StringComparison.OrdinalIgnoreCase)
            ? null
            : gateway.Trim();

    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
