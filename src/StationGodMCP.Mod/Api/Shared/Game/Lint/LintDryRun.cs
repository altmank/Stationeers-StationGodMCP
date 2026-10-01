#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;


namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// The dry_run rules over what a request places, in the world around it (the touched 2 m cells and their neighbours,
/// with what the request removes or replaces left out). Each finding about a planned thing becomes a warning
/// lint_&lt;rule id&gt;, whatever the rule's level: the plan is never refused for one.
/// </summary>
internal static class LintDryRun
{
    internal const string CodePrefix = "lint_";

    /// <summary>The findings about the planned things, report order.</summary>
    internal static List<LintFinding> Findings(List<PlannedThing> planned, HashSet<long> gone)
    {
        if (planned.Count == 0)
        {
            return new List<LintFinding>();
        }

        try
        {
            LintRuleSet rules = LintRuleFiles.Current();
            GameLintWorld world = GameLintWorld.DryRun(planned, gone);
            return LintReport.Ordered(LintEngine.Run(rules, world, "dry_run").Findings);
        }
        catch (Exception error)
        {
            return new List<LintFinding>
            {
                new LintFinding(CodePrefix + "unavailable", ConflictLevel.Warning, 0,
                    $"The dry_run lint rules could not run: {error.GetType().Name}: {error.Message}", null, Vec3.Zero,
                    null, null, null)
            };
        }
    }

    /// <summary>The lint warnings of a cable, pipe or chute run's planned pieces.</summary>
    internal static void Check(RunPlan plan)
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

        foreach (LintFinding finding in Findings(planned, gone))
        {
            plan.Warnings.Add(new LayoutIssue(CodePrefix + finding.Code, finding.Message,
                CellOf(finding.At), finding.OtherId));
        }
    }

    /// <summary>The lint warnings of place_structure's resolved placements.</summary>
    internal static void Check(PlacePlan plan)
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

        foreach (LintFinding finding in Findings(planned, new HashSet<long>()))
        {
            plan.Warn(CodePrefix + finding.Code, finding.Message, IndexOf(finding, planned));
        }
    }

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
