#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;


namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// The report rules over what a request places, in the world around it (the touched 2 m cells and their neighbours,
/// with what the request removes or replaces left out). Each finding about a planned thing becomes lint_&lt;rule id&gt;:
/// a problem (the rule's level, or its level_when, says problem) refuses the plan, naming how the save's file allows
/// it; a warning or info is a warning. A rule turned off in the files never runs.
/// </summary>
internal static class LintReport
{
    internal const string CodePrefix = "lint_";

    /// <summary>
    /// The findings about the planned things, report order, as a Work body: the world around gathered a cell per step,
    /// then a rule's subject per step. Rules that throw at any step give the one unavailable warning.
    /// </summary>
    internal static IEnumerable<object?> Finding(List<PlannedThing> planned, HashSet<long> gone,
        Answer<List<LintFinding>> answer)
    {
        if (planned.Count == 0)
        {
            answer.Value = new List<LintFinding>();
            yield break;
        }

        Answer<List<LintFinding>> found = new Answer<List<LintFinding>>();
        using IEnumerator<object?> steps = Run(planned, gone, found).GetEnumerator();
        Exception? failed = null;
        while (true)
        {
            bool more;
            try
            {
                more = steps.MoveNext();
            }
            catch (Exception error)
            {
                failed = error;
                break;
            }

            if (!more)
            {
                break;
            }

            yield return null;
        }

        answer.Value = failed == null ? found.Value : Unavailable(failed);
    }

    private static IEnumerable<object?> Run(List<PlannedThing> planned, HashSet<long> gone,
        Answer<List<LintFinding>> answer)
    {
        LintRuleSet rules = LintRuleFiles.Current();
        Answer<GameLintWorld> world = new Answer<GameLintWorld>();
        foreach (object? step in GameLintWorld.DryRunSteps(planned, gone, world))
        {
            yield return step;
        }

        Answer<LintRun> run = new Answer<LintRun>();
        foreach (object? step in LintEngine.Steps(rules, world.Value, "report", run))
        {
            yield return step;
        }

        answer.Value = Pure.LintReport.Ordered(run.Value.Findings);
    }

    private static List<LintFinding> Unavailable(Exception error) =>
        new List<LintFinding>
        {
            new LintFinding(CodePrefix + "unavailable", ConflictLevel.Warning, 0,
                $"The report lint rules could not run: {error.GetType().Name}: " +
                $"{error.Message}", null, Vec3.Zero,
                null, null, null)
        };

    /// <summary>
    /// The lint findings of a cable, pipe or chute run's planned pieces: a problem refuses the run, unless the call's
    /// allow names lint, which makes it a warning and has the report mark the rows about it PASSED (RunPlan.Lifted). A Work body: the findings in steps of their own (Finding).
    /// </summary>
    internal static IEnumerable<object?> Checking(RunPlan plan, bool allowLint)
    {
        List<PlannedThing> planned = new List<PlannedThing>(plan.Cells.Count);
        HashSet<long> gone = new HashSet<long>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            gone.Add(removal.Piece.ReferenceId);
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            if (cell.Existing != null)
            {
                gone.Add(cell.Existing.ReferenceId);
            }

            planned.Add(new PlannedThing(cell.Choice.Prefab, PieceShapes.CentreOf(cell.Cell), cell.Choice.Rotation,
                cell.Model.Cells, cell.Model.Ends));
        }

        Answer<List<LintFinding>> findings = new Answer<List<LintFinding>>();
        foreach (object? step in Finding(planned, gone, findings))
        {
            yield return step;
        }

        foreach (LintFinding finding in findings.Value)
        {
            if (finding.Level != ConflictLevel.Problem)
            {
                plan.Warnings.Add(new LayoutIssue(CodePrefix + finding.Code, finding.Message, CellOf(finding.At),
                    finding.OtherId));
                continue;
            }

            if (allowLint)
            {
                LayoutIssue lifted = new LayoutIssue(CodePrefix + finding.Code, Lifted(finding), CellOf(finding.At),
                    finding.OtherId);
                plan.Warnings.Add(lifted);
                plan.Lifted.Add(lifted);
                continue;
            }

            plan.Problem(CodePrefix + finding.Code, Refusal(finding), finding.OtherId, CellOf(finding.At));
        }
    }

    /// <summary>The lint findings of build structure's resolved placements: a problem refuses the plan, unless the
    /// call's allow names lint, which makes it a warning and has the report mark the placements it is about PASSED
    /// (PlacePlan.Lifted). A Work body: the findings in steps of their own (Finding).</summary>
    internal static IEnumerable<object?> Checking(PlacePlan plan, bool allowLint)
    {
        List<PlannedThing> planned = new List<PlannedThing>(plan.Placements.Count);
        foreach (PlannedPlacement placement in plan.Placements)
        {
            if (placement.Resolved && placement.Prefab != null && placement.Position.HasValue)
            {
                planned.Add(new PlannedThing(placement.Prefab, placement.Position.Value, placement.Rotation,
                    index: placement.Index));
            }
        }

        Answer<List<LintFinding>> findings = new Answer<List<LintFinding>>();
        foreach (object? step in Finding(planned, new HashSet<long>(), findings))
        {
            yield return step;
        }

        foreach (LintFinding finding in findings.Value)
        {
            int? index = IndexOf(finding, planned);
            if (finding.Level != ConflictLevel.Problem)
            {
                plan.Warn(CodePrefix + finding.Code, finding.Message, index);
                continue;
            }

            if (allowLint)
            {
                plan.Warn(CodePrefix + finding.Code, Lifted(finding), index);
                plan.Lifted.Add(new PlaceLift(WriteLint.Guard, index));
                continue;
            }

            plan.Problem(CodePrefix + finding.Code, Refusal(finding), index);
        }
    }

    /// <summary>A problem finding as the refusal it makes: the rule, and how the save's file allows it.</summary>
    internal static string Refusal(LintFinding finding) =>
        $"{finding.Message} Refused by lint rule {finding.Code} (level problem). To allow it, set its \"level\" to " +
        $"\"warning\" or \"enabled\": false in the save's lint-rules.json (check {{what: \"rules\"}} lists the " +
        "files).";

    /// <summary>
    /// A problem finding allow lint let through: the rule, and that it is now a warning the report marks PASSED.
    /// </summary>
    internal static string Lifted(LintFinding finding) =>
        $"{finding.Message} Lint rule {finding.Code} (level problem) let through by allow: [\"lint\"]; the report " +
        "marks what it is about PASSED.";

    private static GridCell CellOf(Vec3 at) =>
        new GridCell((int)Math.Round(at.X * 2.0) * 5, (int)Math.Round(at.Y * 2.0) * 5, (int)Math.Round(at.Z * 2.0) * 5);

    // The placement a finding is about: its subject key planned:N names the Nth planned thing.
    private static int? IndexOf(LintFinding finding, List<PlannedThing> planned)
    {
        string? key = finding.SubjectKey;
        int at = key?.IndexOf("planned:", StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return null;
        }

        int end = at + "planned:".Length;
        int stop = end;
        while (stop < key!.Length && char.IsDigit(key[stop]))
        {
            stop++;
        }

        return int.TryParse(key.Substring(end, stop - end), out int index) && index < planned.Count
            ? planned[index].Index
            : null;
    }
}
