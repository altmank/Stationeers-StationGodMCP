#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>What plan route found: the route or why none was, the build options that lay it, build's planned view of it, and the notes.</summary>
internal sealed class PlannedRoute
{
    internal PlannedRoute(RouteView? route, string? failure, JObject? buildArguments, RunReportView? report,
        List<string> notes)
    {
        Route = route;
        Failure = failure;
        BuildArguments = buildArguments;
        Report = report;
        Notes = notes;
    }

    internal RouteView? Route { get; }

    internal string? Failure { get; }

    internal JObject? BuildArguments { get; }

    /// <summary>The build's planned view (RunPlanner), its cell list cut to rows; null when no route was found.</summary>
    internal RunReportView? Report { get; }

    internal List<string> Notes { get; }

    internal static PlannedRoute Failed(string failure, List<string> notes) =>
        new PlannedRoute(null, failure, null, null, notes);
}

/// <summary>
/// plan route's search: the cheapest route on the small grid between two ends under a rule set (RoutePlanner, costs
/// from RouteRuleSet over GridFacts), or a replacement for an old run (reroute: its pieces are removed in the same job
/// and the route runs between the two cells where it met the rest). Several starts (several ports of a device) grow
/// one tree (RouteTrees): the first start routes to the end, each other start to the nearest cell of the tree so far (a
/// branch, joined by a junction), never to the end again, so no loop is made. trunk (bus mode) lays a given run
/// instead of searching the main one, and every start branches from it. assume_removed plans as if things were already
/// gone (their cells free, their links absent); the kind's own pieces among them are removed in the same job
/// (build_arguments.remove_ids). style (RouteStyle, default supported) makes cells in air (on no frame and no wall
/// plane) cost RouteRuleSet.AirPenalty more, so a route over frames or along walls wins whenever the search box holds
/// one; when that search gives up, the plain search runs once. A route whose build would close a loop under join all
/// is searched once more keeping away from the ends' own networks. The route's new cells are counted by visibility and
/// the air cells listed. The build's planned view is RunPlanner's over the same request build would make. Read only.
/// </summary>
internal static class RoutePlanning
{
    private const double BendCost = 2.0;
    private const double MinimumBendsCost = 25.0;

    /// <summary>
    /// The route as a Work body: the ends and rules in one step, each search a batch of expansions per step
    /// (RouteTrees.Growing), build's report in steps of its own (RunPlanner.Planning).
    /// </summary>
    internal static IEnumerable<object?> Planning(RouteOrder order, string label, Answer<PlannedRoute> answer)
    {
        RunKind kind = RunKind.Named(order.Kind);
        NetworkTarget? joinTo = order.JoinTo ?? JoinTarget(order.End, kind);
        Grade grade = GradeOf(order.Grade, kind);
        RerouteSegment? segment = Segment(order, kind);
        List<GridCell>? trunk = Trunk(order);
        AssumedRemovals assumed = AssumedRemovals.Read(order.AssumeRemoved, kind);
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
            : RouteEnds.Starts(order.Start, "start", kind, type, ignore);
        RouteEndpoint? end = segment != null
            ? new RouteEndpoint(RouteEnd.Open(segment.Last), NetworksOf(kind, segment))
            : trunk == null
                ? RouteEnds.Target(order.End, "end", kind, type, ignore)
                : null;
        RouteMain main = end != null ? new RouteMain.ToTarget(end) : new RouteMain.Trunk(trunk!);
        Kit kit = WorldModel.Prefabs.KitsOf(kind.Family).For(grade) ??
                  throw ApiErrors.Refused("no_kit", $"No coil or kit places {kind.NameOf(grade)} pieces.");
        SmallGridBlock mask = kit.Pieces.Count > 0 && kit.Pieces[0] is SmallGrid first
            ? first.SmallCollisionType
            : SmallGridBlock.None;
        if (segment == null)
        {
            RefuseBlockedPoints(order.Start, "start", kind, mask, ignore);
            RefuseBlockedPoints(order.End, "end", kind, mask, ignore);
        }

        GridFacts facts = new GridFacts(kind, mask, ignore);
        List<long> own = end?.Networks ?? new List<long>();
        RouteLintRule? lintRule = LintRuleOf(kind, starts, end);
        RouteRuleSet rules = Rules(order, kind, starts, own, false).WithInsideRule(lintRule);
        List<string> notes = Notes(rules, assumed);
        if (lintRule != null)
        {
            notes.Add($"lint rule {lintRule.Id} is in effect: cells outside a frame's body (the ends and the cells " +
                      "next to them excepted) are " + (lintRule.Refuses ? "refused" : "avoided (a warning-level rule)") +
                      ", whatever the style. Lower its level or set \"enabled\": false in the save's lint-rules.json " +
                      "to route through a room.");
        }

        RouteReservation reserved = Reservation(order, starts, end, trunk, notes);
        OpeningGuard openings = Openings(order, facts, starts, end, notes);
        yield return null;
        Answer<RouteTree> grown = new Answer<RouteTree>();
        foreach (object? step in Grow(order, starts, main, facts, rules, reserved, openings, grown))
        {
            yield return step;
        }

        RouteTree tree = grown.Value;
        bool supportedSearched = rules.FramesFirst;
        if (tree.GaveUp && rules.FramesFirst)
        {
            // The air penalty never costs a route the plain search finds: past the air field's box size the search can
            // run out of cells looking for a supported way that does not exist.
            foreach (object? step in Grow(order, starts, main, facts, rules.WithoutFramesFirst(), reserved, openings,
                         grown))
            {
                yield return step;
            }

            RouteTree second = grown.Value;
            if (second.Found)
            {
                tree = second;
                supportedSearched = false;
                notes.Add("The style's air penalty gave up (search_limit) looking for a route over frames or along " +
                          "walls; this route ignores it (as style free). Lower margin or bring the ends closer to " +
                          "try again.");
            }
        }

        if (!tree.Found)
        {
            answer.Value = PlannedRoute.Failed(Failure(tree), notes);
            yield break;
        }

        ReleasedNote(openings, tree, notes);
        string? blocked = BlockedCell(NewCells(tree, facts), kind, mask, ignore);
        if (blocked != null)
        {
            answer.Value = PlannedRoute.Failed(blocked, notes);
            yield break;
        }

        RunOptions options = Options(order, kind, joinTo, label);
        Answer<RunReportView> reported = new Answer<RunReportView>();
        foreach (object? step in BuildReport(kind, grade, order.Join, tree, removes, assumed, options, reported))
        {
            yield return step;
        }

        RunReportView report = reported.Value;
        if (HasWarning(report, RunPlanner.WouldLoop))
        {
            if (end != null && Shared(starts, end))
            {
                notes.Add("would_loop: the start and the end are already on one network, so any route between them " +
                          "closes a loop. To reach another port of a device already on the network, give both ports " +
                          "in start (start: {id, ports: [...]}) so they share one run.");
            }
            else if (order.Join == JoinMode.All)
            {
                foreach (object? step in Grow(order, starts, main, facts,
                             Rules(order, kind, starts, own, true).WithInsideRule(lintRule), reserved, openings, grown))
                {
                    yield return step;
                }

                RouteTree retry = grown.Value;
                RunReportView? second = null;
                if (retry.Found)
                {
                    foreach (object? step in BuildReport(kind, grade, order.Join, retry, removes, assumed, options,
                                 reported))
                    {
                        yield return step;
                    }

                    second = reported.Value;
                }

                if (second != null && !HasWarning(second, RunPlanner.WouldLoop))
                {
                    tree = retry;
                    report = second;
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
                  $"over frames or along walls of at most max_length ({order.MaxLength}) cells exists inside the " +
                  "search box. Raise margin to look wider, raise max_length when the way round is longer, or build " +
                  "a frame under the gap."
                : $"through_air: {visibility.Air} new cells float in air; this route was found with style free.");
        }

        RouteAssumedView? assumedView = AssumedView(assumed, tree, notes);
        JObject build = RouteBuildArguments.Of(order, kind.NameOf(grade), tree, removes, Ids(assumed.Others), joinTo,
            CentreOf);
        RouteView route = ViewOf(tree, removes, visibility, assumedView, RemovalRefund(segment, assumed));
        answer.Value = new PlannedRoute(route, null, build, report, notes);
    }

    private static Vec3 CentreOf(GridCell cell)
    {
        Vector3 centre = PieceShapes.CentreOf(cell);
        return new Vec3(centre.x, centre.y, centre.z);
    }

    // The main route from the first start to the end (or the trunk as given), then each other start to the nearest
    // cell of the tree.
    private static IEnumerable<object?> Grow(RouteOrder order, List<RouteEndpoint> starts, RouteMain main,
        GridFacts facts, RouteRuleSet rules, RouteReservation reserved, OpeningGuard openings,
        Answer<RouteTree> answer) =>
        RouteTrees.Growing(starts, main,
            new RouteSearch(openings.Guard(reserved.Guard(cell => rules.Cost(facts.Small(cell), cell))),
                (from, to) => SearchRules(order, from, to), (search, goals) => AirBoundOf(rules, facts, search, goals)),
            RouteEnds.JunctionCost, answer);

    private static Grade GradeOf(string? name, RunKind kind)
    {
        string? given = name ?? kind.DefaultGrade;
        if (given == null)
        {
            throw ApiErrors.InvalidArgument(
                $"grade is required for a {kind.Noun} route: one of {string.Join(", ", kind.GradeNames)}.");
        }

        return kind.GradeOf(given) ?? throw ApiErrors.InvalidArgument(
            $"grade: a {kind.Noun} route takes one of {string.Join(", ", kind.GradeNames)}, not {given}.");
    }

    /// <summary>
    /// The network the route is meant to reach when join_to is left out: end's network, or the network at end's piece
    /// or device port when a piece of the kind is joined there; null when end names neither, or nothing of the kind is
    /// joined there yet. build then checks the route really joins it (not_joined).
    /// </summary>
    private static NetworkTarget? JoinTarget(JToken? end, RunKind kind)
    {
        if (!(end is JObject given))
        {
            return null;
        }

        NetworkTarget? target = given["network"] is { } network
            ? NetworkTarget.Of(network)
            : ThingId.TryRead(given["id"], out ThingId id)
                ? new NetworkTarget(id.ToString(), given["port"] is { Type: JTokenType.Integer } port
                    ? port.ToString()
                    : null)
                : null;
        if (target == null)
        {
            return null;
        }

        try
        {
            NetworkHandles.Resolve(target, "end", kind.Family);
            return target;
        }
        catch (ApiException)
        {
            // An open port or a device with no network of the kind yet: nothing to join, nothing to check.
            return null;
        }
    }

    /// <summary>
    /// The run options build takes with the same call: allow_bridge and join_to resolved to networks (join_to must name
    /// one), root, join_trunk, the holders that pay, rows cells listed, split_long, door_keepout and include.
    /// </summary>
    private static RunOptions Options(RouteOrder order, RunKind kind, NetworkTarget? joinTo, string label)
    {
        HashSet<long> bridge = NetworkHandles.ResolveAllowances(order.AllowBridge, "allow_bridge", kind.Family,
            kind.Bridges);
        ThingId? join = joinTo != null ? NetworkHandles.Resolve(joinTo, "join_to", kind.Family) : null;
        if (join.HasValue)
        {
            // A handle naming nothing is taken as a network id: it must be one (network_not_found otherwise).
            kind.Family.NetworkMembers(join.Value);
        }

        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(order.Materials, label, more);
        Fields fields = order.Fields;
        return new RunOptions(new EditAllowance(bridge, order.AllowSplit), from, order.Materials.RefundTo, order.Rows,
            order.SplitLong, new RunTargets(order.Root, join, order.JoinTrunk), order.AllowDoorKeepOut,
            new RunDetail(fields.Includes("links"), fields.Includes("notes"), fields.Includes("network_devices")),
            more);
    }

    /// <summary>
    /// reserve_cells (positions) and reserve_ports ({id, port}: the cell a piece joining that port stands in) as cells
    /// the search treats as blocked, so this run leaves them for another; any that is one of this route's own ends is
    /// released, with a note.
    /// </summary>
    private static RouteReservation Reservation(RouteOrder order, List<RouteEndpoint> starts, RouteEndpoint? end,
        List<GridCell>? trunk, List<string> notes)
    {
        if (!order.Reserves)
        {
            return RouteReservation.None;
        }

        List<GridCell> cells = Cells(order.ReserveCells, "reserve_cells");
        for (int index = 0; index < order.ReservePorts.Count; index++)
        {
            RoutePort port = order.ReservePorts[index];
            cells.Add(RouteEnds.JoiningCell(GameLookup.RequireThing(port.Id), port.Port, $"reserve_ports[{index}]"));
        }

        RouteReservation reserved = RouteReservation.Of(cells, EndCells(starts, end));
        notes.Add($"reserve: {reserved.Count} cell(s) kept free (the search treats them as blocked).");
        if (reserved.Released.Count > 0)
        {
            notes.Add($"reserve: {reserved.Released.Count} reserved cell(s) are this route's own ends and were not " +
                      "blocked: " + string.Join(", ", reserved.Released.ConvertAll(cell => PieceShapes.CentreOf(cell)
                          .ToString())) + ".");
        }

        int crossed = trunk?.FindAll(reserved.Contains).Count ?? 0;
        if (crossed > 0)
        {
            notes.Add($"reserve: the trunk is laid as given and passes {crossed} reserved cell(s); only the drops " +
                      "keep clear of them.");
        }

        return reserved;
    }

    // The positions given, each snapped to its small cell.
    private static List<GridCell> Cells(JToken? given, string option)
    {
        JArray points = given as JArray ?? new JArray();
        List<GridCell> cells = new List<GridCell>(points.Count);
        for (int index = 0; index < points.Count; index++)
        {
            cells.Add(RunArgs.CellOf(RunArgs.PositionOf(points[index], $"{option}[{index}]")));
        }

        return cells;
    }

    private static List<GridCell> EndCells(List<RouteEndpoint> starts, RouteEndpoint? end)
    {
        List<GridCell> ends = new List<GridCell>();
        foreach (RouteEndpoint start in starts)
        {
            ends.AddRange(CellsOf(start.Ends));
        }

        if (end != null)
        {
            ends.AddRange(CellsOf(end.Ends));
        }

        return ends;
    }

    /// <summary>
    /// Every door's keep-out ([Layout] DoorKeepOutBand either side of its face, inside its rectangle) blocked unless
    /// allow door_keepout, and window cells made dearer (OpeningGuard); the route's own end cells are released.
    /// </summary>
    private static OpeningGuard Openings(RouteOrder order, GridFacts facts, List<RouteEndpoint> starts,
        RouteEndpoint? end, List<string> notes)
    {
        bool allow = order.AllowDoorKeepOut;
        RouteLintRule? windows = RouteLintRule.From(Lint.LintRuleFiles.Current(), Pure.Lint.LintRoute.OffWindows,
            new GridCell[0]);
        OpeningGuard guard = new OpeningGuard(facts.Opening, EndCells(starts, end), allow,
            RouteLintRule.WindowsUnder(windows));
        notes.Add(allow
            ? "allow: [\"door_keepout\"]: the route may pass through doorways (their face and the band either side)."
            : FormattableString.Invariant($"Doors: the route keeps out of every door's face and {facts.Band.Metres} m either side of it inside ") +
              "the door's rectangle (jambs, top edge and threshold; not inside the floor slab).");
        notes.Add(guard.Windows switch
        {
            WindowRule.Refuse => $"Windows: lint rule {windows!.Id} (level problem) keeps the route off every " +
                                 "window's plane, seams, edges and base included. Lower its level or turn it off in " +
                                 "the save's lint-rules.json to allow it.",
            WindowRule.Penalty => FormattableString.Invariant(
                $"Windows: a cell on a window's plane costs {OpeningGuard.WindowPenalty} more ({windows!.Id} warns)."),
            _ => "Windows: no lint rule with route off_windows is in effect; a window's plane is like any other."
        });
        return guard;
    }

    // The route's own end cells in a door's keep-out, which the search did not block: only those on or beside the
    // route, since an end network's cells far from it say nothing about this route.
    private static void ReleasedNote(OpeningGuard openings, RouteTree tree, List<string> notes)
    {
        List<GridCell> released = openings.ReleasedInKeepOutBeside(tree.Cells);
        if (released.Count > 0)
        {
            notes.Add($"door keep-out: {released.Count} of this route's own end cells stand in a door's keep-out " +
                      "and were not blocked: " + string.Join(", ", released.ConvertAll(cell =>
                          PieceShapes.CentreOf(cell).ToString())) + ".");
        }
    }

    /// <summary>trunk: its waypoints or cells as a run every start branches from; null without one.</summary>
    private static List<GridCell>? Trunk(RouteOrder order)
    {
        if (order.Trunk == null)
        {
            return null;
        }

        string option = order.TrunkByCells ? "trunk.cells" : "trunk.waypoints";
        List<GridCell> points = Cells(order.Trunk, option);
        string? error;
        List<GridCell>? cells = order.TrunkByCells
            ? RunPath.FromCells(points, out error)
            : RunPath.FromWaypoints(points, out error);
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
            ? "assume_removed: the route takes none of the assumed pieces' cells, so it can also be built while they " +
              "still stand (leave remove_ids out of build_arguments and remove them in a later job)."
            : $"assume_removed: the route takes the cells of {inTheWay.Count} assumed piece(s) (in_the_way): they " +
              "go in the same job (build_arguments.remove_ids) or before it.");
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

    // build's planned view of the route: RunPlanner over the request build would make of build_arguments.
    private static IEnumerable<object?> BuildReport(RunKind kind, Grade grade, JoinMode join, RouteTree tree,
        List<ThingId> removes, AssumedRemovals assumed, RunOptions options, Answer<RunReportView> answer)
    {
        RunShape shape = RunShape.Of(tree.Main, tree.Branches, out string? error) ??
                         throw ApiErrors.InvalidArgument(error ?? "The route's shape is not valid.");
        RunRequest request = new RunRequest(kind, kind.PlaceTool, new RunBuild(shape, grade, join, tree.Extra),
            new RunRemoval(removes, new List<GridCell>(), Ids(assumed.Others)), options);
        Answer<RunPlan> plan = new Answer<RunPlan>();
        foreach (object? step in RunPlanner.Planning(request, true, plan))
        {
            yield return step;
        }

        answer.Value = RunReports.Of(plan.Value, RunReports.DryRun, null);
    }

    private static bool HasWarning(RunReportView report, string code) =>
        report.Warnings.Exists(warning => warning.Code == code);

    private static bool Shared(List<RouteEndpoint> starts, RouteEndpoint end) =>
        starts.Exists(start => start.Networks.Exists(end.Networks.Contains));

    private static RerouteSegment? Segment(RouteOrder order, RunKind kind)
    {
        if (order.ReroutePieces != null)
        {
            return kind.Ordered(Reroutes.OfPieces(kind, new List<ThingId>(order.ReroutePieces)));
        }

        return order.RerouteBetween != null
            ? kind.Ordered(Reroutes.Between(kind, order.RerouteBetween[0], order.RerouteBetween[1]))
            : null;
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

    // An {at} end in a cell no piece of the kit may take (a device or a locker stands there) could never be built: it is
    // refused here rather than found as a route build then refuses (cell_blocked).
    private static void RefuseBlockedPoints(JToken? token, string name, RunKind kind, SmallGridBlock mask,
        HashSet<long> ignore)
    {
        if (token is JArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                RefuseBlockedPoints(array[index], $"{name}[{index}]", kind, mask, ignore);
            }

            return;
        }

        if (!(token is JObject item) || item["at"] == null)
        {
            return;
        }

        GridCell cell = RunArgs.CellOf(RunArgs.PositionOf(item["at"]!, $"{name}.at"));
        string? blocked = PlacementCheck.CellBlocked(PieceShapes.Grid(cell), mask, kind, ignore);
        if (blocked != null)
        {
            throw ApiErrors.InvalidArgument(
                $"{name}.at: no {kind.Noun} piece can stand in cell {cell}: {blocked}. Name a port with {{id, port}}, " +
                "or a free cell.");
        }
    }

    // The lint rule in effect a route of the kind honours: route inside_frames, for chutes.
    private static RouteLintRule? LintRuleOf(RunKind kind, List<RouteEndpoint> starts, RouteEndpoint? end)
    {
        if (!(kind is ChuteRunKind))
        {
            return null;
        }

        List<GridCell> ends = new List<GridCell>();
        foreach (RouteEndpoint endpoint in end != null ? new List<RouteEndpoint>(starts) { end } : starts)
        {
            foreach (RouteEnd routeEnd in endpoint.Ends)
            {
                ends.Add(routeEnd.Cell);
            }
        }

        return RouteLintRule.From(Lint.LintRuleFiles.Current(), Pure.Lint.LintRoute.InsideFrames, ends);
    }

    private static RouteRuleSet Rules(RouteOrder order, RunKind kind, List<RouteEndpoint> starts, List<long> target,
        bool avoidOwn)
    {
        HashSet<long> own = new HashSet<long>(target);
        foreach (RouteEndpoint start in starts)
        {
            own.UnionWith(start.Networks);
        }

        RouteStyle style = order.Style;
        return new RouteRuleSet(style.Prefer, style.InsideFrames, order.AvoidRoomInterior, order.AvoidWalkways,
            order.AvoidAllNetworks, own, AvoidedNetworks(order, kind), avoidOwn, style.FramesFirst);
    }

    // avoid_networks as a list: network handles, each naming a network of the kind (network_not_found otherwise).
    private static HashSet<long> AvoidedNetworks(RouteOrder order, RunKind kind)
    {
        HashSet<long> ids = new HashSet<long>();
        for (int index = 0; index < order.AvoidNetworks.Count; index++)
        {
            string entry = $"avoid_networks[{index}]";
            ThingId network = NetworkHandles.Resolve(order.AvoidNetworks[index], entry, kind.Family);
            try
            {
                kind.Family.NetworkMembers(network);
            }
            catch (ApiException missing) when (missing.Code == "network_not_found")
            {
                throw ApiErrors.Refused(missing.Code, $"{entry}: {network} names no {kind.Noun} network.");
            }

            ids.Add(network.Value);
        }

        return ids;
    }

    private static RouteRules SearchRules(RouteOrder order, GridCell from, GridCell to)
    {
        int pad = (int)Math.Ceiling(order.MarginM * 10.0 / GridStep.CellSize) * GridStep.CellSize;
        GridCell min = new GridCell(Math.Min(from.X, to.X) - pad, Math.Min(from.Y, to.Y) - pad,
            Math.Min(from.Z, to.Z) - pad);
        GridCell max = new GridCell(Math.Max(from.X, to.X) + pad, Math.Max(from.Y, to.Y) + pad,
            Math.Max(from.Z, to.Z) + pad);
        return new RouteRules(order.MinBends ? MinimumBendsCost : BendCost, order.AxisOrder, order.MaxLength, min,
            max);
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

    // A route is found only when every new piece may stand where it goes. The search keeps its way clear, but an end
    // cell is taken as given: a port whose joining cell another device fills (a battery pushed against it) would yield
    // a route build then refuses.
    private static string? BlockedCell(List<GridCell> cells, RunKind kind, SmallGridBlock mask, HashSet<long> ignore)
    {
        foreach (GridCell cell in cells)
        {
            string? blocked = PlacementCheck.CellBlocked(PieceShapes.Grid(cell), mask, kind, ignore);
            if (blocked != null)
            {
                return $"cell_blocked: no {kind.Noun} piece can stand in cell {cell} of the route (an end's joining " +
                       $"cell): {blocked}. Clear it, or route from another port or a free cell.";
            }
        }

        return null;
    }

    private static string Failure(RouteTree tree)
    {
        string prefix = tree.FailedAt != null ? $"{tree.FailedAt}: " : string.Empty;
        return prefix + tree.Failure switch
        {
            "too_long" => "too_long: every way under the rules is longer than max_length.",
            "search_limit" => $"search_limit: gave up after {tree.Expanded} cells; bring the ends closer or lower " +
                              "margin.",
            _ => "no_route: no way between the ends under the rules inside the search box (margin around the ends); " +
                 "loosen a rule, raise margin, plan as if old pieces in the way were gone (assume_removed), or check " +
                 "the ends with survey area."
        };
    }

    private static List<string> Notes(RouteRuleSet rules, AssumedRemovals assumed)
    {
        List<string> notes = new List<string>
        {
            "The route never passes through a cell holding a piece of its kind (that would join it), a device or " +
            "chute, nor along the axis of a pipe (for cables) or cable (for pipes) in the same cell; frames and " +
            "walls never block it.",
            "Waypoints are cell centres in metres; pass build_arguments to build (it reports until confirm: true). " +
            "build_report is build's own report of it, with the same guards."
        };
        if (rules.AvoidNetworks)
        {
            notes.Add("avoid_networks: no cell beside a piece of another network than the ends' own.");
        }

        if (rules.Prefer == RoutePreference.Hidden)
        {
            notes.Add($"style hidden: a cell inside a frame costs 1, on a frame's surface " +
                      $"{1 + RouteRuleSet.SurfaceCost}, on a wall's plane {1 + RouteRuleSet.WallCost}, in air " +
                      $"{1 + RouteRuleSet.AirCost} (plus the air penalty); route.visibility counts the " +
                      "new cells by class.");
        }

        if (assumed.Missing.Count > 0)
        {
            notes.Add($"assume_removed: {assumed.Missing.Count} id(s) name nothing standing (already gone?): " +
                      string.Join(", ", assumed.Missing) + ".");
        }

        return notes;
    }
}
