#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// lint_layout's built-in rule ids and their default levels, in report order: the shipped lint-rules.json defines each
/// with the same id and level (a test holds them together).
/// </summary>
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
    internal const string ControlsBlocked = "controls_blocked";

    /// <summary>Every rule with its level, in report order.</summary>
    internal static readonly (string Code, ConflictLevel Level)[] Rules =
    {
        (NotReplaceable, ConflictLevel.Problem),
        (RunInDoorKeepOut, ConflictLevel.Warning),
        (PortIntoDoorway, ConflictLevel.Warning),
        (PortCellForeignNetwork, ConflictLevel.Warning),
        (FloatingRun, ConflictLevel.Warning),
        (RunCrossesWindow, ConflictLevel.Problem),
        (DeviceVisualOverlap, ConflictLevel.Warning),
        (MountedFacesOutOfRoom, ConflictLevel.Warning),
        (DeviceCrossesSeam, ConflictLevel.Warning),
        (ControlsBlocked, ConflictLevel.Problem),
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
        : this(code, LintCodes.LevelOf(code), LintReport.RuleIndex(code), message, thingId, at, otherId, null, null)
    {
    }

    /// <param name="order">The rule's place in its rule set: findings of one level are reported in it.</param>
    /// <param name="subjectKey">The subject's identity (thing:123, port:123/0, cell:..., a pair joined by +).</param>
    /// <param name="ruleId">For a rule_error, the rule that could not be evaluated.</param>
    internal LintFinding(string code, ConflictLevel level, int order, string message, long? thingId, Vec3 at,
        long? otherId, string? subjectKey, string? subject, string? ruleId = null)
    {
        Code = code;
        Level = level;
        Order = order;
        Message = message;
        ThingId = thingId;
        At = at;
        OtherId = otherId;
        SubjectKey = subjectKey;
        Subject = subject;
        RuleId = ruleId ?? code;
    }

    internal string Code { get; }

    internal ConflictLevel Level { get; }

    internal int Order { get; }

    internal string Message { get; }

    internal long? ThingId { get; }

    internal Vec3 At { get; }

    internal long? OtherId { get; }

    internal string? SubjectKey { get; }

    internal string? Subject { get; }

    /// <summary>The rule: the code, or for a rule_error the rule that failed to evaluate.</summary>
    internal string RuleId { get; }
}

/// <summary>Findings ordered for a reader (problems, then warnings, then info, rules in rule-set order) and counted.</summary>
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

            int rule = a.Finding.Order.CompareTo(b.Finding.Order);
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

    internal static int RuleIndex(string code)
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

/// <summary>
/// lint_layout codes and exclude_codes: which findings a reply lists. A finding passes when codes is not given or names
/// its code or rule_id, and exclude_codes names neither.
/// </summary>
internal sealed class LintCodeFilter
{
    private readonly HashSet<string>? _only;
    private readonly HashSet<string> _except;

    private LintCodeFilter(HashSet<string>? only, HashSet<string> except)
    {
        _only = only;
        _except = except;
    }

    internal static LintCodeFilter Every { get; } = new LintCodeFilter(null, new HashSet<string>(StringComparer.Ordinal));

    /// <summary>The filter from the codes to keep (null: every code) and the codes to leave out (null: none).</summary>
    internal static LintCodeFilter Of(IEnumerable<string>? only, IEnumerable<string>? except) =>
        new LintCodeFilter(only != null ? new HashSet<string>(only, StringComparer.Ordinal) : null,
            except != null ? new HashSet<string>(except, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Whether it leaves any finding out.</summary>
    internal bool Narrows => _only != null || _except.Count > 0;

    internal bool Admits(LintFinding finding) =>
        (_only == null || _only.Contains(finding.Code) || _only.Contains(finding.RuleId)) &&
        !_except.Contains(finding.Code) && !_except.Contains(finding.RuleId);

    /// <summary>The findings it admits, in their order.</summary>
    internal List<LintFinding> Keep(List<LintFinding> findings) => findings.FindAll(Admits);
}

/// <summary>
/// lint_layout reference_ids and since_id: only the findings on given things, so a caller can lint what it just built.
/// A finding passes when its thing or the other thing it names is one of reference_ids, or has an id at or above
/// since_id; the game numbers things in the order it makes them (Referencable.RegisterNew hands out NextReferenceId++),
/// so since_id keeps the things made since the one with that id. Both given, either passes. Composes with
/// LintCodeFilter: a listed finding passes both.
/// </summary>
internal sealed class LintThingFilter
{
    private readonly HashSet<long>? _ids;
    private readonly long? _sinceId;

    private LintThingFilter(HashSet<long>? ids, long? sinceId)
    {
        _ids = ids;
        _sinceId = sinceId;
    }

    internal static LintThingFilter Every { get; } = new LintThingFilter(null, null);

    /// <summary>The filter from the ids to keep (null: any) and the lowest id to keep (null: none).</summary>
    internal static LintThingFilter Of(IEnumerable<long>? ids, long? sinceId) =>
        new LintThingFilter(ids != null ? new HashSet<long>(ids) : null, sinceId);

    /// <summary>Whether it leaves any finding out.</summary>
    internal bool Narrows => _ids != null || _sinceId.HasValue;

    internal bool Admits(LintFinding finding) =>
        !Narrows || Names(finding.ThingId) || Names(finding.OtherId);

    private bool Names(long? id) =>
        id.HasValue && ((_ids != null && _ids.Contains(id.Value)) || (_sinceId.HasValue && id.Value >= _sinceId.Value));

    /// <summary>The findings it admits, in their order.</summary>
    internal List<LintFinding> Keep(List<LintFinding> findings) => findings.FindAll(Admits);
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
