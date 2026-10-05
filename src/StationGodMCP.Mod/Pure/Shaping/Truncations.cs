#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Shaping;

/// <summary>
/// One list a reply holds back entries of: its name (a top-level key, or a path such as totals[].top_holders for a
/// list inside each entry), how many entries the reply carries, how many there are, and the argument that gets more.
/// AtLeast: total is a lower bound, counting it exactly would cost what the cut saves.
/// </summary>
internal sealed class Truncation
{
    internal Truncation(string list, int returned, int total, string more, bool atLeast = false)
    {
        List = list;
        Returned = returned;
        Total = total;
        More = more;
        AtLeast = atLeast;
    }

    internal string List { get; }

    internal int Returned { get; }

    internal int Total { get; }

    internal string More { get; }

    internal bool AtLeast { get; }

    /// <summary>This handler's note with the writer's cut of the same list: the fewer returned, both ways to get more.</summary>
    internal Truncation With(Truncation cut) =>
        new Truncation(List, Math.Min(Returned, cut.Returned), Math.Max(Total, cut.Total),
            More + "; " + cut.More, AtLeast);
}

/// <summary>
/// The truncations a handler notes while it builds a reply, collected per call on the thread that runs it, and the
/// wording of the ways to get more. ApiHost begins and takes them around each handler; the shaping writer adds its own
/// cuts (limit, x-default-limits) and writes them all as the reply's truncated list.
/// </summary>
internal static class Truncations
{
    [ThreadStatic]
    private static List<Truncation>? _notes;

    internal static void Begin() => _notes = new List<Truncation>();

    /// <summary>The notes since Begin, and no more collecting until the next Begin.</summary>
    internal static List<Truncation> Take()
    {
        List<Truncation> notes = _notes ?? new List<Truncation>();
        _notes = null;
        return notes;
    }

    /// <summary>
    /// A paged list: returned entries of total from offset; more by limit (up to maximum) or the next offset, then
    /// advice on paging it (empty for none).
    /// </summary>
    internal static void Page(string list, int offset, int returned, int total, int maximum, string advice = "")
    {
        if (offset + returned >= total && offset == 0)
        {
            return;
        }

        string next = offset + returned < total
            ? " or offset " + (offset + returned).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        string earlier = offset > 0 ? "; offset " + offset.ToString(CultureInfo.InvariantCulture) + " skipped the first entries" : string.Empty;
        Note(list, returned, total,
            "pass limit (max " + maximum.ToString(CultureInfo.InvariantCulture) + ")" + next + earlier + advice);
    }

    /// <summary>A list cut at a cap an argument sets: more by that argument, up to its maximum.</summary>
    internal static void Capped(string list, int returned, int total, string argument, int maximum)
    {
        if (returned < total)
        {
            Note(list, returned, total,
                "pass " + argument + " (max " + maximum.ToString(CultureInfo.InvariantCulture) + ")");
        }
    }

    /// <summary>A list cut short; more says how to get the rest. atLeast: total is a lower bound.</summary>
    internal static void Note(string list, int returned, int total, string more, bool atLeast = false)
    {
        if (returned < total || atLeast)
        {
            _notes?.Add(new Truncation(list, returned, total, more, atLeast));
        }
    }

    /// <summary>How a list the writer cut is got whole: the shape's limit, the MCP sidecar's list_limits.</summary>
    internal static string WriterMore(string list, int total) =>
        "pass list_limits {\"" + list + "\": " + total.ToString(CultureInfo.InvariantCulture) +
        "} (shape.limit on the pipe; max " + ShapeRequest.MaximumLimit.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>The handler's notes and the writer's cuts, one entry per list, in the order noted then cut.</summary>
    internal static List<Truncation> Merge(IReadOnlyList<Truncation> notes, IReadOnlyList<KeyValuePair<string, int>> cuts,
        ShapeRequest shape)
    {
        List<Truncation> merged = new List<Truncation>(notes.Count + cuts.Count);
        foreach (Truncation note in notes)
        {
            merged.Add(note);
        }

        foreach (KeyValuePair<string, int> cut in cuts)
        {
            Truncation writer = new Truncation(cut.Key, shape.LimitOf(cut.Key), cut.Value, WriterMore(cut.Key, cut.Value));
            int index = merged.FindIndex(note => note.List == cut.Key);
            if (index >= 0)
            {
                merged[index] = merged[index].With(writer);
            }
            else
            {
                merged.Add(writer);
            }
        }

        return merged;
    }
}
