#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The broken pieces touching a pipe, cable or chute network at its members' ends without being on it
/// (BrokenNeighbourSearch over the game's own attachment, EndsReader.AttachedAt, through ends of the network's kind):
/// a burst or broken piece still sits in its cell, joined at its ends, yet need not be in the network's member list, so
/// a network read from its members alone shows the line whole and "0 damaged" while a broken junction leaks on it.
/// </summary>
internal static class BrokenNeighbours
{
    /// <summary>
    /// The report for a network of the kind, whose members are its pieces and devices: the count, the first
    /// ReplyDefaults.BrokenNeighbours of them (the rest noted as truncated) and the warning about the list named listed.
    /// </summary>
    internal static BrokenNeighbourReport Around(string kind, IReadOnlyList<Thing> members, string listed)
    {
        List<BrokenNeighbour<Thing>> found = Find(kind, members);
        if (found.Count == 0)
        {
            return BrokenNeighbourReport.None;
        }

        int shown = System.Math.Min(found.Count, ReplyDefaults.BrokenNeighbours);
        List<BrokenNeighbourView> views = new List<BrokenNeighbourView>(shown);
        for (int index = 0; index < shown; index++)
        {
            views.Add(ViewOf(found[index]));
        }

        Truncations.Note("broken_neighbours", shown, found.Count,
            "thing_health {broken_only: true, near_player_m} or find_things {broken: true} near the network");
        return new BrokenNeighbourReport(views, found.Count,
            BrokenNeighbourSearch.Warning(found.Count, kind, listed, "broken_neighbours"));
    }

    /// <summary>The warning alone, for a reply that lists none of them (connections' paged members).</summary>
    internal static string? WarningFor(string kind, IReadOnlyList<Thing> members, string listed, string seeAt) =>
        BrokenNeighbourSearch.Warning(Find(kind, members).Count, kind, listed, seeAt);

    private static List<BrokenNeighbour<Thing>> Find(string kind, IReadOnlyList<Thing> members)
    {
        NetworkType types = NetworkReader.EndTypesOf(kind);
        return BrokenNeighbourSearch.Find(members, static thing => thing.ReferenceId,
            thing => AttachedAt(thing, types), static thing => !thing.IsBeingDestroyed && Wrecks.IsBroken(thing));
    }

    private static List<Thing> AttachedAt(Thing thing, NetworkType types)
    {
        List<Thing> attached = new List<Thing>();
        if (!(thing is SmallGrid grid) || grid.OpenEnds == null)
        {
            return attached;
        }

        foreach (Connection end in grid.OpenEnds)
        {
            if (end != null && (end.ConnectionType & types) != NetworkType.None)
            {
                attached.AddRange(EndsReader.AttachedAt(grid, end));
            }
        }

        return attached;
    }

    private static BrokenNeighbourView ViewOf(BrokenNeighbour<Thing> neighbour)
    {
        Thing piece = neighbour.Piece;
        return new BrokenNeighbourView(GameLookup.ViewOf(piece), GameLookup.ViewOf(piece.Position),
            piece is Pipe pipe ? HealthReader.PipeBurstName(pipe.IsBurst) : null,
            piece is SmallGrid grid ? EndsReader.OwnNetwork(grid) : null,
            new ThingId(neighbour.Touches.ReferenceId));
    }
}
