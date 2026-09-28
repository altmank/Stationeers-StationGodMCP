#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A 0.5 m small-grid cell (the game's Grid3, in decimetres).</summary>
internal readonly struct GridCell : IEquatable<GridCell>
{
    internal GridCell(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal int X { get; }

    internal int Y { get; }

    internal int Z { get; }

    public bool Equals(GridCell other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is GridCell other && Equals(other);

    public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791));

    /// <summary>The point in metres, as players and the other tools give positions (the fields are decimetres).</summary>
    public override string ToString() => GridText.Metres(X, Y, Z);
}

/// <summary>
/// One connection end: the cell its transform sits in (Connection.LocalGrid), the cell one step along its forward
/// (FacingGrid), its NetworkType flags and its ConnectionRole, as integers.
/// </summary>
internal readonly struct PieceEnd : IEquatable<PieceEnd>
{
    internal PieceEnd(GridCell local, GridCell facing, int type, int role)
    {
        Local = local;
        Facing = facing;
        Type = type;
        Role = role;
    }

    internal GridCell Local { get; }

    internal GridCell Facing { get; }

    internal int Type { get; }

    internal int Role { get; }

    public bool Equals(PieceEnd other) =>
        Local.Equals(other.Local) && Facing.Equals(other.Facing) && Type == other.Type && Role == other.Role;

    public override bool Equals(object? obj) => obj is PieceEnd other && Equals(other);

    public override int GetHashCode() => unchecked((Local.GetHashCode() * 31 + Facing.GetHashCode()) * 31 + Type);
}

/// <summary>What a pipe carries (Pipe.ContentType as an integer), and whether it takes any (ContentType.All).</summary>
internal sealed class PipeContent
{
    internal PipeContent(int kind, bool takesAny)
    {
        Kind = kind;
        TakesAny = takesAny;
    }

    internal int Kind { get; }

    internal bool TakesAny { get; }

    /// <summary>Pipe.IsContentMatch: the same content, or this pipe takes any.</summary>
    internal bool Accepts(PipeContent other) => other.Kind == Kind || TakesAny;
}

/// <summary>A thing on the small grid as connectivity sees it: its id, the cells it occupies and its ends.</summary>
internal sealed class PieceModel
{
    internal PieceModel(long id, IReadOnlyList<GridCell> cells, IReadOnlyList<PieceEnd> ends, PipeContent? content)
    {
        Id = id;
        Cells = cells;
        Ends = ends;
        Content = content;
    }

    internal long Id { get; }

    internal IReadOnlyList<GridCell> Cells { get; }

    internal IReadOnlyList<PieceEnd> Ends { get; }

    /// <summary>What it carries when it is a pipe; null for anything else.</summary>
    internal PipeContent? Content { get; }

    internal bool Occupies(GridCell cell)
    {
        for (int index = 0; index < Cells.Count; index++)
        {
            if (Cells[index].Equals(cell))
            {
                return true;
            }
        }

        return false;
    }

}

/// <summary>A directed link: To is among the things From's SmallGrid.FillConnected finds.</summary>
internal readonly struct Link : IEquatable<Link>
{
    internal Link(long from, long to)
    {
        From = from;
        To = to;
    }

    internal long From { get; }

    internal long To { get; }

    public bool Equals(Link other) => From == other.From && To == other.To;

    public override bool Equals(object? obj) => obj is Link other && Equals(other);

    public override int GetHashCode() => unchecked(From.GetHashCode() * 397 ^ To.GetHashCode());

    public override string ToString() => $"{From}->{To}";
}

/// <summary>Links present after but not before (Added) and before but not after (Lost).</summary>
internal sealed class LinkDiff
{
    internal LinkDiff(List<Link> added, List<Link> lost)
    {
        Added = added;
        Lost = lost;
    }

    internal List<Link> Added { get; }

    internal List<Link> Lost { get; }

    internal bool Same => Added.Count == 0 && Lost.Count == 0;
}

/// <summary>
/// The game's connection rule on plain values, so a planned replacement can be checked before anything is built.
/// SmallGrid.FillConnected looks, for each end of a thing, in the cell the end sits in and keeps each occupant whose
/// IsConnected accepts the end: SmallGrid.IsConnected wants an end of its own that shares a NetworkType flag and sits
/// in the first end's facing cell; Pipe.IsConnected also wants the content to match when both are pipes.
/// </summary>
internal static class Connectivity
{
    /// <summary>Whether <paramref name="to"/> is among what <paramref name="from"/>'s FillConnected finds.</summary>
    internal static bool Links(PieceModel from, PieceModel to)
    {
        if (from.Id == to.Id)
        {
            return false;
        }

        for (int index = 0; index < from.Ends.Count; index++)
        {
            PieceEnd end = from.Ends[index];
            if (to.Occupies(end.Local) && Accepts(to, end, from.Content))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Accepts(PieceModel owner, PieceEnd other, PipeContent? otherContent)
    {
        if (owner.Content != null && otherContent != null && !owner.Content.Accepts(otherContent))
        {
            return false;
        }

        for (int index = 0; index < owner.Ends.Count; index++)
        {
            PieceEnd end = owner.Ends[index];
            if ((end.Type & other.Type) != 0 && end.Local.Equals(other.Facing))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The piece's ends that take part in a link with any of the others, in the piece's own order: an end links out
    /// when another occupies its cell and accepts it (Links), and links in when another's end sits in one of the
    /// piece's cells and faces this end's cell with a shared NetworkType (the other's FillConnected finding the piece
    /// through this end, SmallGrid.IsConnected). Every other end is open: nothing is connected to it.
    /// </summary>
    internal static List<PieceEnd> ConnectedEnds(PieceModel piece, IReadOnlyList<PieceModel> others)
    {
        List<PieceEnd> connected = new List<PieceEnd>(piece.Ends.Count);
        for (int index = 0; index < piece.Ends.Count; index++)
        {
            PieceEnd end = piece.Ends[index];
            if (LinksThrough(piece, end, others))
            {
                connected.Add(end);
            }
        }

        return connected;
    }

    private static bool LinksThrough(PieceModel piece, PieceEnd end, IReadOnlyList<PieceModel> others)
    {
        for (int index = 0; index < others.Count; index++)
        {
            PieceModel other = others[index];
            if (other.Id == piece.Id)
            {
                continue;
            }

            bool outward = other.Occupies(end.Local) && Accepts(other, end, piece.Content);
            if (outward || ReachesInThrough(other, piece, end))
            {
                return true;
            }
        }

        return false;
    }

    // Links(other, piece) found through this end of the piece: the other's end sits in the piece's cells and the
    // piece's end is the one whose cell that end faces.
    private static bool ReachesInThrough(PieceModel other, PieceModel piece, PieceEnd end)
    {
        if (piece.Content != null && other.Content != null && !piece.Content.Accepts(other.Content))
        {
            return false;
        }

        for (int index = 0; index < other.Ends.Count; index++)
        {
            PieceEnd theirs = other.Ends[index];
            if (piece.Occupies(theirs.Local) && (theirs.Type & end.Type) != 0 && end.Local.Equals(theirs.Facing))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every link among the models that starts or ends at a focus id.</summary>
    internal static HashSet<Link> LinksTouching(IReadOnlyList<PieceModel> models, HashSet<long> focus)
    {
        Dictionary<GridCell, List<PieceModel>> byCell = IndexByCell(models);
        HashSet<Link> links = new HashSet<Link>();
        for (int index = 0; index < models.Count; index++)
        {
            PieceModel from = models[index];
            bool fromFocus = focus.Contains(from.Id);
            for (int endIndex = 0; endIndex < from.Ends.Count; endIndex++)
            {
                if (!byCell.TryGetValue(from.Ends[endIndex].Local, out List<PieceModel>? there))
                {
                    continue;
                }

                foreach (PieceModel to in there)
                {
                    if ((fromFocus || focus.Contains(to.Id)) && Links(from, to))
                    {
                        links.Add(new Link(from.Id, to.Id));
                    }
                }
            }
        }

        return links;
    }

    private static Dictionary<GridCell, List<PieceModel>> IndexByCell(IReadOnlyList<PieceModel> models)
    {
        Dictionary<GridCell, List<PieceModel>> byCell = new Dictionary<GridCell, List<PieceModel>>();
        for (int index = 0; index < models.Count; index++)
        {
            PieceModel model = models[index];
            for (int cellIndex = 0; cellIndex < model.Cells.Count; cellIndex++)
            {
                GridCell cell = model.Cells[cellIndex];
                if (!byCell.TryGetValue(cell, out List<PieceModel>? list))
                {
                    list = new List<PieceModel>(2);
                    byCell[cell] = list;
                }

                if (!list.Contains(model))
                {
                    list.Add(model);
                }
            }
        }

        return byCell;
    }

    /// <summary>
    /// The links with every id replaced by its group's id (a piece swapped together with others, and every piece
    /// replacing them, count as one), dropping links inside a group; an id in no group stands for itself.
    /// </summary>
    internal static HashSet<Link> Grouped(IEnumerable<Link> links, IReadOnlyDictionary<long, long> groupOf)
    {
        HashSet<Link> grouped = new HashSet<Link>();
        foreach (Link link in links)
        {
            long from = groupOf.TryGetValue(link.From, out long a) ? a : link.From;
            long to = groupOf.TryGetValue(link.To, out long b) ? b : link.To;
            if (from != to)
            {
                grouped.Add(new Link(from, to));
            }
        }

        return grouped;
    }

    /// <summary>The links that touch none of the ids (those of removed pieces).</summary>
    internal static HashSet<Link> Without(HashSet<Link> links, HashSet<long> gone)
    {
        HashSet<Link> kept = new HashSet<Link>();
        foreach (Link link in links)
        {
            if (!gone.Contains(link.From) && !gone.Contains(link.To))
            {
                kept.Add(link);
            }
        }

        return kept;
    }

    /// <summary>The links only one side has, each list sorted by from then to.</summary>
    internal static LinkDiff Compare(HashSet<Link> before, HashSet<Link> after)
    {
        List<Link> added = new List<Link>();
        foreach (Link link in after)
        {
            if (!before.Contains(link))
            {
                added.Add(link);
            }
        }

        List<Link> lost = new List<Link>();
        foreach (Link link in before)
        {
            if (!after.Contains(link))
            {
                lost.Add(link);
            }
        }

        added.Sort(static (a, b) => a.From != b.From ? a.From.CompareTo(b.From) : a.To.CompareTo(b.To));
        lost.Sort(static (a, b) => a.From != b.From ? a.From.CompareTo(b.From) : a.To.CompareTo(b.To));
        return new LinkDiff(added, lost);
    }

    /// <summary>
    /// The same cells and the same ends (as multisets), so every IsConnected and FillConnected answer involving
    /// either is the same.
    /// </summary>
    internal static bool SameShape(PieceModel a, PieceModel b)
    {
        return SameCells(a.Cells, b.Cells) && SameEnds(a.Ends, b.Ends) && SameContent(a.Content, b.Content);
    }

    private static bool SameContent(PipeContent? a, PipeContent? b) =>
        a == null ? b == null : b != null && a.Kind == b.Kind && a.TakesAny == b.TakesAny;

    private static bool SameCells(IReadOnlyList<GridCell> a, IReadOnlyList<GridCell> b)
    {
        HashSet<GridCell> left = new HashSet<GridCell>();
        for (int index = 0; index < a.Count; index++)
        {
            left.Add(a[index]);
        }

        HashSet<GridCell> right = new HashSet<GridCell>();
        for (int index = 0; index < b.Count; index++)
        {
            right.Add(b[index]);
        }

        return left.SetEquals(right);
    }

    private static bool SameEnds(IReadOnlyList<PieceEnd> a, IReadOnlyList<PieceEnd> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        bool[] used = new bool[b.Count];
        for (int index = 0; index < a.Count; index++)
        {
            if (!TakeMatch(a[index], b, used))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TakeMatch(PieceEnd end, IReadOnlyList<PieceEnd> candidates, bool[] used)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            if (!used[index] && candidates[index].Equals(end))
            {
                used[index] = true;
                return true;
            }
        }

        return false;
    }
}
