#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// build structure's call, checked without the game: the placements (at most 64) or one top-level placement (prefab,
/// at, rotation, facing, up, face, orient, above_floor, build_state, label, color); spread ({from, to, step} or a list
/// of them) standing in for at; who pays (materials) and whether the build is free; allow door_keepout, lint and pad;
/// include notes and footprint_cells; verbose, acknowledge_gas_lost (checked), confirm and queue. Every placement's
/// fields are read by the shared place readers (PlacementReader), so the checks are the ones the old place surface made; the
/// what's label prefixes every refusal ("build structure: ...").
/// </summary>
internal sealed class StructureBuildOrder
{
    internal const int MaximumPlacements = PlaceRequest.MaximumPlacements;

    /// <summary>The two ways the call names its placements: the list, or one at the top level.</summary>
    internal const string ByPlacements = "placements";

    internal const string ByPrefab = "prefab";

    /// <summary>How far short of to a spread step may land and still be to: world coordinates such as 128.2 - 118.2
    /// come out a few 1e-14 m under the distance the caller wrote.</summary>
    private const double LandsOnToMetres = 1e-6;

    /// <summary>A placement's own fields, at the top level or in an item (above_floor read here: the shared reader
    /// knows it as above_floor).</summary>
    private static readonly string[] PlacementFields =
    {
        "prefab", "at", "rotation", "facing", "up", "face", "orient", "above_floor", "build_state", "label", "color",
        "logic"
    };

    private StructureBuildOrder(WhatSpec spec)
    {
        Label = spec.Label;
        Mark = spec.Write!.UndoMark;
    }

    /// <summary>The call's label, as its refusals prefix it: "build structure".</summary>
    public string Label { get; }

    /// <summary>The what's undo mark, as its report rows carry it ("[undo]").</summary>
    public string Mark { get; }

    /// <summary>Every placement, in order: the list's items, the one at the top level, or spread's points.</summary>
    public List<PlacementRequest> Placements { get; } = new List<PlacementRequest>();

    /// <summary>Who pays; a placement refunds nothing, so the refund route is the default.</summary>
    public MaterialsSource Materials { get; private set; } = MaterialsSource.Player;

    /// <summary>free: place without materials (a cheat).</summary>
    public bool Free { get; private set; }

    /// <summary>allow door_keepout: a placement in a door's keep-out is a warning instead of a problem.</summary>
    public bool AllowDoorKeepOut { get; private set; }

    /// <summary>allow lint: a lint problem is a warning and the rows about it say PASSED.</summary>
    public bool AllowLint { get; private set; }

    /// <summary>allow pad: a placement in a landing pad's approach is a warning and its row says PASSED.</summary>
    public bool AllowPad { get; private set; }

    /// <summary>include footprint_cells: each layout preview lists its footprint's cells, not only a count.</summary>
    public bool FootprintCells { get; private set; }

    /// <summary>include notes (the what's fixed explanations) and verbose, as the call gives them.</summary>
    public Fields Fields { get; private set; } = Fields.None;

    /// <summary>confirm: whether this call acts, and how many rows a report lists.</summary>
    public ReportConfirm Report { get; private set; } = ReportConfirm.Acting;

    /// <summary>queue: start behind a job already running, instead of answering busy while one is.</summary>
    public bool Queue { get; private set; } = true;

    /// <summary>acknowledge_gas_lost, as the call gives it.</summary>
    public string? AcknowledgeGasLost { get; private set; }

    /// <summary>
    /// The order build structure's options give; null with every problem added, or with missing set when the call
    /// names no placement at all.
    /// </summary>
    internal static StructureBuildOrder? Of(BuildStructureOptions options, List<OptionProblem> problems,
        out VerbError? missing)
    {
        StructureBuildOrder order = new StructureBuildOrder(BuildStructureOptions.Spec);
        order.Materials = options.Materials;
        order.Free = options.Free ?? false;
        order.Fields = options.Fields;
        order.FootprintCells = options.Fields.Includes("footprint_cells");
        order.Report = options.Report;
        order.Queue = options.Queue ?? true;
        order.AcknowledgeGasLost = options.AcknowledgeGasLost;
        order.AllowDoorKeepOut = Allow.Lifts(options.Allow, "door_keepout");
        order.AllowLint = Allow.Lifts(options.Allow, WriteLint.Guard);
        order.AllowPad = Allow.Lifts(options.Allow, PadApproachRule.Guard);
        int before = problems.Count;
        GasHoldArgs.Check(order.AcknowledgeGasLost, problems);
        missing = order.Read(options, problems);
        return problems.Count == before && missing == null ? order : null;
    }

    /// <summary>
    /// The order undo's placement replay starts: the placements the job put back, who pays (the undo's from_id,
    /// else the player), whether the call acts (confirm) or only reports, whether it queues behind the removal, and
    /// the caller's acknowledge_gas_lost. Nothing is lifted: a replay gives no allow. Null with every problem added.
    /// </summary>
    internal static StructureBuildOrder? Replay(JArray placements, string? fromId, bool acts, bool queue,
        string? acknowledgeGasLost, List<OptionProblem> problems)
    {
        StructureBuildOrder order = new StructureBuildOrder(BuildStructureOptions.Spec);
        if (fromId != null)
        {
            order.Materials = new MaterialsSource(new Payer[] { new Payer.Holder(fromId) }, RefundRoute.Default);
        }

        order.Report = new ReportConfirm(true, acts, null);
        order.Queue = queue;
        order.AcknowledgeGasLost = acknowledgeGasLost;
        order.Items(placements, problems);
        return problems.Count == 0 ? order : null;
    }

    /// <summary>The request the game plans: the placements, who pays, and what the call includes.</summary>
    internal PlaceRequest Arguments(ThingId? from, List<ThingId> moreFrom) =>
        new PlaceRequest(Placements, from, Free, AllowDoorKeepOut, FootprintCells,
            new ReportDetail(Fields.Includes("notes"), Fields.Verbose), moreFrom, AllowLint, AllowPad);

    // The placements the call names: the list, or the one placement's own fields at the top level (spread standing in
    // for at). Null with missing set when it names none.
    private VerbError? Read(BuildStructureOptions options, List<OptionProblem> problems)
    {
        if (options.Placements != null)
        {
            BesidePlacements(options, problems);
            Items(options.Placements, problems);
            return null;
        }

        return One(options, problems);
    }

    // A placement's own field beside a placements list belongs in each item instead.
    private static void BesidePlacements(BuildStructureOptions options, List<OptionProblem> problems)
    {
        foreach (string field in PlacementFields)
        {
            if (Given(options, field))
            {
                problems.Add(new OptionProblem(field,
                    $"goes without {ByPlacements}: give it in each placement ({ByPlacements}[0].{field})"));
            }
        }

        if (options.Spread != null)
        {
            problems.Add(new OptionProblem("spread",
                $"goes without {ByPlacements}: it makes the one top-level placement's at"));
        }
    }

    // Whether the call gives a placement field at the top level.
    private static bool Given(BuildStructureOptions options, string field) => field switch
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
        "label" => options.Label != null,
        "color" => options.Color != null,
        "logic" => options.Logic != null,
        _ => false
    };

    // The placements list: each item read as a placement, at most the what's MaximumPlacements of them.
    private void Items(JToken given, List<OptionProblem> problems)
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

            if (item["spread"] != null)
            {
                problems.Add(new OptionProblem($"{path}.spread",
                    $"goes with the one top-level placement, not with {ByPlacements}"));
                continue;
            }

            PlacementOrders.Add(Placements, item, index, path + ".", BuildStructureOptions.Spec, problems);
        }
    }

    // The one placement's own fields at the top level, or spread's points sharing them. Null with missing set when
    // the call gives none of them.
    private VerbError? One(BuildStructureOptions options, List<OptionProblem> problems)
    {
        JObject item = new JObject();
        if (!PlacementOrders.Prefab(options.Selector.Prefab, ByPrefab, item, problems))
        {
            return null;
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

        if (options.Label != null)
        {
            item["label"] = options.Label;
        }

        if (options.Color != null)
        {
            item["color"] = options.Color.DeepClone();
        }

        if (options.Logic != null)
        {
            item["logic"] = options.Logic.DeepClone();
        }

        if (item.Count == 0)
        {
            return new VerbError(RemoveRunOrders.MissingTarget,
                $"{Label}: name the placements ({ByPlacements}), or one placement by {ByPrefab} with at.",
                new JObject { ["options"] = new JArray(ByPlacements, ByPrefab) });
        }

        if (options.Spread == null)
        {
            PlacementOrders.Add(Placements, item, 0, string.Empty, BuildStructureOptions.Spec, problems);
            return null;
        }

        if (options.At != null)
        {
            problems.Add(new OptionProblem("spread", "goes without at: it makes the at positions"));
        }

        List<JArray>? points = Spread(options.Spread, problems);
        for (int index = 0; points != null && index < points.Count; index++)
        {
            item["at"] = points[index];
            PlacementOrders.Add(Placements, item, index, string.Empty, BuildStructureOptions.Spec, problems);
        }

        return null;
    }

    // spread: {from, to, step} or a list of them. Each line's points are from + k*step along it: from included, to
    // only when a step lands on it exactly. Every point is one placement.
    private static List<JArray>? Spread(JToken given, List<OptionProblem> problems)
    {
        List<JArray> points = new List<JArray>();
        if (given is JObject line)
        {
            Line(line, "spread", points, problems);
            return points;
        }

        if (given is JArray lines && lines.Count > 0)
        {
            for (int index = 0; index < lines.Count; index++)
            {
                if (lines[index] is JObject one)
                {
                    Line(one, $"spread[{index}]", points, problems);
                }
                else
                {
                    problems.Add(new OptionProblem($"spread[{index}]", "is {from, to, step}"));
                }
            }

            return points;
        }

        problems.Add(new OptionProblem("spread", "is {from, to, step}, or a list of them"));
        return null;
    }

    private static readonly string[] SpreadKeys = { "from", "to", "step" };

    // One spread line. Together with the lines before it it may make at most the what's MaximumPlacements placements.
    private static void Line(JObject line, string path, List<JArray> points, List<OptionProblem> problems)
    {
        int before = problems.Count;
        NestedKeys.Check(line, path, SpreadKeys, problems);
        if (problems.Count > before)
        {
            return;
        }

        JToken? from = line["from"];
        JToken? to = line["to"];
        double step = line["step"] is { Type: JTokenType.Integer or JTokenType.Float } given
            ? given.Value<double>()
            : double.NaN;
        if (from == null || to == null || double.IsNaN(step))
        {
            problems.Add(new OptionProblem(path, "is {from: [x, y, z], to: [x, y, z], step: metres}"));
            return;
        }

        if (!(step > 0.0) || double.IsInfinity(step))
        {
            problems.Add(new OptionProblem($"{path}.step", "is more than 0 metres"));
            return;
        }

        if (!PlacementOrders.Position(from, $"{path}.from", problems, out Metres start) ||
            !PlacementOrders.Position(to, $"{path}.to", problems, out Metres end))
        {
            return;
        }

        double distance = Distance(start, end);
        long count = distance <= 0.0 ? 1 : (long)Math.Floor((distance + LandsOnToMetres) / step) + 1;
        if (points.Count + count > MaximumPlacements)
        {
            problems.Add(new OptionProblem(path, $"makes {points.Count + count} placements with the lines before " +
                $"it; the most is {MaximumPlacements}"));
            return;
        }

        for (long index = 0; index < count; index++)
        {
            double covered = index * step;
            double along = covered >= distance ? 1.0 : covered / distance;
            points.Add(new JArray(Along(start.X, end.X, along), Along(start.Y, end.Y, along),
                Along(start.Z, end.Z, along)));
        }
    }

    // A point along the line: exactly from at its start, and exactly to where a step lands on it.
    private static double Along(double start, double end, double along) =>
        along <= 0.0 ? start : along >= 1.0 ? end : start + (end - start) * along;

    private static double Distance(Metres from, Metres to)
    {
        double x = to.X - from.X;
        double y = to.Y - from.Y;
        double z = to.Z - from.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

}
