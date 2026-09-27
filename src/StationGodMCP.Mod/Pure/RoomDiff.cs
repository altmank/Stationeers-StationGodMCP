#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A room as recorded: its id, its cells (decimetres) and its air summed over them.</summary>
internal sealed class RoomRecord
{
    internal RoomRecord(long id, HashSet<GridPoint> cells, double totalMol, double energyJ)
    {
        Id = id;
        Cells = cells;
        TotalMol = totalMol;
        EnergyJ = energyJ;
    }

    internal long Id { get; }

    internal HashSet<GridPoint> Cells { get; }

    internal double TotalMol { get; }

    internal double EnergyJ { get; }
}

/// <summary>
/// What may move in or out of a room without anything being wrong: cells a sealing frame now blocks leave it, and
/// the gas those cells held is divided among their open neighbours (AtmosphericEventInstance DivideWorldAtmosphere),
/// so up to that much may arrive or leave.
/// </summary>
internal sealed class RoomExpectation
{
    internal RoomExpectation(HashSet<GridPoint> sealedCells, double allowanceMol, double allowanceEnergyJ)
    {
        SealedCells = sealedCells;
        AllowanceMol = allowanceMol;
        AllowanceEnergyJ = allowanceEnergyJ;
    }

    internal static RoomExpectation None => new RoomExpectation(new HashSet<GridPoint>(), 0.0, 0.0);

    internal HashSet<GridPoint> SealedCells { get; }

    internal double AllowanceMol { get; }

    internal double AllowanceEnergyJ { get; }
}

/// <summary>One recorded room compared with the room of the same id after the swap.</summary>
internal sealed class RoomOutcome
{
    internal RoomOutcome(RoomRecord before, RoomRecord? after, int expectedCells, RoomCellDiff cells,
        RoomTolerance tolerance, List<string> problems)
    {
        Before = before;
        After = after;
        ExpectedCells = expectedCells;
        Cells = cells;
        Tolerance = tolerance;
        Problems = problems;
    }

    internal RoomRecord Before { get; }

    /// <summary>Null when no room has the id any more.</summary>
    internal RoomRecord? After { get; }

    internal int ExpectedCells { get; }

    internal RoomCellDiff Cells { get; }

    internal RoomTolerance Tolerance { get; }

    /// <summary>room_gone, room_cells_changed and room_air_changed, each at most once.</summary>
    internal List<string> Problems { get; }

    internal double? DeltaMol => After != null ? After.TotalMol - Before.TotalMol : (double?)null;

    internal double? DeltaEnergyJ => After != null ? After.EnergyJ - Before.EnergyJ : (double?)null;
}

/// <summary>Cells the room was expected to keep but lost, and cells it has that it was not expected to.</summary>
internal readonly struct RoomCellDiff
{
    internal RoomCellDiff(int lost, int gained)
    {
        Lost = lost;
        Gained = gained;
    }

    internal int Lost { get; }

    internal int Gained { get; }
}

internal readonly struct RoomTolerance
{
    internal RoomTolerance(double mol, double energyJ)
    {
        Mol = mol;
        EnergyJ = energyJ;
    }

    internal double Mol { get; }

    internal double EnergyJ { get; }
}

/// <summary>
/// The room check after a swap, once the game has re-evaluated its rooms. A room must still exist under the same id
/// (a refill of the same cells reuses the id of the first room it empties, RoomEvaluator.ThreadedWork), hold exactly
/// its cells less those a sealing frame now blocks, and hold the same air within tolerance: 1 % or 0.5 mol (1 kJ for
/// energy), whichever is larger, plus the sealed cells' allowance.
/// </summary>
internal static class RoomDiff
{
    internal const string RoomGone = "room_gone";
    internal const string RoomCellsChanged = "room_cells_changed";
    internal const string RoomAirChanged = "room_air_changed";

    internal const double RelativeTolerance = 0.01;
    internal const double MinimumMolTolerance = 0.5;
    internal const double MinimumEnergyToleranceJ = 1000.0;

    internal static RoomOutcome Compare(RoomRecord before, RoomRecord? after, RoomExpectation expectation)
    {
        HashSet<GridPoint> expected = new HashSet<GridPoint>(before.Cells);
        expected.ExceptWith(expectation.SealedCells);
        RoomTolerance tolerance = new RoomTolerance(
            Tolerance(before.TotalMol, MinimumMolTolerance, expectation.AllowanceMol),
            Tolerance(before.EnergyJ, MinimumEnergyToleranceJ, expectation.AllowanceEnergyJ));
        List<string> problems = new List<string>();
        if (after == null)
        {
            problems.Add(RoomGone);
            return new RoomOutcome(before, null, expected.Count, new RoomCellDiff(expected.Count, 0), tolerance,
                problems);
        }

        RoomCellDiff cells = CellsAgainst(expected, after.Cells);
        if (cells.Lost > 0 || cells.Gained > 0)
        {
            problems.Add(RoomCellsChanged);
        }

        if (Math.Abs(after.TotalMol - before.TotalMol) > tolerance.Mol ||
            Math.Abs(after.EnergyJ - before.EnergyJ) > tolerance.EnergyJ)
        {
            problems.Add(RoomAirChanged);
        }

        return new RoomOutcome(before, after, expected.Count, cells, tolerance, problems);
    }

    internal static double Tolerance(double before, double floor, double allowance) =>
        Math.Max(RelativeTolerance * Math.Abs(before), floor) + Math.Max(0.0, allowance);

    private static RoomCellDiff CellsAgainst(HashSet<GridPoint> expected, HashSet<GridPoint> actual)
    {
        int lost = 0;
        foreach (GridPoint cell in expected)
        {
            if (!actual.Contains(cell))
            {
                lost++;
            }
        }

        int gained = 0;
        foreach (GridPoint cell in actual)
        {
            if (!expected.Contains(cell))
            {
                gained++;
            }
        }

        return new RoomCellDiff(lost, gained);
    }
}
