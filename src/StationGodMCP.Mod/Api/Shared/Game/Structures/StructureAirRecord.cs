#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// An atmosphere's moles per gas (GasTypes.All order) and its energy; for a room, summed over its cells in a fixed
/// order, so the same unchanged cells always give exactly the same numbers.
/// </summary>
internal sealed class AirSample
{
    private AirSample(bool exists, double[] moles, double energyJ)
    {
        Exists = exists;
        Moles = moles;
        EnergyJ = energyJ;
        double total = 0.0;
        foreach (double mole in moles)
        {
            total += mole;
        }

        TotalMol = total;
    }

    internal bool Exists { get; }

    internal double[] Moles { get; }

    internal double EnergyJ { get; }

    internal double TotalMol { get; }

    internal static AirSample None => new AirSample(false, new double[GasTypes.All.Length], 0.0);

    internal static AirSample Of(Atmosphere? atmosphere)
    {
        if (atmosphere == null)
        {
            return None;
        }

        Chemistry.GasType[] types = GasTypes.All;
        double[] moles = new double[types.Length];
        double energy = 0.0;
        for (int index = 0; index < types.Length; index++)
        {
            Mole mole = atmosphere.GasMixture.GetMoleValue(types[index]);
            moles[index] = mole.Quantity.ToDouble();
            energy += mole.Energy.ToDouble();
        }

        return new AirSample(true, moles, energy);
    }

    /// <summary>
    /// A room's air as the rooms tool sums it: every cell's SampleGlobalAtmosphere (its own, or the planet's), in
    /// the given order.
    /// </summary>
    internal static AirSample OfCells(AtmosphericsController air, List<GridPoint> cells)
    {
        double[] moles = new double[GasTypes.All.Length];
        double energy = 0.0;
        foreach (GridPoint cell in cells)
        {
            AirSample sample = Of(air.SampleGlobalAtmosphere(new WorldGrid(StructureSlots.GridOf(cell))));
            for (int index = 0; index < moles.Length; index++)
            {
                moles[index] += sample.Moles[index];
            }

            energy += sample.EnergyJ;
        }

        return new AirSample(true, moles, energy);
    }

    /// <summary>
    /// The same air within the gas audit's tolerance (GasTolerance.Default): the atmospherics thread sums a room's cells
    /// in doubles, and a sum read twice can differ in its last digits with nothing changed.
    /// </summary>
    internal bool SameAs(AirSample other)
    {
        GasTolerance tolerance = GasTolerance.Default;
        if (Exists != other.Exists || !tolerance.SameEnergy(EnergyJ, other.EnergyJ) ||
            Moles.Length != other.Moles.Length)
        {
            return false;
        }

        for (int index = 0; index < Moles.Length; index++)
        {
            if (!tolerance.SameMol(Moles[index], other.Moles[index]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A room next to the pieces as recorded before the swap.</summary>
internal sealed class RoomBefore
{
    internal RoomBefore(long id, List<GridPoint> cells, AirSample air)
    {
        Id = id;
        Cells = cells;
        Air = air;
    }

    internal long Id { get; }

    /// <summary>In Room.Grids order, which the sums follow.</summary>
    internal List<GridPoint> Cells { get; }

    internal AirSample Air { get; }

    internal string IdText => Id.ToString(CultureInfo.InvariantCulture);

    internal RoomRecord Record() => new RoomRecord(Id, new HashSet<GridPoint>(Cells), Air.TotalMol, Air.EnergyJ);
}

/// <summary>A cell next to the pieces that is in no room, with its own atmosphere (or none) as recorded.</summary>
internal sealed class CellBefore
{
    internal CellBefore(GridPoint cell, AirSample air)
    {
        Cell = cell;
        Air = air;
    }

    internal GridPoint Cell { get; }

    internal AirSample Air { get; }
}

/// <summary>What one swap touches, for the room check: its rooms, the cells it seals, their gas.</summary>
internal sealed class SwapAirScope
{
    internal SwapAirScope(long oldId, HashSet<long> rooms, HashSet<GridPoint> sealedCells, AirSample divided)
    {
        OldId = oldId;
        Rooms = rooms;
        SealedCells = sealedCells;
        Divided = divided;
    }

    internal long OldId { get; }

    internal HashSet<long> Rooms { get; }

    internal HashSet<GridPoint> SealedCells { get; }

    /// <summary>The gas of the cells whose air the new piece closes, which the game divides among neighbours.</summary>
    internal AirSample Divided { get; }
}

/// <summary>
/// The rooms and cell atmospheres around the pieces, recorded before the swap, and the two checks against them: with
/// the tick held (nothing may have changed at all) and after the game re-evaluated its rooms (RoomDiff).
/// </summary>
internal sealed class StructureAirRecord
{
    private StructureAirRecord()
    {
    }

    internal List<RoomBefore> Rooms { get; } = new List<RoomBefore>();

    internal List<CellBefore> Cells { get; } = new List<CellBefore>();

    internal List<SwapAirScope> Scopes { get; } = new List<SwapAirScope>();

    /// <summary>Every cell a swap can touch, for finding rooms that appear there.</summary>
    internal HashSet<GridPoint> Affected { get; } = new HashSet<GridPoint>();

    internal static StructureAirRecord Take(List<PlannedStructureSwap> swaps)
    {
        StructureAirRecord record = new StructureAirRecord();
        RoomController rooms = RoomController.World;
        AtmosphericsController air = AtmosphericsController.World;
        foreach (PlannedStructureSwap swap in swaps)
        {
            HashSet<long> swapRooms = new HashSet<long>();
            foreach (GridPoint cell in swap.AffectedCells)
            {
                Room? room = LiveRoomAt(rooms, cell);
                if (room != null)
                {
                    swapRooms.Add(room.RoomId);
                    record.AddRoom(room, air);
                    record.Affected.Add(cell);
                    continue;
                }

                if (record.Affected.Add(cell))
                {
                    record.Cells.Add(new CellBefore(cell, AirSample.Of(LocalAir(air, cell))));
                }
            }

            record.Scopes.Add(new SwapAirScope(swap.OldId, swapRooms, swap.SealedCells, Divided(air, swap)));
        }

        return record;
    }

    internal string? RoomOf(GridPoint cell)
    {
        foreach (RoomBefore room in Rooms)
        {
            if (room.Cells.Contains(cell))
            {
                return room.IdText;
            }
        }

        return null;
    }

    /// <summary>Nothing ran while the tick was held: every recorded air must be as it was (AirSample.SameAs).</summary>
    internal void CheckHeld(List<UpgradeProblemView> problems)
    {
        AtmosphericsController air = AtmosphericsController.World;
        foreach (RoomBefore room in Rooms)
        {
            AirSample now = AirSample.OfCells(air, room.Cells);
            if (!now.SameAs(room.Air))
            {
                problems.Add(new UpgradeProblemView("air_changed_while_held",
                    $"Room {room.IdText}: {room.Air.TotalMol} mol and {room.Air.EnergyJ} J before the swap, " +
                    $"{now.TotalMol} mol and {now.EnergyJ} J after it, with the game tick held."));
            }
        }

        foreach (CellBefore cell in Cells)
        {
            AirSample now = AirSample.Of(LocalAir(air, cell.Cell));
            if (!now.SameAs(cell.Air))
            {
                problems.Add(new UpgradeProblemView("air_changed_while_held",
                    $"Cell {cell.Cell}: {cell.Air.TotalMol} mol before the swap, {now.TotalMol} mol after it " +
                    $"(atmosphere {(cell.Air.Exists ? "present" : "none")} before, " +
                    $"{(now.Exists ? "present" : "none")} after), with the game tick held."));
            }
        }
    }

    /// <summary>The rooms once the game has re-evaluated them, for the swaps that were made.</summary>
    internal StructureRoomCheckView CheckRooms(HashSet<long> swapped, bool settled, int ticksRun)
    {
        RoomController rooms = RoomController.World;
        AtmosphericsController air = AtmosphericsController.World;
        List<UpgradeProblemView> problems = new List<UpgradeProblemView>();
        if (!settled)
        {
            problems.Add(new UpgradeProblemView("rooms_not_settled",
                "The game did not run and re-evaluate its rooms within the wait (is it paused?); the rooms were " +
                "checked as they are."));
        }

        Dictionary<long, Room> live = LiveRoomsById();
        List<StructureRoomResultView> results = new List<StructureRoomResultView>(Rooms.Count);
        foreach (RoomBefore before in Rooms)
        {
            RoomRecord? after = live.TryGetValue(before.Id, out Room room) ? RecordOf(room, air) : null;
            RoomOutcome outcome = RoomDiff.Compare(before.Record(), after, ExpectationFor(before, swapped));
            results.Add(ViewOf(before, outcome));
            foreach (string code in outcome.Problems)
            {
                problems.Add(new UpgradeProblemView(code, MessageOf(code, before, outcome)));
            }
        }

        return new StructureRoomCheckView(settled, ticksRun, problems, results, NewRooms(rooms, air));
    }

    internal List<StructureRoomView> RoomViews()
    {
        List<StructureRoomView> views = new List<StructureRoomView>(Rooms.Count);
        foreach (RoomBefore room in Rooms)
        {
            views.Add(new StructureRoomView(room.IdText, room.Cells.Count, room.Air.TotalMol, room.Air.EnergyJ));
        }

        return views;
    }

    internal static Room? FindRoom(long id) => LiveRoomsById().TryGetValue(id, out Room room) ? room : null;

    private void AddRoom(Room room, AtmosphericsController air)
    {
        foreach (RoomBefore known in Rooms)
        {
            if (known.Id == room.RoomId)
            {
                return;
            }
        }

        List<GridPoint> cells = CellsOf(room);
        Rooms.Add(new RoomBefore(room.RoomId, cells, AirSample.OfCells(air, cells)));
    }

    private RoomExpectation ExpectationFor(RoomBefore room, HashSet<long> swapped)
    {
        HashSet<GridPoint> sealedCells = new HashSet<GridPoint>();
        double mol = 0.0;
        double energy = 0.0;
        foreach (SwapAirScope scope in Scopes)
        {
            if (!swapped.Contains(scope.OldId) || !scope.Rooms.Contains(room.Id))
            {
                continue;
            }

            sealedCells.UnionWith(scope.SealedCells);
            mol += scope.Divided.TotalMol;
            energy += scope.Divided.EnergyJ;
        }

        return new RoomExpectation(sealedCells, mol, energy);
    }

    private List<StructureRoomView> NewRooms(RoomController rooms, AtmosphericsController air)
    {
        List<StructureRoomView> views = new List<StructureRoomView>();
        HashSet<long> seen = new HashSet<long>();
        foreach (RoomBefore before in Rooms)
        {
            seen.Add(before.Id);
        }

        foreach (GridPoint cell in Affected)
        {
            Room? room = LiveRoomAt(rooms, cell);
            if (room != null && seen.Add(room.RoomId))
            {
                RoomRecord record = RecordOf(room, air);
                views.Add(new StructureRoomView(room.RoomId.ToString(CultureInfo.InvariantCulture),
                    record.Cells.Count, record.TotalMol, record.EnergyJ));
            }
        }

        return views;
    }

    private static StructureRoomResultView ViewOf(RoomBefore before, RoomOutcome outcome) =>
        new StructureRoomResultView(before.IdText, outcome.After != null,
            new StructureRoomCells(before.Cells.Count, outcome.ExpectedCells, outcome.After?.Cells.Count,
                outcome.Cells.Lost, outcome.Cells.Gained),
            new StructureRoomAir(before.Air.TotalMol, outcome.After?.TotalMol, outcome.Tolerance.Mol,
                before.Air.EnergyJ, outcome.After?.EnergyJ, outcome.Tolerance.EnergyJ),
            outcome.Problems);

    private static string MessageOf(string code, RoomBefore before, RoomOutcome outcome) => code switch
    {
        RoomDiff.RoomGone => $"Room {before.IdText} no longer exists.",
        RoomDiff.RoomCellsChanged =>
            $"Room {before.IdText} lost {outcome.Cells.Lost} and gained {outcome.Cells.Gained} cell(s) against the " +
            $"{outcome.ExpectedCells} expected.",
        _ => $"Room {before.IdText} changed by {outcome.DeltaMol} mol and {outcome.DeltaEnergyJ} J (tolerance " +
             $"{outcome.Tolerance.Mol} mol, {outcome.Tolerance.EnergyJ} J)."
    };

    private static RoomRecord RecordOf(Room room, AtmosphericsController air)
    {
        List<GridPoint> cells = CellsOf(room);
        AirSample sample = AirSample.OfCells(air, cells);
        return new RoomRecord(room.RoomId, new HashSet<GridPoint>(cells), sample.TotalMol, sample.EnergyJ);
    }

    private static List<GridPoint> CellsOf(Room room)
    {
        List<WorldGrid> grids = new List<WorldGrid>(room.Grids);
        List<GridPoint> cells = new List<GridPoint>(grids.Count);
        foreach (WorldGrid grid in grids)
        {
            cells.Add(StructureSlots.PointOf(grid.Value));
        }

        return cells;
    }

    private static AirSample Divided(AtmosphericsController air, PlannedStructureSwap swap)
    {
        List<GridPoint> cells = new List<GridPoint>();
        foreach (GridPoint cell in swap.DividedCells)
        {
            if (LocalAir(air, cell) != null)
            {
                cells.Add(cell);
            }
        }

        return cells.Count > 0 ? AirSample.OfCells(air, cells) : AirSample.None;
    }

    private static Atmosphere? LocalAir(AtmosphericsController air, GridPoint cell) =>
        air.GetAtmosphereLocal(new WorldGrid(StructureSlots.GridOf(cell)));

    private static Room? LiveRoomAt(RoomController rooms, GridPoint cell)
    {
        Room room = rooms.GetRoom(StructureSlots.GridOf(cell));
        return room != null && !room.IsDeletionCandidate && room.IsValid() ? room : null;
    }

    private static Dictionary<long, Room> LiveRoomsById()
    {
        Dictionary<long, Room> byId = new Dictionary<long, Room>();
        foreach (Room room in new List<Room>(Room.AllRooms))
        {
            if (room != null && !room.IsDeletionCandidate && room.IsValid())
            {
                byId[room.RoomId] = room;
            }
        }

        return byId;
    }
}
