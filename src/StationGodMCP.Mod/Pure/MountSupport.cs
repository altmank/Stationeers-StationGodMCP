#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Whether a mounted or standing piece keeps something to rest on once a removal is done. Each face its back or bottom
/// rests on (MountRect.Faces) is held by the structures registered on that face (walls, windows, floor plates) and by
/// the structures filling the two cells beside it (frames). A piece loses its support when a face the removed piece
/// holds is left with no holder at all: the game leaves it where it was, hanging in the air.
/// </summary>
internal static class MountSupport
{
    /// <param name="faceHolders">For each face the piece rests on, the ids of everything holding that face now.</param>
    /// <param name="piece">The structure being judged for removal.</param>
    /// <param name="removed">Every structure the request removes.</param>
    internal static bool Loses(IEnumerable<IReadOnlyCollection<long>> faceHolders, long piece,
        ICollection<long> removed)
    {
        foreach (IReadOnlyCollection<long> holders in faceHolders)
        {
            if (Contains(holders, piece) && !AnyStays(holders, piece, removed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyStays(IReadOnlyCollection<long> holders, long piece, ICollection<long> removed)
    {
        foreach (long holder in holders)
        {
            if (holder != piece && !removed.Contains(holder))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(IReadOnlyCollection<long> holders, long piece)
    {
        foreach (long holder in holders)
        {
            if (holder == piece)
            {
                return true;
            }
        }

        return false;
    }
}
