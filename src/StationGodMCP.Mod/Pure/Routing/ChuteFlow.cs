#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Which way items cross one end of a chute piece: into it, out of it, or fixed by nothing.</summary>
internal enum FlowDirection
{
    Unknown,
    In,
    Out
}

/// <summary>
/// ConnectionRole as an integer and what it means for items (CODE, ConnectionRole): a chute piece's Input and Input2
/// ends take items in, its Output and Output2 ends let them out (Chute.IsValidInputConnection refuses an item through
/// an Output end; ChuteJunction and the splitters send every item to an Output end). A device's chute port with role
/// Input takes items off the chute (DeviceImport.TryChuteImport, ChuteExportBin, ChuteOutlet); one with role Output
/// pushes items into it (DeviceImportExport's export, ChuteBin, ChuteInlet). None fixes nothing.
/// </summary>
internal static class ChuteRoles
{
    internal const int None = 0;
    internal const int Input = 1;
    internal const int Input2 = 2;
    internal const int Output = 3;
    internal const int Output2 = 4;

    internal static bool TakesIn(int role) => role == Input || role == Input2;

    internal static bool LetsOut(int role) => role == Output || role == Output2;

    internal static bool IsDirected(int role) => TakesIn(role) || LetsOut(role);

    /// <summary>
    /// Why a route running with the items cannot end at a device port of this role (target) or start at it: a port
    /// that pushes items out is only ever a start, one that takes items in only an end. Null when it may.
    /// </summary>
    internal static string? WrongWay(int role, bool target) =>
        target && LetsOut(role) ? "pushes items out"
        : !target && TakesIn(role) ? "takes items in"
        : null;
}

/// <summary>One end of a piece: the piece's id and the end's index among its model's ends.</summary>
internal readonly struct PieceEndRef : IEquatable<PieceEndRef>
{
    internal PieceEndRef(long piece, int end)
    {
        Piece = piece;
        End = end;
    }

    internal long Piece { get; }

    internal int End { get; }

    public bool Equals(PieceEndRef other) => Piece == other.Piece && End == other.End;

    public override bool Equals(object? obj) => obj is PieceEndRef other && Equals(other);

    public override int GetHashCode() => unchecked(Piece.GetHashCode() * 31 + End);
}

/// <summary>A run's own direction at one of its cells: items leave the cell towards the next cell of the run.</summary>
internal readonly struct RunLeg
{
    internal RunLeg(GridCell cell, GridStep toward)
    {
        Cell = cell;
        Toward = toward;
    }

    internal GridCell Cell { get; }

    internal GridStep Toward { get; }

    /// <summary>The legs of a run in order: every cell but the last sends items to the next.</summary>
    internal static List<RunLeg> Of(IReadOnlyList<GridCell> run)
    {
        List<RunLeg> legs = new List<RunLeg>(Math.Max(0, run.Count - 1));
        for (int index = 0; index + 1 < run.Count; index++)
        {
            GridStep? step = GridStep.Between(run[index], run[index + 1]);
            if (step.HasValue)
            {
                legs.Add(new RunLeg(run[index], step.Value));
            }
        }

        return legs;
    }
}

/// <summary>A contradiction in the flow the edit causes, with the piece and cell where it shows.</summary>
internal sealed class FlowConflict
{
    internal const string Conflict = "flow_conflict";
    internal const string Reversed = "flow_reversed";

    internal FlowConflict(string code, string message, long piece, GridCell cell)
    {
        Code = code;
        Message = message;
        Piece = piece;
        Cell = cell;
    }

    internal string Code { get; }

    internal string Message { get; }

    internal long Piece { get; }

    internal GridCell Cell { get; }
}

/// <summary>
/// Chute pieces and device ports as the flow sees them after an edit: every piece's model, the device chute ports
/// (DevicePort, End.Role the port's ConnectionRole), which pieces the edit places or changes, and the run's own legs.
/// </summary>
internal sealed class ChuteFlowGraph
{
    internal ChuteFlowGraph(IReadOnlyList<PieceModel> pieces, IReadOnlyList<DevicePort> ports,
        IReadOnlyCollection<long> edited, IReadOnlyList<RunLeg> legs)
    {
        Pieces = pieces;
        Ports = ports;
        Edited = new HashSet<long>(edited);
        Legs = legs;
    }

    internal IReadOnlyList<PieceModel> Pieces { get; }

    internal IReadOnlyList<DevicePort> Ports { get; }

    internal HashSet<long> Edited { get; }

    internal IReadOnlyList<RunLeg> Legs { get; }
}

/// <summary>The flow through every end, the contradictions the edit causes, and the ends items fall out of.</summary>
internal sealed class ChuteFlowResult
{
    private readonly Dictionary<PieceEndRef, FlowDirection> _directions;

    internal ChuteFlowResult(Dictionary<PieceEndRef, FlowDirection> directions, List<FlowConflict> conflicts,
        List<PieceEndRef> outlets)
    {
        _directions = directions;
        Conflicts = conflicts;
        Outlets = outlets;
    }

    internal List<FlowConflict> Conflicts { get; }

    /// <summary>Ends that let items out with nothing joined to them: items fall to the ground there.</summary>
    internal List<PieceEndRef> Outlets { get; }

    internal FlowDirection Of(long piece, int end) =>
        _directions.TryGetValue(new PieceEndRef(piece, end), out FlowDirection direction)
            ? direction
            : FlowDirection.Unknown;

    /// <summary>Whether any end of the piece has a known direction.</summary>
    internal bool IsDirected(PieceModel piece)
    {
        for (int index = 0; index < piece.Ends.Count; index++)
        {
            if (Of(piece.Id, index) != FlowDirection.Unknown)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Which way items move through a chute network, on plain values (CODE, Chute.OnServerTick and SetNeighbor). A plain
/// piece (no end with a role: straight, corner, window, the long straights) passes an item out of the end it did not
/// come in by, so its two ends always run opposite ways. A directed piece (a junction, valve, overflow or splitter)
/// takes items in only through its Input ends and lets them out only through its Output ends. Two joined ends run
/// opposite ways (what leaves one piece enters the next), and a device port fixes the end joined to it: Input takes
/// items off the chute, Output pushes them in. Each end is a variable and each rule an equal-or-opposite relation, so
/// the whole network is solved by union-find with parity against one constant; a relation that contradicts what is
/// already known is a conflict. Relations among pieces the edit leaves alone are added first, so only what the edit
/// adds is blamed (a network that was already contradictory stays so without refusing the edit); the run's own legs
/// (items travel from its first cell to its last) come last and are blamed as flow_reversed.
/// </summary>
internal static class ChuteFlow
{
    internal static ChuteFlowResult Solve(ChuteFlowGraph graph)
    {
        Variables variables = new Variables(graph.Pieces);
        ParityForest forest = new ParityForest();
        Dictionary<GridCell, List<PieceModel>> byCell = ByCell(graph.Pieces);
        List<FlowConflict> ignored = new List<FlowConflict>();
        List<FlowConflict> conflicts = new List<FlowConflict>();
        HashSet<PieceEndRef> joined = new HashSet<PieceEndRef>();
        foreach (bool edited in new[] { false, true })
        {
            List<FlowConflict> blame = edited ? conflicts : ignored;
            foreach (PieceModel piece in graph.Pieces)
            {
                if (graph.Edited.Contains(piece.Id) == edited)
                {
                    Inside(piece, variables, forest, blame);
                }
            }

            Pairs(graph, byCell, variables, forest, blame, edited, joined);
            Ports(graph, byCell, variables, forest, blame, edited, joined);
        }

        List<GridCell> reversed = new List<GridCell>();
        long reversedPiece = 0;
        foreach (RunLeg leg in graph.Legs)
        {
            Leg(leg, byCell, variables, forest, reversed, ref reversedPiece);
        }

        if (reversed.Count > 0)
        {
            conflicts.Add(Reversed(reversed, reversedPiece));
        }

        Dictionary<PieceEndRef, FlowDirection> directions = new Dictionary<PieceEndRef, FlowDirection>();
        List<PieceEndRef> outlets = new List<PieceEndRef>();
        foreach (PieceModel piece in graph.Pieces)
        {
            for (int index = 0; index < piece.Ends.Count; index++)
            {
                PieceEndRef end = new PieceEndRef(piece.Id, index);
                FlowDirection direction = forest.Value(variables.Of(end));
                directions[end] = direction;
                if (direction == FlowDirection.Out && !joined.Contains(end))
                {
                    outlets.Add(end);
                }
            }
        }

        return new ChuteFlowResult(directions, conflicts, outlets);
    }

    // A plain piece with two ends runs them opposite ways; a directed piece fixes each end with a role.
    private static void Inside(PieceModel piece, Variables variables, ParityForest forest, List<FlowConflict> blame)
    {
        bool directed = false;
        for (int index = 0; index < piece.Ends.Count; index++)
        {
            directed |= ChuteRoles.IsDirected(piece.Ends[index].Role);
        }

        GridCell cell = piece.Cells.Count > 0 ? piece.Cells[0] : default;
        if (!directed)
        {
            if (piece.Ends.Count == 2 && !forest.Union(variables.Of(new PieceEndRef(piece.Id, 0)),
                    variables.Of(new PieceEndRef(piece.Id, 1)), true))
            {
                blame.Add(new FlowConflict(FlowConflict.Conflict,
                    $"Items would have to enter piece {piece.Id} through both ends at once (two flows meet in it).",
                    piece.Id, cell));
            }

            return;
        }

        for (int index = 0; index < piece.Ends.Count; index++)
        {
            int role = piece.Ends[index].Role;
            if (!ChuteRoles.IsDirected(role))
            {
                continue;
            }

            FlowDirection fixedTo = ChuteRoles.TakesIn(role) ? FlowDirection.In : FlowDirection.Out;
            if (!forest.Fix(variables.Of(new PieceEndRef(piece.Id, index)), fixedTo))
            {
                blame.Add(new FlowConflict(FlowConflict.Conflict,
                    fixedTo == FlowDirection.Out
                        ? $"Items would have to enter piece {piece.Id} through its output end (it only lets items " +
                          "out there)."
                        : $"Items would have to leave piece {piece.Id} through an input end (it only takes items in " +
                          "there).", piece.Id, piece.Ends[index].Facing));
            }
        }
    }

    // Each end joined to another piece's end: what leaves one enters the other.
    private static void Pairs(ChuteFlowGraph graph, Dictionary<GridCell, List<PieceModel>> byCell, Variables variables,
        ParityForest forest, List<FlowConflict> blame, bool edited, HashSet<PieceEndRef> joined)
    {
        foreach (PieceModel piece in graph.Pieces)
        {
            for (int index = 0; index < piece.Ends.Count; index++)
            {
                PieceEnd end = piece.Ends[index];
                if (!byCell.TryGetValue(end.Local, out List<PieceModel> there))
                {
                    continue;
                }

                foreach (PieceModel other in there)
                {
                    int match = MatchingEnd(other, end);
                    if (other.Id == piece.Id || match < 0)
                    {
                        continue;
                    }

                    PieceEndRef mine = new PieceEndRef(piece.Id, index);
                    PieceEndRef theirs = new PieceEndRef(other.Id, match);
                    joined.Add(mine);
                    joined.Add(theirs);
                    bool touchesEdit = graph.Edited.Contains(piece.Id) || graph.Edited.Contains(other.Id);
                    if (touchesEdit != edited || piece.Id > other.Id)
                    {
                        continue;
                    }

                    if (!forest.Union(variables.Of(mine), variables.Of(theirs), true))
                    {
                        blame.Add(new FlowConflict(FlowConflict.Conflict,
                            $"Items would meet head-on between pieces {piece.Id} and {other.Id} (both push towards " +
                            "each other, or both wait for the other).", piece.Id, end.Facing));
                    }
                }
            }
        }
    }

    // A device port fixes the chute end joined to it: Input takes items off the chute, Output pushes them in.
    private static void Ports(ChuteFlowGraph graph, Dictionary<GridCell, List<PieceModel>> byCell, Variables variables,
        ParityForest forest, List<FlowConflict> blame, bool edited, HashSet<PieceEndRef> joined)
    {
        foreach (DevicePort port in graph.Ports)
        {
            if (!byCell.TryGetValue(port.End.Local, out List<PieceModel> there))
            {
                continue;
            }

            foreach (PieceModel piece in there)
            {
                int match = MatchingEnd(piece, port.End);
                if (match < 0)
                {
                    continue;
                }

                PieceEndRef end = new PieceEndRef(piece.Id, match);
                joined.Add(end);
                if (graph.Edited.Contains(piece.Id) != edited || !ChuteRoles.IsDirected(port.End.Role))
                {
                    continue;
                }

                bool takes = ChuteRoles.TakesIn(port.End.Role);
                if (!forest.Fix(variables.Of(end), takes ? FlowDirection.Out : FlowDirection.In))
                {
                    blame.Add(new FlowConflict(FlowConflict.Conflict,
                        takes
                            ? $"Port {port.Index} of device {port.DeviceId} takes items in, but the chute there " +
                              "would carry items away from it."
                            : $"Port {port.Index} of device {port.DeviceId} pushes items out, but the chute there " +
                              "would carry items towards it.", piece.Id, port.End.Local));
                }
            }
        }
    }

    // One flow_reversed for the whole run, naming the cells where items would move against it (once per cell).
    private static FlowConflict Reversed(List<GridCell> cells, long piece)
    {
        const int Shown = 6;
        List<string> named = cells.GetRange(0, Math.Min(Shown, cells.Count)).ConvertAll(static cell => cell.ToString());
        string more = cells.Count > Shown ? $" and {cells.Count - Shown} more" : string.Empty;
        return new FlowConflict(FlowConflict.Reversed,
            "The run carries items from its first cell to its last, but at " +
            $"{string.Join(", ", named)}{more} they would move the other way: what the run joins fixes the flow " +
            "against it. Reverse the run (from the source to the sink) or join other ends.", piece, cells[0]);
    }

    private static void Leg(RunLeg leg, Dictionary<GridCell, List<PieceModel>> byCell, Variables variables,
        ParityForest forest, List<GridCell> reversed, ref long reversedPiece)
    {
        if (!byCell.TryGetValue(leg.Cell, out List<PieceModel> there))
        {
            return;
        }

        GridCell next = leg.Toward.From(leg.Cell);
        foreach (PieceModel piece in there)
        {
            for (int index = 0; index < piece.Ends.Count; index++)
            {
                PieceEnd end = piece.Ends[index];
                if (!end.Local.Equals(next) || !end.Facing.Equals(leg.Cell))
                {
                    continue;
                }

                if (!forest.Fix(variables.Of(new PieceEndRef(piece.Id, index)), FlowDirection.Out) &&
                    !reversed.Contains(leg.Cell))
                {
                    if (reversed.Count == 0)
                    {
                        reversedPiece = piece.Id;
                    }

                    reversed.Add(leg.Cell);
                }
            }
        }
    }

    // The other's end that joins this end: it sits in this end's facing cell, faces this end's cell and shares a type.
    private static int MatchingEnd(PieceModel other, PieceEnd end)
    {
        for (int index = 0; index < other.Ends.Count; index++)
        {
            PieceEnd theirs = other.Ends[index];
            if ((theirs.Type & end.Type) != 0 && theirs.Local.Equals(end.Facing) && theirs.Facing.Equals(end.Local))
            {
                return index;
            }
        }

        return -1;
    }

    private static Dictionary<GridCell, List<PieceModel>> ByCell(IReadOnlyList<PieceModel> pieces)
    {
        Dictionary<GridCell, List<PieceModel>> byCell = new Dictionary<GridCell, List<PieceModel>>();
        foreach (PieceModel piece in pieces)
        {
            foreach (GridCell cell in piece.Cells)
            {
                if (!byCell.TryGetValue(cell, out List<PieceModel> list))
                {
                    list = new List<PieceModel>(1);
                    byCell[cell] = list;
                }

                list.Add(piece);
            }
        }

        return byCell;
    }

    /// <summary>A number per end; 0 is the constant In.</summary>
    private sealed class Variables
    {
        private readonly Dictionary<PieceEndRef, int> _ids = new Dictionary<PieceEndRef, int>();

        internal Variables(IReadOnlyList<PieceModel> pieces)
        {
            foreach (PieceModel piece in pieces)
            {
                for (int index = 0; index < piece.Ends.Count; index++)
                {
                    _ids[new PieceEndRef(piece.Id, index)] = _ids.Count + 1;
                }
            }
        }

        internal int Of(PieceEndRef end) => _ids[end];
    }

    /// <summary>
    /// Union-find where each node knows whether it equals or opposes its parent; node 0 is In, so a node in node 0's
    /// set has a known direction.
    /// </summary>
    private sealed class ParityForest
    {
        private readonly Dictionary<int, int> _parent = new Dictionary<int, int>();
        private readonly Dictionary<int, bool> _flipped = new Dictionary<int, bool>();

        /// <summary>Joins the two as opposite (or equal); false when that contradicts what is known.</summary>
        internal bool Union(int a, int b, bool opposite)
        {
            int rootA = Find(a, out bool flipA);
            int rootB = Find(b, out bool flipB);
            if (rootA == rootB)
            {
                return (flipA ^ flipB) == opposite;
            }

            int child = Math.Max(rootA, rootB);
            int root = Math.Min(rootA, rootB);
            _parent[child] = root;
            _flipped[child] = flipA ^ flipB ^ opposite;
            return true;
        }

        internal bool Fix(int node, FlowDirection direction) => Union(node, 0, direction == FlowDirection.Out);

        internal FlowDirection Value(int node)
        {
            int root = Find(node, out bool flip);
            return root != 0 ? FlowDirection.Unknown : flip ? FlowDirection.Out : FlowDirection.In;
        }

        private int Find(int node, out bool flip)
        {
            flip = false;
            int at = node;
            while (_parent.TryGetValue(at, out int parent) && parent != at)
            {
                flip ^= _flipped[at];
                at = parent;
            }

            Compress(node, at, flip);
            return at;
        }

        private void Compress(int node, int root, bool flip)
        {
            int at = node;
            bool remaining = flip;
            while (at != root && _parent.TryGetValue(at, out int parent))
            {
                bool own = _flipped[at];
                _parent[at] = root;
                _flipped[at] = remaining;
                remaining ^= own;
                at = parent;
            }
        }
    }
}

/// <summary>Which of a cell's ends take items in and which let them out, as world directions.</summary>
internal readonly struct CellFlow
{
    internal CellFlow(EndSet into, EndSet outOf)
    {
        Into = into;
        OutOf = outOf;
    }

    internal EndSet Into { get; }

    internal EndSet OutOf { get; }

    /// <summary>The piece's flow at the cell: each of its ends there with a known direction.</summary>
    internal static CellFlow Of(PieceModel piece, GridCell cell, ChuteFlowResult flow)
    {
        EndSet into = EndSet.None;
        EndSet outOf = EndSet.None;
        for (int index = 0; index < piece.Ends.Count; index++)
        {
            PieceEnd end = piece.Ends[index];
            GridStep? step = end.Facing.Equals(cell) ? GridStep.Between(cell, end.Local) : null;
            if (!step.HasValue)
            {
                continue;
            }

            switch (flow.Of(piece.Id, index))
            {
                case FlowDirection.In:
                    into = into.With(step.Value);
                    break;
                case FlowDirection.Out:
                    outOf = outOf.With(step.Value);
                    break;
            }
        }

        return new CellFlow(into, outOf);
    }
}

/// <summary>What the valid combinations of turns say about one cell.</summary>
internal enum TurnAgreement
{
    /// <summary>Every valid combination turns the cell the same way.</summary>
    Unique,

    /// <summary>No combination is valid.</summary>
    NoneValid,

    /// <summary>Valid combinations turn the cell different ways.</summary>
    Several
}

/// <summary>A cell's verdict, and its turn when that is Unique.</summary>
internal readonly struct TurnVerdict
{
    internal TurnVerdict(TurnAgreement agreement, int turn)
    {
        Agreement = agreement;
        Turn = turn;
    }

    internal TurnAgreement Agreement { get; }

    /// <summary>The agreed turn's index when Unique; 0 (the first turn) otherwise.</summary>
    internal int Turn { get; }
}

/// <summary>
/// Every combination of turns for a few cells, each tried whole (a junction's turn changes the flow the others see),
/// and per cell the turn the valid ones agree on.
/// </summary>
internal static class TurnSearch
{
    internal static TurnVerdict[] Agreed(IReadOnlyList<int> counts, Func<int[], bool> valid)
    {
        int combinations = 1;
        foreach (int count in counts)
        {
            combinations *= count;
        }

        HashSet<int>[] seen = new HashSet<int>[counts.Count];
        for (int index = 0; index < counts.Count; index++)
        {
            seen[index] = new HashSet<int>();
        }

        for (int combination = 0; combination < combinations; combination++)
        {
            int[] turns = new int[counts.Count];
            int rest = combination;
            for (int index = 0; index < counts.Count; index++)
            {
                turns[index] = rest % counts[index];
                rest /= counts[index];
            }

            if (!valid(turns))
            {
                continue;
            }

            for (int index = 0; index < turns.Length; index++)
            {
                seen[index].Add(turns[index]);
            }
        }

        TurnVerdict[] verdicts = new TurnVerdict[counts.Count];
        for (int index = 0; index < counts.Count; index++)
        {
            verdicts[index] = seen[index].Count switch
            {
                0 => new TurnVerdict(TurnAgreement.NoneValid, 0),
                1 => new TurnVerdict(TurnAgreement.Unique, First(seen[index])),
                _ => new TurnVerdict(TurnAgreement.Several, 0)
            };
        }

        return verdicts;
    }

    private static int First(HashSet<int> set)
    {
        foreach (int value in set)
        {
            return value;
        }

        return 0;
    }
}
