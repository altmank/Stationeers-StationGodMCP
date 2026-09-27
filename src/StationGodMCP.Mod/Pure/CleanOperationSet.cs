#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The clean tools' operations by name, the order they run in, and which may not run together. The order is fixed:
/// dead ends go first so every later operation sees the network without them; then loops are cut (never by default:
/// a loop may be deliberate redundancy); then long straights are split or runs merged; junctions are simplified last,
/// against the network as the earlier operations leave it.
/// </summary>
internal static class CleanOperationSet
{
    internal const string RemoveDeadEnds = "remove_dead_ends";
    internal const string RemoveLoops = "remove_loops";
    internal const string SplitLongStraights = "split_long_straights";
    internal const string MergeStraights = "merge_straights";
    internal const string SimplifyJunctions = "simplify_junctions";

    /// <summary>Every operation, in the order they run.</summary>
    internal static readonly string[] Order =
        { RemoveDeadEnds, RemoveLoops, SplitLongStraights, MergeStraights, SimplifyJunctions };

    /// <summary>What runs when the request names none: junction simplification only.</summary>
    internal static readonly string[] Default = { SimplifyJunctions };

    // Pairs that undo each other.
    private static readonly string[][] Conflicts = { new[] { SplitLongStraights, MergeStraights } };

    /// <summary>
    /// The named operations in run order; null with the reason when a name is unknown, none is named, or two
    /// conflict. Names are matched ignoring case and surrounding space; a repeated name counts once.
    /// </summary>
    internal static List<string>? Parse(IReadOnlyList<string> names, out string? error)
    {
        HashSet<string> asked = new HashSet<string>();
        foreach (string raw in names)
        {
            string name = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (System.Array.IndexOf(Order, name) < 0)
            {
                error = $"Unknown operation '{raw}'; use {string.Join(", ", Order)}.";
                return null;
            }

            asked.Add(name);
        }

        if (asked.Count == 0)
        {
            error = $"operations needs at least one of {string.Join(", ", Order)}.";
            return null;
        }

        foreach (string[] pair in Conflicts)
        {
            if (asked.Contains(pair[0]) && asked.Contains(pair[1]))
            {
                error = $"{pair[0]} and {pair[1]} undo each other; ask for one of them.";
                return null;
            }
        }

        List<string> ordered = new List<string>(asked.Count);
        foreach (string name in Order)
        {
            if (asked.Contains(name))
            {
                ordered.Add(name);
            }
        }

        error = null;
        return ordered;
    }
}
