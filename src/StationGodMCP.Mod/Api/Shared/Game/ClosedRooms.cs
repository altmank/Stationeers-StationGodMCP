#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using UnityEngine;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Closed rooms: the game's closed rooms (Room.AllRooms) measured cell by cell.
///
/// A Room is a flood fill of 2 m cells (Room.Grids, at most 1200) that closed; RoomController.RoomLookup maps each
/// cell centre to its room (see OuterFrames for the cell rules). The air is summed fresh from every cell's
/// atmosphere (AtmosphericsController.SampleGlobalAtmosphere, the cell's own or the planet's) the way
/// Room.CacheRoomData pools it, rather than read from the room's cached fields, which only refresh when the game runs
/// its room jobs. On the main thread the Mole getters return last tick's values.
///
/// thermal_energy_j is GasMixture.TotalEnergy summed over the cells: sampled twice, its change over the seconds
/// between is the room's net heat flow in watts (machines, lights, sun, coolers), with gas moved in or out counted too.
/// </summary>
internal static class ClosedRooms
{
    private const double TraceMoles = 1e-9;

    /// <summary>
    /// A measuring of the loaded world's rooms, with every cell centre (includeCells) and the devices in each room
    /// (includeDevices); refused not_ready while the room or atmospherics controller is not loaded.
    /// </summary>
    internal static RoomMeasure Measuring(bool includeCells, bool includeDevices)
    {
        RoomController rooms = RoomController.World;
        AtmosphericsController air = AtmosphericsController.World;
        if (rooms == null || air == null)
        {
            throw ApiErrors.Refused("not_ready", "find room: the room or atmospherics controller is not loaded.");
        }

        return new RoomMeasure(rooms, air, RoomOf(PlayerOrigin.Current().Player, rooms),
            includeDevices ? DevicesByRoom(rooms) : null, includeCells);
    }

    /// <summary>Every live room, largest first.</summary>
    internal static List<RoomView> All(RoomMeasure measure)
    {
        List<Room> live = LiveRooms();
        List<RoomView> views = new List<RoomView>(live.Count);
        foreach (Room room in live)
        {
            views.Add(measure.Of(room));
        }

        views.Sort(static (a, b) => b.CellCount.CompareTo(a.CellCount));
        return views;
    }

    internal static List<Room> LiveRooms()
    {
        IReadOnlyList<Room> all = World.Rooms;
        List<Room> live = new List<Room>(all.Count);
        foreach (Room room in all)
        {
            if (room != null && !room.IsDeletionCandidate && room.IsValid())
            {
                live.Add(room);
            }
        }

        return live;
    }

    /// <summary>The room a thing is in, or refused not_in_room.</summary>
    internal static Room RequireRoom(ThingId id, RoomController rooms)
    {
        Thing thing = GameLookup.RequireThing(id);
        Room? room = RoomOf(thing, rooms);
        if (room == null)
        {
            throw ApiErrors.Refused("not_in_room", $"Thing {id} is in no closed room: its cell is outside.");
        }

        return room;
    }

    /// <summary>A live room by the room_id this tool reports, or refused room_not_found.</summary>
    internal static Room RequireRoomById(ThingId roomId)
    {
        foreach (Room room in LiveRooms())
        {
            if (room.RoomId == roomId.Value)
            {
                return room;
            }
        }

        throw ApiErrors.Refused("room_not_found",
            $"No closed room has room id {roomId}; find room lists them (a room's id changes when it is worked out " +
            "again).");
    }

    internal static string IdOf(Room room) => room.RoomId.ToString(CultureInfo.InvariantCulture);

    // The cell of the centre (WorldGrid(Thing)) of the thing, or of its outermost holder when it is in a slot (a seed
    // in a tray sits on the cell boundary below the tray's cell; a carried item is where its player stands).
    private static Room? RoomOf(Thing? thing, RoomController rooms) =>
        thing == null ? null : rooms.GetRoom(new WorldGrid(HolderChain.PlaceOf(thing)).Value);

    // Device.AllDevices holds every registered device structure; each is placed by its own registered cell.
    private static Dictionary<long, List<ThingView>> DevicesByRoom(RoomController rooms)
    {
        Dictionary<long, List<ThingView>> byRoom = new Dictionary<long, List<ThingView>>();
        foreach (Device device in World.Devices())
        {
            if (device == null || device.IsCursor || device.IsBeingDestroyed || device.WorldGrid == WorldGrid.INVALID)
            {
                continue;
            }

            Room room = rooms.GetRoom(device.WorldGrid.Value);
            if (room == null)
            {
                continue;
            }

            if (!byRoom.TryGetValue(room.RoomId, out List<ThingView> list))
            {
                list = new List<ThingView>();
                byRoom[room.RoomId] = list;
            }

            list.Add(GameLookup.ViewOf(device));
        }

        return byRoom;
    }

    internal static RoomView Measure(Room room, AtmosphericsController air, Room? playerRoom,
        Dictionary<long, List<ThingView>>? devices, bool includeCells)
    {
        Chemistry.GasType[] types = GasTypes.All;
        bool[] isGas = new bool[types.Length];
        for (int index = 0; index < types.Length; index++)
        {
            isGas[index] = Mole.MatterState(types[index]) == AtmosphereHelper.MatterState.Gas;
        }

        RoomAir sum = new RoomAir(types.Length);
        List<WorldGrid> grids = new List<WorldGrid>(room.Grids);
        List<PositionView>? cells = includeCells ? new List<PositionView>(grids.Count) : null;
        foreach (WorldGrid grid in grids)
        {
            Vector3 centre = grid.Value.ToVector3();
            sum.AddCell(centre.x, centre.y, centre.z);
            cells?.Add(GameLookup.ViewOf(centre));
            Atmosphere atmosphere = air.SampleGlobalAtmosphere(grid);
            if (atmosphere == null)
            {
                continue;
            }

            sum.AddVolume(atmosphere.Volume.ToDouble());
            for (int index = 0; index < types.Length; index++)
            {
                Mole mole = atmosphere.GasMixture.GetMoleValue(types[index]);
                sum.AddGas(index, mole.Quantity.ToDouble(), mole.Energy.ToDouble(), mole.HeatCapacity.ToDouble(),
                    isGas[index]);
            }
        }

        List<ThingView>? inside = null;
        if (devices != null)
        {
            inside = devices.TryGetValue(room.RoomId, out List<ThingView> found) ? found : new List<ThingView>();
        }

        return new RoomView(IdOf(room), room.RoomType.ToString(), AirOf(sum), GasesOf(sum, types),
            new RoomBoundsView(new PositionView(sum.MinX, sum.MinY, sum.MinZ),
                new PositionView(sum.MaxX, sum.MaxY, sum.MaxZ)),
            ReferenceEquals(room, playerRoom), inside, cells);
    }

    private static RoomAirView AirOf(RoomAir sum) =>
        new RoomAirView(sum.CellCount, sum.VolumeL, sum.PressureKpa, sum.TemperatureK, sum.TotalMol,
            sum.HeatCapacityJPerK, sum.EnergyJ);

    private static List<RoomGasView> GasesOf(RoomAir sum, Chemistry.GasType[] types)
    {
        List<RoomGasView> gases = new List<RoomGasView>();
        for (int index = 0; index < types.Length; index++)
        {
            double moles = sum.MolesOf(index);
            if (moles > TraceMoles)
            {
                gases.Add(new RoomGasView(types[index].ToString(), moles, moles / sum.TotalMol));
            }
        }

        gases.Sort(static (a, b) => b.AmountMol.CompareTo(a.AmountMol));
        return gases;
    }
}

/// <summary>One call's measuring of rooms: the controllers, the player's room, and what each room adds.</summary>
internal sealed class RoomMeasure
{
    private readonly AtmosphericsController _air;
    private readonly Dictionary<long, List<ThingView>>? _devices;
    private readonly bool _cells;

    internal RoomMeasure(RoomController rooms, AtmosphericsController air, Room? playerRoom,
        Dictionary<long, List<ThingView>>? devices, bool cells)
    {
        Rooms = rooms;
        _air = air;
        PlayerRoom = playerRoom;
        _devices = devices;
        _cells = cells;
    }

    internal RoomController Rooms { get; }

    internal Room? PlayerRoom { get; }

    /// <summary>The player's room's id; null when the player is in none, or there is no player.</summary>
    internal string? PlayerRoomId => PlayerRoom != null ? ClosedRooms.IdOf(PlayerRoom) : null;

    internal RoomView Of(Room room) => ClosedRooms.Measure(room, _air, PlayerRoom, _devices, _cells);
}
