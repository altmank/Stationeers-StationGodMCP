#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The order a run's new pieces are built in so that each joins what already stands. A piece the game registers
/// with no connected neighbour gets a network of its own (Pipe.OnRegistered: new PipeNetwork), and the piece that
/// later joins it to a standing network merges the two into whichever it met first (StructureNetwork.Merge), so the
/// standing network may be merged away and its id lost. Grown outward from the standing pieces instead, every new
/// piece meets only the network already there and joins it.
/// </summary>
internal static class GrowthOrder
{
    /// <summary>
    /// The pieces, each next one the first (in the given order) that connects to what stands or is built so far, by
    /// the game's own connection rule (Connectivity.Links, either way). A piece that connects to nothing yet waits;
    /// when none connects, the first left starts a new chain. With nothing standing, the given order.
    /// </summary>
    internal static List<T> From<T>(IReadOnlyList<PieceModel> standing, IReadOnlyList<T> pieces,
        Func<T, PieceModel> modelOf)
    {
        List<T> ordered = new List<T>(pieces.Count);
        if (standing.Count == 0)
        {
            ordered.AddRange(pieces);
            return ordered;
        }

        List<PieceModel> grown = new List<PieceModel>(standing);
        List<T> left = new List<T>(pieces);
        while (left.Count > 0)
        {
            int next = Math.Max(left.FindIndex(piece => Connects(modelOf(piece), grown)), 0);
            T chosen = left[next];
            left.RemoveAt(next);
            ordered.Add(chosen);
            grown.Add(modelOf(chosen));
        }

        return ordered;
    }

    private static bool Connects(PieceModel piece, List<PieceModel> grown) =>
        grown.Exists(other => Connectivity.Links(piece, other) || Connectivity.Links(other, piece));
}
