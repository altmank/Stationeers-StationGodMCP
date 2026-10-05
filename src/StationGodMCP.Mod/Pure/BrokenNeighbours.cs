#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationGodMCP.Pure;

/// <summary>A broken piece that touches a network without being on it, and the thing it was found from.</summary>
internal sealed class BrokenNeighbour<T>
{
    internal BrokenNeighbour(T piece, T touches)
    {
        Piece = piece;
        Touches = touches;
    }

    internal T Piece { get; }

    /// <summary>The member, or the broken piece nearer the network, that it is attached to.</summary>
    internal T Touches { get; }
}

/// <summary>
/// The broken pieces that touch a network but are not among its members. Attachment is the game's own
/// (SmallGrid.IsConnected: an end's facing cell is another thing's end cell), which does not ask whether either side is
/// broken, so a broken or burst piece still meets the line at its ends while the network's member list may not hold
/// it. The search starts at every member, takes each attached thing that is broken and not a member, and goes on from
/// those through further broken pieces, so a broken stretch is found whole. Each piece once, in the order found.
/// </summary>
internal static class BrokenNeighbourSearch
{
    internal static List<BrokenNeighbour<T>> Find<T>(IReadOnlyList<T> members, Func<T, long> idOf,
        Func<T, IEnumerable<T>> attachedTo, Func<T, bool> isBroken)
    {
        HashSet<long> seen = new HashSet<long>();
        Queue<T> frontier = new Queue<T>(members.Count);
        foreach (T member in members)
        {
            seen.Add(idOf(member));
            frontier.Enqueue(member);
        }

        List<BrokenNeighbour<T>> found = new List<BrokenNeighbour<T>>();
        while (frontier.Count > 0)
        {
            T from = frontier.Dequeue();
            foreach (T next in attachedTo(from))
            {
                if (!isBroken(next) || !seen.Add(idOf(next)))
                {
                    continue;
                }

                found.Add(new BrokenNeighbour<T>(next, from));
                frontier.Enqueue(next);
            }
        }

        return found;
    }

    /// <summary>
    /// The reply's warning when broken pieces touch the network; null when none do. listed names the reply's list of
    /// the network's own things, which does not show them; seeAt where they are listed.
    /// </summary>
    internal static string? Warning(int count, string kind, string listed, string seeAt) =>
        count == 0
            ? null
            : count == 1
                ? $"1 broken {kind} piece touches this network at its ends but is not on it, so {listed} does not " +
                  $"show it: see {seeAt}."
                : $"{count.ToString(CultureInfo.InvariantCulture)} broken {kind} pieces touch this network at its " +
                  $"ends but are not on it, so {listed} does not show them: see {seeAt}.";
}

/// <summary>
/// How a piece the upgrade and clean tools leave alone is replaced in one job: the place tool's piece form at its cell
/// with its own ends and grade (waypoints from end to end for a piece longer than a cell), with the old piece in
/// remove_ids. The place tools ask the game's cursor rules wherever they build, rocket cells included, and the new
/// piece joins whatever its ends meet, as a piece a player builds does.
/// </summary>
internal static class ReplaceInPlace
{
    internal static string Call(string placeTool, long id, PieceModel piece, string? grade)
    {
        StringBuilder call = new StringBuilder(placeTool).Append(" {");
        if (piece.Cells.Count == 1)
        {
            call.Append("piece: {at: ").Append(Point(piece.Cells[0])).Append(", ends: [");
            List<string> directions = EndCleanup.DirectionsOf(piece.Ends);
            for (int index = 0; index < directions.Count; index++)
            {
                call.Append(index > 0 ? ", " : string.Empty).Append('"').Append(directions[index]).Append('"');
            }

            call.Append("]}");
        }
        else
        {
            call.Append("waypoints: [").Append(Point(Extreme(piece.Cells, -1))).Append(", ")
                .Append(Point(Extreme(piece.Cells, 1))).Append(']');
        }

        call.Append(", grade: \"").Append(grade ?? "<its grade>").Append("\", remove_ids: [\"")
            .Append(id.ToString(CultureInfo.InvariantCulture)).Append("\"]}");
        return call.ToString();
    }

    /// <summary>The refusal's advice: the call, run as a dry run first.</summary>
    internal static string Advice(string placeTool, long id, PieceModel piece, string? grade) =>
        "To replace it in one job, as a player would: " + Call(placeTool, id, piece, grade) +
        " (a dry run first, then dry_run false and confirm true).";

    // The cell with the lowest (sign -1) or highest (sign 1) x + y + z: the two ends of a straight piece.
    private static GridCell Extreme(IReadOnlyList<GridCell> cells, int sign)
    {
        GridCell best = cells[0];
        for (int index = 1; index < cells.Count; index++)
        {
            GridCell cell = cells[index];
            if (sign * (cell.X + cell.Y + cell.Z) > sign * (best.X + best.Y + best.Z))
            {
                best = cell;
            }
        }

        return best;
    }

    // [x, y, z] in metres (cells are in decimetres).
    private static string Point(GridCell cell) =>
        string.Format(CultureInfo.InvariantCulture, "[{0:0.##}, {1:0.##}, {2:0.##}]", cell.X / 10.0, cell.Y / 10.0,
            cell.Z / 10.0);
}

/// <summary>
/// Whether a pipe is still on the line its ends meet: the network of every pipe attached at its ends, against its
/// own. A swap joins the replacement to the old piece's own network (UpgradeSwap), which is only right when that is
/// the line's network: a piece that left its line would take its replacement off the line with it
/// (ReferencableNetwork.Add moves a member from the network it is on).
/// </summary>
internal static class LineMembership
{
    /// <summary>
    /// True when the piece meets no pipe (nothing to compare), or one of the pipes it meets shares its network; false
    /// when it is on no network or on one none of them is on.
    /// </summary>
    internal static bool OnItsLine(long? own, IReadOnlyList<long?> attached)
    {
        if (attached.Count == 0)
        {
            return true;
        }

        if (!own.HasValue)
        {
            return false;
        }

        foreach (long? network in attached)
        {
            if (network == own)
            {
                return true;
            }
        }

        return false;
    }
}
