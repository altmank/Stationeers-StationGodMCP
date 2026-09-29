#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Which of a prefab's own axes reads as its top, whether it may also lie on its side, and how sure that is.
/// </summary>
internal sealed class VisualUp
{
    private VisualUp(string match, GridStep localUp, bool lyingAllowed, string source, bool verified)
    {
        Match = match;
        LocalUp = localUp;
        LyingAllowed = lyingAllowed;
        Source = source;
        Verified = verified;
    }

    /// <summary>The prefab name prefix the entry is for; empty for the default.</summary>
    internal string Match { get; }

    internal GridStep LocalUp { get; }

    /// <summary>Lying on its side is a normal way to build it (in-line tanks along a pipe): not_upright is only info.</summary>
    internal bool LyingAllowed { get; }

    /// <summary>Where the entry comes from.</summary>
    internal string Source { get; }

    /// <summary>Seen in a live game; false for a guess waiting to be checked.</summary>
    internal bool Verified { get; }

    private static readonly VisualUp Default = new VisualUp(string.Empty, GridStep.All[2], false,
        "default: a prefab's local +y is its top", true);

    // Longest prefix first wins. Add an entry only from a live observation, or mark it unverified.
    private static readonly List<VisualUp> Table = new List<VisualUp>
    {
        new VisualUp("StructureInsulatedInLineTank", GridStep.All[2], true,
            "LIVE 2026-09-28: an in-line tank's long axis is its local +y (a 1x3 standing with up +y fills three " +
            "cells along y); laid along a pipe it lies on its side", true),
        new VisualUp("StructureInLineTank", GridStep.All[2], true,
            "LIVE 2026-09-28: as StructureInsulatedInLineTank", true),
        new VisualUp("StructureTankSmallInLine", GridStep.All[2], true,
            "GUESS: the StructureInLineTank class like the in-line tanks; not seen", false),
        new VisualUp("StructureTankBigInLine", GridStep.All[2], true,
            "GUESS: the StructureInLineTank class like the in-line tanks; not seen", false),
        new VisualUp("StructureLiquidTankSmallInLine", GridStep.All[2], true,
            "GUESS: the StructureInLineTank class like the in-line tanks; not seen", false),
        new VisualUp("StructureLiquidTankBigInLine", GridStep.All[2], true,
            "GUESS: the StructureInLineTank class like the in-line tanks; not seen", false)
    };

    internal static VisualUp Of(string? prefabName)
    {
        VisualUp? best = null;
        foreach (VisualUp entry in Table)
        {
            if (prefabName != null && prefabName.StartsWith(entry.Match, StringComparison.Ordinal) &&
                (best == null || entry.Match.Length > best.Match.Length))
            {
                best = entry;
            }
        }

        return best ?? Default;
    }
}
