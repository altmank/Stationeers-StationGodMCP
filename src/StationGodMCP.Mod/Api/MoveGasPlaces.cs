#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// One end of a move as resolved: one atmosphere (with whatever the game joins to it), or a closed room's cells. A
/// closed set.
///
/// Rooms (CODE, Assets.Scripts.GridSystem.Room, AtmosphericsController): a room is a list of 2 m cells (Room.Grids),
/// and the game keeps no room-wide atmosphere: each cell has its own World-mode atmosphere
/// (AtmosphericsController.GetAtmosphereLocal, AtmosphericsManager.Find) that mixes with its neighbours every tick, and
/// Room.CacheRoomData only sums them for display. A room cell's atmosphere is never removed while it is in a room
/// (Atmosphere.IsLive is true for IsRoom). A cell with no atmosphere of its own is left out: there is nothing in it to
/// take, and giving it gas would mean creating an atmosphere, which only the game's own events do.
///   From a room, each gas is taken from every cell in proportion to what the cell holds (amount_mol caps the
///   total), each with its own share of energy, in one atmospherics tick, as from a joined set.
///   Into a room, each gas is spread over the cells in proportion to their volume, with its energy in the same
///   proportion, so every cell gets the same mix at once. Putting it all into one cell and letting the game mix
///   would instead leave that cell at many times the room's pressure for as long as the game takes to spread it
///   over up to 1200 cells, a local pressure spike and a wrong room reading meanwhile; spread by volume, the room is
///   immediately where the game's mixing would settle it (for gases; liquids settle as the game moves them).
/// Cells are found when the move is asked for; a cell whose atmosphere is gone by the tick is skipped, and a move
/// fails only when every cell is gone.
/// </summary>
internal abstract class GasPlace
{
    private GasPlace()
    {
    }

    /// <summary>Whether move_gas must be told which gases to take from here (a room: never empty it silently).</summary>
    internal abstract bool RequiresNamedGases { get; }

    internal static GasPlace Resolve(GasPlaceArg arg, string name) => arg switch
    {
        GasPlaceArg.Atmosphere atmosphere => new One(GasEnd.Resolve(atmosphere.Id, name)),
        GasPlaceArg.Room room => RoomCells.Of(RoomsApi.RequireRoomById(room.RoomId), name),
        GasPlaceArg.RoomOf roomOf => RoomCells.Of(RoomsApi.RequireRoom(roomOf.ThingId, Rooms()), name),
        _ => throw ApiErrors.Refused("refused",
            $"'{name}': the planet only gives gas up (from \"planet\" with delete: true); gas is not moved into it."),
    };

    internal abstract bool SameAs(GasPlace other);

    internal abstract GasSide SideOf(bool joined);

    /// <summary>The prediction: where the moved moles and energy go on the receiving side.</summary>
    internal abstract void Receive(GasSide side, int gasIndex, double moles, double energy);

    /// <summary>The move itself, on the atmospherics thread: the gas added with the game's GasMixture.Add.</summary>
    internal abstract void Deliver(GasSide side, Chemistry.GasType gas, double moles, double energy);

    /// <summary>Whether the move can no longer be applied because an atmosphere of this side was destroyed.</summary>
    internal abstract bool IsGone(GasSide side);

    /// <summary>The room, for a room; null for an atmosphere, whose members are listed instead.</summary>
    internal virtual GasRoomView? RoomView(GasSnapshot[] cells) => null;

    private static RoomController Rooms() =>
        RoomController.World ?? throw ApiErrors.Refused("not_ready", "The room controller is not loaded.");

    private static void Add(Atmosphere atmosphere, Chemistry.GasType gas, double moles, double energy) =>
        atmosphere.GasMixture.Add(new Mole(gas, new MoleQuantity(moles), new MoleEnergy(energy)));

    /// <summary>A named atmosphere; the side is it and, when joined, what the game mixes with it.</summary>
    internal sealed class One : GasPlace
    {
        internal One(GasEnd end)
        {
            End = end;
        }

        internal GasEnd End { get; }

        internal override bool RequiresNamedGases => false;

        internal override bool SameAs(GasPlace other) =>
            other is One one && ReferenceEquals(one.End.Atmosphere, End.Atmosphere);

        internal override GasSide SideOf(bool joined) => GasSide.Of(this, End, joined);

        // Into a joined set the gas goes into the named atmosphere, and the game spreads it.
        internal override void Receive(GasSide side, int gasIndex, double moles, double energy) =>
            side.Change(End.Atmosphere, gasIndex, moles, energy);

        internal override void Deliver(GasSide side, Chemistry.GasType gas, double moles, double energy) =>
            Add(End.Atmosphere, gas, moles, energy);

        internal override bool IsGone(GasSide side) => side.All.Members.Exists(member => member.IsGone);
    }

    /// <summary>A closed room: every cell of it that has an atmosphere of its own.</summary>
    internal sealed class RoomCells : GasPlace
    {
        private RoomCells(Room room, int cellCount, List<GasEnd> cells)
        {
            Room = room;
            CellCount = cellCount;
            Cells = cells;
        }

        internal Room Room { get; }

        /// <summary>All the room's cells, with an atmosphere of their own or not.</summary>
        internal int CellCount { get; }

        internal List<GasEnd> Cells { get; }

        internal override bool RequiresNamedGases => true;

        internal static RoomCells Of(Room room, string name)
        {
            AtmosphericsController air = AtmosphericsController.World ??
                                         throw ApiErrors.Refused("not_ready",
                                             "The atmospherics controller is not loaded.");
            List<WorldGrid> grids = new List<WorldGrid>(room.Grids);
            List<GasEnd> cells = new List<GasEnd>(grids.Count);
            HashSet<Atmosphere> seen = new HashSet<Atmosphere>(AtmosphereIdentity.Instance);
            foreach (WorldGrid grid in grids)
            {
                Atmosphere atmosphere = air.GetAtmosphereLocal(grid, false);
                if (atmosphere != null && atmosphere.Mode == AtmosphereHelper.AtmosphereMode.World &&
                    !atmosphere.IsGlobalAtmosphere && !atmosphere.BeingDestroyed && seen.Add(atmosphere))
                {
                    cells.Add(GasEnd.OfWorldCell(atmosphere));
                }
            }

            if (cells.Count == 0)
            {
                throw ApiErrors.Refused("no_atmosphere",
                    $"'{name}': none of the {grids.Count} cells of room {RoomsApi.IdOf(room)} has air of its own " +
                    "yet, so there is nothing to take and nowhere the game would keep it.");
            }

            return new RoomCells(room, grids.Count, cells);
        }

        internal override bool SameAs(GasPlace other) => other is RoomCells cells && ReferenceEquals(cells.Room, Room);

        internal override GasSide SideOf(bool joined) => GasSide.OfCells(this, Cells);

        internal override void Receive(GasSide side, int gasIndex, double moles, double energy)
        {
            double[] volumes = new double[Cells.Count];
            for (int index = 0; index < volumes.Length; index++)
            {
                volumes[index] = side.Before[side.IndexOf(Cells[index].Atmosphere)].VolumeL;
            }

            double[] shares = GasShares.ByVolume(volumes);
            for (int index = 0; index < shares.Length; index++)
            {
                side.Change(Cells[index].Atmosphere, gasIndex, moles * shares[index], energy * shares[index]);
            }
        }

        internal override void Deliver(GasSide side, Chemistry.GasType gas, double moles, double energy)
        {
            List<GasEnd> live = Cells.FindAll(cell => !cell.IsGone);
            double[] volumes = new double[live.Count];
            for (int index = 0; index < volumes.Length; index++)
            {
                volumes[index] = live[index].Atmosphere.Volume.ToDouble();
            }

            double[] shares = GasShares.ByVolume(volumes);
            for (int index = 0; index < shares.Length; index++)
            {
                Add(live[index].Atmosphere, gas, moles * shares[index], energy * shares[index]);
            }
        }

        internal override bool IsGone(GasSide side) => Cells.TrueForAll(cell => cell.IsGone);

        internal override GasRoomView RoomView(GasSnapshot[] cells)
        {
            double volume = 0.0;
            foreach (GasSnapshot cell in cells)
            {
                volume += cell.VolumeL;
            }

            return new GasRoomView(RoomsApi.IdOf(Room), Room.RoomType.ToString(), CellCount, Cells.Count, volume);
        }
    }
}

/// <summary>Atmospheres compared by reference, as the game tells them apart.</summary>
internal sealed class AtmosphereIdentity : IEqualityComparer<Atmosphere>
{
    internal static readonly AtmosphereIdentity Instance = new AtmosphereIdentity();

    public bool Equals(Atmosphere? x, Atmosphere? y) => ReferenceEquals(x, y);

    public int GetHashCode(Atmosphere obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
