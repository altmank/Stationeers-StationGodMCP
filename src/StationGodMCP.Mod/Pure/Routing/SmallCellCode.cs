#nullable enable

using System.Collections.Generic;
using System.Text;

namespace StationGodMCP.Pure;

/// <summary>What stands in one small-grid cell, as grid_survey encodes it.</summary>
internal readonly struct SmallOccupancy
{
    internal SmallOccupancy(bool cable, bool pipe, bool chute, bool device, bool other, bool rocket)
    {
        Cable = cable;
        Pipe = pipe;
        Chute = chute;
        Device = device;
        Other = other;
        Rocket = rocket;
    }

    internal bool Cable { get; }

    internal bool Pipe { get; }

    internal bool Chute { get; }

    internal bool Device { get; }

    /// <summary>The cell's Other or Rail slot (a small wall-mounted thing, a robotic arm rail).</summary>
    internal bool Other { get; }

    /// <summary>The cell belongs to a rocket (SmallCell.Owner).</summary>
    internal bool Rocket { get; }
}

/// <summary>
/// The small grid inside the 2 m grid, and grid_survey's compact encoding. Large cells are centred on odd metres
/// (Grid3 10 + 20k); along each axis a large cell holds four small-cell centres at -1, -0.5, 0 and +0.5 m from its
/// centre (index 0 to 3): index 0 lies on the cell's minimum face plane, shared with the neighbour (its +1 m is the
/// neighbour's index 0). A large cell's 64 small cells are one string, index x + 4y + 16z, one character each:
/// '.' empty, 'c' cable, 'p' pipe, 'b' cable and pipe, 'h' chute, 'd' device, 'o' another small-grid thing
/// (a wall-mounted item, a rail), 'r' a rocket's empty cell. A new cable may take a '.' cell, and a 'p' cell only
/// across the pipe's axis (their ends would meet otherwise); a pipe likewise a '.' or, across, a 'c'. Frames and
/// walls never block small-grid pieces, so they are not in the string.
/// </summary>
internal static class SmallCellCode
{
    internal const int Large = 20;
    internal const int PerAxis = 4;
    internal const int PerCell = PerAxis * PerAxis * PerAxis;

    /// <summary>The large cell whose four small positions along each axis include the small cell's.</summary>
    internal static GridCell LargeOf(GridCell small) =>
        new GridCell(LargeAxis(small.X), LargeAxis(small.Y), LargeAxis(small.Z));

    /// <summary>The small cell's index 0 to 3 along each axis inside its large cell.</summary>
    internal static int IndexOnAxis(int small) => (small - (LargeAxis(small) - Large / 2)) / GridStep.CellSize;

    internal static int IndexOf(GridCell small) =>
        IndexOnAxis(small.X) + PerAxis * IndexOnAxis(small.Y) + PerAxis * PerAxis * IndexOnAxis(small.Z);

    /// <summary>The small cell of a large cell at an index (x + 4y + 16z).</summary>
    internal static GridCell SmallAt(GridCell large, int index)
    {
        int x = index % PerAxis;
        int y = index / PerAxis % PerAxis;
        int z = index / (PerAxis * PerAxis);
        int origin = -Large / 2;
        return new GridCell(large.X + origin + x * GridStep.CellSize, large.Y + origin + y * GridStep.CellSize,
            large.Z + origin + z * GridStep.CellSize);
    }

    internal static char Encode(SmallOccupancy cell)
    {
        if (cell.Device)
        {
            return 'd';
        }

        if (cell.Chute)
        {
            return 'h';
        }

        if (cell.Other)
        {
            return 'o';
        }

        if (cell.Cable && cell.Pipe)
        {
            return 'b';
        }

        if (cell.Cable)
        {
            return 'c';
        }

        if (cell.Pipe)
        {
            return 'p';
        }

        return cell.Rocket ? 'r' : '.';
    }

    /// <summary>A large cell's 64 small cells as one string, from each small cell's occupancy.</summary>
    internal static string Encode(GridCell large, System.Func<GridCell, SmallOccupancy> read)
    {
        StringBuilder text = new StringBuilder(PerCell);
        for (int index = 0; index < PerCell; index++)
        {
            text.Append(Encode(read(SmallAt(large, index))));
        }

        return text.ToString();
    }

    /// <summary>The large cells of a box (both corners included, any order), y then z then x ascending.</summary>
    internal static List<GridCell> LargeCellsIn(GridCell a, GridCell b)
    {
        GridCell min = LargeOf(new GridCell(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y),
            System.Math.Min(a.Z, b.Z)));
        GridCell max = LargeOf(new GridCell(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y),
            System.Math.Max(a.Z, b.Z)));
        List<GridCell> cells = new List<GridCell>();
        for (int y = min.Y; y <= max.Y; y += Large)
        {
            for (int z = min.Z; z <= max.Z; z += Large)
            {
                for (int x = min.X; x <= max.X; x += Large)
                {
                    cells.Add(new GridCell(x, y, z));
                }
            }
        }

        return cells;
    }

    /// <summary>How many large cells a box holds, without listing them.</summary>
    internal static long CountIn(GridCell a, GridCell b)
    {
        GridCell min = LargeOf(new GridCell(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y),
            System.Math.Min(a.Z, b.Z)));
        GridCell max = LargeOf(new GridCell(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y),
            System.Math.Max(a.Z, b.Z)));
        return (long)((max.X - min.X) / Large + 1) * ((max.Y - min.Y) / Large + 1) * ((max.Z - min.Z) / Large + 1);
    }

    /// <summary>How many small cells a box holds (both corners included, any order).</summary>
    internal static long SmallCountIn(GridCell a, GridCell b) =>
        (long)(System.Math.Abs(b.X - a.X) / GridStep.CellSize + 1) * (System.Math.Abs(b.Y - a.Y) / GridStep.CellSize + 1) *
        (System.Math.Abs(b.Z - a.Z) / GridStep.CellSize + 1);

    private static int LargeAxis(int small) => FloorDiv(small, Large) * Large + Large / 2;

    private static int FloorDiv(int value, int by) => value >= 0 ? value / by : -((-value + by - 1) / by);
}
