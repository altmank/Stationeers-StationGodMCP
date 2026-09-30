#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// What the player-placement rule says about a prefab at a spot: a player's cursor would build it there, would refuse
/// it (which rule, why), or the rule could not be asked (why: no placement cursor for the prefab, no kit builds it).
/// </summary>
internal abstract class PlacementVerdict
{
    private PlacementVerdict()
    {
    }

    internal static PlacementVerdict Placeable { get; } = new Allowed();

    /// <summary>Refused; the rule read from the reason (PlayerPlacementRule.RuleOf) unless given.</summary>
    internal static PlacementVerdict Refuse(string reason, string? rule = null) =>
        new Refused(rule ?? PlayerPlacementRule.RuleOf(reason), reason);

    internal static PlacementVerdict Unchecked(string reason, string? rule = null) => new NotChecked(rule, reason);

    internal sealed class Allowed : PlacementVerdict
    {
    }

    internal sealed class Refused : PlacementVerdict
    {
        internal Refused(string? rule, string reason)
        {
            Rule = rule;
            Reason = reason;
        }

        /// <summary>One of PlacementRules; null when the game's text is not one the rule table knows.</summary>
        internal string? Rule { get; }

        internal string Reason { get; }
    }

    internal sealed class NotChecked : PlacementVerdict
    {
        internal NotChecked(string? rule, string reason)
        {
            Rule = rule;
            Reason = reason;
        }

        /// <summary>no_kit when no kit builds the prefab (so no player can place it anywhere); null otherwise.</summary>
        internal string? Rule { get; }

        internal string Reason { get; }
    }
}

/// <summary>The rule names check_replaceable and lint_layout's not_replaceable report a refusal under.</summary>
internal static class PlacementRules
{
    /// <summary>Nothing to stand on or mount to: a frame below, a wall or frame behind its back.</summary>
    internal const string Support = "support";

    /// <summary>Something behind it, but not one that allows mounting, or the wrong face of it.</summary>
    internal const string Mount = "mount";

    /// <summary>The piece it mounts on is missing or wrong: a straight pipe or cable, a valve, its machine.</summary>
    internal const string Host = "host";

    /// <summary>The place itself: terrain, outside any room, a rocket's cell.</summary>
    internal const string Location = "location";

    /// <summary>A port straight onto another device's port.</summary>
    internal const string Adjacent = "adjacent";

    /// <summary>Something else takes its cells, slot or face.</summary>
    internal const string Collision = "collision";

    /// <summary>The cursor never turns the prefab that way.</summary>
    internal const string Rotation = "rotation";

    /// <summary>No kit builds the prefab.</summary>
    internal const string NoKit = "no_kit";

    /// <summary>It does not stand where the cursor snaps it, or not at a quarter turn.</summary>
    internal const string OffGrid = "off_grid";
}

/// <summary>One of the game's checks on the cursor (CanConstruct, CanMountOnWall): its refusal and whose it is.</summary>
internal readonly struct GameCheck
{
    internal GameCheck(string? refusal, bool blamesReplaced)
    {
        Refusal = refusal;
        BlamesReplaced = refusal != null && blamesReplaced;
    }

    /// <summary>The game's reason; null when this check allows it.</summary>
    internal string? Refusal { get; }

    /// <summary>The refusal names only replaced things (their own cells, slot or face).</summary>
    internal bool BlamesReplaced { get; }
}

/// <summary>
/// The decisions of the player-placement rule that do not need the game. The game's cursor check sees the world as it
/// stands, so a placement that stands in for existing things (a swap's old piece, or a thing asked whether it could be
/// placed again where it stands) meets those things in its own cells. Every CanConstruct override and CanMountOnWall
/// ask their support first and collisions last, so a refusal that only names a replaced thing means that check's
/// support held; the neighbours are then checked again without the replaced things. The cursor asks CanMountOnWall of
/// a face-mounted piece apart from CanConstruct, so both are asked: a piece whose CanConstruct stops at itself still
/// has its mount checked.
/// </summary>
internal static class PlayerPlacementRule
{
    // The game's English refusal texts (Language/english.xml, CODE GameStrings / InterfaceStrings) by rule, lower
    // case, in the order they are tried; and this mod's own texts (PlacementCheck, PlayerPlacement).
    private static readonly (string Rule, string Text)[] Texts =
    {
        (PlacementRules.Support, "requires support"),
        (PlacementRules.Support, "requires a frame"),
        (PlacementRules.Support, "requires a support frame"),
        (PlacementRules.Support, "only allowed on"),
        (PlacementRules.Mount, "does not allow mounting"),
        (PlacementRules.Mount, "cannot mount to this face"),
        (PlacementRules.Host, "must be mounted to"),
        (PlacementRules.Host, "cannot place on broken"),
        (PlacementRules.Host, "cannot place on burst"),
        (PlacementRules.Host, "content type does not match"),
        (PlacementRules.Host, "after a pipe valve"),
        (PlacementRules.Host, "requires a hydroponics tray"),
        (PlacementRules.Host, "compatible structure"),
        (PlacementRules.Location, "requires terrain"),
        (PlacementRules.Location, "must be on terrain"),
        (PlacementRules.Location, "must be placed outside"),
        (PlacementRules.Support, "must be constructed on top of"),
        (PlacementRules.Location, "belongs to a rocket"),
        (PlacementRules.Location, "inside a rocket"),
        (PlacementRules.Location, "outside rocket"),
        (PlacementRules.Location, "outside of a rocket"),
        (PlacementRules.Location, "connects to a rocket"),
        (PlacementRules.Location, "rocket's cell here"),
        (PlacementRules.Location, "inside an engine fuselage"),
        (PlacementRules.Location, "inside a rocket tower"),
        (PlacementRules.Location, "builds and removes nothing of it now"),
        (PlacementRules.Adjacent, "adjacent to"),
        (PlacementRules.Adjacent, "placed next to each other"),
        (PlacementRules.Collision, "blocked by"),
        (PlacementRules.Collision, "is in the way"),
        (PlacementRules.Collision, "already takes that slot"),
        (PlacementRules.Collision, "cannot merge with"),
        (PlacementRules.Collision, "is mounted on the pipe there"),
        (PlacementRules.Collision, "for another pipe content"),
        (PlacementRules.Collision, "the way it faces")
    };

    /// <summary>The rule a refusal falls under, from its text; null when the text is not a known one.</summary>
    internal static string? RuleOf(string reason)
    {
        string lower = reason.ToLowerInvariant();
        foreach ((string rule, string text) in Texts)
        {
            if (lower.Contains(text))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// The verdict from the game's checks: the first refusal that does not blame replaced things stands; when every
    /// refusal blames them (or none refuses but one did), the neighbours decide; with no refusal it is placeable.
    /// </summary>
    internal static PlacementVerdict Judge(IReadOnlyList<GameCheck> checks, Func<string?> neighbours)
    {
        bool blamed = false;
        foreach (GameCheck check in checks)
        {
            if (check.Refusal != null && !check.BlamesReplaced)
            {
                return PlacementVerdict.Refuse(check.Refusal);
            }

            blamed |= check.BlamesReplaced;
        }

        if (!blamed)
        {
            return PlacementVerdict.Placeable;
        }

        string? other = neighbours();
        return other == null ? PlacementVerdict.Placeable : PlacementVerdict.Refuse(other);
    }

    /// <summary>
    /// Whether the refusal is one of the texts the game names a thing in the way with, for a replaced thing ("Placement
    /// is blocked by ..." for a small-grid slot, a cell or a face; a mount blocked; a pipe that cannot merge).
    /// </summary>
    internal static bool BlamesReplaced(string refusal, IEnumerable<string> textsNamingReplaced)
    {
        foreach (string text in textsNamingReplaced)
        {
            if (!string.IsNullOrEmpty(text) && string.Equals(refusal.Trim(), text.Trim(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// "Requires a Frame below" (SmallGrid.HasFrameBelow) also fails when the cell the piece stands in is blocked, and a
    /// replaced thing that fills its cell blocks it. When the frame below is there and the replaced thing fills a
    /// cell, the refusal is the replaced thing's; otherwise it stands.
    /// </summary>
    internal static bool FrameRefusalBlamesReplaced(bool requiresFrameRefusal, bool replacedFillsCell,
        bool frameBelow) =>
        requiresFrameRefusal && replacedFillsCell && frameBelow;

    /// <summary>
    /// Pipe.CanConstruct for a pipe whose origin cell holds a device mounted on a pipe (a pipe analyser, a pipe
    /// heater): only a straight pipe along the device's axis and of the device's content (gas or liquid) may pass
    /// under it. Null when it may. Axes are the absolute forwards (0 x, 1 y, 2 z).
    /// </summary>
    internal static string? PipeUnderMountedDevice(bool pipeStraight, int pipeAxis, int deviceAxis, bool sameContent,
        string device)
    {
        if (!pipeStraight || pipeAxis != deviceAxis)
        {
            return $"{device} is mounted on the pipe there; only a straight pipe along it may pass under it";
        }

        return sameContent ? null : $"{device} is mounted there for another pipe content (gas or liquid)";
    }

    /// <summary>
    /// Whether a thing stands where the cursor would put it: within a centimetre of the snapped point (squared
    /// distance in square metres).
    /// </summary>
    internal static bool OnGrid(double squaredOffset) => squaredOffset <= 1e-4;
}
