#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// How a remove or plan removal call names its cable, pipe or chute pieces: one of id, waypoints and cells, and for
/// plan removal network (every piece of that network).
/// </summary>
internal static class RemoveRunOrders
{
    internal const string MissingTarget = "missing_target";

    internal const string ById = "id";
    internal const string ByWaypoints = "waypoints";
    internal const string ByCells = "cells";
    internal const string ByNetwork = "network";

    /// <summary>
    /// The one way the call names its pieces; null with a problem added when it names several, or with missing set
    /// when it names none.
    /// </summary>
    internal static string? Form(bool ids, JToken? waypoints, JToken? cells, string label, string noun,
        List<OptionProblem> problems, out VerbError? missing) =>
        Form(ids, waypoints, cells, null, label, noun, problems, out missing);

    /// <summary>
    /// As Form, for a what that also takes network (network: whether it was given); null network for a what that
    /// does not take it.
    /// </summary>
    internal static string? Form(bool ids, JToken? waypoints, JToken? cells, bool? network, string label, string noun,
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

        if (network == true)
        {
            given.Add(ByNetwork);
        }

        string ways = network.HasValue ? "id, waypoints, cells or network" : "id, waypoints or cells";
        if (given.Count > 1)
        {
            problems.Add(new OptionProblem(given[1], $"goes without {given[0]}: name the {noun} pieces by {ways}"));
            return null;
        }

        if (given.Count == 0)
        {
            JArray options = new JArray(ById, ByWaypoints, ByCells);
            if (network.HasValue)
            {
                options.Add(ByNetwork);
            }

            missing = new VerbError(MissingTarget, $"{label}: name the {noun} pieces by {ways}.",
                new JObject { ["options"] = options });
            return null;
        }

        return given[0];
    }
}
