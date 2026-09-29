#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// show_preview: in-game wire boxes (Previews) for what place_structure would do, read from its own dry run: each
/// placement's footprint (green, red when it has a problem), its render box (white) and its ports' joining cells
/// (cyan, red when blocked); plus any cells (yellow 0.5 m cubes, e.g. a planned route) and boxes given. Timed; a new
/// call replaces the last unless keep is true; clear: true only removes them. Changes nothing in the world.
/// </summary>
internal static class ShowPreviewApi
{
    private const double DefaultSeconds = 30.0;
    private const double MaximumSeconds = 300.0;
    private const int MaximumCells = 512;
    private const int MaximumBoxes = 64;

    private static readonly Color Footprint = new Color(0.2f, 1f, 0.3f, 1f);
    private static readonly Color Problem = new Color(1f, 0.2f, 0.2f, 1f);
    private static readonly Color Body = new Color(1f, 1f, 1f, 0.8f);
    private static readonly Color Port = new Color(0.2f, 0.9f, 1f, 1f);
    private static readonly Color Cell = new Color(1f, 0.9f, 0.2f, 1f);

    internal static ShowPreviewView Handle(Args args)
    {
        if (args.OptionalBool("clear") ?? false)
        {
            return new ShowPreviewView(0, Previews.Clear(), 0, null, new List<string> { "Cleared." });
        }

        // The arguments first, then the world (structures-28: no_camera answered a bad seconds).
        double seconds = args.OptionalPositiveDouble("seconds") ?? DefaultSeconds;
        if (seconds > MaximumSeconds)
        {
            throw ApiErrors.InvalidArgument($"seconds is at most {MaximumSeconds}.");
        }

        if (Look.Basis(out _) == null)
        {
            throw ApiErrors.Refused("no_camera", "There is no player camera to draw for (a dedicated server has none).");
        }

        if (!Previews.CanDraw)
        {
            throw ApiErrors.Refused("no_line_shader", "This build of the game has no built-in shader to draw lines with.");
        }

        int cleared = (args.OptionalBool("keep") ?? false) ? 0 : Previews.Clear();
        float time = (float)seconds;
        List<string> notes = new List<string>();
        int shown = 0;
        PlaceReportView? report = null;
        if (args.Has("placements") || args.Has("prefab"))
        {
            Args placeArgs = new Args(Strip(args));
            if (!(BuildArgs.ParsePlace(placeArgs) is BuildForm<PlaceArguments>.Run run))
            {
                throw ApiErrors.InvalidArgument("show_preview takes placements, not a job_id.");
            }

            PlacePlan plan = PlacePlanner.Plan(run.Arguments);
            report = BuildReports.Of(plan, BuildReports.DryRun, null);
            foreach (PlannedPlacement placement in plan.Placements)
            {
                shown += Draw(placement, plan, time);
            }
        }

        foreach (Vec3 cell in Cells(args))
        {
            shown += Draw(new Box3(cell - new Vec3(0.25, 0.25, 0.25), cell + new Vec3(0.25, 0.25, 0.25)), Cell, time,
                "cell");
        }

        foreach ((Box3 box, Color color) in Boxes(args))
        {
            shown += Draw(box, color, time, "box");
        }

        if (shown == 0)
        {
            notes.Add("Nothing to draw: pass placements (or prefab and at), cells or boxes.");
        }

        notes.Add("Green: footprint (red with a problem); white: render box; cyan: port joining cells (red when " +
                  "blocked); yellow: cells. Only this game draws them.");
        return new ShowPreviewView(shown, cleared, seconds, report, notes);
    }

    private static int Draw(PlannedPlacement placement, PlacePlan plan, float seconds)
    {
        LayoutPreview? layout = placement.Layout;
        if (layout == null)
        {
            return 0;
        }

        bool problem = plan.Problems.Exists(issue => issue.Index == placement.Index);
        int shown = 0;
        if (layout.SmallCells.Count > 0)
        {
            shown += Draw(Box3.OfSmallCells(layout.SmallCells), problem ? Problem : Footprint, seconds, "footprint");
        }

        shown += Draw(layout.Render, Body, seconds, "body");
        foreach (PortCheckView port in layout.View.PortChecks ?? new List<PortCheckView>())
        {
            Vec3 centre = new Vec3(port.At.X, port.At.Y, port.At.Z);
            shown += Draw(new Box3(centre - new Vec3(0.2, 0.2, 0.2), centre + new Vec3(0.2, 0.2, 0.2)),
                port.Blocked != null && !port.Joins ? Problem : Port, seconds, "port");
        }

        return shown;
    }

    private static int Draw(Box3 box, Color color, float seconds, string name)
    {
        if (!Previews.Box(box, color, seconds, "StationGodPreview " + name))
        {
            throw ApiErrors.Refused("no_line_shader", "This build of the game has no built-in shader to draw lines with.");
        }

        return 1;
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

    private static List<(Box3, Color)> Boxes(Args args)
    {
        List<(Box3, Color)> boxes = new List<(Box3, Color)>();
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

    private static Color ColorOf(string? name) => (name ?? "yellow").Trim().ToLowerInvariant() switch
    {
        "red" => Problem,
        "green" => Footprint,
        "white" => Body,
        "cyan" => Port,
        "yellow" => Cell,
        _ => throw ApiErrors.InvalidArgument("color must be red, green, white, cyan or yellow.")
    };
}
