#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// How a material's need is paid by several sources in the order given (from_id as a list): each source gives what it
/// holds until the need is met, as the job takes the stacks in that order.
/// </summary>
internal static class SourceSplit
{
    /// <summary>What each source pays, in order (0 for one not needed); available is each source's own count.</summary>
    internal static List<int> Of(IReadOnlyList<int> available, int needed)
    {
        List<int> paid = new List<int>(available.Count);
        int left = Math.Max(0, needed);
        foreach (int held in available)
        {
            int part = Math.Min(left, Math.Max(0, held));
            paid.Add(part);
            left -= part;
        }

        return paid;
    }

    /// <summary>Names joined as a list reads: "A", "A and B", "A, B and C".</summary>
    internal static string Joined(IReadOnlyList<string> names)
    {
        if (names.Count <= 1)
        {
            return names.Count == 1 ? names[0] : string.Empty;
        }

        List<string> head = new List<string>(names);
        string last = head[head.Count - 1];
        head.RemoveAt(head.Count - 1);
        return string.Join(", ", head) + " and " + last;
    }
}
