#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
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

    /// <summary>
    /// The tidy operations a swap cable or pipe call asks for, in run order; null for the upgrade (into, or neither
    /// into nor tidy: tidy false is tidy left out). true is [simplify_junctions]. Every problem is added: into with
    /// tidy; an empty list or two operations that undo each other; spare without remove_loops or remove_redundant;
    /// only, older_than or root without remove_redundant.
    /// </summary>
    internal static List<string>? Tidy(JToken? tidy, string? into, TidyLimits limits, List<OptionProblem> problems)
    {
        List<string>? names = tidy switch
        {
            JValue { Type: JTokenType.Boolean } flag => (bool)flag ? new List<string>(CleanOperationSet.Default) : null,
            JArray list => Words(list),
            _ => null
        };

        if (names != null && into != null)
        {
            problems.Add(new OptionProblem("tidy", "goes without into: give into (upgrade) or tidy, not both"));
            return null;
        }

        List<string>? ordered = null;
        if (names != null)
        {
            ordered = CleanOperationSet.Parse(names, out string? error);
            if (ordered == null)
            {
                problems.Add(new OptionProblem("tidy", error!));
            }
        }

        bool loops = ordered?.Contains(CleanOperationSet.RemoveLoops) == true;
        bool redundant = ordered?.Contains(CleanOperationSet.RemoveRedundant) == true;
        if (limits.Spare && !loops && !redundant)
        {
            problems.Add(new OptionProblem("spare", "spares pieces from remove_loops and remove_redundant; ask " +
                "tidy for one of them"));
        }

        if (!redundant)
        {
            Needs(problems, "only", limits.Only);
            Needs(problems, "older_than", limits.OlderThan);
            Needs(problems, "root", limits.Root);
        }

        return ordered;
    }

    private static void Needs(List<OptionProblem> problems, string option, bool given)
    {
        if (given)
        {
            problems.Add(new OptionProblem(option, "goes with remove_redundant; ask tidy for it"));
        }
    }

    private static List<string> Words(JArray list)
    {
        List<string> words = new List<string>(list.Count);
        foreach (JToken entry in list)
        {
            words.Add(entry.Type == JTokenType.String ? (string)entry! : string.Empty);
        }

        return words;
    }
}

/// <summary>Which of the options that narrow a tidy a swap cable or pipe call gives.</summary>
internal sealed class TidyLimits
{
    internal TidyLimits(bool spare, bool only, bool olderThan, bool root)
    {
        Spare = spare;
        Only = only;
        OlderThan = olderThan;
        Root = root;
    }

    public bool Spare { get; }

    public bool Only { get; }

    public bool OlderThan { get; }

    public bool Root { get; }
}
