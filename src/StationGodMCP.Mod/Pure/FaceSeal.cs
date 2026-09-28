#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A structure as air sees it: its id and whether it blocks air (!Structure.CanAirPass).</summary>
internal readonly struct AirBlocker
{
    internal AirBlocker(long id, bool blocksAir)
    {
        Id = id;
        BlocksAir = blocksAir;
    }

    internal long Id { get; }

    internal bool BlocksAir { get; }
}

/// <summary>
/// Whether a face between two 2 m cells still keeps air apart once a set of structures is gone. The game lets air
/// through a face only when nothing on the face blocks it and neither cell's own structure does
/// (GridController.CanAirPass reads the cell's structure and every face structure; Atmosphere.ReactWithStructures
/// skips a neighbour whose cell IsBlocked): a wall plate on the face of a finished frame opens nothing, and two plates
/// back to back on one face open it only when both go. The removal is judged as a whole: every structure the request
/// removes counts as gone at once.
/// </summary>
internal static class FaceSeal
{
    /// <param name="onFace">Every structure registered on the face.</param>
    /// <param name="cellA">The structure filling the cell on one side, if any.</param>
    /// <param name="cellB">The structure filling the cell on the other side, if any.</param>
    /// <param name="removed">Every structure the request removes.</param>
    internal static bool Sealed(IEnumerable<AirBlocker> onFace, AirBlocker? cellA, AirBlocker? cellB,
        ICollection<long> removed)
    {
        foreach (AirBlocker blocker in onFace)
        {
            if (Stays(blocker, removed))
            {
                return true;
            }
        }

        return (cellA.HasValue && Stays(cellA.Value, removed)) || (cellB.HasValue && Stays(cellB.Value, removed));
    }

    private static bool Stays(AirBlocker blocker, ICollection<long> removed) =>
        blocker.BlocksAir && !removed.Contains(blocker.Id);
}
