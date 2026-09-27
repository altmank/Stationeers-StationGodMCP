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
/// (several ports of a device) grow one tree: the first start routes to the target, each other start to the nearest
/// cell of the tree so far (a branch, joined by a junction), never to the target again, so no loop is made.
/// frames_first (default on) makes cells in air (CellSupports: on no frame and no wall plane) cost
/// RouteRuleSet.AirPenalty more, so a route over frames or along walls wins whenever the search box holds one; the
/// route's new cells in air are counted (air_cells) and listed, with a through_air note. Returns the route, the
/// arguments that build it with the place tool, and that tool's dry run. Read only.
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
        HashSet<long> ignore = new HashSet<long>();
        List<ThingId> removes = new List<ThingId>();
        foreach (SmallGrid piece in segment?.Pieces ?? new List<SmallGrid>())
        {
            ignore.Add(piece.ReferenceId);
            removes.Add(new ThingId(piece.ReferenceId));
        }

        int type = kind.EndType(grade);
        List<RouteEndpoint> starts = segment != null
            ? new List<RouteEndpoint> { new RouteEndpoint(RouteEnd.Open(segment.First), NetworksOf(kind, segment)) }
            : RouteEnds.Starts(args.Optional("from"), "from", kind, type, ignore);
        RouteEndpoint to = segment != null
            ? new RouteEndpoint(RouteEnd.Open(segment.Last), NetworksOf(kind, segment))
            : RouteEnds.Target(args.Optional("to"), "to", kind, type, ignore);
        Kit kit = KitCatalogue.Of(kind.Family).For(grade) ??
                  throw ApiErrors.Refused("no_kit", $"No coil or kit places {kind.NameOf(grade)} pieces.");
        SmallGridBlock mask = kit.Pieces.Count > 0 && kit.Pieces[0] is SmallGrid first
            ? first.SmallCollisionType
            : SmallGridBlock.None;
        GridFacts facts = new GridFacts(kind, mask, ignore);
        RouteRuleSet rules = Rules(args, starts, to, false);
        List<string> notes = Notes(rules);
        RouteTree tree = Grow(args, starts, to, facts, rules);
        bool supportedSearched = rules.FramesFirst;
        if (tree.GaveUp && rules.FramesFirst)
        {
            // frames_first never costs a route the plain search finds: past the air field's box size the search can
            // run out of cells looking for a supported way that does not exist.
            RouteRuleSet plain = rules.WithoutFramesFirst();
            RouteTree second = Grow(args, starts, to, facts, plain);
            if (second.Failure == null)
            {
                tree = second;
                supportedSearched = false;
                notes.Add("frames_first gave up (search_limit) looking for a route over frames or along walls; this " +
                          "route ignores frames_first. Lower margin_m or bring the ends closer to try again.");
            }
        }

        if (tree.Failure != null)
        {
            return new PlanRouteView(tool, null, tree.Failure, null, null, notes);
        }

        RunReportView dryRun = DryRun(args, kind, grade, tree, removes);
        if (HasWarning(dryRun, RunPlanner.WouldLoop))
        {
            if (Shared(starts, to))
            {
                notes.Add("would_loop: the start and the target are already on one network, so any route between " +
                          "them closes a loop. To reach another port of a device already on the network, give both " +
                          "ports in from (from: {reference_id, ports: [...]}) so they share one run.");
            }
            else if (RunArgs.Join(args) == JoinMode.All)
            {
                RouteTree retry = Grow(args, starts, to, facts, Rules(args, starts, to, true));
                RunReportView? second = retry.Failure == null ? DryRun(args, kind, grade, retry, removes) : null;
                if (second != null && !HasWarning(second, RunPlanner.WouldLoop))
                {
                    tree = retry;
                    dryRun = second;
                    notes.Add("would_loop: the first route closed a loop; this one keeps away from the ends' own " +
                              "networks except where it starts and ends.");
                }
            }
        }

        List<GridCell> air = AirCells(tree, facts);
        if (air.Count > 0)
        {
            notes.Add(supportedSearched
                ? $"through_air: {air.Count} new cells float in air (on no frame and no wall plane); no route over " +
                  "frames or along walls exists inside the search box. Raise margin_m to look wider, or build a " +
                  "frame under the gap."
                : $"through_air: {air.Count} new cells float in air; this route was found without frames_first.");
        }

        JObject place = PlaceArguments(args, kind, grade, tree, removes);
        return new PlanRouteView(tool, ViewOf(tree, removes, air), null, place, dryRun, notes);
    }

    // The main route from the first start to the target, then each other start to the nearest cell of the tree.
    private static RouteTree Grow(Args args, List<RouteEndpoint> starts, RouteEndpoint to, GridFacts facts,
        RouteRuleSet rules)
    {
        RouteEndpoint origin = starts[0];
        RouteRules search = SearchRules(args, origin.End.Cell, to.NearestTo(origin.End.Cell).Cell);
        RouteResult main = RoutePlanner.FindAny(origin.End, to.Ends, cell => rules.Cost(facts.Small(cell)), search,
            AirBoundOf(rules, facts, search, to.Ends));
        if (main.Cells == null)
        {
            return RouteTree.Failed(Failure(main, starts.Count > 1 ? "from[0]" : null), main.Failure);
        }

        RouteTree tree = new RouteTree(main.Cells, main.Cost, main.Expanded);
        for (int index = 1; index < starts.Count; index++)
        {
            RouteEndpoint start = starts[index];
            if (tree.Contains(start.End.Cell))
            {
                if (start.IntoDevice.HasValue)
                {
                    tree.AddExtra(new ExtraEnd(start.End.Cell, start.IntoDevice.Value));
                }

                continue;
            }

            List<RouteEnd> goals = tree.Cells.FindAll(cell => tree.EndsAt(cell) < 3)
                .ConvertAll(cell => new RouteEnd(cell, EndSet.None, RouteEnds.JunctionCost));
            RouteEndpoint treeEnds = new RouteEndpoint(goals, new List<long>());
            RouteRules branchSearch = SearchRules(args, start.End.Cell, treeEnds.NearestTo(start.End.Cell).Cell);
            RouteResult branch = RoutePlanner.FindAny(start.End, goals,
                cell => tree.Contains(cell) ? CellCost.Blocked : rules.Cost(facts.Small(cell)), branchSearch,
                AirBoundOf(rules, facts, branchSearch, goals));
            if (branch.Cells == null)
            {
                return RouteTree.Failed(Failure(branch, $"from[{index}]"), branch.Failure);
            }

            tree.Add(branch);
        }

        return tree;
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

    // The tree's new cells (not a piece of the kind already) that no frame or wall holds up.
    private static List<GridCell> AirCells(RouteTree tree, GridFacts facts) =>
        tree.Cells.FindAll(cell => !facts.Small(cell).FamilyPiece && facts.Support(cell) == CellSupport.Air);

    private static RunReportView DryRun(Args args, RunKind kind, Grade grade, RouteTree tree, List<ThingId> removes)
    {
        RunShape shape = RunShape.Of(tree.Main, tree.Branches, out string? error) ??
                         throw new InvalidOperationException(error);
        RunRequest request = new RunRequest(kind, kind.PlaceTool,
            new RunBuild(shape, grade, RunArgs.Join(args), tree.Extra),
            new RunRemoval(removes, new List<GridCell>()), RunArgs.Options(args));
        return RunReports.Of(RunPlanner.Plan(request), RunReports.DryRun, null);
    }

    private static bool HasWarning(RunReportView report, string code) =>
        report.Warnings.Exists(warning => warning.Code == code);

    private static bool Shared(List<RouteEndpoint> starts, RouteEndpoint to)
    {
        foreach (RouteEndpoint start in starts)
        {
            if (start.Networks.Exists(to.Networks.Contains))
            {
                return true;
            }
        }

        return false;
    }

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

    private static RouteRuleSet Rules(Args args, List<RouteEndpoint> starts, RouteEndpoint to, bool avoidOwn)
    {
        string prefer = (args.OptionalString("prefer") ?? "none").Trim().ToLowerInvariant();
        RoutePreference preference = prefer switch
        {
            "none" => RoutePreference.None,
            "frame_edges" or "frame_corners" => RoutePreference.FrameEdges,
            "walls" => RoutePreference.Walls,
            _ => throw ApiErrors.InvalidArgument("prefer must be none, frame_edges or walls.")
        };
        HashSet<long> own = new HashSet<long>(to.Networks);
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
        List<ThingId> removes)
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
            JArray ids = new JArray();
            foreach (ThingId id in removes)
            {
                ids.Add(id.ToString());
            }

            place["remove_ids"] = ids;
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

    private static RouteView ViewOf(RouteTree tree, List<ThingId> removes, List<GridCell> air)
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

        return new RouteView(Positions(tree.Main), tree.Main.Count, RunPath.Bends(tree.Main), tree.Cost,
            tree.Expanded, removes, air.Count,
            air.Count > 0 ? air.ConvertAll(cell => GameLookup.ViewOf(PieceShapes.CentreOf(cell))) : null, branches,
            tree.Extra.Count > 0 ? tree.Extra.Count : (int?)null);
    }

    private static List<PositionView> Positions(IReadOnlyList<GridCell> cells) =>
        RunPath.Waypoints(cells).ConvertAll(cell => GameLookup.ViewOf(PieceShapes.CentreOf(cell)));

    private static string Failure(RouteResult result, string? which)
    {
        string prefix = which != null ? $"{which}: " : string.Empty;
        return prefix + result.Failure switch
        {
            "too_long" => "too_long: every way under the rules is longer than max_length.",
            "search_limit" => $"search_limit: gave up after {result.Expanded} cells; bring the ends closer or " +
                              "lower margin_m.",
            _ => "no_route: no way between the ends under the rules inside the search box (margin_m around the " +
                 "ends); loosen a rule, raise margin_m, or check the ends with grid_survey."
        };
    }

    private static List<string> Notes(RouteRuleSet rules)
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

        return notes;
    }

    private static double Round(float value) => Math.Round(value * 100.0) / 100.0;

    /// <summary>The route found so far: the main run, its branches, extra ends for ports it passes, or a failure.</summary>
    private sealed class RouteTree
    {
        private readonly HashSet<GridCell> _cells = new HashSet<GridCell>();
        private readonly Dictionary<GridCell, int> _ends = new Dictionary<GridCell, int>();

        internal RouteTree(List<GridCell> main, double cost, int expanded)
        {
            Main = main;
            Cost = cost;
            Expanded = expanded;
            Cells.AddRange(main);
            _cells.UnionWith(main);
            CountEnds(main);
        }

        private RouteTree(string failure, string? reason)
        {
            Main = new List<GridCell>();
            Failure = failure;
            GaveUp = reason == "search_limit";
        }

        internal List<GridCell> Main { get; }

        internal List<RunBranch> Branches { get; } = new List<RunBranch>();

        internal List<ExtraEnd> Extra { get; } = new List<ExtraEnd>();

        /// <summary>Every cell of the tree: the main run's, then each branch's.</summary>
        internal List<GridCell> Cells { get; } = new List<GridCell>();

        internal double Cost { get; private set; }

        internal int Expanded { get; private set; }

        internal string? Failure { get; }

        internal static RouteTree Failed(string failure, string? reason) => new RouteTree(failure, reason);

        /// <summary>The search gave up (search_limit) rather than finding no route.</summary>
        internal bool GaveUp { get; }

        internal bool Contains(GridCell cell) => _cells.Contains(cell);

        /// <summary>How many ends the piece in the cell has so far (a branch never lands on a junction already).</summary>
        internal int EndsAt(GridCell cell) => _ends.TryGetValue(cell, out int count) ? count : 0;

        internal void AddExtra(ExtraEnd end)
        {
            Extra.Add(end);
            Count(end.Cell);
        }

        // A branch search ends on a tree cell: the cells before it are the branch, attached to that cell.
        internal void Add(RouteResult branch)
        {
            List<GridCell> path = branch.Cells!;
            List<GridCell> cells = path.GetRange(0, path.Count - 1);
            Branches.Add(new RunBranch(cells, path[path.Count - 1]));
            Cells.AddRange(cells);
            _cells.UnionWith(cells);
            CountEnds(path);
            Cost += branch.Cost;
            Expanded += branch.Expanded;
        }

        // Each step of a path gives an end to both of its cells.
        private void CountEnds(List<GridCell> path)
        {
            for (int index = 1; index < path.Count; index++)
            {
                Count(path[index - 1]);
                Count(path[index]);
            }
        }

        private void Count(GridCell cell) => _ends[cell] = EndsAt(cell) + 1;
    }
}
