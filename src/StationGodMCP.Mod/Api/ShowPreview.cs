#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;

namespace StationGodMCP.Api;

/// <summary>
/// show_preview: in-game wire boxes (Previews) for what place_structure would do, read from its own dry run: each
/// placement's footprint (green, red when it has a problem), its render box (white) and its ports' joining cells
/// (cyan, red when blocked); plus any cells (yellow 0.5 m cubes, e.g. a planned route) and boxes given. Timed; a new
/// call replaces the last unless keep is true; clear: true only removes them; xray: true draws them through walls,
/// frames and terrain (XRay). Drawn on the player's screen (PlayerView): this game's, or a remote player's through
/// their StationGod. Changes nothing in the world.
/// </summary>
internal static class ShowPreviewApi
{
    private const double DefaultSeconds = 30.0;
    private const double MaximumSeconds = 300.0;
    private const int MaximumCells = 512;
    private const int MaximumBoxes = 64;

    private static readonly Rgba Footprint = new Rgba(0.2f, 1f, 0.3f, 1f);
    private static readonly Rgba Problem = new Rgba(1f, 0.2f, 0.2f, 1f);
    private static readonly Rgba Body = new Rgba(1f, 1f, 1f, 0.8f);
    private static readonly Rgba Port = new Rgba(0.2f, 0.9f, 1f, 1f);
    private static readonly Rgba Cell = new Rgba(1f, 0.9f, 0.2f, 1f);

    internal static ShowPreviewView Handle(Args args)
    {
        PlayerView player = PlayerView.Current();
        if (args.OptionalBool("clear") ?? false)
        {
            PlayerScreen shown = player.ScreenOrLocal();
            return new ShowPreviewView(0, shown.ClearPreviews(), 0, null, new List<string> { "Cleared." },
                shown.DrawnOn, player.Source);
        }

        // The arguments first, then the world (structures-28: a missing view answered a bad seconds).
        double seconds = args.OptionalPositiveDouble("seconds") ?? DefaultSeconds;
        if (seconds > MaximumSeconds)
        {
            throw ApiErrors.InvalidArgument($"seconds is at most {MaximumSeconds}.");
        }

        PlayerScreen screen = player.RequireScreen("show_preview");
        bool xray = args.OptionalBool("xray") ?? false;
        float time = (float)seconds;
        List<PreviewBox> boxes = new List<PreviewBox>();
        PlaceReportView? report = null;
        if (args.Has("placements") || args.Has("prefab"))
        {
            Args placeArgs = new Args(Strip(args));
            if (!(BuildArgs.ParsePlace(placeArgs) is BuildForm<PlaceArguments>.Run run))
            {
                throw ApiErrors.InvalidArgument("show_preview takes placements, not a job_id.");
            }

            PlacePlan plan = PlacePlanner.Plan(run.Arguments, lint: false);
            report = BuildReports.Of(plan, BuildReports.DryRun, null);
            foreach (PlannedPlacement placement in plan.Placements)
            {
                Draw(placement, plan, time, xray, boxes);
            }
        }

        foreach (Vec3 cell in Cells(args))
        {
            boxes.Add(new PreviewBox(new Box3(cell - new Vec3(0.25, 0.25, 0.25), cell + new Vec3(0.25, 0.25, 0.25)),
                Cell, time, "cell", xray));
        }

        foreach ((Box3 box, Rgba color) in Boxes(args))
        {
            boxes.Add(new PreviewBox(box, color, time, "box", xray));
        }

        int cleared = screen.Preview(!(args.OptionalBool("keep") ?? false), boxes);
        List<string> notes = new List<string>();
        if (boxes.Count == 0)
        {
            notes.Add("Nothing to draw: pass placements (or prefab and at), cells or boxes.");
        }

        notes.Add("Green: footprint (red with a problem); white: render box; cyan: port joining cells (red when " +
                  "blocked); yellow: cells. " + (screen.DrawnOn != null
                      ? $"Drawn by {screen.DrawnOn}'s game, on their screen only."
                      : "Only this game draws them."));
        return new ShowPreviewView(boxes.Count, cleared, seconds, report, notes, screen.DrawnOn, player.Source);
    }

    private static void Draw(PlannedPlacement placement, PlacePlan plan, float seconds, bool xray, List<PreviewBox> boxes)
    {
        LayoutPreview? layout = placement.Layout;
        if (layout == null)
        {
            return;
        }

        bool problem = plan.Problems.Exists(issue => issue.Index == placement.Index);
        if (layout.SmallCells.Count > 0)
        {
            boxes.Add(new PreviewBox(Box3.OfSmallCells(layout.SmallCells), problem ? Problem : Footprint, seconds,
                "footprint", xray));
        }

        boxes.Add(new PreviewBox(layout.Render, Body, seconds, "body", xray));
        foreach (PortCheckView port in layout.View.PortChecks ?? new List<PortCheckView>())
        {
            Vec3 centre = new Vec3(port.At.X, port.At.Y, port.At.Z);
            boxes.Add(new PreviewBox(new Box3(centre - new Vec3(0.2, 0.2, 0.2), centre + new Vec3(0.2, 0.2, 0.2)),
                port.Blocked != null && !port.Joins ? Problem : Port, seconds, "port", xray));
        }
    }

    // The request without show_preview's own fields, as place_structure's dry run takes it.
    private static JObject Strip(Args args)
    {
        JObject copy = new JObject();
        foreach (string name in new[]
                 {
                     "placements", "prefab", "at", "rotation", "facing", "up", "face", "orient", "above_floor_m",
                     "build_state", "from_id", "free", "allow_door_keepout"
                 })
        {
            JToken? value = args.Optional(name);
            if (value != null)
            {
                copy[name] = value.DeepClone();
            }
        }

        return copy;
    }

    private static List<Vec3> Cells(Args args)
    {
        List<Vec3> cells = new List<Vec3>();
        if (!(args.Optional("cells") is JArray array))
        {
            return cells;
        }

        if (array.Count > MaximumCells)
        {
            throw ApiErrors.InvalidArgument($"cells: at most {MaximumCells}.");
        }

        for (int index = 0; index < array.Count; index++)
        {
            Metres point = BuildArgs.PositionOf(array[index], $"cells[{index}]");
            cells.Add(new Vec3(point.X, point.Y, point.Z));
        }

        return cells;
    }

    private static List<(Box3, Rgba)> Boxes(Args args)
    {
        List<(Box3, Rgba)> boxes = new List<(Box3, Rgba)>();
        if (!args.Has("boxes"))
        {
            return boxes;
        }

        List<Args?> items = args.Objects("boxes", MaximumBoxes);
        for (int index = 0; index < items.Count; index++)
        {
            Args item = items[index] ?? throw ApiErrors.InvalidArgument($"boxes[{index}] must be an object.");
            Metres min = BuildArgs.PositionOf(item.Optional("min") ??
                                              throw ApiErrors.InvalidArgument($"boxes[{index}].min is required."),
                $"boxes[{index}].min");
            Metres max = BuildArgs.PositionOf(item.Optional("max") ??
                                              throw ApiErrors.InvalidArgument($"boxes[{index}].max is required."),
                $"boxes[{index}].max");
            boxes.Add((new Box3(new Vec3(min.X, min.Y, min.Z), new Vec3(max.X, max.Y, max.Z)),
                ColorOf(item.OptionalString("color"))));
        }

        return boxes;
    }

    private static Rgba ColorOf(string? name) => (name ?? "yellow").Trim().ToLowerInvariant() switch
    {
        "red" => Problem,
        "green" => Footprint,
        "white" => Body,
        "cyan" => Port,
        "yellow" => Cell,
        _ => throw ApiErrors.InvalidArgument("color must be red, green, white, cyan or yellow.")
    };
}
