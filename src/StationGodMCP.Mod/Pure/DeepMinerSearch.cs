#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A horizontal direction from one point to another: compass degrees (0 = +z, north; 90 = +x, east).</summary>
internal readonly struct Bearing
{
    private static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private Bearing(double degrees)
    {
        Degrees = degrees;
    }

    internal double Degrees { get; }

    /// <summary>The nearest of the eight compass points.</summary>
    internal string Compass => Points[(int)Math.Round(Degrees / 45.0) % Points.Length];

    internal static Bearing Of(double dx, double dz)
    {
        double degrees = Math.Atan2(dx, dz) * 180.0 / Math.PI;
        return new Bearing(degrees < 0 ? degrees + 360.0 : degrees);
    }
}

/// <summary>One sampled spot: where (x, z) and how far from the search centre.</summary>
internal readonly struct MinerSample
{
    internal MinerSample(double x, double z, double distance)
    {
        X = x;
        Z = z;
        Distance = distance;
    }

    internal double X { get; }

    internal double Z { get; }

    internal double Distance { get; }
}

/// <summary>What a search found and how much of its disc it looked at.</summary>
internal sealed class MinerSearchResult
{
    internal MinerSearchResult(List<MinerSample> spots, int samples, int matched, double searchedRadius, bool truncated)
    {
        Spots = spots;
        Samples = samples;
        Matched = matched;
        SearchedRadius = searchedRadius;
        Truncated = truncated;
    }

    /// <summary>The nearest matching samples, nearest first, each the separation from the ones before.</summary>
    internal List<MinerSample> Spots { get; }

    internal int Samples { get; }

    internal int Matched { get; }

    /// <summary>The radius searched in full: all of it, unless enough spots came sooner or it was truncated.</summary>
    internal double SearchedRadius { get; }

    /// <summary>The sample or time budget ran out before the radius was searched.</summary>
    internal bool Truncated { get; }
}

/// <summary>
/// A nearest-first search over a disc on a square grid of samples: square rings outward from the centre, so every
/// sample of ring r lies at least r steps away. After each ring, the matches no later ring can come closer than are
/// final; the nearest of them, each at least the separation from those already picked, are the spots. The search
/// stops once it has the spots wanted, at the radius, or at its sample budget.
/// </summary>
internal sealed class MinerSpotSearch
{
    private readonly double _step;
    private readonly double _radius;
    private readonly double _separation;
    private readonly int _wanted;
    private readonly int _maximumSamples;

    internal MinerSpotSearch(double step, double radius, double separation, int wanted, int maximumSamples)
    {
        _step = step;
        _radius = radius;
        _separation = separation;
        _wanted = wanted;
        _maximumSamples = maximumSamples;
    }

    /// <summary>
    /// The finest step at least minimumStep (the region map's own resolution) that keeps the disc within the budget.
    /// </summary>
    internal static double StepFor(double radius, double minimumStep, int maximumSamples)
    {
        double budgetStep = radius * Math.Sqrt(Math.PI / maximumSamples);
        return Math.Max(minimumStep, Math.Ceiling(budgetStep));
    }

    /// <param name="matches">Whether the sample at (x, z) is wanted.</param>
    /// <param name="timeLeft">Asked once per ring: false stops the search (truncated).</param>
    internal MinerSearchResult Run(double centreX, double centreZ, Func<double, double, bool> matches,
        Func<bool> timeLeft)
    {
        int rings = (int)Math.Ceiling(_radius / _step);
        List<MinerSample> found = new List<MinerSample>();
        int samples = 0;
        for (int ring = 0; ring <= rings; ring++)
        {
            if (!timeLeft() || samples >= _maximumSamples)
            {
                return new MinerSearchResult(Pick(found, (ring - 1) * _step), samples, found.Count,
                    Math.Max(0, ring - 1) * _step, true);
            }

            foreach ((int i, int j) in Ring(ring))
            {
                double distance = Math.Sqrt((double)i * i + (double)j * j) * _step;
                if (distance > _radius)
                {
                    continue;
                }

                samples++;
                double x = centreX + i * _step;
                double z = centreZ + j * _step;
                if (matches(x, z))
                {
                    found.Add(new MinerSample(x, z, distance));
                }
            }

            List<MinerSample> settled = Pick(found, ring * _step);
            if (settled.Count >= _wanted)
            {
                return new MinerSearchResult(settled, samples, found.Count, ring * _step, false);
            }
        }

        return new MinerSearchResult(Pick(found, _radius), samples, found.Count, _radius, false);
    }

    /// <summary>The cells of square ring r around the origin (r = 0: the origin alone).</summary>
    internal static IEnumerable<(int I, int J)> Ring(int ring)
    {
        if (ring == 0)
        {
            yield return (0, 0);
            yield break;
        }

        for (int i = -ring; i <= ring; i++)
        {
            yield return (i, -ring);
            yield return (i, ring);
        }

        for (int j = -ring + 1; j <= ring - 1; j++)
        {
            yield return (-ring, j);
            yield return (ring, j);
        }
    }

    // The nearest matches within reach, each at least the separation from those picked before it.
    private List<MinerSample> Pick(List<MinerSample> found, double reach)
    {
        List<MinerSample> settled = found.FindAll(spot => spot.Distance <= reach);
        settled.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        List<MinerSample> picked = new List<MinerSample>(_wanted);
        foreach (MinerSample spot in settled)
        {
            if (picked.Count >= _wanted)
            {
                break;
            }

            if (picked.TrueForAll(other => Apart(other, spot) >= _separation))
            {
                picked.Add(spot);
            }
        }

        return picked;
    }

    private static double Apart(MinerSample a, MinerSample b)
    {
        double dx = a.X - b.X;
        double dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>
/// The reagents asked for (deep_miner_spots ores): a profile matches when its mix holds every one of them, names
/// compared ignoring case.
/// </summary>
internal sealed class OreQuery
{
    private readonly List<string> _ores;

    internal OreQuery(List<string> ores)
    {
        _ores = ores;
    }

    internal IReadOnlyList<string> Ores => _ores;

    internal bool Matches(IReadOnlyDictionary<string, double> mix)
    {
        foreach (string ore in _ores)
        {
            if (!HasOre(mix, ore))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasOre(IReadOnlyDictionary<string, double> mix, string ore)
    {
        foreach (KeyValuePair<string, double> reagent in mix)
        {
            if (reagent.Value > 0.0 && string.Equals(reagent.Key, ore, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
