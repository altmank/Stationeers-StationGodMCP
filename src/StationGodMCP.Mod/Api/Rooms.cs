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
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// rooms: the game's closed rooms (Room.AllRooms) measured cell by cell. Read only.
///
/// A Room is a flood fill of 2 m cells (Room.Grids, at most 1200) that closed; RoomController.RoomLookup maps each
/// cell centre to its room (see outer_frames for the cell rules). The air is summed fresh from every cell's
/// atmosphere (AtmosphericsController.SampleGlobalAtmosphere, the cell's own or the planet's) the way
/// Room.CacheRoomData pools it, rather than read from the room's cached fields, which only refresh when the game runs
/// its room jobs. On the main thread the Mole getters return last tick's values.
///
/// thermal_energy_j is GasMixture.TotalEnergy summed over the cells: sampled twice, its change over the seconds
/// between is the room's net heat flow in watts (machines, lights, sun, coolers), with gas moved in or out counted too.
/// </summary>
internal static class RoomsApi
{
    private const double TraceMoles = 1e-9;

    internal static RoomsView Handle(Args args)
    {
        ThingId? only = args.OptionalThingId("reference_id");
        bool includeCells = args.OptionalBool("include_cells") ?? false;
        bool includeDevices = args.OptionalBool("include_devices") ?? true;
        RoomController rooms = RoomController.World;
        AtmosphericsController air = AtmosphericsController.World;
        if (rooms == null || air == null)
        {
            throw ApiErrors.Refused("not_ready", "The room or atmospherics controller is not loaded.");
        }

        Room? playerRoom = RoomOf(Human.LocalHuman, rooms);
        List<Room> chosen = only.HasValue ? new List<Room> { RequireRoom(only.Value, rooms) } : LiveRooms();
        Dictionary<long, List<ThingView>>? devices = includeDevices ? DevicesByRoom(rooms) : null;
        List<RoomView> views = new List<RoomView>(chosen.Count);
        foreach (Room room in chosen)
        {
            views.Add(Measure(room, air, playerRoom, devices, includeCells));
        }

        views.Sort(static (a, b) => b.CellCount.CompareTo(a.CellCount));
        return new RoomsView(views, playerRoom != null ? IdOf(playerRoom) : null);
    }

    private static List<Room> LiveRooms()
    {
        List<Room> all = new List<Room>(Room.AllRooms);
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
            throw ApiErrors.Refused("not_in_room", $"Thing {id} is not in a closed room: its cell is outside.");
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
            $"No closed room has room_id {roomId}; the rooms tool lists them (a room's id changes when it is " +
            "worked out again).");
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
        foreach (Device device in Device.AllDevices.ToList())
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

    private static RoomView Measure(Room room, AtmosphericsController air, Room? playerRoom,
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
