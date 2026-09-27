#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// One of the six world axis directions on the small grid: +x, -x, +y (up), -y, +z, -z. Cells are Grid3 values in
/// decimetres; small-grid cell centres sit on multiples of 0.5 m (Grid3(position, 0.5, 0.25) rounds with
/// GridCenter(0.5, 0.25): round((p - 0.5) / 0.5) * 0.5 + 0.5), so they are multiples of 5 and a neighbour is 5 away.
/// </summary>
internal readonly struct GridStep : IEquatable<GridStep>
{
    /// <summary>A small-grid cell in Grid3 units (decimetres).</summary>
    internal const int CellSize = 5;

    private static readonly string[] Names = { "+x", "-x", "+y", "-y", "+z", "-z" };
    private static readonly int[][] Unit =
    {
        new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 },
        new[] { 0, 0, -1 }
    };

    /// <summary>Every direction, in index order +x, -x, +y, -y, +z, -z.</summary>
    internal static readonly GridStep[] All =
        { new GridStep(0), new GridStep(1), new GridStep(2), new GridStep(3), new GridStep(4), new GridStep(5) };

    private GridStep(int index)
    {
        Index = index;
    }

    /// <summary>0 to 5 in the order of All.</summary>
    internal int Index { get; }

    internal string Name => Names[Index];

    internal int Dx => Unit[Index][0];

    internal int Dy => Unit[Index][1];

    internal int Dz => Unit[Index][2];

    /// <summary>0 for x, 1 for y, 2 for z.</summary>
    internal int Axis => Index / 2;

    internal bool IsVertical => Axis == 1;

    internal GridStep Opposite => All[Index ^ 1];

    /// <summary>The cell that many small-grid cells along this direction.</summary>
    internal GridCell From(GridCell cell, int cells = 1) =>
        new GridCell(cell.X + Dx * CellSize * cells, cell.Y + Dy * CellSize * cells, cell.Z + Dz * CellSize * cells);

    /// <summary>"+x", "-y" and so on, ignoring case and space; false for anything else.</summary>
    internal static bool TryParse(string? text, out GridStep step)
    {
        string name = (text ?? string.Empty).Trim().ToLowerInvariant();
        int index = Array.IndexOf(Names, name);
        step = index >= 0 ? All[index] : All[0];
        return index >= 0;
    }

    /// <summary>The direction from a cell to its neighbour; null when the cells are not neighbours.</summary>
    internal static GridStep? Between(GridCell from, GridCell to)
    {
        int x = to.X - from.X;
        int y = to.Y - from.Y;
        int z = to.Z - from.Z;
        int moved = (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0) + (z != 0 ? 1 : 0);
        if (moved != 1 || Math.Abs(x + y + z) != CellSize)
        {
            return null;
        }

        int axis = x != 0 ? 0 : y != 0 ? 1 : 2;
        return All[axis * 2 + (x + y + z > 0 ? 0 : 1)];
    }

    /// <summary>Whether the cell is a small-grid cell centre: every coordinate a multiple of CellSize.</summary>
    internal static bool IsOnGrid(GridCell cell) =>
        cell.X % CellSize == 0 && cell.Y % CellSize == 0 && cell.Z % CellSize == 0;

    public bool Equals(GridStep other) => Index == other.Index;

    public override bool Equals(object? obj) => obj is GridStep other && Equals(other);

    public override int GetHashCode() => Index;

    public override string ToString() => Name;
}

/// <summary>
/// The directions a one-cell piece has ends towards, as a set of GridSteps. Its shape names the piece a coil would
/// place: straight (two opposite), corner (two at right angles), tee (three in one plane), corner3 (three at right
/// angles to each other), cross (four in one plane), corner4 (a straight with two ends off it on different axes),
/// five_way and six_way.
/// </summary>
internal readonly struct EndSet : IEquatable<EndSet>
{
    private EndSet(int mask)
    {
        Mask = mask;
    }

    internal static EndSet None => new EndSet(0);

    /// <summary>Bit i set for GridStep.All[i].</summary>
    internal int Mask { get; }

    internal int Count
    {
        get
        {
            int count = 0;
            for (int bits = Mask; bits != 0; bits &= bits - 1)
            {
                count++;
            }

            return count;
        }
    }

    internal bool IsEmpty => Mask == 0;

    internal static EndSet Of(params GridStep[] steps)
    {
        int mask = 0;
        foreach (GridStep step in steps)
        {
            mask |= 1 << step.Index;
        }

        return new EndSet(mask);
    }

    internal static EndSet FromMask(int mask) => new EndSet(mask & 0x3F);

    internal bool Contains(GridStep step) => (Mask & (1 << step.Index)) != 0;

    internal EndSet With(GridStep step) => new EndSet(Mask | (1 << step.Index));

    internal EndSet Union(EndSet other) => new EndSet(Mask | other.Mask);

    /// <summary>Whether every direction of the other set is in this one.</summary>
    internal bool Covers(EndSet other) => (Mask & other.Mask) == other.Mask;

    internal List<GridStep> Steps()
    {
        List<GridStep> steps = new List<GridStep>(Count);
        foreach (GridStep step in GridStep.All)
        {
            if (Contains(step))
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    internal List<string> Names()
    {
        List<string> names = new List<string>(Count);
        foreach (GridStep step in GridStep.All)
        {
            if (Contains(step))
            {
                names.Add(step.Name);
            }
        }

        return names;
    }

    /// <summary>How many axes have both of their directions in the set.</summary>
    private int OppositePairs
    {
        get
        {
            int pairs = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                int both = 3 << (axis * 2);
                pairs += (Mask & both) == both ? 1 : 0;
            }

            return pairs;
        }
    }

    internal string Shape =>
        Count switch
        {
            0 => "none",
            1 => "end",
            2 => OppositePairs == 1 ? "straight" : "corner",
            3 => OppositePairs == 1 ? "tee" : "corner3",
            4 => OppositePairs == 2 ? "cross" : "corner4",
            5 => "five_way",
            _ => "six_way"
        };

    /// <summary>
    /// The directions a piece's ends point at from the cell: each end whose facing cell is the cell (the end sits in
    /// the neighbour, SmallGrid connection geometry) and whose own cell is one step away. Ends of a long piece's other
    /// cells are left out.
    /// </summary>
    internal static EndSet AtCell(PieceModel piece, GridCell cell)
    {
        int mask = 0;
        for (int index = 0; index < piece.Ends.Count; index++)
        {
            PieceEnd end = piece.Ends[index];
            if (!end.Facing.Equals(cell))
            {
                continue;
            }

            GridStep? step = GridStep.Between(cell, end.Local);
            if (step.HasValue)
            {
                mask |= 1 << step.Value.Index;
            }
        }

        return new EndSet(mask);
    }

    /// <summary>The one-cell piece at the cell with ends towards each direction, as the connectivity model has it.</summary>
    internal static PieceModel ModelAt(long id, GridCell cell, EndSet ends, int type, int role, PipeContent? content)
    {
        List<PieceEnd> list = new List<PieceEnd>(ends.Count);
        foreach (GridStep step in ends.Steps())
        {
            list.Add(new PieceEnd(step.From(cell), cell, type, role));
        }

        return new PieceModel(id, new[] { cell }, list, content);
    }

    public bool Equals(EndSet other) => Mask == other.Mask;

    public override bool Equals(object? obj) => obj is EndSet other && Equals(other);

    public override int GetHashCode() => Mask;

    public override string ToString() => $"[{string.Join(", ", Names())}] {Shape}";
}
