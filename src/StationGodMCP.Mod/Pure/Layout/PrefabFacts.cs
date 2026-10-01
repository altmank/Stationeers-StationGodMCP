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

    /// <summary>
    /// Seen in a live game, or forced by the game's code (a prefab the cursor only turns about y always stands with
    /// +y up); false for an assumption or a guess waiting to be checked.
    /// </summary>
    internal bool Verified { get; }

    private static readonly VisualUp Upright = new VisualUp(string.Empty, GridStep.All[2], false,
        "CODE: the placement cursor turns it about y only, so it always stands with local +y up", true);

    // Until 1.7.0 this default claimed verified for every prefab, though no one had looked at most of them: modded
    // Compact Filtration was reported "+y, verified" while it reads upside down that way.
    private static readonly VisualUp Assumed = new VisualUp(string.Empty, GridStep.All[2], false,
        "ASSUMED: local +y as its top; the cursor can build it any way up and no one has checked how it reads", false);

    // Longest prefix first wins. Add an entry only from a live observation, or mark it unverified.
    private static readonly List<VisualUp> Table = new List<VisualUp>
    {
        new VisualUp("StructureCompactFiltration", GridStep.All[3], true,
            "LIVE 2026-09-30 (mod Compact Filtration, Workshop 3603064996, plain and Mirror): LU judged two units " +
            "built up +y under a ceiling upside down; the units LU built by hand stand up -y, +z, -z or +x, " +
            "never +y. " +
            "-y as its top is read from that, not seen standing on a floor; it is built on its side as often", false),
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

    /// <summary>
    /// The entry for a prefab; without one, local +y: a fact when the cursor cannot tip it (tips false), an
    /// assumption otherwise.
    /// </summary>
    internal static VisualUp Of(string? prefabName, bool tips)
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

        return best ?? (tips ? Assumed : Upright);
    }
}
