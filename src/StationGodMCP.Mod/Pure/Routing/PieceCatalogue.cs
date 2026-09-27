#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A one-cell piece of a kit at one rotation: its place in the kit, the rotation's index, its ends, and the direction
/// of its one output end when it has one (a chute junction: ConnectionRole Output or Output2).
/// </summary>
internal readonly struct PieceOption
{
    internal PieceOption(int piece, int rotation, EndSet ends, GridStep? output = null)
    {
        Piece = piece;
        Rotation = rotation;
        Ends = ends;
        Output = output;
    }

    /// <summary>The piece's index in the kit's own order.</summary>
    internal int Piece { get; }

    /// <summary>The rotation's index among the 24 axis rotations.</summary>
    internal int Rotation { get; }

    internal EndSet Ends { get; }

    /// <summary>Where its output end points; null for a piece whose ends have no direction.</summary>
    internal GridStep? Output { get; }
}

/// <summary>
/// Which one-cell piece of a kit, turned how, has exactly a set of ends: the first in the kit's order, then in
/// rotation order, as the kit's own merge picks the first matching constructable (MultiMergeConstructor.Construct).
/// Pieces of more than one cell (the long straights) are never offered: LU prefers single straights, and a run is
/// built one cell at a time. A directed piece (a chute junction) fits the same ends at more than one turn, each
/// sending items out a different way: Orientations lists the first option per output direction, and the caller picks.
/// </summary>
internal sealed class PieceCatalogue
{
    private readonly Dictionary<int, List<PieceOption>> _byMask = new Dictionary<int, List<PieceOption>>();
    private readonly Dictionary<int, PieceOption> _first = new Dictionary<int, PieceOption>();

    internal PieceCatalogue(IEnumerable<PieceOption> options)
    {
        foreach (PieceOption option in options)
        {
            if (!_first.ContainsKey(option.Ends.Mask))
            {
                _first[option.Ends.Mask] = option;
                _byMask[option.Ends.Mask] = new List<PieceOption>();
            }

            List<PieceOption> turns = _byMask[option.Ends.Mask];
            if (!turns.Exists(turn => Same(turn.Output, option.Output)))
            {
                turns.Add(option);
            }
        }
    }

    internal PieceOption? Find(EndSet ends) => _first.TryGetValue(ends.Mask, out PieceOption option) ? option : null;

    /// <summary>
    /// The first option with the ends for each direction its output end can point; one entry (or none) for pieces
    /// without direction.
    /// </summary>
    internal List<PieceOption> Orientations(EndSet ends) =>
        _byMask.TryGetValue(ends.Mask, out List<PieceOption> turns)
            ? new List<PieceOption>(turns)
            : new List<PieceOption>();

    private static bool Same(GridStep? a, GridStep? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || a.Value.Equals(b!.Value));

    /// <summary>The shapes the kit offers, for a refusal's message.</summary>
    internal List<string> Shapes()
    {
        HashSet<string> seen = new HashSet<string>();
        List<string> shapes = new List<string>();
        foreach (PieceOption option in _first.Values)
        {
            if (seen.Add(option.Ends.Shape))
            {
                shapes.Add(option.Ends.Shape);
            }
        }

        shapes.Sort(System.StringComparer.Ordinal);
        return shapes;
    }
}
