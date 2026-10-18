#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// remove structure's box: every structure whose position lies in it (inclusive, to the millimetre), filtered by prefab
/// when given, taken down in one job. At most MostMetres of it on a side (a 32 m cube), and at most MostStructures found:
/// a box that holds more is refused with the count, so it is narrowed, never cut short in silence.
/// </summary>
internal sealed class RemovalBox
{
    /// <summary>The longest side a box may have, in metres.</summary>
    internal const double MostMetres = 32.0;

    /// <summary>The most structures one call takes down (as many as an id list may name).</summary>
    internal const int MostStructures = 256;

    private const double Tolerance = 0.001;

    private RemovalBox(Vec3 min, Vec3 max)
    {
        Min = min;
        Max = max;
    }

    internal Vec3 Min { get; }

    internal Vec3 Max { get; }

    /// <summary>The box the corners give, or null with a problem on box when a side is longer than MostMetres.</summary>
    internal static RemovalBox? Of(BoxCorners corners, List<OptionProblem> problems)
    {
        Vec3 min = new Vec3(Math.Min(corners.First.X, corners.Second.X), Math.Min(corners.First.Y, corners.Second.Y),
            Math.Min(corners.First.Z, corners.Second.Z));
        Vec3 max = new Vec3(Math.Max(corners.First.X, corners.Second.X), Math.Max(corners.First.Y, corners.Second.Y),
            Math.Max(corners.First.Z, corners.Second.Z));
        double longest = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
        if (longest > MostMetres)
        {
            problems.Add(new OptionProblem("box",
                $"is {longest:0.#} m on its longest side; at most {MostMetres:0} m (a {MostMetres:0} m cube)"));
            return null;
        }

        return new RemovalBox(min, max);
    }

    /// <summary>Whether a structure at this position is in the box.</summary>
    internal bool Holds(double x, double y, double z) =>
        x >= Min.X - Tolerance && x <= Max.X + Tolerance &&
        y >= Min.Y - Tolerance && y <= Max.Y + Tolerance &&
        z >= Min.Z - Tolerance && z <= Max.Z + Tolerance;

    /// <summary>
    /// The coordinates a scan visits along one axis, step metres apart, from the step-aligned value at or under from
    /// (less margin) to to (plus margin): every cell whose centre may hold a structure in the box.
    /// </summary>
    internal static List<double> Steps(double from, double to, double step, double margin)
    {
        List<double> steps = new List<double>();
        double start = Math.Floor((from - margin) / step) * step;
        for (double at = start; at <= to + margin + Tolerance; at += step)
        {
            steps.Add(at);
        }

        return steps;
    }

    /// <summary>Why the structures found are refused (too many), or null when the call takes them.</summary>
    internal static string? TooMany(int found) =>
        found > MostStructures
            ? $"holds {found} structures; one call takes at most {MostStructures}: narrow the box or add a prefab filter"
            : null;
}

/// <summary>
/// A value worked out once per key and kept for the rest of one plan: a removal's checks ask about the same neighbour
/// (its mesh, its cells) for every piece beside it, and the plan reads it once. Computed counts how often the work ran.
/// </summary>
internal sealed class PlanMemo<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _known = new Dictionary<TKey, TValue>();
    private readonly Func<TKey, TValue> _work;

    internal PlanMemo(Func<TKey, TValue> work)
    {
        _work = work;
    }

    /// <summary>How many times the work ran: once per distinct key asked for.</summary>
    internal int Computed { get; private set; }

    internal TValue Get(TKey key)
    {
        if (_known.TryGetValue(key, out TValue known))
        {
            return known;
        }

        Computed++;
        TValue value = _work(key);
        _known[key] = value;
        return value;
    }
}
