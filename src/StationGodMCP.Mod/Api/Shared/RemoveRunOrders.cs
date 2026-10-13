#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>How a remove cable, pipe or chute call names its pieces: one of id, waypoints and cells.</summary>
internal static class RemoveRunOrders
{
    internal const string MissingTarget = "missing_target";

    internal const string ById = "id";
    internal const string ByWaypoints = "waypoints";
    internal const string ByCells = "cells";

    /// <summary>
    /// The one way the call names its pieces; null with a problem added when it names several, or with missing set
    /// when it names none.
    /// </summary>
    internal static string? Form(bool ids, JToken? waypoints, JToken? cells, string label, string noun,
        List<OptionProblem> problems, out VerbError? missing)
    {
        missing = null;
        List<string> given = new List<string>();
        if (ids)
        {
            given.Add(ById);
        }

        if (waypoints != null)
        {
            given.Add(ByWaypoints);
        }

        if (cells != null)
        {
            given.Add(ByCells);
        }

        if (given.Count > 1)
        {
            problems.Add(new OptionProblem(given[1],
                $"goes without {given[0]}: name the {noun} pieces by id, waypoints or cells"));
            return null;
        }

        if (given.Count == 0)
        {
            missing = new VerbError(MissingTarget, $"{label}: name the {noun} pieces by id, waypoints or cells.",
                new JObject { ["options"] = new JArray(ById, ByWaypoints, ByCells) });
            return null;
        }

        return given[0];
    }
}
