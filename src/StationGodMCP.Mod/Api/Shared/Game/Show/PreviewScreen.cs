#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game.Show;

/// <summary>
/// show preview (catalogue show.json), the two branches the old show preview handler ran: clear removes every box the
/// player's screen has of the mod's, and a draw plans the placements (PlacePlanner.Plan without the lint: a preview
/// reports no lint problems) and draws each placement's footprint (green, red when it has a problem), its render box
/// (white) and its ports' joining cells (cyan, red when blocked), then the call's own cells and boxes. The plan's
/// report is the reply's plan: build structure's own report of the placements, without the keys a job reply has and a
/// report does not (tool, status, job_id). Drawn on the player's screen (PlayerSight): this game's, or a remote
/// player's through their StationGod; with several players in play and none named (as_player), each of theirs
/// (ShowAudience). Nothing in the world changes.
/// </summary>
internal static class PreviewScreen
{
    /// <summary>clear: every box drawn for the player so far goes; nothing is planned and nothing drawn.</summary>
    internal static ShowPreviewView Clear()
    {
        ShowAudience audience = ShowAudience.ToClear();
        int cleared = audience.Each(static screen => screen.ClearPreviews());
        audience.Notes.Insert(0, "Cleared.");
        return new ShowPreviewView(0, cleared, 0.0, null, audience.Notes, audience.DrawnOn, audience.Source);
    }

    /// <summary>The call's boxes: the plan's, then the cells and boxes it gave.</summary>
    internal static ShowPreviewView Show(PreviewOrder order)
    {
        ShowAudience audience = ShowAudience.ToDraw("show preview", measures: false);
        float seconds = (float)order.Seconds;
        List<PreviewBox> boxes = new List<PreviewBox>();
        ReplyObject? plan = null;
        if (order.HasPlacements)
        {
            PlacePlan planned = PlacePlanner.Plan(order.Arguments, lint: false);
            plan = JobReports.Body(BuildReports.Of(planned, BuildReports.DryRun, null), planned.Ready);
            foreach (PlannedPlacement placement in planned.Placements)
            {
                Draw(placement, planned, seconds, order.Xray, boxes);
            }
        }

        boxes.AddRange(order.Cells);
        boxes.AddRange(order.Boxes);
        int cleared = audience.Each(screen => screen.Preview(!order.Keep, boxes));
        List<string> notes = audience.Notes;
        if (boxes.Count == 0)
        {
            notes.Add("Nothing to draw: pass placements (or prefab and at), cells or boxes.");
        }

        return new ShowPreviewView(boxes.Count, cleared, order.Seconds, plan, notes, audience.DrawnOn, audience.Source);
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
