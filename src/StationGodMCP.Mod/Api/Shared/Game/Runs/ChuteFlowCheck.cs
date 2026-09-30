#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The chute pieces and device chute ports of every network an edit touches, as they are before it: each piece as the
/// connectivity model reads it (PieceShapes.Live), each device port with its ConnectionRole. Networks too large to
/// read whole are not read (TooLarge), and the flow is then left unchecked.
/// </summary>
internal sealed class ChuteSurroundings
{
    internal const int MaximumPieces = 4096;

    private ChuteSurroundings(Dictionary<long, PieceModel> before, List<DevicePort> ports, bool tooLarge)
    {
        Before = before;
        Ports = ports;
        TooLarge = tooLarge;
    }

    internal Dictionary<long, PieceModel> Before { get; }

    internal List<DevicePort> Ports { get; }

    internal bool TooLarge { get; }

    /// <summary>
    /// The pieces and devices given, every member and device of the chute networks any of those pieces is on, and
    /// the ports of every device among them.
    /// </summary>
    internal static ChuteSurroundings Of(IEnumerable<SmallGrid> seeds)
    {
        Dictionary<long, SmallGrid> pieces = new Dictionary<long, SmallGrid>();
        Dictionary<long, Device> devices = new Dictionary<long, Device>();
        Dictionary<long, ChuteNetwork> networks = new Dictionary<long, ChuteNetwork>();
        foreach (SmallGrid seed in seeds)
        {
            Take(seed, pieces, devices, networks);
        }

        int count = pieces.Count;
        foreach (ChuteNetwork network in networks.Values)
        {
            count += network.StructureList.Count;
        }

        if (count > MaximumPieces)
        {
            return new ChuteSurroundings(new Dictionary<long, PieceModel>(), new List<DevicePort>(), true);
        }

        foreach (ChuteNetwork network in new List<ChuteNetwork>(networks.Values))
        {
            foreach (INetworkedStructure member in RunNetworks.Copy(network.StructureList))
            {
                if (member.GetAsThing is SmallGrid grid)
                {
                    Take(grid, pieces, devices, networks);
                }
            }

            foreach (Device device in RunNetworks.Copy(network.DeviceList))
            {
                Take(device, pieces, devices, networks);
            }
        }

        Dictionary<long, PieceModel> before = new Dictionary<long, PieceModel>(pieces.Count);
        foreach (SmallGrid piece in pieces.Values)
        {
            before[piece.ReferenceId] = PieceShapes.Live(piece);
        }

        List<DevicePort> ports = new List<DevicePort>();
        foreach (Device device in devices.Values)
        {
            AddPorts(ports, device);
        }

        return new ChuteSurroundings(before, ports, false);
    }

    private static void Take(SmallGrid thing, Dictionary<long, SmallGrid> pieces, Dictionary<long, Device> devices,
        Dictionary<long, ChuteNetwork> networks)
    {
        if (thing == null || thing.IsBeingDestroyed)
        {
            return;
        }

        if (thing is Chute chute)
        {
            pieces[chute.ReferenceId] = chute;
            if (chute.ChuteNetwork != null)
            {
                networks[chute.ChuteNetwork.ReferenceId] = chute.ChuteNetwork;
            }
        }
        else if (thing is Device device)
        {
            devices[device.ReferenceId] = device;
        }
    }

    private static void AddPorts(List<DevicePort> ports, Device device)
    {
        if (device.OpenEnds == null)
        {
            return;
        }

        for (int index = 0; index < device.OpenEnds.Count; index++)
        {
            Connection end = device.OpenEnds[index];
            if (end?.Transform == null || (end.ConnectionType & NetworkType.Chute) == NetworkType.None)
            {
                continue;
            }

            ports.Add(new DevicePort(device.ReferenceId, index,
                new PieceEnd(PieceShapes.Cell(end.GetLocalGrid()), PieceShapes.Cell(end.GetFacingGrid()),
                    (int)end.ConnectionType,
                    TwoWayChutePorts.RoleOf(device, (int)end.ConnectionType, (int)end.ConnectionRole)), false));
        }
    }
}

/// <summary>
/// The chute flow of an edit (ChuteFlow over ChuteSurroundings): which way each junction must face, whether the
/// edit makes items meet head-on, enter a piece through its output or run against the run's own direction (items
/// travel from the run's first cell to its last), where items would fall out of an open end, and which new pieces
/// nothing gives a direction.
/// </summary>
internal static class ChuteFlowCheck
{
    internal const int MaximumOrientable = 6;
    internal const string Ambiguous = "flow_ambiguous";
    internal const string DropsItems = "drops_items";
    internal const string Unknown = "flow_unknown";
    internal const string Unchecked = "flow_unchecked";

    /// <summary>
    /// For each cell a junction must stand in, the turn under which the whole flow holds: the one turn every
    /// conflict-free combination agrees on. None holds: the first turn (the check reports the conflict). Several do:
    /// flow_ambiguous, since nothing says which way items should leave.
    /// </summary>
    internal static Dictionary<GridCell, RunChoice> Orient(RunPlan plan, List<OrientableCell> cells)
    {
        Dictionary<GridCell, RunChoice> picked = new Dictionary<GridCell, RunChoice>();
        foreach (OrientableCell cell in cells)
        {
            picked[cell.Layout.Cell] = cell.Options[0];
        }

        ChuteSurroundings around = ChuteSurroundings.Of(Seeds(plan));
        if (around.TooLarge)
        {
            plan.Warnings.Add(new LayoutIssue(Unchecked,
                $"The chute networks here have more than {ChuteSurroundings.MaximumPieces} pieces; each junction " +
                "takes its first turn unchecked. Check its flow in grid_survey after building."));
            return picked;
        }

        if (cells.Count > MaximumOrientable)
        {
            plan.Problem(Ambiguous,
                $"{cells.Count} junctions in one edit; at most {MaximumOrientable}. Build this in smaller steps.");
            return picked;
        }

        TurnVerdict[] verdicts = Verdicts(plan, around, cells);
        for (int index = 0; index < cells.Count; index++)
        {
            OrientableCell cell = cells[index];
            if (verdicts[index].Agreement == TurnAgreement.Unique)
            {
                picked[cell.Layout.Cell] = cell.Options[verdicts[index].Turn];
            }
            else if (verdicts[index].Agreement == TurnAgreement.Several)
            {
                plan.Problem(Ambiguous,
                    $"A chute junction must stand in {cell.Layout.Cell}, and nothing around fixes which way items " +
                    "should leave it (no device port or directed piece gives the flow). Join a device's chute port " +
                    "or a line whose direction is known, or lay the runs one at a time from their sources.",
                    cell.Layout.Existing?.Id, cell.Layout.Cell);
            }
        }

        return picked;
    }

    /// <summary>Adds the flow's problems and warnings to the plan, and each touched cell's flow for the report.</summary>
    internal static void Check(RunPlan plan)
    {
        ChuteSurroundings around = ChuteSurroundings.Of(Seeds(plan));
        if (around.TooLarge)
        {
            plan.Warnings.Add(new LayoutIssue(Unchecked,
                $"The chute networks here have more than {ChuteSurroundings.MaximumPieces} pieces; the item flow " +
                "was not checked."));
            return;
        }

        HashSet<long> edited = EditedIds(plan);
        ChuteFlowResult before = ChuteFlow.Solve(new ChuteFlowGraph(new List<PieceModel>(around.Before.Values),
            around.Ports, new HashSet<long>(), new List<RunLeg>()));
        List<PieceModel> after = After(plan, around, new List<PieceModel>());
        ChuteFlowResult flow = ChuteFlow.Solve(new ChuteFlowGraph(after, around.Ports, edited, Legs(plan)));
        foreach (FlowConflict conflict in flow.Conflicts)
        {
            plan.Problem(conflict.Code, conflict.Message + JunctionHint(after, conflict.Piece),
                conflict.Piece > 0 ? conflict.Piece : (long?)null, conflict.Cell);
        }

        Outlets(plan, around, before, after, flow);
        Undirected(plan, after, edited, flow);
        foreach (PieceModel piece in after)
        {
            if (edited.Contains(piece.Id))
            {
                foreach (GridCell cell in piece.Cells)
                {
                    plan.Flow[cell] = CellFlow.Of(piece, cell, flow);
                }
            }
        }

        foreach (LayoutCell kept in plan.KeptCells)
        {
            if (kept.Existing != null)
            {
                plan.Flow[kept.Cell] = CellFlow.Of(kept.Existing, kept.Cell, flow);
            }
        }
    }

    /// <summary>
    /// The old run with its upstream end first, when the flow now says which end items come in by; as it is when
    /// nothing says.
    /// </summary>
    internal static RerouteSegment Ordered(RerouteSegment segment)
    {
        ChuteSurroundings around = ChuteSurroundings.Of(segment.Pieces);
        if (around.TooLarge)
        {
            return segment;
        }

        List<PieceModel> models = new List<PieceModel>(around.Before.Values);
        ChuteFlowResult flow = ChuteFlow.Solve(new ChuteFlowGraph(models, around.Ports, new HashSet<long>(),
            new List<RunLeg>()));
        HashSet<long> inside = new HashSet<long>();
        foreach (SmallGrid piece in segment.Pieces)
        {
            inside.Add(piece.ReferenceId);
        }

        FlowDirection atFirst = Boundary(segment.First, models, inside, flow);
        FlowDirection atLast = Boundary(segment.Last, models, inside, flow);
        bool reversed = atFirst == FlowDirection.Out || atLast == FlowDirection.In;
        return reversed ? new RerouteSegment(segment.Pieces, segment.Last, segment.First) : segment;
    }

    // Which way items cross the segment's boundary at the cell: its piece's end that leads out of the segment.
    private static FlowDirection Boundary(GridCell cell, List<PieceModel> models, HashSet<long> inside,
        ChuteFlowResult flow)
    {
        HashSet<GridCell> segmentCells = new HashSet<GridCell>();
        foreach (PieceModel model in models)
        {
            if (inside.Contains(model.Id))
            {
                segmentCells.UnionWith(model.Cells);
            }
        }

        foreach (PieceModel model in models)
        {
            if (!inside.Contains(model.Id) || !model.Occupies(cell))
            {
                continue;
            }

            for (int index = 0; index < model.Ends.Count; index++)
            {
                PieceEnd end = model.Ends[index];
                if (end.Facing.Equals(cell) && !segmentCells.Contains(end.Local))
                {
                    FlowDirection direction = flow.Of(model.Id, index);
                    if (direction != FlowDirection.Unknown)
                    {
                        return direction;
                    }
                }
            }
        }

        return FlowDirection.Unknown;
    }

    // Each combination of turns is valid when every candidate can be modelled and the whole flow has no conflict.
    private static TurnVerdict[] Verdicts(RunPlan plan, ChuteSurroundings around, List<OrientableCell> cells)
    {
        HashSet<long> edited = EditedIds(plan);
        List<int> counts = new List<int>(cells.Count);
        foreach (OrientableCell cell in cells)
        {
            edited.Add(cell.Id);
            counts.Add(cell.Options.Count);
        }

        List<RunLeg> legs = Legs(plan);
        return TurnSearch.Agreed(counts, turns =>
        {
            List<PieceModel> candidates = new List<PieceModel>(cells.Count);
            for (int index = 0; index < cells.Count; index++)
            {
                OrientableCell cell = cells[index];
                PieceModel? model = RunCatalogue.Verified(cell.Options[turns[index]], cell.Layout.Cell,
                    cell.Layout.Ends, cell.Id);
                if (model == null)
                {
                    return false;
                }

                candidates.Add(model);
            }

            ChuteFlowResult flow = ChuteFlow.Solve(new ChuteFlowGraph(After(plan, around, candidates), around.Ports,
                edited, legs));
            return flow.Conflicts.Count == 0;
        });
    }

    // Items newly falling out of an open end: an end letting items out with nothing joined, that did not before.
    private static void Outlets(RunPlan plan, ChuteSurroundings around, ChuteFlowResult before,
        List<PieceModel> after, ChuteFlowResult flow)
    {
        HashSet<(long, GridCell)> old = new HashSet<(long, GridCell)>();
        foreach (PieceEndRef end in before.Outlets)
        {
            old.Add((end.Piece, around.Before[end.Piece].Ends[end.End].Local));
        }

        Dictionary<long, PieceModel> byId = new Dictionary<long, PieceModel>();
        foreach (PieceModel piece in after)
        {
            byId[piece.Id] = piece;
        }

        foreach (PieceEndRef end in flow.Outlets)
        {
            PieceEnd open = byId[end.Piece].Ends[end.End];
            if (old.Contains((end.Piece, open.Local)))
            {
                continue;
            }

            GridStep? toward = GridStep.Between(open.Facing, open.Local);
            plan.Warnings.Add(new LayoutIssue(DropsItems,
                $"Items leaving {open.Facing} towards {toward?.Name ?? "?"} fall to the ground there: nothing is " +
                "joined to that end (Chute.OnServerTick drops them at its end).", open.Facing,
                end.Piece > 0 ? end.Piece : (long?)null));
        }
    }

    private static void Undirected(RunPlan plan, List<PieceModel> after, HashSet<long> edited, ChuteFlowResult flow)
    {
        List<string> cells = new List<string>();
        foreach (PieceModel piece in after)
        {
            if (edited.Contains(piece.Id) && !flow.IsDirected(piece) && piece.Cells.Count > 0)
            {
                cells.Add(piece.Cells[0].ToString());
            }
        }

        if (cells.Count > 0)
        {
            plan.Warnings.Add(new LayoutIssue(Unknown,
                $"No device port or directed piece gives the flow at {cells.Count} new or changed piece(s) " +
                $"({string.Join(", ", cells.GetRange(0, System.Math.Min(cells.Count, 8)))}); items there move " +
                "whichever way they are first pushed."));
        }
    }

    private static string JunctionHint(List<PieceModel> after, long piece)
    {
        PieceModel? model = after.Find(found => found.Id == piece);
        bool directed = model != null && model.Ends.Count > 2;
        return directed
            ? " A junction merges its two inputs into its output; splitting one flow into two needs a splitter, " +
              "which this tool does not build."
            : string.Empty;
    }

    private static List<PieceModel> After(RunPlan plan, ChuteSurroundings around, List<PieceModel> candidates)
    {
        HashSet<long> removed = plan.RemovedIds();
        Dictionary<long, PieceModel> after = new Dictionary<long, PieceModel>();
        foreach (KeyValuePair<long, PieceModel> piece in around.Before)
        {
            if (!removed.Contains(piece.Key))
            {
                after[piece.Key] = piece.Value;
            }
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            after[cell.ForecastId] = cell.Model;
        }

        foreach (PieceModel candidate in candidates)
        {
            after[candidate.Id] = candidate;
        }

        return new List<PieceModel>(after.Values);
    }

    private static HashSet<long> EditedIds(RunPlan plan)
    {
        HashSet<long> edited = new HashSet<long>();
        foreach (PlannedCell cell in plan.Cells)
        {
            edited.Add(cell.ForecastId);
        }

        return edited;
    }

    private static List<RunLeg> Legs(RunPlan plan) =>
        plan.Request.Build != null ? plan.Request.Build.Shape.Legs : new List<RunLeg>();

    // Every piece and device the plan read, and the pieces it removes.
    private static List<SmallGrid> Seeds(RunPlan plan)
    {
        List<SmallGrid> seeds = new List<SmallGrid>(plan.Things.Values);
        foreach (PlannedRemoval removal in plan.Removals)
        {
            seeds.Add(removal.Piece);
        }

        return seeds;
    }
}
