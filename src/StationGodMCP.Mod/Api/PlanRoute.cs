#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// plan_cable_route, plan_pipe_route and plan_chute_route: the cheapest route on the small grid between two ends under
/// a rule set (RoutePlanner, costs from RouteRuleSet over GridFacts), or a replacement for an old run (reroute: its
/// pieces are removed in the same job and the route runs between the two cells where it met the rest). Several starts
/// (several ports of a device) grow one tree (RouteTrees): the first start routes to the target, each other start to
/// the nearest cell of the tree so far (a branch, joined by a junction), never to the target again, so no loop is made.
/// trunk (bus mode) lays a given run instead of searching the main one, and every start branches from it: a trunk and
/// its drops in one job. assume_removed plans as if things were already gone (their cells free, their links absent);
/// the kind's own pieces among them are removed in the same job (place_arguments.remove_ids). frames_first (default
/// on) makes cells in air (CellSupports: on no frame and no wall plane) cost RouteRuleSet.AirPenalty more, so a route
/// over frames or along walls wins whenever the search box holds one; prefer hidden grades every cell by how visible
/// it is. The route's new cells are counted by visibility (inside a frame, on its surface, on a wall, in air) and the
/// air cells listed, with a through_air note. Returns the route, the arguments that build it with the place tool, and
/// that tool's dry run. Read only.
/// </summary>
internal static class PlanRouteApi
{
    private const double DefaultMarginM = 6.0;
    private const double MaximumMarginM = 32.0;
    private const int DefaultMaxLength = 400;
    private const double BendCost = 2.0;
    private const double MinimumBendsCost = 25.0;

    internal static PlanRouteView Handle(Args args, RunKind kind)
    {
        string tool = kind.PlanTool;
        Grade grade = RunArgs.Grade(args, kind);
        RerouteSegment? segment = Segment(args, kind);
        List<GridCell>? trunk = Trunk(args);
        AssumedRemovals assumed = AssumedRemovals.Read(args, kind);
        HashSet<long> ignore = new HashSet<long>(assumed.Ids);
        List<ThingId> removes = new List<ThingId>();
        foreach (SmallGrid piece in RemovedPieces(segment, assumed))
        {
            ignore.Add(piece.ReferenceId);
            if (!removes.Exists(id => id.Value == piece.ReferenceId))
            {
                removes.Add(new ThingId(piece.ReferenceId));
            }
        }

        int type = kind.EndType(grade);
        List<RouteEndpoint> starts = segment != null
            ? new List<RouteEndpoint> { new RouteEndpoint(RouteEnd.Open(segment.First), NetworksOf(kind, segment)) }
            : RouteEnds.Starts(args.Optional("from"), "from", kind, type, ignore);
        RouteEndpoint? to = segment != null
            ? new RouteEndpoint(RouteEnd.Open(segment.Last), NetworksOf(kind, segment))
            : trunk == null
                ? RouteEnds.Target(args.Optional("to"), "to", kind, type, ignore)
                : null;
        RouteMain main = to != null ? new RouteMain.ToTarget(to) : new RouteMain.Trunk(trunk!);
        Kit kit = KitCatalogue.Of(kind.Family).For(grade) ??
                  throw ApiErrors.Refused("no_kit", $"No coil or kit places {kind.NameOf(grade)} pieces.");
        SmallGridBlock mask = kit.Pieces.Count > 0 && kit.Pieces[0] is SmallGrid first
            ? first.SmallCollisionType
            : SmallGridBlock.None;
        GridFacts facts = new GridFacts(kind, mask, ignore);
        List<long> own = to?.Networks ?? new List<long>();
        RouteRuleSet rules = Rules(args, starts, own, false);
        List<string> notes = Notes(rules, assumed);
        RouteTree tree = Grow(args, starts, main, facts, rules);
        bool supportedSearched = rules.FramesFirst;
        if (tree.GaveUp && rules.FramesFirst)
        {
            // frames_first never costs a route the plain search finds: past the air field's box size the search can
            // run out of cells looking for a supported way that does not exist.
            RouteTree second = Grow(args, starts, main, facts, rules.WithoutFramesFirst());
            if (second.Found)
            {
                tree = second;
                supportedSearched = false;
                notes.Add("frames_first gave up (search_limit) looking for a route over frames or along walls; this " +
                          "route ignores frames_first. Lower margin_m or bring the ends closer to try again.");
            }
        }

        if (!tree.Found)
        {
            return new PlanRouteView(tool, null, Failure(tree), null, null, notes);
        }

        RunReportView dryRun = DryRun(args, kind, grade, tree, removes, assumed);
        if (HasWarning(dryRun, RunPlanner.WouldLoop))
        {
            if (to != null && Shared(starts, to))
            {
                notes.Add("would_loop: the start and the target are already on one network, so any route between " +
                          "them closes a loop. To reach another port of a device already on the network, give both " +
                          "ports in from (from: {reference_id, ports: [...]}) so they share one run.");
            }
            else if (RunArgs.Join(args) == JoinMode.All)
            {
                RouteTree retry = Grow(args, starts, main, facts, Rules(args, starts, own, true));
                RunReportView? second = retry.Found ? DryRun(args, kind, grade, retry, removes, assumed) : null;
                if (second != null && !HasWarning(second, RunPlanner.WouldLoop))
                {
                    tree = retry;
                    dryRun = second;
                    notes.Add("would_loop: the first route closed a loop; this one keeps away from the ends' own " +
                              "networks except where it starts and ends.");
                }
            }
        }

        VisibilityTally visibility = VisibilityTally.Of(NewCells(tree, facts), facts.Visibility);
        if (visibility.Air > 0)
        {
            notes.Add(supportedSearched
                ? $"through_air: {visibility.Air} new cells float in air (on no frame and no wall plane); no route " +
                  "over frames or along walls exists inside the search box. Raise margin_m to look wider, or build " +
                  "a frame under the gap."
                : $"through_air: {visibility.Air} new cells float in air; this route was found without frames_first.");
        }

        RouteAssumedView? assumedView = AssumedView(assumed, tree, notes);
        JObject place = PlaceArguments(args, kind, grade, tree, removes, assumed);
        return new PlanRouteView(tool,
            ViewOf(tree, removes, visibility, assumedView, RemovalRefund(segment, assumed)), null, place, dryRun,
            notes);
    }

    // The main route from the first start to the target (or the trunk as given), then each other start to the
    // nearest cell of the tree.
    private static RouteTree Grow(Args args, List<RouteEndpoint> starts, RouteMain main, GridFacts facts,
        RouteRuleSet rules) =>
        RouteTrees.Grow(starts, main,
            new RouteSearch(cell => rules.Cost(facts.Small(cell)), (from, to) => SearchRules(args, from, to),
                (search, goals) => AirBoundOf(rules, facts, search, goals)),
            RouteEnds.JunctionCost);

    /// <summary>trunk: {waypoints} or {cells}, a run laid as given that every start branches from; null without one.</summary>
    private static List<GridCell>? Trunk(Args args)
    {
        JObject? trunk = args.OptionalObject("trunk");
        if (trunk == null)
        {
            return null;
        }

        if (args.Has("to") || args.Has("reroute"))
        {
            throw ApiErrors.InvalidArgument("trunk replaces to and does not go with reroute: every start branches " +
                                            "from the trunk, whose own ends join what they meet.");
        }

        if (!args.Has("from"))
        {
            throw ApiErrors.InvalidArgument("trunk needs from: the drops (ports or cells) that branch from it.");
        }

        Args fields = new Args(trunk);
        if (fields.Has("waypoints") == fields.Has("cells"))
        {
            throw ApiErrors.InvalidArgument("trunk needs waypoints or cells (one of them).");
        }

        string? error;
        List<GridCell>? cells = fields.Has("waypoints")
            ? RunPath.FromWaypoints(RunArgs.Cells(fields, "waypoints"), out error)
            : RunPath.FromCells(RunArgs.Cells(fields, "cells"), out error);
        return cells ?? throw ApiErrors.InvalidArgument($"trunk: {error}");
    }

    // The tree's cells not already holding a piece of the kind: what the route adds.
    private static List<GridCell> NewCells(RouteTree tree, GridFacts facts) =>
        tree.Cells.FindAll(cell => !facts.Small(cell).FamilyPiece);

    // What removing the reroute's old run and the kind's assumed pieces gives back.
    private static List<UpgradeAmountView> RemovalRefund(RerouteSegment? segment, AssumedRemovals assumed)
    {
        List<ItemAmount> refund = new List<ItemAmount>();
        HashSet<long> seen = new HashSet<long>();
        foreach (SmallGrid piece in RemovedPieces(segment, assumed))
        {
            if (seen.Add(piece.ReferenceId))
            {
                refund.AddRange(BuildMaterials.RefundOf(piece));
            }
        }

        return RunReports.Amounts(refund);
    }

    private static RouteAssumedView? AssumedView(AssumedRemovals assumed, RouteTree tree, List<string> notes)
    {
        if (assumed.IsEmpty)
        {
            return null;
        }

        Dictionary<long, IReadOnlyList<GridCell>> cells = new Dictionary<long, IReadOnlyList<GridCell>>();
        List<SmallGrid> things = new List<SmallGrid>(assumed.Pieces);
        things.AddRange(assumed.Others);
        foreach (SmallGrid thing in things)
        {
            cells[thing.ReferenceId] = PieceShapes.Live(thing).Cells;
        }

        List<long> inTheWay = RemovedInTheWay.Of(tree.Cells, cells);
        notes.Add(inTheWay.Count == 0
            ? "assume_removed: the route takes none of the assumed pieces' cells, so it can also be built while " +
              "they still stand (leave remove_ids out of place_arguments and remove them in a later job)."
            : $"assume_removed: the route takes the cells of {inTheWay.Count} assumed piece(s) (in_the_way): they " +
              "go in the same job (place_arguments.remove_ids) or before it.");
        return new RouteAssumedView(Ids(assumed.Pieces), Ids(assumed.Others), assumed.Missing,
            inTheWay.ConvertAll(id => new ThingId(id)));
    }

    private static List<ThingId> Ids(List<SmallGrid> things) =>
        things.ConvertAll(thing => new ThingId(thing.ReferenceId));

    // The reroute's old run, then the kind's assumed pieces.
    private static List<SmallGrid> RemovedPieces(RerouteSegment? segment, AssumedRemovals assumed)
    {
        List<SmallGrid> pieces = new List<SmallGrid>(segment?.Pieces ?? new List<SmallGrid>());
        pieces.AddRange(assumed.Pieces);
        return pieces;
    }

    private static AirBound? AirBoundOf(RouteRuleSet rules, GridFacts facts, RouteRules search,
        IReadOnlyList<RouteEnd> goals) =>
        rules.FramesFirst
            ? AirBound.Build(search.Min, search.Max, CellsOf(goals), facts.Support, facts.Anchors,
                RouteRuleSet.AirPenalty)
            : null;

    private static List<GridCell> CellsOf(IReadOnlyList<RouteEnd> ends)
    {
        List<GridCell> cells = new List<GridCell>(ends.Count);
        foreach (RouteEnd end in ends)
        {
            cells.Add(end.Cell);
        }

        return cells;
    }

    private static RunReportView DryRun(Args args, RunKind kind, Grade grade, RouteTree tree, List<ThingId> removes,
        AssumedRemovals assumed)
    {
        RunShape shape = RunShape.Of(tree.Main, tree.Branches, out string? error) ??
                         throw ApiErrors.InvalidArgument(error ?? "The route's shape is not valid.");
        RunRequest request = new RunRequest(kind, kind.PlaceTool,
            new RunBuild(shape, grade, RunArgs.Join(args), tree.Extra),
            new RunRemoval(removes, new List<GridCell>(), Ids(assumed.Others)), RunArgs.Options(args));
        return RunReports.Of(RunPlanner.Plan(request), RunReports.DryRun, null);
    }

    private static bool HasWarning(RunReportView report, string code) =>
        report.Warnings.Exists(warning => warning.Code == code);

    private static bool Shared(List<RouteEndpoint> starts, RouteEndpoint to) =>
        starts.Exists(start => start.Networks.Exists(to.Networks.Contains));

    private static RerouteSegment? Segment(Args args, RunKind kind)
    {
        JObject? reroute = args.OptionalObject("reroute");
        if (reroute == null)
        {
            return null;
        }

        if (args.Has("from") || args.Has("to"))
        {
            throw ApiErrors.InvalidArgument("reroute takes its ends from the old run; leave out from and to.");
        }

        Args fields = new Args(reroute);
        if (fields.Has("reference_ids") == fields.Has("between"))
        {
            throw ApiErrors.InvalidArgument(
                "reroute needs reference_ids (the old run's pieces) or between: [a, b] (ids, or {reference_id, port}).");
        }

        if (fields.Has("reference_ids"))
        {
            return kind.Ordered(Reroutes.OfPieces(kind, fields.ThingIds("reference_ids", 1024)));
        }

        List<RerouteEndArg> between = RerouteArgs.Between(reroute["between"]);
        return kind.Ordered(Reroutes.Between(kind, between[0], between[1]));
    }

    private static List<long> NetworksOf(RunKind kind, RerouteSegment segment)
    {
        List<long> networks = new List<long>();
        foreach (SmallGrid piece in segment.Pieces)
        {
            long? id = kind.Family.NetworkOf(piece)?.ReferenceId;
            if (id.HasValue && !networks.Contains(id.Value))
            {
                networks.Add(id.Value);
            }
        }

        return networks;
    }

    private static RouteRuleSet Rules(Args args, List<RouteEndpoint> starts, List<long> target, bool avoidOwn)
    {
        string prefer = (args.OptionalString("prefer") ?? "none").Trim().ToLowerInvariant();
        RoutePreference preference = prefer switch
        {
            "none" => RoutePreference.None,
            "frame_edges" or "frame_corners" => RoutePreference.FrameEdges,
            "walls" => RoutePreference.Walls,
            "hidden" => RoutePreference.Hidden,
            _ => throw ApiErrors.InvalidArgument("prefer must be none, frame_edges, walls or hidden.")
        };
        HashSet<long> own = new HashSet<long>(target);
        foreach (RouteEndpoint start in starts)
        {
            own.UnionWith(start.Networks);
        }

        bool avoidAll = false;
        HashSet<long> avoidIds = new HashSet<long>();
        JToken? avoid = args.Optional("avoid_networks");
        if (avoid != null && avoid.Type == JTokenType.Boolean)
        {
            avoidAll = avoid.Value<bool>();
        }
        else if (avoid != null)
        {
            foreach (ThingId id in args.ThingIds("avoid_networks", 256))
            {
                avoidIds.Add(id.Value);
            }
        }

        return new RouteRuleSet(preference, args.OptionalBool("inside_frames") ?? false,
            args.OptionalBool("avoid_room_interior") ?? false, args.OptionalBool("avoid_walkways") ?? false, avoidAll,
            own, avoidIds, avoidOwn, args.OptionalBool("frames_first") ?? true);
    }

    private static RouteRules SearchRules(Args args, GridCell from, GridCell to)
    {
        double margin = args.OptionalPositiveDouble("margin_m") ?? DefaultMarginM;
        if (margin > MaximumMarginM)
        {
            throw ApiErrors.InvalidArgument($"margin_m is at most {MaximumMarginM}.");
        }

        int pad = (int)Math.Ceiling(margin * 10.0 / GridStep.CellSize) * GridStep.CellSize;
        GridCell min = new GridCell(Math.Min(from.X, to.X) - pad, Math.Min(from.Y, to.Y) - pad,
            Math.Min(from.Z, to.Z) - pad);
        GridCell max = new GridCell(Math.Max(from.X, to.X) + pad, Math.Max(from.Y, to.Y) + pad,
            Math.Max(from.Z, to.Z) + pad);
        string order = (args.OptionalString("axis_order") ?? "any").Trim().ToLowerInvariant();
        AxisOrder axisOrder = order switch
        {
            "any" => AxisOrder.Any,
            "vertical_first" => AxisOrder.VerticalFirst,
            "horizontal_first" => AxisOrder.HorizontalFirst,
            _ => throw ApiErrors.InvalidArgument("axis_order must be any, vertical_first or horizontal_first.")
        };
        return new RouteRules((args.OptionalBool("min_bends") ?? false) ? MinimumBendsCost : BendCost, axisOrder,
            args.OptionalInt("max_length", 2, RunPath.MaximumCells) ?? DefaultMaxLength, min, max);
    }

    private static JObject PlaceArguments(Args args, RunKind kind, Grade grade, RouteTree tree,
        List<ThingId> removes, AssumedRemovals assumed)
    {
        JObject place = new JObject
        {
            ["waypoints"] = Points(tree.Main),
            ["grade"] = kind.NameOf(grade),
            ["join"] = (args.OptionalString("join") ?? "ends").Trim().ToLowerInvariant()
        };
        if (tree.Branches.Count > 0)
        {
            JArray branches = new JArray();
            foreach (RunBranch branch in tree.Branches)
            {
                branches.Add(new JObject
                {
                    ["waypoints"] = Points(branch.Cells),
                    ["attach"] = Point(branch.Attach!.Value)
                });
            }

            place["branches"] = branches;
        }

        if (tree.Extra.Count > 0)
        {
            JArray extra = new JArray();
            foreach (ExtraEnd end in tree.Extra)
            {
                extra.Add(new JObject { ["at"] = Point(end.Cell), ["toward"] = end.Step.Name });
            }

            place["extra_ends"] = extra;
        }

        if (removes.Count > 0)
        {
            place["remove_ids"] = IdArray(removes);
        }

        if (assumed.Others.Count > 0)
        {
            place["assume_removed"] = IdArray(Ids(assumed.Others));
        }

        foreach (string name in new[] { "allow_bridge", "allow_split", "allow_split_long", "from_id" })
        {
            JToken? value = args.Optional(name);
            if (value != null)
            {
                place[name] = value.DeepClone();
            }
        }

        return place;
    }

    private static JArray IdArray(List<ThingId> ids)
    {
        JArray array = new JArray();
        foreach (ThingId id in ids)
        {
            array.Add(id.ToString());
        }

        return array;
    }

    private static JArray Points(IReadOnlyList<GridCell> cells)
    {
        JArray points = new JArray();
        foreach (GridCell cell in RunPath.Waypoints(cells))
        {
            points.Add(Point(cell));
        }

        return points;
    }

    private static JArray Point(GridCell cell)
    {
        Vector3 centre = PieceShapes.CentreOf(cell);
        return new JArray(Round(centre.x), Round(centre.y), Round(centre.z));
    }

    private static RouteView ViewOf(RouteTree tree, List<ThingId> removes, VisibilityTally visibility,
        RouteAssumedView? assumed, List<UpgradeAmountView> refund)
    {
        List<RouteBranchView>? branches = null;
        if (tree.Branches.Count > 0)
        {
            branches = new List<RouteBranchView>(tree.Branches.Count);
            foreach (RunBranch branch in tree.Branches)
            {
                branches.Add(new RouteBranchView(Positions(branch.Cells), branch.Cells.Count,
                    GameLookup.ViewOf(PieceShapes.CentreOf(branch.Attach!.Value))));
            }
        }

        List<GridCell> air = visibility.AirCells;
        return new RouteView(Positions(tree.Main), tree.Main.Count, RunPath.Bends(tree.Main), tree.Cost,
            tree.Expanded, removes, air.Count,
            air.Count > 0 ? air.ConvertAll(cell => GameLookup.ViewOf(PieceShapes.CentreOf(cell))) : null, branches,
            tree.Extra.Count > 0 ? tree.Extra.Count : (int?)null,
            new RouteVisibilityView(visibility.Inside, visibility.FrameSurface, visibility.Wall, visibility.Air),
            assumed, refund.Count > 0 ? refund : null);
    }

    private static List<PositionView> Positions(IReadOnlyList<GridCell> cells) =>
        RunPath.Waypoints(cells).ConvertAll(cell => GameLookup.ViewOf(PieceShapes.CentreOf(cell)));

    private static string Failure(RouteTree tree)
    {
        string prefix = tree.FailedAt != null ? $"{tree.FailedAt}: " : string.Empty;
        return prefix + tree.Failure switch
        {
            "too_long" => "too_long: every way under the rules is longer than max_length.",
            "search_limit" => $"search_limit: gave up after {tree.Expanded} cells; bring the ends closer or " +
                              "lower margin_m.",
            _ => "no_route: no way between the ends under the rules inside the search box (margin_m around the " +
                 "ends); loosen a rule, raise margin_m, plan as if old pieces in the way were gone " +
                 "(assume_removed), or check the ends with grid_survey."
        };
    }

    private static List<string> Notes(RouteRuleSet rules, AssumedRemovals assumed)
    {
        List<string> notes = new List<string>
        {
            "The route never passes through a cell holding a piece of its kind (that would join it), a device or " +
            "chute, nor along the axis of a pipe (for cables) or cable (for pipes) in the same cell; frames and " +
            "walls never block it.",
            "Waypoints are cell centres in metres; pass place_arguments to the place tool (add dry_run false and " +
            "confirm true to build). The dry run is that tool's own, with the same guards."
        };
        if (rules.AvoidNetworks)
        {
            notes.Add("avoid_networks: no cell beside a piece of another network than the ends' own.");
        }

        if (rules.Prefer == RoutePreference.Hidden)
        {
            notes.Add($"prefer hidden: a cell inside a frame costs 1, on a frame's surface " +
                      $"{1 + RouteRuleSet.SurfaceCost}, on a wall's plane {1 + RouteRuleSet.WallCost}, in air " +
                      $"{1 + RouteRuleSet.AirCost} (plus frames_first's air penalty); route.visibility counts the " +
                      "new cells by class.");
        }

        if (assumed.Missing.Count > 0)
        {
            notes.Add($"assume_removed: {assumed.Missing.Count} id(s) name nothing standing (already gone?): " +
                      string.Join(", ", assumed.Missing) + ".");
        }

        return notes;
    }

    private static double Round(float value) => Math.Round(value * 100.0) / 100.0;
}
