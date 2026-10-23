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
/// The tint a ghost is drawn with: the agent's or the player's choice, with no meaning of its own. A name (show
/// highlight's palette and the game's paint colours: purple, pink, khaki, brown, black, gray), [r, g, b] or
/// [r, g, b, a], each 0 to 1; without an alpha a ghost is see-through at DefaultAlpha.
/// </summary>
internal static class GhostTints
{
    // The game's paint colours show highlight's palette lacks.
    private static readonly (string Name, Rgba Tint)[] Paints =
    {
        ("purple", new Rgba(0.6f, 0.3f, 0.95f, DefaultAlpha)),
        ("pink", new Rgba(1f, 0.5f, 0.75f, DefaultAlpha)),
        ("khaki", new Rgba(0.76f, 0.7f, 0.45f, DefaultAlpha)),
        ("brown", new Rgba(0.55f, 0.35f, 0.2f, DefaultAlpha)),
        ("black", new Rgba(0.12f, 0.12f, 0.12f, DefaultAlpha)),
        ("gray", new Rgba(0.6f, 0.6f, 0.6f, DefaultAlpha))
    };

    /// <summary>Every name a tint takes, in the order the ghost menu lists them.</summary>
    internal static readonly IReadOnlyList<string> Names = NamesOf();

    /// <summary>How see-through a ghost is when its tint gives no alpha.</summary>
    internal const float DefaultAlpha = 0.4f;

    /// <summary>The tint a call that gives none draws its ghosts with.</summary>
    internal static readonly Rgba Default = new Rgba(0.45f, 0.75f, 1f, DefaultAlpha);

    private static readonly string Wanted =
        $"is one of {string.Join(", ", Names)}, or [r, g, b] or [r, g, b, a] with each 0 to 1";

    /// <summary>The tint a name gives (trimmed, any case; grey is gray), at DefaultAlpha; null for no such name.</summary>
    internal static Rgba? ByName(string name)
    {
        string trimmed = name.Trim();
        if (HighlightRequest.ByName(trimmed) is { } known)
        {
            return new Rgba((float)known.Tint.Red, (float)known.Tint.Green, (float)known.Tint.Blue, DefaultAlpha);
        }

        if (string.Equals(trimmed, "grey", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "gray";
        }

        foreach ((string paint, Rgba tint) in Paints)
        {
            if (string.Equals(paint, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return tint;
            }
        }

        return null;
    }

    /// <summary>The tint given at path; false with the problem when it reads as none.</summary>
    internal static bool TryRead(JToken given, string path, List<OptionProblem> problems, out Rgba tint)
    {
        tint = Default;
        if (given.Type == JTokenType.String)
        {
            string name = given.Value<string>()!.Trim();
            if (ByName(name) is { } known)
            {
                tint = known;
                return true;
            }

            problems.Add(new OptionProblem(path, Wanted, NearestNames.Of(name, Names)));
            return false;
        }

        if (given is JArray channels && (channels.Count == 3 || channels.Count == 4))
        {
            float[] read = new float[4];
            read[3] = DefaultAlpha;
            bool all = true;
            for (int index = 0; index < channels.Count; index++)
            {
                all &= Channel(channels[index], out read[index]);
            }

            if (all)
            {
                tint = new Rgba(read[0], read[1], read[2], read[3]);
                return true;
            }
        }

        problems.Add(new OptionProblem(path, Wanted));
        return false;
    }

    private static List<string> NamesOf()
    {
        List<string> names = new List<string>(HighlightRequest.ColorNames);
        foreach ((string name, Rgba _) in Paints)
        {
            names.Add(name);
        }

        return names;
    }

    private static bool Channel(JToken token, out float value)
    {
        value = 0f;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
        {
            return false;
        }

        double read = token.Value<double>();
        value = (float)read;
        return read >= 0 && read <= 1;
    }
}

/// <summary>How show preview draws a plan: wire boxes (as before ghosts), ghosts of the structures, or both.</summary>
internal enum PreviewStyle
{
    Boxes,
    Ghost,
    Both
}

/// <summary>
/// show preview's call, checked without the game (planned free: a preview pays nothing, so materials never make it
/// not ready): the placements (1 to 64, or the one at the top level by prefab and
/// at) read by the shared place readers, each with its own tint, allow door_keepout, a run (build cable, pipe or chute's
/// options with what, read by RunBuildOrder as those whats read them), the style (boxes, ghost or both) and the ghosts'
/// tint, the cells (up to 512 yellow 0.5 m cubes) and boxes (up to 64, each {min, max, color}) it draws, seconds (30
/// by default, at most 300), keep, clear and xray. The request the game plans is built here: nobody pays, it is
/// free, no lint and no pad, a brief report. clear only removes what is drawn, so nothing else is read then. The
/// what's label prefixes every refusal ("show preview: ..."), every problem naming its path (boxes[2].color).
/// </summary>
internal sealed class PreviewOrder
{
    /// <summary>The most placements one call draws (the option's maxItems).</summary>
    internal const int MaximumPlacements = 512;

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
    internal const double MaximumSeconds = 3600.0;

    /// <summary>The two ways the call names its placements: the list, or one at the top level.</summary>
    private const string ByPlacements = "placements";

    private const string ByPrefab = "prefab";

    /// <summary>The option naming a run to plan and draw.</summary>
    internal const string ByRun = "run";

    /// <summary>A placement item's own tint, read here and never passed to the place readers.</summary>
    private const string TintKey = "tint";

    /// <summary>The whats a run builds as.</summary>
    private static readonly string[] RunKinds = { "cable", "pipe", "chute" };

    private static readonly string[] StyleNames = { "boxes", "ghost", "both" };

    /// <summary>A placement's own field at the top level, which goes in each item beside a placements list.</summary>
    private static readonly string[] PlacementFields =
    {
        "prefab", "at", "rotation", "facing", "up", "face", "orient", "above_floor", "build_state", "label"
    };

    /// <summary>A placement item's keys: the place readers' and its tint.</summary>
    private static readonly string[] PlacementItemKeys = ItemKeys();

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

    /// <summary>boxes (the default), ghost or both.</summary>
    public PreviewStyle Style { get; private set; } = PreviewStyle.Boxes;

    /// <summary>Whether wire boxes are drawn for the plan.</summary>
    internal bool DrawsBoxes => Style != PreviewStyle.Ghost;

    /// <summary>Whether ghosts are drawn for the plan.</summary>
    internal bool DrawsGhosts => Style != PreviewStyle.Boxes;

    /// <summary>The call's tint for its ghosts (a placement's own tint wins).</summary>
    public Rgba Tint { get; private set; } = GhostTints.Default;

    /// <summary>The placements' own tints, by placement index.</summary>
    public Dictionary<int, Rgba> PlacementTints { get; } = new Dictionary<int, Rgba>();

    /// <summary>The run to plan and draw, as build cable, pipe or chute would read it; null without run.</summary>
    public RunBuildOrder? Run { get; private set; }

    /// <summary>The placements' labels (each ghost's tooltip), by placement index.</summary>
    public Dictionary<int, string> Labels { get; } = new Dictionary<int, string>();

    /// <summary>The label placement index shows as its ghost's tooltip; null for none.</summary>
    internal string? LabelOf(int index) => Labels.TryGetValue(index, out string? label) ? label : null;

    /// <summary>The tint placement index draws its ghost with.</summary>
    internal Rgba TintOf(int index) => PlacementTints.TryGetValue(index, out Rgba own) ? own : Tint;

    /// <summary>The request the game plans: the placements, from nobody, free, no lint, no pad, a brief report.</summary>
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

    /// <summary>plan: build structure's whole report, not the brief one.</summary>
    public bool Verbose { get; private set; }

    /// <summary>Whether the call names placements: without them nothing is planned and only cells and boxes are drawn.</summary>
    internal bool HasPlacements => Arguments.Placements.Count > 0;

    /// <summary>Whether the call names a run: planned in the game (over ticks) before anything is drawn.</summary>
    internal bool HasRun => Run != null;

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

        Dictionary<int, Rgba> tints = new Dictionary<int, Rgba>();
        Dictionary<int, string> labels = new Dictionary<int, string>();
        List<PlacementRequest> placements = Placements(GivenPlacements.Of(options), tints, labels, problems);
        List<PreviewBox> cells = CellsOf(options.Cells, seconds, xray, problems);
        List<PreviewBox> boxes = BoxesOf(options.Boxes, seconds, xray, problems);
        PreviewStyle style = StyleOf(options.Style, problems);
        Rgba tint = GhostTints.Default;
        if (options.Tint != null && GhostTints.TryRead(options.Tint, TintKey, problems, out Rgba given))
        {
            tint = given;
        }

        RunBuildOrder? run = RunOf(options.Run, problems);
        if (problems.Count > before)
        {
            return null;
        }

        PreviewOrder order = new PreviewOrder(
            new PlaceRequest(placements, null, true, Allow.Lifts(options.Allow, "door_keepout"),
                detail: PreviewDetail), cells, boxes,
            seconds, options.Keep ?? false, false, xray)
        {
            Style = style,
            Tint = tint,
            Run = run,
            Verbose = options.Fields.Verbose
        };
        foreach (KeyValuePair<int, Rgba> own in tints)
        {
            order.PlacementTints[own.Key] = own.Value;
        }

        foreach (KeyValuePair<int, string> own in labels)
        {
            order.Labels[own.Key] = own.Value;
        }

        return order;
    }

    // style: boxes (the default), ghost or both.
    private static PreviewStyle StyleOf(string? given, List<OptionProblem> problems)
    {
        switch (given?.Trim().ToLowerInvariant())
        {
            case null:
            case "boxes":
                return PreviewStyle.Boxes;
            case "ghost":
                return PreviewStyle.Ghost;
            case "both":
                return PreviewStyle.Both;
            default:
                problems.Add(new OptionProblem("style", "is boxes, ghost or both", NearestNames.Of(given, StyleNames)));
                return PreviewStyle.Boxes;
        }
    }

    // run: build cable, pipe or chute's options with what, read as that what reads them (its own option names only;
    // confirm and the like are read and never acted on: a preview builds nothing). Problems keep their path under run.
    private static RunBuildOrder? RunOf(JToken? given, List<OptionProblem> problems)
    {
        if (given == null)
        {
            return null;
        }

        if (!(given is JObject run))
        {
            problems.Add(new OptionProblem(ByRun,
                "is build cable, pipe or chute's options with what: {what: \"pipe\", grade, waypoints, ...}"));
            return null;
        }

        string? kind = run["what"] is { Type: JTokenType.String } what ? ((string)what!).Trim() : null;
        if (kind == null || Array.IndexOf(RunKinds, kind) < 0)
        {
            problems.Add(new OptionProblem(ByRun + ".what", "is cable, pipe or chute",
                NearestNames.Of(kind ?? string.Empty, RunKinds)));
            return null;
        }

        JObject options = (JObject)run.DeepClone();
        options.Remove("what");
        WhatSpec spec = RunBuildOrder.SpecOf(kind);
        int before = problems.Count;
        NestedKeys.Check(options, ByRun, spec.OptionNames, problems);
        if (problems.Count > before)
        {
            return null;
        }

        // A preview builds nothing, so it pays nothing: planned free, ready means build would place it.
        options["free"] = true;
        OptionReader reader = new OptionReader(spec, options);
        List<OptionProblem> read = new List<OptionProblem>();
        VerbError? missing;
        RunBuildOrder? order = kind switch
        {
            "pipe" => RunBuildOrder.Of(BuildPipeOptions.Bind(reader), read, out missing),
            "chute" => RunBuildOrder.Of(BuildChuteOptions.Bind(reader), read, out missing),
            _ => RunBuildOrder.Of(BuildCableOptions.Bind(reader), read, out missing)
        };
        if (reader.HasProblems)
        {
            read.InsertRange(0, reader.Problems);
        }

        foreach (OptionProblem problem in read)
        {
            problems.Add(new OptionProblem($"{ByRun}.{problem.Path}", problem.Problem, problem.Nearest));
        }

        if (missing != null)
        {
            problems.Add(new OptionProblem(ByRun, "names no run: give waypoints, cells, piece or pieces"));
        }

        return problems.Count == before ? order : null;
    }

    // A placement item's keys: the place readers' and tint.
    private static string[] ItemKeys()
    {
        List<string> keys = new List<string>(PlacementReader.PlacementKeys) { TintKey };
        return keys.ToArray();
    }

    // The placements the call names: the list read item by item, or the one placement's own fields at the top level.
    // Nothing at all is no problem: the call may draw only cells and boxes.
    // A label is a ghost's tooltip, kept by index (labels), never sent to the planner: a frame or wall the Labeller
    // cannot name would make every such placement a problem.
    internal static List<PlacementRequest> Placements(GivenPlacements options, Dictionary<int, Rgba> tints,
        Dictionary<int, string> labels, List<OptionProblem> problems)
    {
        List<PlacementRequest> placements = new List<PlacementRequest>();
        if (options.Placements != null)
        {
            BesideTheList(options, problems);
            Items(options.Placements, placements, tints, labels, options.LabelForAll ? options.Label : null,
                options.Spec, problems);
            return placements;
        }

        One(options, placements, problems);
        if (placements.Count > 0 && options.Label != null)
        {
            labels[0] = options.Label;
        }

        return placements;
    }

    // A placement's own field beside a placements list belongs in each item instead.
    private static void BesideTheList(GivenPlacements options, List<OptionProblem> problems)
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
    private static bool Given(GivenPlacements options, string field) => field switch
    {
        "prefab" => options.Prefab != null,
        "at" => options.At != null,
        "rotation" => options.Rotation != null,
        "facing" => options.Facing != null,
        "up" => options.Up != null,
        "face" => options.Face != null,
        "orient" => options.Orient != null,
        "above_floor" => options.AboveFloor.HasValue,
        "build_state" => options.BuildState != null,
        "label" => options.Label != null && !options.LabelForAll,
        _ => false
    };

    // The placements list: each item read as a placement, 1 to MaximumPlacements of them, with its own tint and label.
    private static void Items(JToken given, List<PlacementRequest> placements, Dictionary<int, Rgba> tints,
        Dictionary<int, string> labels, string? label, WhatSpec spec, List<OptionProblem> problems)
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
            if (!(items[index] is JObject entry))
            {
                problems.Add(new OptionProblem(path, "is an object: {prefab, at, ...}"));
                continue;
            }

            int before = problems.Count;
            NestedKeys.Check(entry, path, PlacementItemKeys, problems);
            if (problems.Count > before)
            {
                continue;
            }

            JObject item = entry;
            if (entry["label"] is { } ownLabel)
            {
                item = (JObject)entry.DeepClone();
                item.Remove("label");
                if (ownLabel.Type == JTokenType.String)
                {
                    labels[index] = (string)ownLabel!;
                }
                else
                {
                    problems.Add(new OptionProblem($"{path}.label", "is text: what the ghost is"));
                }
            }
            else if (label != null)
            {
                labels[index] = label;
            }

            if (entry[TintKey] is { } own)
            {
                item = (JObject)item.DeepClone();
                item.Remove(TintKey);
                if (GhostTints.TryRead(own, $"{path}.{TintKey}", problems, out Rgba tint))
                {
                    tints[index] = tint;
                }
            }

            PlacementOrders.Add(placements, item, index, path + ".", spec, problems);
        }
    }

    // The one placement's own fields at the top level; a call that gives none is no problem (it may draw only cells
    // and boxes). The prefab first: a * pattern is no placement, and reading one would only repeat that it is missing.
    private static void One(GivenPlacements options, List<PlacementRequest> placements, List<OptionProblem> problems)
    {
        JObject item = new JObject();
        if (!PlacementOrders.Prefab(options.Prefab, ByPrefab, item, problems))
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
            PlacementOrders.Add(placements, item, 0, string.Empty, options.Spec, problems);
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

    // A box's colour: the five box names as they always were, else any tint name or [r, g, b] / [r, g, b, a] (a box is
    // a line, so opaque unless an alpha is given); none is yellow.
    private static bool BoxColour(JToken? given, string path, List<OptionProblem> problems, out Rgba colour)
    {
        if (given == null || given.Type == JTokenType.Null)
        {
            colour = PreviewColours.Cell;
            return true;
        }

        if (given.Type == JTokenType.String && PreviewColours.TryRead(given.Value<string>(), out colour))
        {
            return true;
        }

        bool alpha = given is JArray { Count: 4 };
        if (!GhostTints.TryRead(given, path, problems, out Rgba tint))
        {
            colour = default;
            return false;
        }

        colour = alpha ? tint : new Rgba(tint.Red, tint.Green, tint.Blue, 1f);
        return true;
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

            if (!BoxColour(item["color"], path + ".color", problems, out Rgba tint))
            {
                continue;
            }

            boxes.Add(new PreviewBox(new Box3(new Vec3(first.X, first.Y, first.Z), new Vec3(second.X, second.Y, second.Z)),
                tint, (float)seconds, "box", xray));
        }

        return boxes;
    }
}


/// <summary>
/// The placement fields a call gives (show preview's, show ghost's): the placements list, or one placement's own
/// fields at the top level. With LabelForAll a top-level label goes beside a list, into each placement without one.
/// </summary>
internal sealed class GivenPlacements
{
    internal WhatSpec Spec { get; private set; } = ShowPreviewOptions.Spec;

    internal JToken? Placements { get; private set; }

    internal PrefabFilter? Prefab { get; private set; }

    internal JToken? At { get; private set; }

    internal JToken? Rotation { get; private set; }

    internal string? Facing { get; private set; }

    internal string? Up { get; private set; }

    internal string? Face { get; private set; }

    internal JToken? Orient { get; private set; }

    internal double? AboveFloor { get; private set; }

    internal JToken? BuildState { get; private set; }

    internal string? Label { get; private set; }

    internal bool LabelForAll { get; private set; }

    internal static GivenPlacements Of(ShowPreviewOptions options) => new GivenPlacements
    {
        Spec = ShowPreviewOptions.Spec,
        Placements = options.Placements,
        Prefab = options.Selector.Prefab,
        At = options.At,
        Rotation = options.Rotation,
        Facing = options.Facing,
        Up = options.Up,
        Face = options.Face,
        Orient = options.Orient,
        AboveFloor = options.AboveFloor,
        BuildState = options.BuildState,
        Label = options.Label
    };

    internal static GivenPlacements Of(ShowGhostOptions options) => new GivenPlacements
    {
        Spec = ShowGhostOptions.Spec,
        Placements = options.Placements,
        Prefab = options.Selector.Prefab,
        At = options.At,
        Rotation = options.Rotation,
        Facing = options.Facing,
        Up = options.Up,
        Face = options.Face,
        Orient = options.Orient,
        AboveFloor = options.AboveFloor,
        BuildState = options.BuildState,
        Label = options.Label,
        LabelForAll = true
    };
}
