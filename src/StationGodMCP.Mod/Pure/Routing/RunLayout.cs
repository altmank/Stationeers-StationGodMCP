#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>How a run's cells join what is already there besides the run itself.</summary>
internal enum JoinMode
{
    /// <summary>Only the run's own cells and any explicit joins.</summary>
    None,

    /// <summary>
    /// The default: the run's first and last cells join every open piece end and device port pointing at them, and
    /// the piece straight ahead of a run end that joins no device port and no piece (turned into a junction when it
    /// has no end there). A run end at a port, or on or at a piece, has reached it: the piece beyond is some other
    /// network's (a route to a network ends on one of its pieces; the piece past it may be across a transformer).
    /// </summary>
    Ends,

    /// <summary>As Ends, and every run cell joins every open piece end and device port pointing at it.</summary>
    All
}

/// <summary>A device end of the family's network type: the device, the end's index, and the end.</summary>
internal sealed class DevicePort
{
    internal DevicePort(long deviceId, int index, PieceEnd end, bool power)
    {
        DeviceId = deviceId;
        Index = index;
        End = end;
        Power = power;
    }

    internal long DeviceId { get; }

    internal int Index { get; }

    /// <summary>End.Local is the cell a piece joining the port stands in; End.Facing is the device's own cell.</summary>
    internal PieceEnd End { get; }

    /// <summary>The end carries power (a cable port that is not data only).</summary>
    internal bool Power { get; }
}

/// <summary>
/// What the family already has around a run, read from the game: its pieces by cell, why a piece may not be
/// changed, device ports, the cells devices stand in, and the cells a new piece may not take (with why).
/// </summary>
internal sealed class RunSurroundings
{
    internal Dictionary<GridCell, PieceModel> Pieces { get; } = new Dictionary<GridCell, PieceModel>();

    /// <summary>Pieces that may not be replaced, with the reason (long_piece, device_mounted, not_a_kit_piece...).</summary>
    internal Dictionary<long, string> Fixed { get; } = new Dictionary<long, string>();

    internal List<DevicePort> Ports { get; } = new List<DevicePort>();

    internal Dictionary<GridCell, long> DeviceCells { get; } = new Dictionary<GridCell, long>();

    internal Dictionary<GridCell, string> Blocked { get; } = new Dictionary<GridCell, string>();

    internal void AddPiece(PieceModel piece)
    {
        foreach (GridCell cell in piece.Cells)
        {
            Pieces[cell] = piece;
        }
    }

    internal PieceModel? PieceAt(GridCell cell) => Pieces.TryGetValue(cell, out PieceModel piece) ? piece : null;

    /// <summary>Device ports a piece standing in the cell would join through its end towards the step.</summary>
    internal List<DevicePort> PortsAt(GridCell cell, GridStep step)
    {
        GridCell facing = step.From(cell);
        List<DevicePort> ports = new List<DevicePort>();
        foreach (DevicePort port in Ports)
        {
            if (port.End.Local.Equals(cell) && port.End.Facing.Equals(facing))
            {
                ports.Add(port);
            }
        }

        return ports;
    }
}

/// <summary>One explicit join the caller asked for: the run cell and the direction of its extra end.</summary>
internal readonly struct ExtraEnd
{
    internal ExtraEnd(GridCell cell, GridStep step)
    {
        Cell = cell;
        Step = step;
    }

    internal GridCell Cell { get; }

    internal GridStep Step { get; }
}

/// <summary>What a run cell's end towards a direction meets.</summary>
internal sealed class LayoutJoin
{
    internal LayoutJoin(GridCell cell, GridStep step, string kind, long? targetId, int? portIndex)
    {
        Cell = cell;
        Step = step;
        Kind = kind;
        TargetId = targetId;
        PortIndex = portIndex;
    }

    internal GridCell Cell { get; }

    internal GridStep Step { get; }

    /// <summary>
    /// run (the next or previous run cell), piece, port, open (nothing there) or split (a single of a long straight
    /// split in the same job).
    /// </summary>
    internal string Kind { get; }

    internal long? TargetId { get; }

    internal int? PortIndex { get; }
}

/// <summary>A problem that refuses the run, or a warning that does not, with where it is.</summary>
internal sealed class LayoutIssue
{
    internal LayoutIssue(string code, string message, GridCell? cell = null, long? id = null)
    {
        Code = code;
        Message = message;
        Cell = cell;
        Id = id;
    }

    internal string Code { get; }

    internal string Message { get; }

    internal GridCell? Cell { get; }

    internal long? Id { get; }
}

/// <summary>What happens in one cell: a new piece, an existing one replaced by one with more ends, or kept.</summary>
internal enum CellAction
{
    Place,
    Change,
    Keep
}

/// <summary>One cell of the layout: its piece's ends when the run is built, and what stands there now.</summary>
internal sealed class LayoutCell
{
    internal LayoutCell(GridCell cell, EndSet ends, PieceModel? existing, EndSet existingEnds, bool inRun)
    {
        Cell = cell;
        Ends = ends;
        Existing = existing;
        ExistingEnds = existingEnds;
        InRun = inRun;
    }

    internal GridCell Cell { get; }

    /// <summary>The ends the piece in the cell will have.</summary>
    internal EndSet Ends { get; private set; }

    /// <summary>The family's piece there now; null for an empty cell.</summary>
    internal PieceModel? Existing { get; }

    internal EndSet ExistingEnds { get; }

    /// <summary>A cell of the run (not a neighbour changed to join it).</summary>
    internal bool InRun { get; }

    /// <summary>The end added because a run end had only one (a straight's far end, open).</summary>
    internal GridStep? OpenEnd { get; private set; }

    internal List<LayoutJoin> Joins { get; } = new List<LayoutJoin>();

    internal CellAction Action =>
        Existing == null ? CellAction.Place : Ends.Equals(ExistingEnds) ? CellAction.Keep : CellAction.Change;

    internal void Add(GridStep step) => Ends = Ends.With(step);

    internal void AddOpen(GridStep step)
    {
        Ends = Ends.With(step);
        OpenEnd = step;
    }
}

/// <summary>The layout of a run: every cell touched, in run order then neighbours, and what was found.</summary>
internal sealed class RunLayout
{
    internal List<LayoutCell> Cells { get; } = new List<LayoutCell>();

    internal List<LayoutIssue> Problems { get; } = new List<LayoutIssue>();

    internal List<LayoutIssue> Warnings { get; } = new List<LayoutIssue>();

    internal LayoutCell? At(GridCell cell)
    {
        foreach (LayoutCell entry in Cells)
        {
            if (entry.Cell.Equals(cell))
            {
                return entry;
            }
        }

        return null;
    }
}

/// <summary>
/// The ends every cell of a run needs, and what it meets, on plain values. A run cell has ends towards the run cells
/// before and after it; the join mode and explicit joins add ends towards what is already there. A run cell holding
/// one of the family's pieces keeps that piece's ends and gains the run's (a straight crossed by the run becomes a
/// cross); a neighbour piece a run end joins but that has no end towards it gains one (the inverse of
/// simplify_junctions). A run end that would have a single end gets a straight, its far end left open, because no
/// coil or kit has a one-ended piece. Which piece has exactly the ends is the kit's to say (PieceCatalogue).
/// </summary>
internal static class RunLayoutPlanner
{
    /// <summary>Warning: a new piece takes a free device port's joining cell without joining the port.</summary>
    internal const string BlocksPort = "blocks_port";

    internal static RunLayout Plan(IReadOnlyList<GridCell> run, RunSurroundings around, JoinMode mode,
        IReadOnlyList<ExtraEnd> extra, PipeContent? content) =>
        Plan(RunShape.Line(run), around, mode, extra, content);

    /// <summary>As for a run, over a run with branches: every tip joins what is around it as a run end does.</summary>
    internal static RunLayout Plan(RunShape shape, RunSurroundings around, JoinMode mode,
        IReadOnlyList<ExtraEnd> extra, PipeContent? content)
    {
        RunLayout layout = new RunLayout();
        List<GridCell> run = shape.Cells;
        Dictionary<GridCell, EndSet> ends = shape.Ends;
        HashSet<GridCell> inRun = new HashSet<GridCell>(run);
        for (int index = 0; index < run.Count; index++)
        {
            GridCell cell = run[index];
            PieceModel? existing = around.PieceAt(cell);
            EndSet existingEnds = existing != null ? EndSet.AtCell(existing, cell) : EndSet.None;
            LayoutCell entry = new LayoutCell(cell, existingEnds.Union(ends[cell]), existing, existingEnds, true);
            layout.Cells.Add(entry);
            foreach (GridStep step in ends[cell].Steps())
            {
                entry.Joins.Add(new LayoutJoin(cell, step, "run", null, null));
            }

            if (existing == null && around.Blocked.TryGetValue(cell, out string blocked))
            {
                layout.Problems.Add(new LayoutIssue("cell_blocked", $"Cell {cell}: {blocked}.", cell));
            }

            if (existing != null && content != null && existing.Content != null &&
                !(existing.Content.Accepts(content) && content.Accepts(existing.Content)))
            {
                layout.Problems.Add(new LayoutIssue("content_mismatch",
                    $"Cell {cell} holds a pipe of other content; the run cannot join it.", cell, existing.Id));
            }
        }

        for (int index = 0; index < run.Count; index++)
        {
            bool isEnd = shape.IsTip(run[index]);
            bool joins = !shape.IsFill(run[index]) && (mode == JoinMode.All || (mode == JoinMode.Ends && isEnd));
            JoinAround(layout, around, layout.Cells[index], joins, inRun, content);
        }

        if (mode != JoinMode.None)
        {
            foreach (RunTip tip in shape.Tips)
            {
                if (tip.Behind.HasValue)
                {
                    JoinAhead(layout, around, layout.At(tip.Cell)!, tip.Behind.Value, inRun, content, shape);
                }
            }
        }

        foreach (ExtraEnd end in extra)
        {
            Explicit(layout, around, end, inRun, content, shape);
        }

        foreach (LayoutCell entry in new List<LayoutCell>(layout.Cells))
        {
            Finish(layout, around, entry, content);
        }

        return layout;
    }

    // Every open piece end and device port pointing at the cell.
    private static void JoinAround(RunLayout layout, RunSurroundings around, LayoutCell entry, bool join,
        HashSet<GridCell> inRun, PipeContent? content)
    {
        if (!join)
        {
            return;
        }

        foreach (GridStep step in GridStep.All)
        {
            if (entry.Ends.Contains(step))
            {
                continue;
            }

            GridCell next = step.From(entry.Cell);
            PieceModel? piece = around.PieceAt(next);
            if (piece != null && !inRun.Contains(next) && piece != entry.Existing &&
                EndSet.AtCell(piece, next).Contains(step.Opposite) && Compatible(piece, content))
            {
                entry.Add(step);
                entry.Joins.Add(new LayoutJoin(entry.Cell, step, "piece", piece.Id, null));
                continue;
            }

            List<DevicePort> ports = around.PortsAt(entry.Cell, step);
            if (ports.Count > 0)
            {
                entry.Add(step);
                foreach (DevicePort port in ports)
                {
                    entry.Joins.Add(new LayoutJoin(entry.Cell, step, "port", port.DeviceId, port.Index));
                }
            }
        }
    }

    // A run end meets the piece straight ahead of it, which gains an end towards the run when it has none; a run end
    // that joins a device port or a piece (standing on one, or at its open end) has reached where it goes and meets
    // nothing beyond.
    private static void JoinAhead(RunLayout layout, RunSurroundings around, LayoutCell entry, GridCell behind,
        HashSet<GridCell> inRun, PipeContent? content, RunShape shape)
    {
        GridStep ahead = GridStep.Between(behind, entry.Cell)!.Value;
        if (entry.Ends.Contains(ahead) || entry.Existing != null ||
            entry.Joins.Exists(static join => join.Kind == "port" || join.Kind == "piece"))
        {
            return;
        }

        GridCell next = ahead.From(entry.Cell);
        if (shape.IsFill(next) && !shape.IsFill(entry.Cell))
        {
            // The long straight ahead is being split: its single in that cell gains the end instead.
            entry.Add(ahead);
            entry.Joins.Add(new LayoutJoin(entry.Cell, ahead, "split", null, null));
            layout.At(next)?.Add(ahead.Opposite);
            return;
        }

        PieceModel? piece = around.PieceAt(next);
        if (piece == null || inRun.Contains(next) || piece == entry.Existing || !Compatible(piece, content))
        {
            return;
        }

        entry.Add(ahead);
        entry.Joins.Add(new LayoutJoin(entry.Cell, ahead, "piece", piece.Id, null));
        NeighbourGains(layout, around, piece, next, ahead.Opposite);
    }

    private static void Explicit(RunLayout layout, RunSurroundings around, ExtraEnd end, HashSet<GridCell> inRun,
        PipeContent? content, RunShape shape)
    {
        LayoutCell? entry = inRun.Contains(end.Cell) ? layout.At(end.Cell) : null;
        if (entry == null)
        {
            layout.Problems.Add(new LayoutIssue("join_not_on_run",
                $"The join at {end.Cell} towards {end.Step.Name} is not on a cell of the run.", end.Cell));
            return;
        }

        if (entry.Ends.Contains(end.Step))
        {
            return;
        }

        entry.Add(end.Step);
        GridCell next = end.Step.From(end.Cell);
        if (shape.IsFill(next) && !shape.IsFill(end.Cell))
        {
            entry.Joins.Add(new LayoutJoin(end.Cell, end.Step, "split", null, null));
            layout.At(next)?.Add(end.Step.Opposite);
            return;
        }

        PieceModel? piece = around.PieceAt(next);
        List<DevicePort> ports = around.PortsAt(end.Cell, end.Step);
        if (piece != null && !inRun.Contains(next))
        {
            if (!Compatible(piece, content))
            {
                layout.Problems.Add(new LayoutIssue("content_mismatch",
                    $"The join at {end.Cell} towards {end.Step.Name} meets a pipe of other content.", end.Cell,
                    piece.Id));
                return;
            }

            entry.Joins.Add(new LayoutJoin(end.Cell, end.Step, "piece", piece.Id, null));
            NeighbourGains(layout, around, piece, next, end.Step.Opposite);
        }
        else if (ports.Count > 0)
        {
            foreach (DevicePort port in ports)
            {
                entry.Joins.Add(new LayoutJoin(end.Cell, end.Step, "port", port.DeviceId, port.Index));
            }
        }
        else if (around.DeviceCells.TryGetValue(next, out long device))
        {
            layout.Problems.Add(new LayoutIssue("no_port_there",
                $"The join at {end.Cell} towards {end.Step.Name} meets device {device}, which has no port of this " +
                "kind facing that cell.", end.Cell, device));
        }
        else if (!inRun.Contains(next))
        {
            entry.Joins.Add(new LayoutJoin(end.Cell, end.Step, "open", null, null));
            layout.Warnings.Add(new LayoutIssue("open_end",
                $"The join at {end.Cell} towards {end.Step.Name} meets nothing; the end stays open.", end.Cell));
        }
    }

    // The neighbour piece is changed to have an end towards the run cell, unless it already has one.
    private static void NeighbourGains(RunLayout layout, RunSurroundings around, PieceModel piece, GridCell cell,
        GridStep towards)
    {
        LayoutCell? entry = layout.At(cell);
        if (entry == null)
        {
            EndSet now = EndSet.AtCell(piece, cell);
            entry = new LayoutCell(cell, now, piece, now, false);
            layout.Cells.Add(entry);
        }

        entry.Add(towards);
    }

    internal const string NothingToJoin = "nothing_to_join";

    // Changed pieces must be changeable; a single end becomes a straight with its far end open; a new piece with no
    // end at all (a one-cell run that joins nothing) has no direction for one.
    private static void Finish(RunLayout layout, RunSurroundings around, LayoutCell entry, PipeContent? content)
    {
        if (entry.Ends.IsEmpty && entry.Existing == null)
        {
            Unjoined(layout, around, entry, content);
            return;
        }

        if (entry.Action == CellAction.Change && entry.Existing != null)
        {
            if (entry.Existing.Cells.Count > 1)
            {
                layout.Problems.Add(new LayoutIssue("long_piece",
                    $"Cell {entry.Cell} is part of a long straight ({entry.Existing.Cells.Count} cells) that would " +
                    "need a new end in the middle; split it first (clean_cables or clean_pipes with " +
                    "split_long_straights) or route around it.", entry.Cell, entry.Existing.Id));
            }
            else if (around.Fixed.TryGetValue(entry.Existing.Id, out string reason))
            {
                layout.Problems.Add(new LayoutIssue("cannot_change",
                    $"The piece in {entry.Cell} would need ends [{string.Join(", ", entry.Ends.Names())}] but may " +
                    $"not be replaced: {reason}.", entry.Cell, entry.Existing.Id));
            }
        }

        if (entry.Ends.Count == 1 && !ContinuesInside(entry))
        {
            GridStep only = entry.Ends.Steps()[0];
            entry.AddOpen(only.Opposite);
            layout.Warnings.Add(new LayoutIssue("open_end",
                $"Cell {entry.Cell} ends the run with nothing to join; it gets a straight whose " +
                $"{only.Opposite.Name} end stays open.", entry.Cell));
        }

        BlockedPorts(layout, around, entry);
    }

    // A one-cell run with nothing to join: a pipe end of other content pointing at it is why (content_mismatch);
    // otherwise nothing gives the piece a direction (nothing_to_join), where the piece form names its ends.
    private static void Unjoined(RunLayout layout, RunSurroundings around, LayoutCell entry, PipeContent? content)
    {
        foreach (GridStep step in GridStep.All)
        {
            GridCell next = step.From(entry.Cell);
            PieceModel? piece = around.PieceAt(next);
            if (piece != null && !Compatible(piece, content) && EndSet.AtCell(piece, next).Contains(step.Opposite))
            {
                layout.Problems.Add(new LayoutIssue("content_mismatch",
                    $"Cell {entry.Cell} is next to a pipe of other content ({piece.Id}, towards {step.Name}); the " +
                    "run cannot join it, and a one-cell run with nothing else to join has no direction.", entry.Cell,
                    piece.Id));
                return;
            }
        }

        layout.Problems.Add(new LayoutIssue(NothingToJoin,
            $"Cell {entry.Cell} is a one-cell run that joins nothing, so it has no direction for a piece. Name its " +
            "ends with piece {at, ends}, or lay a run of two or more cells.", entry.Cell));
    }

    // An end cell of a long straight: the piece goes on into its next cell, so a single end there is not open.
    private static bool ContinuesInside(LayoutCell entry)
    {
        if (entry.Existing == null || entry.Existing.Cells.Count < 2)
        {
            return false;
        }

        foreach (GridStep step in GridStep.All)
        {
            if (entry.Existing.Occupies(step.From(entry.Cell)))
            {
                return true;
            }
        }

        return false;
    }

    // A new piece in a free port's joining cell with no end towards the port leaves that port unusable: nothing else
    // can stand there to join it. plan_*_route reserve_ports keeps such a cell out of a route.
    private static void BlockedPorts(RunLayout layout, RunSurroundings around, LayoutCell entry)
    {
        if (entry.Action != CellAction.Place)
        {
            return;
        }

        foreach (GridStep step in GridStep.All)
        {
            if (entry.Ends.Contains(step))
            {
                continue;
            }

            foreach (DevicePort port in around.PortsAt(entry.Cell, step))
            {
                layout.Warnings.Add(new LayoutIssue(BlocksPort,
                    $"Cell {entry.Cell} is where a piece joining port {port.Index} of device {port.DeviceId} " +
                    $"stands; the piece placed there has no end towards it ({step.Name}), so nothing can join that " +
                    "port afterwards. Route around it (plan_*_route reserve_ports), or join it (extra_ends).",
                    entry.Cell, port.DeviceId));
            }
        }
    }

    private static bool Compatible(PieceModel piece, PipeContent? content) => PipeContent.Join(piece.Content, content);
}
