#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The buildable prefabs whose name holds the name of one no kit builds (StructurePipeOneWayValveLever for
/// StructurePipeOneWayValve), shortest first then by name, so a refusal can name what a player can build instead.
/// </summary>
internal static class BuildableVariants
{
    internal const int Maximum = 5;

    internal static List<string> Of(string unbuildable, IEnumerable<string> buildable)
    {
        List<string> found = new List<string>();
        foreach (string name in buildable)
        {
            if (!string.Equals(name, unbuildable, StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf(unbuildable, StringComparison.OrdinalIgnoreCase) >= 0 && !found.Contains(name))
            {
                found.Add(name);
            }
        }

        found.Sort(static (a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
        return found.Count > Maximum ? found.GetRange(0, Maximum) : found;
    }

    /// <summary>The refusal's tail: " Buildable: a, b." or nothing when none.</summary>
    internal static string Hint(List<string> variants) =>
        variants.Count == 0 ? string.Empty : $" Buildable variants: {string.Join(", ", variants)}.";
}
