#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// Why rocket_mining_options with collectable_only lists no site: no site to judge, nothing on board that mines, or
/// every site judged and none giving what the loadout collects (with the site kinds and the machines' own reasons).
/// </summary>
internal static class MiningEmptyReason
{
    private const int NamedProblems = 3;

    internal static string Of(int sites, int machines, IReadOnlyList<string> siteKinds, int depleted,
        IReadOnlyList<string> machineProblems, string? to)
    {
        if (sites == 0)
        {
            return to != null
                ? $"No site listed: {to} has no deposit at that node or its discovered sites."
                : "No site listed: no charted node has a deposit. Chart nodes (a scanner on a flight) first.";
        }

        if (machines == 0)
        {
            return "No site listed: the rocket carries no miner or gas collector, so nothing is collectable anywhere.";
        }

        SortedDictionary<string, int> kinds = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
        foreach (string kind in siteKinds)
        {
            kinds[kind] = (kinds.TryGetValue(kind, out int count) ? count : 0) + 1;
        }

        List<string> parts = new List<string>(kinds.Count);
        foreach (KeyValuePair<string, int> kind in kinds)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1}", kind.Value, kind.Key));
        }

        string text = $"No site listed: none of the {sites} sites ({string.Join(", ", parts)}" +
                      (depleted > 0 ? $"; {depleted} depleted" : string.Empty) +
                      ") gives anything the loadout collects.";
        List<string> named = new List<string>(NamedProblems);
        foreach (string problem in machineProblems)
        {
            if (named.Count == NamedProblems)
            {
                break;
            }

            if (!named.Contains(problem))
            {
                named.Add(problem);
            }
        }

        return named.Count == 0 ? text : text + " " + string.Join(" ", named);
    }
}
