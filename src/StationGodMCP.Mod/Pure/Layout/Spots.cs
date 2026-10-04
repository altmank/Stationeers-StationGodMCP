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
        int blockedFrontCells, int bodyClashes = 0)
    {
        BodyClashes = bodyClashes;
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

    /// <summary>The wall sections its mesh box's rectangle spans (0 when it rests on no plane).</summary>
    internal int Sections { get; }

    internal double BottomAboveFloorM { get; }

    /// <summary>Cells within front_clear_m in front of it that hold something.</summary>
    internal int BlockedFrontCells { get; }

    /// <summary>Things whose mesh box its own clashes with (VisualClash): under a console's overhang, say.</summary>
    internal int BodyClashes { get; }
}

/// <summary>
/// Why find_spot ruled spots out, counted: each reason once per spot (the filter's first, or the check's), grouped with
/// the numbers in it ignored ("2 cell(s) taken" and "3 cell(s) taken" are one reason), the first wording kept as the
/// example. Most frequent first, then first seen.
/// </summary>
internal sealed class SpotReasons
{
    private readonly List<string> _keys = new List<string>();
    private readonly Dictionary<string, (string Example, int Count)> _counts =
        new Dictionary<string, (string, int)>();

    internal void Add(string reason)
    {
        string key = System.Text.RegularExpressions.Regex.Replace(reason, @"-?\d+(\.\d+)?", "#");
        if (_counts.TryGetValue(key, out (string Example, int Count) found))
        {
            _counts[key] = (found.Example, found.Count + 1);
            return;
        }

        _keys.Add(key);
        _counts[key] = (reason, 1);
    }

    /// <summary>How many different reasons were counted.</summary>
    internal int Distinct => _keys.Count;

    internal List<(string Reason, int Count)> Top(int limit)
    {
        List<(string Reason, int Count, int Order)> all = new List<(string, int, int)>();
        for (int index = 0; index < _keys.Count; index++)
        {
            (string example, int count) = _counts[_keys[index]];
            all.Add((example, count, index));
        }

        all.Sort(static (a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : a.Order.CompareTo(b.Order));
        return all.GetRange(0, Math.Min(limit, all.Count)).ConvertAll(static item => (item.Reason, item.Count));
    }
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

        if (require.NoVisualOverlap && geometry.BodyClashes > 0)
        {
            failed.Add($"its body clashes with {geometry.BodyClashes} thing(s)");
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
            failed.Add(System.FormattableString.Invariant($"bottom {geometry.BottomAboveFloorM:0.##} m above the floor"));
        }

        if (geometry.BlockedFrontCells > 0)
        {
            failed.Add("its front is not clear");
        }

        return failed;
    }

    /// <summary>
    /// The 0.5 m spots of a plane within radius of a point, nearest first: (u, v) on the plane around the point's
    /// projection (qu, qv), depth the point's distance from the plane, and each spot's distance from the point. A
    /// plane farther than the radius has none.
    /// </summary>
    internal static List<(double U, double V, double Distance)> Within(double qu, double qv, double depth,
        double radius)
    {
        List<(double U, double V, double Distance)> spots = new List<(double, double, double)>();
        for (double u = System.Math.Floor((qu - radius) * 2.0) / 2.0; u <= qu + radius; u += 0.5)
        {
            for (double v = System.Math.Floor((qv - radius) * 2.0) / 2.0; v <= qv + radius; v += 0.5)
            {
                double distance = System.Math.Sqrt((u - qu) * (u - qu) + (v - qv) * (v - qv) + depth * depth);
                if (distance <= radius + 1e-9)
                {
                    spots.Add((u, v, distance));
                }
            }
        }

        spots.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        return spots;
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
