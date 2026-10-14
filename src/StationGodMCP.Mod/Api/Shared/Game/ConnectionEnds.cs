#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Connection ends (CODE, Assets.Scripts.Objects): a SmallGrid (pipe, cable, chute, device) keeps its ends in
/// SmallGrid.OpenEnds. A Connection has ConnectionType (NetworkType flags), ConnectionRole, a Transform, and two 0.5 m
/// small-grid cells, LocalGrid and FacingGrid (Connection.SetGrids). Another SmallGrid is attached at an end when one
/// of its ends of a shared type has LocalGrid equal to this end's FacingGrid (SmallGrid.IsConnected; a Pipe also needs
/// matching gas or liquid content). The game looks in the SmallCell at the end's LocalGrid
/// (Connection.GetSmallGridOccupant), which keeps one Chute, Pipe, Device, Cable, Other and Rail; this checks every
/// slot of both cells with IsConnected, so two things sharing a cell are both found. The network at an end is the one
/// Device.PrintDebugInfo prints: the attached pipe's PipeNetwork, cable's CableNetwork or chute's ChuteNetwork; a pipe,
/// cable or chute's own ends are on its own network. type_name and role_name are the game's display names
/// (LocalizedEnumCollections.GetName).
/// </summary>
internal static class EndsReader
{
    internal static ConnectionsView Read(Thing thing)
    {
        if (!(thing is SmallGrid grid))
        {
            throw ApiErrors.Refused(
                "not_connectable",
                $"{Names.Of(thing)} ({thing.GetType().Name}) is not a pipe, cable, chute or device, so it has no "
                + "connection ends.");
        }

        List<Connection> ends = grid.OpenEnds != null ? new List<Connection>(grid.OpenEnds) : new List<Connection>();
        List<ConnectionEndView> views = new List<ConnectionEndView>(ends.Count);
        for (int index = 0; index < ends.Count; index++)
        {
            if (ends[index] != null)
            {
                views.Add(ReadEnd(grid, ends[index], index));
            }
        }

        return new ConnectionsView(
            GameLookup.ViewOf(thing), GameLookup.ViewOf(thing.Position), OwnNetwork(grid), views,
            Orientations.Of(thing), ThingColors.ShownColorOf(thing, ThingColors.Swatches()));
    }

    private static ConnectionEndView ReadEnd(SmallGrid grid, Connection end, int index)
    {
        List<Thing> attached = AttachedAt(grid, end);
        List<ThingView> connected = new List<ThingView>(attached.Count);
        foreach (Thing thing in attached)
        {
            connected.Add(GameLookup.ViewOf(thing));
        }

        ConnectionKind kind = new ConnectionKind(
            end.ConnectionType.ToString(), TypeName(end.ConnectionType), end.ConnectionRole.ToString(),
            RoleName(end.ConnectionRole));
        PositionView? position = end.Transform != null ? GameLookup.ViewOf(end.Transform.position) : null;
        return new ConnectionEndView(index, kind, position, EndNetwork(grid, end, attached), connected);
    }

    private static string? TypeName(NetworkType type)
    {
        try
        {
            return NonEmpty(type.GetName());
        }
        catch (Exception)
        {
            // LocalizedEnumCollections.GetName has no name for a NetworkType flag combination it does not declare.
            return null;
        }
    }

    private static string? RoleName(ConnectionRole role)
    {
        try
        {
            return NonEmpty(role.GetName());
        }
        catch (Exception)
        {
            // LocalizedEnumCollections.GetName, as for the type.
            return null;
        }
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>
    /// The networks a thing is part of (a cable, pipe or chute piece) or its ends join, each once, as inspect connections
    /// reports them; empty for a thing that is not on the small grid.
    /// </summary>
    internal static List<NetworkRefView> NetworksOf(Thing thing)
    {
        List<NetworkRefView> found = new List<NetworkRefView>();
        if (!(thing is SmallGrid grid))
        {
            return found;
        }

        AddNetwork(found, OwnNetwork(grid));
        if (grid.OpenEnds == null)
        {
            return found;
        }

        foreach (Connection end in grid.OpenEnds)
        {
            if (end != null)
            {
                AddNetwork(found, EndNetwork(grid, end, AttachedAt(grid, end)));
            }
        }

        return found;
    }

    private static void AddNetwork(List<NetworkRefView> found, NetworkRefView? network)
    {
        if (network != null && !found.Exists(known => known.Kind == network.Kind && known.Id.Equals(network.Id)))
        {
            found.Add(network);
        }
    }

    // Each occupant of the end's two small-grid cells that the game's own IsConnected links to it, not the owner.
    internal static List<Thing> AttachedAt(SmallGrid owner, Connection end)
    {
        List<Thing> found = new List<Thing>();
        GridController world = GridController.World;
        if (world == null || end.Transform == null)
        {
            return found;
        }

        AddAttached(world.GetSmallCell(end.GetLocalGrid()), owner, end, found);
        AddAttached(world.GetSmallCell(end.GetFacingGrid()), owner, end, found);
        return found;
    }

    private static void AddAttached(SmallCell? cell, SmallGrid owner, Connection end, List<Thing> found)
    {
        if (cell == null)
        {
            return;
        }

        ISmallGrid[] occupants = { cell.Chute, cell.Pipe, cell.Device, cell.Cable, cell.Other, cell.Rail };
        foreach (ISmallGrid occupant in occupants)
        {
            Thing? thing = occupant as Thing;
            if (thing != null && thing != owner && !found.Contains(thing) && IsConnected(occupant, end))
            {
                found.Add(thing);
            }
        }
    }

    private static bool IsConnected(ISmallGrid occupant, Connection end)
    {
        try
        {
            return occupant.IsConnected(end);
        }
        catch (Exception)
        {
            // SmallGrid.IsConnected reads the occupant's ends' transforms, which a half-built or removed thing lacks.
            return false;
        }
    }

    internal static NetworkRefView? OwnNetwork(SmallGrid grid)
    {
        switch (grid)
        {
            case Pipe pipe:
                return Ref("pipe", pipe.PipeNetwork);
            case Cable cable:
                return Ref("cable", cable.CableNetwork);
            case Chute chute:
                return Ref("chute", chute.ChuteNetwork);
            default:
                return null;
        }
    }

    private static NetworkRefView? EndNetwork(SmallGrid owner, Connection end, List<Thing> attached)
    {
        NetworkType type = end.ConnectionType;
        if ((type & (NetworkType.Pipe | NetworkType.PipeLiquid)) != NetworkType.None)
        {
            PipeNetwork? own = owner is INetworkedPipe pipe ? pipe.PipeNetwork : null;
            return Ref("pipe", own ?? FirstPipeNetwork(attached));
        }

        if ((type & NetworkType.PowerAndData) != NetworkType.None)
        {
            CableNetwork? own = owner is Cable cable ? cable.CableNetwork : null;
            return Ref("cable", own ?? FirstCableNetwork(attached));
        }

        if ((type & NetworkType.Chute) != NetworkType.None)
        {
            ChuteNetwork? own = owner is Chute chute ? chute.ChuteNetwork : null;
            return Ref("chute", own ?? FirstChuteNetwork(attached));
        }

        return null;
    }

    private static PipeNetwork? FirstPipeNetwork(List<Thing> attached)
    {
        foreach (Thing thing in attached)
        {
            if (thing is INetworkedPipe pipe && pipe.PipeNetwork != null)
            {
                return pipe.PipeNetwork;
            }
        }

        return null;
    }

    private static CableNetwork? FirstCableNetwork(List<Thing> attached)
    {
        foreach (Thing thing in attached)
        {
            if (thing is Cable cable && cable.CableNetwork != null)
            {
                return cable.CableNetwork;
            }
        }

        return null;
    }

    private static ChuteNetwork? FirstChuteNetwork(List<Thing> attached)
    {
        foreach (Thing thing in attached)
        {
            if (thing is Chute chute && chute.ChuteNetwork != null)
            {
                return chute.ChuteNetwork;
            }
        }

        return null;
    }

    private static NetworkRefView? Ref(string kind, IReferencable? network) =>
        network != null ? new NetworkRefView(kind, new ThingId(network.ReferenceId)) : null;
}
