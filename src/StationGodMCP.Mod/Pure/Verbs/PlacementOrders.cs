#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The placement readers the build orders share (build structure, show preview): one placement through the shared place
/// reader (PlacementReader.Placement, so the refusals are the ones the old place surface made), a placement's fields with
/// above_floor as this catalogue's name for the reader's above_floor_m, the one prefab a top-level prefab option may
/// name, and a position. Every refusal the shared readers throw becomes an OptionProblem at the option's path, so a
/// call answers with every problem at once.
/// </summary>
internal static class PlacementOrders
{
    /// <summary>The option a top-level placement's name comes from: the fallback for a refusal that names no option.</summary>
    internal const string PrefabOption = "prefab";

    /// <summary>
    /// One placement read by the shared reader (PlacementReader.Placement): a placements list reads it with the index it has
    /// there, the one placement at the top level with index 0 and no prefix.
    /// </summary>
    internal static void Add(List<PlacementRequest> placements, JObject item, int index, string prefix, WhatSpec what,
        List<OptionProblem> problems)
    {
        try
        {
            placements.Add(PlacementReader.Placement(new Args(item), index, prefix));
        }
        catch (ApiException refused)
        {
            problems.Add(Problem(prefix.Length == 0 ? PrefabOption : prefix.TrimEnd('.'), prefix, what,
                refused.Message));
        }
    }

    /// <summary>
    /// A placement's fields as the shared reader reads them: an item that gives above_floor is read from a copy with
    /// the reader's above_floor_m set; an item that does not is read as given, so nothing is copied.
    /// </summary>
    internal static JObject Shared(JObject item, string path, List<OptionProblem> problems)
    {
        JToken? above = item["above_floor"];
        if (above == null)
        {
            return item;
        }

        JObject fields = new JObject();
        foreach (JProperty key in item.Properties())
        {
            fields[key.Name] = key.Value.DeepClone();
        }

        AboveFloor(above, path + ".above_floor", fields, problems);
        return fields;
    }

    /// <summary>above_floor: a placement's bottom this many metres above the floor below at, 0 to 20.</summary>
    internal static void AboveFloor(JToken? given, string path, JObject item, List<OptionProblem> problems)
    {
        if (given == null)
        {
            return;
        }

        double metres = given.Type is JTokenType.Integer or JTokenType.Float ? given.Value<double>() : double.NaN;
        if (double.IsNaN(metres) || double.IsInfinity(metres) || metres < 0.0 || metres > 20.0)
        {
            problems.Add(new OptionProblem(path, "is 0 to 20, in metres"));
            return;
        }

        item["above_floor_m"] = metres;
    }

    /// <summary>
    /// The one prefab a top-level placement names, from the what's prefab binding: its name or its hash. A * pattern is
    /// a filter, not a placement, so false with the problem (the placement is not read then: it would only repeat that
    /// its prefab is missing).
    /// </summary>
    internal static bool Prefab(PrefabFilter? filter, string option, JObject item, List<OptionProblem> problems)
    {
        if (filter == null)
        {
            return true;
        }

        if (filter.Patterns.Count == 1 && filter.Patterns[0] is PrefabPattern.Hash hash)
        {
            item[option] = hash.Value;
            return true;
        }

        if (filter.Patterns.Count == 1 && filter.Patterns[0] is PrefabPattern.Whole whole)
        {
            item[option] = whole.Name;
            return true;
        }

        problems.Add(new OptionProblem(option, "is one prefab's name or its hash, not a * pattern"));
        return false;
    }

    /// <summary>A position in metres through the shared reader (PlacementReader.PositionOf); false with its problem added.</summary>
    internal static bool Position(JToken token, string path, List<OptionProblem> problems, out Metres point)
    {
        try
        {
            point = PlacementReader.PositionOf(token, path);
            return true;
        }
        catch (ApiException refused)
        {
            point = default;
            problems.Add(Problem(path, path, null, refused.Message));
            return false;
        }
    }

    // A shared reader's refusal as a problem: a message that names the option it is about (placements[2].at is
    // required, or at is required) gives the path and the rest is the text; a message that names none (Give at most
    // one of rotation, facing and face) is about the placement, known by its form.
    private static OptionProblem Problem(string form, string path, WhatSpec? what, string message)
    {
        string text = message.Length > 0 && message[message.Length - 1] == '.'
            ? message.Substring(0, message.Length - 1)
            : message;
        int space = text.IndexOf(' ');
        string named = space < 0 ? text : text.Substring(0, space);
        return space > 0 && Named(named, path, what)
            ? new OptionProblem(named, text.Substring(space + 1))
            : new OptionProblem(form, text);
    }

    // Whether the message's first word names the option it is about: a placement item's path (placements[2].at), or
    // one of the what's own options (at) for the placement given at the top level.
    private static bool Named(string word, string path, WhatSpec? what) =>
        path.Length > 0
            ? word.StartsWith(path, StringComparison.Ordinal)
            : what != null && what.Option(word) != null;
}
