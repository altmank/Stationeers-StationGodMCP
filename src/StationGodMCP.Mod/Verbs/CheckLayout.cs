#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// check layout (catalogue check.json): the audit rules of the effective rule set (lint-rules.json of the mod, with
/// the save's file over it) over a room or a box (LayoutCheck), from what stands there now; codes and exclude_codes
/// (LintCodeFilter) and id and since (LintThingFilter) pick the findings listed, counts still counts them all; limit
/// pages them with has_more, no offset (narrowing does the paging). Read only.
/// </summary>
[ReplyFact(FactRule.Live, "cells, structures and their ports")]
internal static class CheckLayout
{
    private const int MaximumLimit = 500;

    internal static VerbOutcome Run(CheckLayoutOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        LayoutCheck? check = LayoutCheck.Of(options, problems);
        if (check == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(CheckLayoutOptions.Spec.Label, problems));
        }

        List<GridCell> region = Cells(check.Region, out string described);
        LintRuleSet rules = LintRuleFiles.Current();
        return WorkResult<VerbOutcome>.Of(answer => Checked(check, region, described, rules, answer), static outcome => outcome);
    }

    // The audit over frames: the region's things gathered a cell per step (GameLintWorld.AuditSteps), the rules judged a
    // subject per step (LintEngine.Steps), then the findings kept, ordered and listed.
    private static IEnumerable<object?> Checked(LayoutCheck check, List<GridCell> region, string described,
        LintRuleSet rules, Answer<VerbOutcome> answer)
    {
        Answer<GameLintWorld> gathered = new Answer<GameLintWorld>();
        foreach (object? step in GameLintWorld.AuditSteps(region, gathered))
        {
            yield return step;
        }

        GameLintWorld world = gathered.Value;
        Answer<LintRun> judged = new Answer<LintRun>();
        foreach (object? step in LintEngine.Steps(rules, world, "audit", judged))
        {
            yield return step;
        }

        LintRun run = judged.Value;
        List<LintFinding> ordered = check.Things.Keep(check.Codes.Keep(Pure.LintReport.Ordered(run.Findings)));
        int limit = check.Limit ?? ordered.Count;
        List<LintFindingView> views = new List<LintFindingView>(System.Math.Min(limit, ordered.Count));
        for (int index = 0; index < ordered.Count && index < limit; index++)
        {
            views.Add(new LintFindingView(ordered[index]));
        }

        Truncations.Capped("findings", views.Count, ordered.Count, "limit", MaximumLimit);
        answer.Value = Answer.Of(new LintLayoutView(described, region.Count, world.Subjects("pieces").Count,
            world.Subjects("devices").Count, world.Subjects("structures").Count, world.Doors,
            Pure.LintReport.Counts(run.Findings), views, ordered.Count, new LintRuleSourceView(rules, LintRuleFiles.SaveFile, SaveFolders.How), run.Milliseconds,
            check.Codes.Narrows || check.Things.Narrows ? run.Findings.Count - ordered.Count : null));
    }

    private static List<GridCell> Cells(LayoutRegion region, out string described)
    {
        if (region is LayoutRegion.Box box)
        {
            described = box.Described;
            return LargeCells.InBox(box.Corners.First, box.Corners.Second);
        }

        long id = ((LayoutRegion.Room)region).Id;
        Room room = StructureAirRecord.FindRoom(id) ??
                    throw ApiErrors.Refused("room_not_found",
                        $"check layout: no closed room has id {id}; find room lists them.");
        List<GridCell> cells = new List<GridCell>();
        foreach (WorldGrid grid in new List<WorldGrid>(room.Grids))
        {
            cells.Add(PieceShapes.Cell(grid.Value));
        }

        described = $"room {id} ({cells.Count} cells)";
        return cells;
    }
}
