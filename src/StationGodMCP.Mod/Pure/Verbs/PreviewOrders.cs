#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.RemoteView;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The colours show preview draws with: the palette of its boxes' names, and the footprint, body and port colours its
/// plan's boxes carry.
/// </summary>
internal static class PreviewColours
{
    /// <summary>A placement's footprint with a problem, and a blocked port.</summary>
    internal static readonly Rgba Problem = new Rgba(1f, 0.2f, 0.2f, 1f);

    /// <summary>A placement's footprint when nothing is wrong with it.</summary>
    internal static readonly Rgba Footprint = new Rgba(0.2f, 1f, 0.3f, 1f);

    /// <summary>A placement's render box.</summary>
    internal static readonly Rgba Body = new Rgba(1f, 1f, 1f, 0.8f);

    /// <summary>A port's joining cell.</summary>
    internal static readonly Rgba Port = new Rgba(0.2f, 0.9f, 1f, 1f);

    /// <summary>A cell, and a box without a colour.</summary>
    internal static readonly Rgba Cell = new Rgba(1f, 0.9f, 0.2f, 1f);

    /// <summary>The names a box's color takes, in the order a refusal lists them.</summary>
    internal static readonly IReadOnlyList<string> Names = new List<string>
    {
        "red", "green", "white", "cyan", "yellow"
    };

    /// <summary>
    /// A box's colour by the name it gives (trimmed, any case); false when no such colour is named. A box that gives
    /// none is yellow, as the old surface drew it.
    /// </summary>
    internal static bool TryRead(string? name, out Rgba colour)
    {
        switch ((name ?? "yellow").Trim().ToLowerInvariant())
        {
            case "red":
                colour = Problem;
                return true;
            case "green":
                colour = Footprint;
                return true;
            case "white":
                colour = Body;
                return true;
            case "cyan":
                colour = Port;
                return true;
            case "yellow":
                colour = Cell;
                return true;
            default:
                colour = default;
                return false;
        }
    }
}

/// <summary>
/// show preview's call, checked without the game: the placements (1 to 64, or the one at the top level by prefab and
/// at) read by the shared place readers, allow door_keepout, the cells (up to 512 yellow 0.5 m cubes) and boxes (up to
/// 64, each {min, max, color}) it draws, seconds (30 by default, at most 300), keep, clear and xray. The request the
/// game plans is built here: nobody pays, it is not free, no lint and no pad, a brief report. clear only removes what
/// is drawn, so nothing else is read then. The what's label prefixes every refusal ("show preview: ..."), every
/// problem naming its path (boxes[2].color).
/// </summary>
internal sealed class PreviewOrder
{
    /// <summary>The most placements one call draws (the option's maxItems).</summary>
    internal const int MaximumPlacements = PlaceRequest.MaximumPlacements;

    /// <summary>The most cells one call draws (the option's maxItems).</summary>
    internal const int MaximumCells = 512;

    /// <summary>The most boxes one call draws (the option's maxItems).</summary>
    internal const int MaximumBoxes = 64;

    /// <summary>The note a drawing without xray answers: the lines are drawn as solid things are, so anything in front hides them.</summary>
    internal const string HiddenNote =
        "Drawn without xray: walls, frames and terrain in front hide the lines (a box in the ground cannot be seen). " +
        "xray: true draws every box, cell and placement through them.";

    private static readonly string[] BoxKeys = { "min", "max", "color" };

    /// <summary>How long it shows when the call gives no seconds (the option's default).</summary>
    internal const double DefaultSeconds = 30.0;

    /// <summary>The longest it shows (the option's maximum).</summary>
    internal const double MaximumSeconds = 300.0;

    /// <summary>The two ways the call names its placements: the list, or one at the top level.</summary>
    private const string ByPlacements = "placements";

    private const string ByPrefab = "prefab";

    /// <summary>A placement's own field at the top level, which goes in each item beside a placements list.</summary>
    private static readonly string[] PlacementFields =
    {
        "prefab", "at", "rotation", "facing", "up", "face", "orient", "above_floor", "build_state"
    };

    private PreviewOrder(PlaceRequest arguments, List<PreviewBox> cells, List<PreviewBox> boxes, double seconds,
        bool keep, bool clear, bool xray)
    {
        Arguments = arguments;
        Cells = cells;
        Boxes = boxes;
        Seconds = seconds;
        Keep = keep;
        Clear = clear;
        Xray = xray;
    }

    /// <summary>The request the game plans: the placements, from nobody, not free, no lint, no pad, a brief report.</summary>
    public PlaceRequest Arguments { get; }

    /// <summary>The yellow 0.5 m cubes the call draws.</summary>
    public List<PreviewBox> Cells { get; }

    /// <summary>The boxes the call draws.</summary>
    public List<PreviewBox> Boxes { get; }

    public double Seconds { get; }

    /// <summary>Keep what earlier calls drew (default: a new call replaces them).</summary>
    public bool Keep { get; }

    /// <summary>Only remove what is drawn.</summary>
    public bool Clear { get; }

    /// <summary>Draw through walls, frames and terrain.</summary>
    public bool Xray { get; }

    /// <summary>Whether the call names placements: without them nothing is planned and only cells and boxes are drawn.</summary>
    internal bool HasPlacements => Arguments.Placements.Count > 0;

    /// <summary>The order show preview's options give; null with every problem added.</summary>
    // A preview is asked for most when the spot is blocked, and show takes no verbose: a refused placement keeps its
    // layout (what blocks it) in the reply.
    private static readonly ReportDetail PreviewDetail = new ReportDetail(false, true);

    internal static PreviewOrder? Of(ShowPreviewOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        if (options.Clear ?? false)
        {
            // Only what is drawn goes: the old surface read nothing else with clear.
            return new PreviewOrder(new PlaceRequest(new List<PlacementRequest>(), null, false),
                new List<PreviewBox>(), new List<PreviewBox>(), 0.0, false, true, false);
        }

        bool xray = options.Xray ?? false;
        double seconds = options.Seconds ?? DefaultSeconds;
        if (seconds > MaximumSeconds)
        {
            problems.Add(new OptionProblem("seconds", $"is at most {MaximumSeconds}"));
        }

        List<PlacementRequest> placements = Placements(options, problems);
        List<PreviewBox> cells = CellsOf(options.Cells, seconds, xray, problems);
        List<PreviewBox> boxes = BoxesOf(options.Boxes, seconds, xray, problems);
        return problems.Count == before
            ? new PreviewOrder(
                new PlaceRequest(placements, null, false, Allow.Lifts(options.Allow, "door_keepout"),
                    detail: PreviewDetail), cells, boxes,
                seconds, options.Keep ?? false, false, xray)
            : null;
    }

    // The placements the call names: the list read item by item, or the one placement's own fields at the top level.
    // Nothing at all is no problem: the call may draw only cells and boxes.
    private static List<PlacementRequest> Placements(ShowPreviewOptions options, List<OptionProblem> problems)
    {
        List<PlacementRequest> placements = new List<PlacementRequest>();
        if (options.Placements != null)
        {
            BesideTheList(options, problems);
            Items(options.Placements, placements, problems);
            return placements;
        }

        One(options, placements, problems);
        return placements;
    }

    // A placement's own field beside a placements list belongs in each item instead.
    private static void BesideTheList(ShowPreviewOptions options, List<OptionProblem> problems)
    {
        foreach (string field in PlacementFields)
        {
            if (Given(options, field))
            {
                problems.Add(new OptionProblem(field,
                    $"goes without {ByPlacements}: give it in each placement ({ByPlacements}[0].{field})"));
            }
        }
    }

    // Whether the call gives a placement field at the top level.
    private static bool Given(ShowPreviewOptions options, string field) => field switch
    {
        "prefab" => options.Selector.Prefab != null,
        "at" => options.At != null,
        "rotation" => options.Rotation != null,
        "facing" => options.Facing != null,
        "up" => options.Up != null,
        "face" => options.Face != null,
        "orient" => options.Orient != null,
        "above_floor" => options.AboveFloor.HasValue,
        "build_state" => options.BuildState != null,
        _ => false
    };

    // The placements list: each item read as a placement, the what's 1 to 64 of them.
    private static void Items(JToken given, List<PlacementRequest> placements, List<OptionProblem> problems)
    {
        if (!(given is JArray items) || items.Count == 0 || items.Count > MaximumPlacements)
        {
            problems.Add(new OptionProblem(ByPlacements,
                $"is a list of 1 to {MaximumPlacements} placements, each {{prefab, at, ...}}"));
            return;
        }

        for (int index = 0; index < items.Count; index++)
        {
            string path = $"{ByPlacements}[{index}]";
            if (!(items[index] is JObject item))
            {
                problems.Add(new OptionProblem(path, "is an object: {prefab, at, ...}"));
                continue;
            }

            PlacementOrders.Add(placements, item, index, path + ".", ShowPreviewOptions.Spec, problems);
        }
    }

    // The one placement's own fields at the top level; a call that gives none is no problem (it may draw only cells
    // and boxes). The prefab first: a * pattern is no placement, and reading one would only repeat that it is missing.
    private static void One(ShowPreviewOptions options, List<PlacementRequest> placements, List<OptionProblem> problems)
    {
        JObject item = new JObject();
        if (!PlacementOrders.Prefab(options.Selector.Prefab, ByPrefab, item, problems))
        {
            return;
        }

        if (options.At != null)
        {
            item["at"] = options.At.DeepClone();
        }

        if (options.Rotation != null)
        {
            item["rotation"] = options.Rotation.DeepClone();
        }

        if (options.Facing != null)
        {
            item["facing"] = options.Facing;
        }

        if (options.Up != null)
        {
            item["up"] = options.Up;
        }

        if (options.Face != null)
        {
            item["face"] = options.Face;
        }

        if (options.Orient != null)
        {
            item["orient"] = options.Orient.DeepClone();
        }

        if (options.AboveFloor.HasValue)
        {
            item["above_floor"] = options.AboveFloor.Value;
        }

        if (options.BuildState != null)
        {
            item["build_state"] = options.BuildState.DeepClone();
        }

        if (item.Count > 0)
        {
            PlacementOrders.Add(placements, item, 0, string.Empty, ShowPreviewOptions.Spec, problems);
        }
    }

    // The cells: up to 512 points, each a yellow 0.5 m cube around it (the old surface's boxes).
    private static List<PreviewBox> CellsOf(JToken? given, double seconds, bool xray, List<OptionProblem> problems)
    {
        List<PreviewBox> cells = new List<PreviewBox>();
        if (given == null)
        {
            return cells;
        }

        if (!(given is JArray points) || points.Count > MaximumCells)
        {
            problems.Add(new OptionProblem("cells", $"is up to {MaximumCells} points, each [x, y, z] in metres"));
            return cells;
        }

        for (int index = 0; index < points.Count; index++)
        {
            if (PlacementOrders.Position(points[index], $"cells[{index}]", problems, out Metres point))
            {
                Vec3 at = new Vec3(point.X, point.Y, point.Z);
                cells.Add(new PreviewBox(
                    new Box3(at - new Vec3(0.25, 0.25, 0.25), at + new Vec3(0.25, 0.25, 0.25)),
                    PreviewColours.Cell, (float)seconds, "cell", xray));
            }
        }

        return cells;
    }

    // The boxes: up to 64, each {min, max, color}; a box without a colour is yellow.
    private static List<PreviewBox> BoxesOf(JToken? given, double seconds, bool xray, List<OptionProblem> problems)
    {
        List<PreviewBox> boxes = new List<PreviewBox>();
        if (given == null)
        {
            return boxes;
        }

        if (!(given is JArray items) || items.Count > MaximumBoxes)
        {
            problems.Add(new OptionProblem("boxes",
                $"is up to {MaximumBoxes} boxes, each {{min, max, color}}"));
            return boxes;
        }

        for (int index = 0; index < items.Count; index++)
        {
            string path = $"boxes[{index}]";
            if (!(items[index] is JObject item))
            {
                problems.Add(new OptionProblem(path, "is an object: {min, max, color}"));
                continue;
            }

            int before = problems.Count;
            NestedKeys.Check(item, path, BoxKeys, problems);
            if (problems.Count > before)
            {
                continue;
            }

            JToken? min = item["min"];
            JToken? max = item["max"];
            if (min == null)
            {
                problems.Add(new OptionProblem(path + ".min", "is required: one corner, [x, y, z] in metres"));
            }

            if (max == null)
            {
                problems.Add(new OptionProblem(path + ".max", "is required: the other corner, [x, y, z] in metres"));
            }

            if (min == null || max == null ||
                !PlacementOrders.Position(min, path + ".min", problems, out Metres first) ||
                !PlacementOrders.Position(max, path + ".max", problems, out Metres second))
            {
                continue;
            }

            string? colour = null;
            if (item["color"] is JToken name && name.Type == JTokenType.String)
            {
                colour = name.Value<string>();
            }

            if (!PreviewColours.TryRead(colour, out Rgba tint))
            {
                problems.Add(new OptionProblem(path + ".color",
                    "is red, green, white, cyan or yellow", NearestNames.Of(colour ?? string.Empty, PreviewColours.Names)));
                continue;
            }

            boxes.Add(new PreviewBox(new Box3(new Vec3(first.X, first.Y, first.Z), new Vec3(second.X, second.Y, second.Z)),
                tint, (float)seconds, "box", xray));
        }

        return boxes;
    }
}
