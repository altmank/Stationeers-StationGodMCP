#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A device end as a piece joining it sees it: the end's index among the thing's ends, the small cell that piece stands
/// in (the end's own cell), the way that piece needs an end (toward the end's facing cell, into the device; null when
/// the two cells are not neighbours), and the end's type and role as the game's enum values.
/// </summary>
internal readonly struct PortCell
{
    internal PortCell(int index, GridCell cell, GridStep? toward, int type, int role)
    {
        Index = index;
        Cell = cell;
        Toward = toward;
        Type = type;
        Role = role;
    }

    internal int Index { get; }

    internal GridCell Cell { get; }

    internal GridStep? Toward { get; }

    internal int Type { get; }

    internal int Role { get; }
}

/// <summary>The ports grid_survey lists for a device, from its ends as they stand or as a prefab would stand.</summary>
internal static class PortCells
{
    /// <summary>The ends whose type shares a bit with the mask, each keeping its index among all the ends.</summary>
    internal static List<PortCell> Of(IReadOnlyList<PieceEnd> ends, int typeMask)
    {
        List<PortCell> ports = new List<PortCell>();
        for (int index = 0; index < ends.Count; index++)
        {
            PieceEnd end = ends[index];
            if ((end.Type & typeMask) != 0)
            {
                ports.Add(new PortCell(index, end.Local, GridStep.Between(end.Local, end.Facing), end.Type, end.Role));
            }
        }

        return ports;
    }
}
