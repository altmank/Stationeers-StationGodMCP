#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Verbs;

/// <summary>allow (PLAN 2.0, world rules on writes): the guards a call lifts, each named by its word.</summary>
internal static class Allow
{
    /// <summary>True when allow names the guard.</summary>
    internal static bool Lifts(IReadOnlyList<string>? allow, string guard)
    {
        if (allow == null)
        {
            return false;
        }

        for (int index = 0; index < allow.Count; index++)
        {
            if (allow[index] == guard)
            {
                return true;
            }
        }

        return false;
    }
}
