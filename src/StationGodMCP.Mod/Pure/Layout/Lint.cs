#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>lint_layout's rule codes.</summary>
internal static class LintCodes
{
    internal const string FloatingRun = "floating_run";
    internal const string RunInDoorKeepOut = "run_in_door_keepout";
    internal const string DeviceCrossesSeam = "device_crosses_seam";
    internal const string DeviceVisualOverlap = "device_visual_overlap";
    internal const string MountedFacesOutOfRoom = "mounted_faces_out_of_room";
    internal const string ControlsNotOnWall = "controls_not_on_wall";
    internal const string PortIntoDoorway = "port_into_doorway";
    internal const string PortCellForeignNetwork = "port_cell_foreign_network";
    internal const string RunAlongDoor = "run_along_door";
    internal const string RunCrossesWindow = "run_crosses_window";
    internal const string NotReplaceable = "not_replaceable";
    internal const string ReplaceableUnchecked = "replaceable_unchecked";

    /// <summary>Every rule with its level, in report order.</summary>
    internal static readonly (string Code, ConflictLevel Level)[] Rules =
    {
        (NotReplaceable, ConflictLevel.Problem),
        (RunInDoorKeepOut, ConflictLevel.Warning),
        (PortIntoDoorway, ConflictLevel.Warning),
        (PortCellForeignNetwork, ConflictLevel.Warning),
        (FloatingRun, ConflictLevel.Warning),
        (RunCrossesWindow, ConflictLevel.Warning),
        (DeviceVisualOverlap, ConflictLevel.Warning),
        (MountedFacesOutOfRoom, ConflictLevel.Warning),
        (DeviceCrossesSeam, ConflictLevel.Warning),
        (RunAlongDoor, ConflictLevel.Info),
        (ControlsNotOnWall, ConflictLevel.Info),
        (ReplaceableUnchecked, ConflictLevel.Info)
    };

    internal static ConflictLevel LevelOf(string code)
    {
        foreach ((string rule, ConflictLevel level) in Rules)
        {
            if (rule == code)
            {
                return level;
            }
        }

        throw new ArgumentException($"No lint rule {code}.", nameof(code));
    }
}

/// <summary>One lint finding: rule, level, message, the thing it is about and where.</summary>
internal sealed class LintFinding
{
    internal LintFinding(string code, string message, long? thingId, Vec3 at, long? otherId = null)
    {
        Code = code;
        Level = LintCodes.LevelOf(code);
        Message = message;
        ThingId = thingId;
        At = at;
        OtherId = otherId;
    }

    internal string Code { get; }

    internal ConflictLevel Level { get; }

    internal string Message { get; }

    internal long? ThingId { get; }

    internal Vec3 At { get; }

    internal long? OtherId { get; }
}

/// <summary>Findings ordered for a reader (problems, then warnings, then info, rules in LintCodes.Rules order) and counted.</summary>
internal static class LintReport
{
    internal static List<LintFinding> Ordered(List<LintFinding> findings)
    {
        List<(LintFinding Finding, int Order)> ordered = new List<(LintFinding, int)>(findings.Count);
        for (int index = 0; index < findings.Count; index++)
        {
            ordered.Add((findings[index], index));
        }

        ordered.Sort(static (a, b) =>
        {
            int level = b.Finding.Level.CompareTo(a.Finding.Level);
            if (level != 0)
            {
                return level;
            }

            int rule = RuleIndex(a.Finding.Code).CompareTo(RuleIndex(b.Finding.Code));
            return rule != 0 ? rule : a.Order.CompareTo(b.Order);
        });
        return ordered.ConvertAll(item => item.Finding);
    }

    internal static Dictionary<string, int> Counts(List<LintFinding> findings)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>();
        foreach (LintFinding finding in findings)
        {
            counts[finding.Code] = (counts.TryGetValue(finding.Code, out int count) ? count : 0) + 1;
        }

        return counts;
    }

    private static int RuleIndex(string code)
    {
        for (int index = 0; index < LintCodes.Rules.Length; index++)
        {
            if (LintCodes.Rules[index].Code == code)
            {
                return index;
            }
        }

        return LintCodes.Rules.Length;
    }
}

/// <summary>Which devices a player works at: their front carries a screen or controls.</summary>
internal static class Controls
{
    private static readonly string[] Words = { "Console", "Computer", "LogicDisplay", "Dial", "Button", "Switch", "Lever", "Keypad" };

    /// <summary>By prefab name: consoles, computers, displays, dials, buttons, switches, levers, keypads.</summary>
    internal static bool Has(string? prefabName)
    {
        if (prefabName == null)
        {
            return false;
        }

        foreach (string word in Words)
        {
            if (prefabName.IndexOf(word, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
