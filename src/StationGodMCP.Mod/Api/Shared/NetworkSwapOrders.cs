#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>How a swap cable, pipe or chute call names its pieces: one of id, network and (chute) box.</summary>
internal static class NetworkSwapOrders
{
    internal const string MissingTarget = "missing_target";

    internal const string ById = "id";
    internal const string ByNetwork = "network";
    internal const string ByBox = "box";

    /// <summary>
    /// The one way the call names its pieces: id, network or box; null with a problem added when it names several,
    /// or with missing set when it names none.
    /// </summary>
    internal static string? Form(Selector selector, bool takesBox, string label, string noun,
        List<OptionProblem> problems, out VerbError? missing)
    {
        missing = null;
        List<string> given = new List<string>();
        if (selector.Ids != null)
        {
            given.Add(ById);
        }

        if (selector.Network != null)
        {
            given.Add(ByNetwork);
        }

        if (takesBox && selector.Box != null)
        {
            given.Add(ByBox);
        }

        string ways = takesBox ? "id, network or box" : "id or network";
        if (given.Count > 1)
        {
            problems.Add(new OptionProblem(given[1], $"goes without {given[0]}: name the {noun} pieces by {ways}"));
            return null;
        }

        if (given.Count == 0)
        {
            JArray options = takesBox ? new JArray(ById, ByNetwork, ByBox) : new JArray(ById, ByNetwork);
            missing = new VerbError(MissingTarget, $"{label}: name the {noun} pieces by {ways}.",
                new JObject { ["options"] = options });
            return null;
        }

        return given[0];
    }
}
