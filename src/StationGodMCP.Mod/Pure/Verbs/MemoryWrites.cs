#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// write memory (catalogue write.json, memory): values written from start. The reply is {gateway_id, reference_id,
/// start_address, requested_values, previous_values, current_values, count, stack_size}; the report has the same keys
/// but current_values, and one target, [id, name, the memory now -> the values asked], [undo].
/// </summary>
internal static class MemoryWrites
{
    private const string Undo = "[undo]";

    /// <summary>The first address as an int, or null with the problem added.</summary>
    internal static int? Start(long? start, List<OptionProblem> problems)
    {
        if (start is { } given && given <= int.MaxValue)
        {
            return (int)given;
        }

        problems.Add(new OptionProblem("start", $"is an address from 0 to {int.MaxValue}"));
        return null;
    }

    /// <summary>The values as numbers, or null with each one that is no finite number a problem.</summary>
    internal static List<double>? Values(JToken? given, List<OptionProblem> problems)
    {
        if (given is not JArray list)
        {
            problems.Add(new OptionProblem("values", "is a list of numbers"));
            return null;
        }

        List<double> values = new List<double>(list.Count);
        int problemsBefore = problems.Count;
        for (int index = 0; index < list.Count; index++)
        {
            if (OptionReader.NumberOf(list[index]) is { } value && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                values.Add(value);
            }
            else
            {
                problems.Add(new OptionProblem($"values[{index}]", "must be a finite number"));
            }
        }

        return problems.Count == problemsBefore ? values : null;
    }

    internal static ReplyObject Written(MemoryPlace place, IReadOnlyList<double> requested, IReadOnlyList<double>? previous,
        IReadOnlyList<double>? current) =>
        ReplyFields.Of(("gateway_id", place.GatewayId), ("reference_id", place.ReferenceId.ToString()),
            ("start_address", place.StartAddress), ("requested_values", requested), ("previous_values", previous),
            ("current_values", current), ("count", requested.Count), ("stack_size", place.StackSize));

    internal static ReportReply Reported(MemoryPlace place, string? name, IReadOnlyList<double> requested,
        IReadOnlyList<double>? previous)
    {
        JsonSerializer serializer = ApiJson.Serializer;
        ReportTarget target = new ReportTarget(place.ReferenceId.ToString(), null, name,
            ReplyFields.Value(previous, serializer), ReplyFields.Value(requested, serializer), Undo);
        return Report.Of(new[] { target }, Body(place, requested, previous), new List<ReportProblem>());
    }

    private static ReplyObject Body(MemoryPlace place, IReadOnlyList<double> requested, IReadOnlyList<double>? previous) =>
        ReplyFields.Of(("gateway_id", place.GatewayId), ("reference_id", place.ReferenceId.ToString()),
            ("start_address", place.StartAddress), ("requested_values", requested), ("previous_values", previous),
            ("count", requested.Count), ("stack_size", place.StackSize));
}
