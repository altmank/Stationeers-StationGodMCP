#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game.Show;

/// <summary>
/// show preview (catalogue show.json): clear removes every box and ghost the player's screen has of the mod's, and a
/// draw plans the placements (PlacePlanner.Plan without the lint: a preview reports no lint problems) and draws, by the
/// style, each placement's footprint (green, red when it has a problem), its render box (white) and its ports' joining
/// cells (cyan, red when blocked), or its ghost in the call's tint (Ghosts); a run, planned before (RunPlanner without
/// the lint), the same way: its new and changed pieces' cells, or their ghosts; then the call's own cells and boxes.
/// The plan's report is the reply's plan: build structure's own report of the placements, without the keys a job reply
/// has and a report does not (tool, status, job_id); a run's is the reply's run, brief. Drawn on the player's screen
/// (PlayerSight): this game's, or a remote player's through their StationGod; with several players in play and none
/// named (as_player), each of theirs (ShowAudience). Nothing in the world changes.
/// </summary>
internal static class PreviewScreen
{
    /// <summary>clear: every box and ghost drawn for the player so far goes; nothing is planned and nothing drawn.</summary>
    internal static ShowPreviewView Clear()
    {
        ShowAudience audience = ShowAudience.ToClear();
        int cleared = audience.Each(static screen => screen.ClearPreviews() + screen.ClearGhosts());
        audience.Notes.Insert(0, "Cleared.");
        return new ShowPreviewView(0, 0, cleared, 0.0, null, null, audience.Notes, audience.DrawnOn, audience.Source);
    }

    /// <summary>
    /// The call's drawings: the placements' and the run's (planned already, over ticks) as boxes, ghosts or both by
    /// the style, then the cells and boxes it gave. A call without keep replaces every box and ghost shown.
    /// </summary>
    internal static ShowPreviewView Show(PreviewOrder order, RunPlan? run)
    {
        ShowAudience audience = ShowAudience.ToDraw("show preview", measures: false);
        float seconds = (float)order.Seconds;
        List<PreviewBox> boxes = new List<PreviewBox>();
        List<GhostSpec> ghosts = new List<GhostSpec>();
        List<string> notes = audience.Notes;
        object? plan = null;
        if (order.HasPlacements)
        {
            PlacePlan planned = PlacePlanner.Plan(order.Arguments, lint: false);
            plan = order.Verbose
                ? JobReports.Body(BuildReports.Of(planned, BuildReports.DryRun, null), planned.Ready)
                : Brief(planned);
            foreach (PlannedPlacement placement in planned.Placements)
            {
                if (order.DrawsBoxes)
                {
                    Draw(placement, planned, seconds, order.Xray, boxes);
                }

                if (order.DrawsGhosts && GhostOf(placement, order, seconds) is { } ghost)
                {
                    ghosts.Add(ghost);
                }
            }
        }

        RunPreviewView? runView = null;
        if (run != null && order.Run != null)
        {
            runView = new RunPreviewView(order.Run.Kind, run.Ready, run.Cells.Count, run.Problems, run.Warnings);
            Draw(run, order, seconds, boxes, ghosts, notes);
        }

        boxes.AddRange(order.Cells);
        boxes.AddRange(order.Boxes);
        int total = 0;
        int cleared = audience.Each(screen =>
        {
            int gone = screen.Preview(!order.Keep, boxes) + screen.Ghost(!order.Keep, ghosts);
            total = System.Math.Max(total, screen.GhostsShown);
            return gone;
        });
        if (boxes.Count == 0 && ghosts.Count == 0)
        {
            notes.Add("Nothing to draw: pass placements (or prefab and at), run, cells or boxes.");
        }
        else if (!order.Xray)
        {
            notes.Add(order.HasRun && order.DrawsGhosts
                ? PreviewOrder.HiddenNote + " A run's ghosts are drawn see-through anyway."
                : PreviewOrder.HiddenNote);
        }

        return new ShowPreviewView(boxes.Count, ghosts.Count, cleared, order.Seconds, plan, runView, notes,
            audience.DrawnOn, audience.Source) { GhostsTotal = total };
    }

    // A placement's ghost: its prefab at its build state, where and as it would be placed; null when the plan could
    // not resolve the prefab or the point.
    private static GhostSpec? GhostOf(PlannedPlacement placement, PreviewOrder order, float seconds)
    {
        if (placement.Prefab == null || !placement.Position.HasValue)
        {
            return null;
        }

        int state = placement.State ?? placement.Prefab.BuildStates.Count - 1;
        return new GhostSpec(placement.Prefab.PrefabHash, state, Bodies.V(placement.Position.Value),
            Bodies.V(placement.Rotation.eulerAngles), order.TintOf(placement.Index), seconds, order.Xray,
            label: order.LabelOf(placement.Index));
    }

    // The plan in short (verbose has build structure's whole report): ready, the problems and warnings, and each
    // placement's spot.
    private static PreviewPlanView Brief(PlacePlan plan) =>
        new PreviewPlanView(plan.Ready, plan.Problems, plan.Warnings, Spots(plan));

    /// <summary>
    /// Where each placement went, as build structure's report says it: position, orientation, build state, and a
    /// device's ports with the layout's port checks, so planning ports needs no build call. show ghost's plan too.
    /// </summary>
    internal static List<PreviewPlacementView> Spots(PlacePlan plan)
    {
        List<PreviewPlacementView> placements = new List<PreviewPlacementView>(plan.Placements.Count);
        foreach (PlannedPlacement placement in plan.Placements)
        {
            placements.Add(new PreviewPlacementView(placement.Index,
                placement.Prefab != null ? placement.Prefab.PrefabName : placement.Args.Prefab.ToString(),
                placement.Position.HasValue ? GameLookup.ViewOf(placement.Position.Value) : null,
                placement.Turn != null ? OrientationView.Of(placement.Turn) : null, placement.State,
                placement.Ports, placement.Layout?.View.PortChecks));
        }

        return placements;
    }

    // A run's new and changed pieces: each one's cell as a box (red where no piece could be chosen), and each piece's
    // ghost, finished, in the turn the run lays it; at most DrawWire.MaximumGhosts ghosts.
    private static void Draw(RunPlan run, PreviewOrder order, float seconds, List<PreviewBox> boxes,
        List<GhostSpec> ghosts, List<string> notes)
    {
        foreach (PlannedCell cell in run.Cells)
        {
            if (order.DrawsBoxes)
            {
                boxes.Add(CellBox(cell.Cell, PreviewColours.Footprint, seconds, order.Xray));
            }

            if (!order.DrawsGhosts)
            {
                continue;
            }

            if (ghosts.Count == DrawWire.MaximumGhosts)
            {
                notes.Add($"Only the first {DrawWire.MaximumGhosts} ghosts are drawn.");
                break;
            }

            Structure prefab = cell.Choice.Prefab;
            // Pieces lie inside floors and frames: without the see-through pass a floor's run cannot be seen.
            ghosts.Add(new GhostSpec(prefab.PrefabHash, prefab.BuildStates.Count - 1,
                Bodies.V(PieceShapes.CentreOf(cell.Cell)), Bodies.V(cell.Choice.Rotation.eulerAngles), order.Tint,
                seconds, xray: true));
        }

        if (order.DrawsBoxes)
        {
            foreach (LayoutCell unchosen in run.Unchosen)
            {
                boxes.Add(CellBox(unchosen.Cell, PreviewColours.Problem, seconds, order.Xray));
            }
        }
    }

    private static PreviewBox CellBox(GridCell cell, Rgba colour, float seconds, bool xray)
    {
        Vec3 centre = Bodies.V(PieceShapes.CentreOf(cell));
        Vec3 half = new Vec3(0.25, 0.25, 0.25);
        return new PreviewBox(new Box3(centre - half, centre + half), colour, seconds, "run", xray);
    }

    // One placement: its footprint, its render box and each port's joining cell, as the old surface drew them.
    private static void Draw(PlannedPlacement placement, PlacePlan plan, float seconds, bool xray,
        List<PreviewBox> boxes)
    {
        LayoutPreview? layout = placement.Layout;
        if (layout == null)
        {
            return;
        }

        bool problem = plan.Problems.Exists(issue => issue.Index == placement.Index);
        if (layout.SmallCells.Count > 0)
        {
            boxes.Add(new PreviewBox(Box3.OfSmallCells(layout.SmallCells),
                problem ? PreviewColours.Problem : PreviewColours.Footprint, seconds, "footprint", xray));
        }

        boxes.Add(new PreviewBox(layout.Render, PreviewColours.Body, seconds, "body", xray));
        foreach (PortCheckView port in layout.View.PortChecks ?? new List<PortCheckView>())
        {
            Vec3 centre = new Vec3(port.At.X, port.At.Y, port.At.Z);
            boxes.Add(new PreviewBox(
                new Box3(centre - new Vec3(0.2, 0.2, 0.2), centre + new Vec3(0.2, 0.2, 0.2)),
                port.Blocked != null && !port.Joins ? PreviewColours.Problem : PreviewColours.Port, seconds, "port",
                xray));
        }
    }
}
