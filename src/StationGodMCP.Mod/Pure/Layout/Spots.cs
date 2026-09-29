#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>find_spot's require flags: what a spot must meet to be offered.</summary>
internal sealed class SpotRequirements
{
    internal SpotRequirements(bool oneSection, double? minBottomAboveFloorM, bool portsReachable, bool avoidDoors,
        double frontClearM, bool noVisualOverlap)
    {
        OneSection = oneSection;
        MinBottomAboveFloorM = minBottomAboveFloorM;
        PortsReachable = portsReachable;
        AvoidDoors = avoidDoors;
        FrontClearM = frontClearM;
        NoVisualOverlap = noVisualOverlap;
    }

    internal bool OneSection { get; }

    internal double? MinBottomAboveFloorM { get; }

    internal bool PortsReachable { get; }

    internal bool AvoidDoors { get; }

    internal double FrontClearM { get; }

    internal bool NoVisualOverlap { get; }

    /// <summary>Small cells in front of the piece that must be free for FrontClearM.</summary>
    internal int FrontClearCells => (int)Math.Ceiling(FrontClearM * 2.0 - 1e-6);
}

/// <summary>The cheap geometric facts of one candidate spot, read before any cursor check.</summary>
internal sealed class SpotGeometry
{
    internal SpotGeometry(int cells, int occupiedCells, int keepOutCells, int sections, double bottomAboveFloorM,
        int blockedFrontCells)
    {
        Cells = cells;
        OccupiedCells = occupiedCells;
        KeepOutCells = keepOutCells;
        Sections = sections;
        BottomAboveFloorM = bottomAboveFloorM;
        BlockedFrontCells = blockedFrontCells;
    }

    internal int Cells { get; }

    /// <summary>Its cells already holding a device, mounted thing or chute.</summary>
    internal int OccupiedCells { get; }

    internal int KeepOutCells { get; }

    /// <summary>The wall sections its mount rectangle spans (0 when it rests on no plane).</summary>
    internal int Sections { get; }

    internal double BottomAboveFloorM { get; }

    /// <summary>Cells within front_clear_m in front of it that hold something.</summary>
    internal int BlockedFrontCells { get; }
}

/// <summary>
/// find_spot's cheap filter and ranking. The filter runs on geometry only (SpotGeometry), so the costly cursor check and
/// layout preview run only on spots that could pass; the rank orders passed spots by the layout preview's penalty, then
/// distance from the point asked, then search order.
/// </summary>
internal static class SpotSearch
{
    /// <summary>Why the geometry fails the requirements; empty when it passes.</summary>
    internal static List<string> Filter(SpotGeometry geometry, SpotRequirements require)
    {
        List<string> failed = new List<string>();
        if (geometry.Cells == 0)
        {
            failed.Add("no footprint");
        }

        if (geometry.OccupiedCells > 0)
        {
            failed.Add($"{geometry.OccupiedCells} cell(s) taken");
        }

        if (require.AvoidDoors && geometry.KeepOutCells > 0)
        {
            failed.Add("in a door's keep-out");
        }

        if (require.OneSection && geometry.Sections != 1)
        {
            failed.Add(geometry.Sections == 0 ? "rests on no face plane" : $"spans {geometry.Sections} sections");
        }

        if (require.MinBottomAboveFloorM.HasValue && geometry.BottomAboveFloorM < require.MinBottomAboveFloorM - 1e-6)
        {
            failed.Add($"bottom {geometry.BottomAboveFloorM:0.##} m above the floor");
        }

        if (geometry.BlockedFrontCells > 0)
        {
            failed.Add("its front is not clear");
        }

        return failed;
    }

    /// <summary>Indices of the scored spots, best first: penalty, then distance, then search order.</summary>
    internal static List<int> Rank(IReadOnlyList<(int Penalty, double Distance)> spots)
    {
        List<int> order = new List<int>(spots.Count);
        for (int index = 0; index < spots.Count; index++)
        {
            order.Add(index);
        }

        order.Sort((a, b) =>
        {
            int penalty = spots[a].Penalty.CompareTo(spots[b].Penalty);
            if (penalty != 0)
            {
                return penalty;
            }

            int distance = spots[a].Distance.CompareTo(spots[b].Distance);
            return distance != 0 ? distance : a.CompareTo(b);
        });
        return order;
    }
}
