#nullable enable

using System;

namespace StationGodMCP.Pure.Paging;

/// <summary>
/// A small-grid cell as one walk key (WalkCursors keeps longs): each coordinate in 21 signed bits, x, y, z from the high
/// end, so a world of ±104 km in decimetres fits. A cell outside that is refused rather than folded onto another.
/// </summary>
internal static class CellKey
{
    private const int Bits = 21;
    private const long Mask = (1L << Bits) - 1;
    private const int Limit = 1 << (Bits - 1);

    internal static long Of(GridCell cell) => (Part(cell.X) << (2 * Bits)) | (Part(cell.Y) << Bits) | Part(cell.Z);

    internal static GridCell Cell(long key) =>
        new GridCell(Signed((key >> (2 * Bits)) & Mask), Signed((key >> Bits) & Mask), Signed(key & Mask));

    private static long Part(int coordinate) =>
        coordinate >= -Limit && coordinate < Limit
            ? coordinate & Mask
            : throw new ArgumentOutOfRangeException(nameof(coordinate), coordinate, "A cell coordinate past 21 bits.");

    private static int Signed(long part) => (int)(part >= Limit ? part - (1L << Bits) : part);
}
