#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Water sources: every thing and pipe network in the world whose atmosphere holds water, polluted water or steam.
/// Read only.
///
/// Found through the game's own list of every atmosphere (World.Atmospheres): Thing-mode atmospheres (a thing's own)
/// and Network-mode ones (a pipe network's), and the pipe networks (World.PipeNetworks) for a network whose atmosphere
/// the game has not listed yet; only atmospheres holding at least the minimum are taken from either. Room and world air is left out, and so are organs and
/// bodies (a stomach holds water too). Largest liquid water first. A call that holds the main thread over
/// WaterSourcesWarning.ThresholdMs logs where the time went (rate-limited).
/// </summary>
internal static class WaterSources
{
    private static readonly WaterSourcesWarning SlowCalls = new WaterSourcesWarning();

    /// <summary>How many atmospheres one step of Collecting reads into rows, and how many rows it describes.</summary>
    internal const int RowsAStep = 256;

    internal const int ViewsAStep = 64;

    /// <summary>
    /// Every source holding at least minimum mol of water, polluted water and steam together, largest first, as work
    /// over frames (A3): each pool read when its lock is free (a step that finds the atmospherics thread holding it
    /// tries again in the next, never waits), the rows and their views a batch a step. The slow-call log sums the
    /// steps' own time.
    /// </summary>
    [Profiled]
    internal static IEnumerable<object?> Collecting(double minimum, Pure.Scheduling.Answer<WaterFound> answer)
    {
        int collections = GC.CollectionCount(0);
        long started = Stopwatch.GetTimestamp();
        PlayerOrigin origin = PlayerOrigin.Current();
        List<Atmosphere> atmospheres = new List<Atmosphere>();
        while (!World.TryAtmospheres(atmosphere => Holds(atmosphere, minimum), atmospheres))
        {
            yield return null;
            started = Stopwatch.GetTimestamp();
        }

        double atmosphereListMs = Ms(Stopwatch.GetTimestamp() - started);
        yield return null;
        started = Stopwatch.GetTimestamp();
        List<PipeNetwork> networks = new List<PipeNetwork>();
        while (!World.TryPipeNetworks(network => Holds(network.Atmosphere, minimum), networks))
        {
            yield return null;
            started = Stopwatch.GetTimestamp();
        }

        HashSet<Atmosphere> listed = new HashSet<Atmosphere>(atmospheres);
        foreach (PipeNetwork network in networks)
        {
            if (listed.Add(network.Atmosphere))
            {
                atmospheres.Add(network.Atmosphere);
            }
        }

        double networkListMs = Ms(Stopwatch.GetTimestamp() - started);
        yield return null;
        double scanMs = 0.0;
        started = Stopwatch.GetTimestamp();
        List<WaterRow> rows = new List<WaterRow>();
        for (int index = 0; index < atmospheres.Count; index++)
        {
            Atmosphere atmosphere = atmospheres[index];
            if (atmosphere != null && !atmosphere.BeingDestroyed && IsSource(atmosphere))
            {
                WaterRow row = new WaterRow(atmosphere);
                if (row.Total >= minimum)
                {
                    rows.Add(row);
                }
            }

            if (index % RowsAStep == RowsAStep - 1)
            {
                scanMs += Ms(Stopwatch.GetTimestamp() - started);
                yield return null;
                started = Stopwatch.GetTimestamp();
            }
        }

        // Summed in the game's list order, then sorted.
        WaterSum sum = new WaterSum();
        foreach (WaterRow row in rows)
        {
            sum.Add(row);
        }

        rows.Sort(static (a, b) => WaterRow.LargestFirst(a, b));
        scanMs += Ms(Stopwatch.GetTimestamp() - started);
        yield return null;
        double rowsMs = 0.0;
        started = Stopwatch.GetTimestamp();
        List<WaterSourceView> sources = new List<WaterSourceView>(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            sources.Add(rows[index].ToView(origin));
            if (index % ViewsAStep == ViewsAStep - 1)
            {
                rowsMs += Ms(Stopwatch.GetTimestamp() - started);
                yield return null;
                started = Stopwatch.GetTimestamp();
            }
        }

        rowsMs += Ms(Stopwatch.GetTimestamp() - started);
        WarnWhenSlow(new WaterSourcesTiming(atmosphereListMs + networkListMs + scanMs + rowsMs, 0.0, atmosphereListMs,
            0.0, networkListMs, scanMs, rowsMs, atmospheres.Count, rows.Count, GC.CollectionCount(0) - collections));
        answer.Value = new WaterFound(sources, sum.ToView(), origin.View);
    }

    private static void WarnWhenSlow(in WaterSourcesTiming timing)
    {
        string? warning = SlowCalls.Check(Ms(Stopwatch.GetTimestamp()) / 1000.0, in timing);
        if (warning != null)
        {
            StationGodMod.LogWarning(warning);
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // Water, polluted water and steam together at least minimum mol (WaterRow.Total).
    private static bool Holds(Atmosphere? atmosphere, double minimum)
    {
        if (atmosphere == null || atmosphere.BeingDestroyed)
        {
            return false;
        }

        GasMixture mixture = atmosphere.GasMixture;
        return mixture.Water.Quantity.ToDouble() + mixture.PollutedWater.Quantity.ToDouble() +
            mixture.Steam.Quantity.ToDouble() >= minimum;
    }

    // A thing's own atmosphere, not a body's or an organ's; or a pipe network's.
    private static bool IsSource(Atmosphere atmosphere)
    {
        if (atmosphere.Mode == AtmosphereHelper.AtmosphereMode.Thing)
        {
            Thing thing = atmosphere.Thing;
            return thing != null && !thing.IsBeingDestroyed && !(thing is Organ) && !(thing is Entity) &&
                   !InOrganSlot(thing);
        }

        return atmosphere.Mode == AtmosphereHelper.AtmosphereMode.Network && atmosphere.AtmosphericsNetwork != null;
    }

    private static bool InOrganSlot(Thing thing) =>
        thing is DynamicThing dynamic && dynamic.ParentSlot != null && dynamic.ParentSlot.Type == Slot.Class.Organ;
}

/// <summary>One water source: its atmosphere and the water in it.</summary>
internal sealed class WaterRow
{
    internal WaterRow(Atmosphere atmosphere)
    {
        Atmosphere = atmosphere;
        GasMixture mixture = atmosphere.GasMixture;
        Liquid = mixture.Water.Quantity.ToDouble();
        Polluted = mixture.PollutedWater.Quantity.ToDouble();
        Steam = mixture.Steam.Quantity.ToDouble();
    }

    internal Atmosphere Atmosphere { get; }

    internal double Liquid { get; }

    internal double Polluted { get; }

    internal double Steam { get; }

    internal double Total => Liquid + Polluted + Steam;

    /// <summary>Most liquid water first, then most polluted water and steam, then by atmosphere id.</summary>
    internal static int LargestFirst(WaterRow a, WaterRow b)
    {
        int byLiquid = b.Liquid.CompareTo(a.Liquid);
        if (byLiquid != 0)
        {
            return byLiquid;
        }

        int byRest = (b.Polluted + b.Steam).CompareTo(a.Polluted + a.Steam);
        return byRest != 0 ? byRest : a.Atmosphere.ReferenceId.CompareTo(b.Atmosphere.ReferenceId);
    }

    internal WaterSourceView ToView(PlayerOrigin origin)
    {
        bool isThing = Atmosphere.Mode == AtmosphereHelper.AtmosphereMode.Thing;
        object owner = isThing
            ? AtmosphereOwners.OwnerOf(Atmosphere.Thing, origin)
            : AtmosphereOwners.OwnerOf(Atmosphere.AtmosphericsNetwork, origin);
        GasMixture mixture = Atmosphere.GasMixture;
        AtmosphereState state = new AtmosphereState(Atmosphere.Volume.ToDouble(),
            Atmosphere.PressureGassesAndLiquids.ToDouble(), Atmosphere.Temperature.ToDouble(), 0.0, 0.0);
        return new WaterSourceView(isThing, owner, new ThingId(Atmosphere.ReferenceId), state,
            AtmosphereOwners.WaterOf(mixture));
    }
}

/// <summary>The water of every source together, in litres by each kind's molar volume.</summary>
internal sealed class WaterSum
{
    private double _liquid;
    private double _polluted;
    private double _steam;

    internal void Add(WaterRow row)
    {
        _liquid += row.Liquid;
        _polluted += row.Polluted;
        _steam += row.Steam;
    }

    internal WaterView ToView()
    {
        double litresPerMole = Mole.MolarVolume(Chemistry.GasType.Water).ToDouble();
        double pollutedLitresPerMole = Mole.MolarVolume(Chemistry.GasType.PollutedWater).ToDouble();
        return new WaterView(
            new WaterAmount(_liquid, _liquid * litresPerMole),
            _liquid * HydrationBase.HydrationPerMole,
            new WaterAmount(_polluted, _polluted * pollutedLitresPerMole),
            new WaterAmount(_steam, _steam * litresPerMole));
    }
}

/// <summary>The water sources found, largest first, their totals, and the player they were measured from.</summary>
internal sealed class WaterFound
{
    internal WaterFound(List<WaterSourceView> sources, WaterView totals, LocalPlayerView? localPlayer)
    {
        Sources = sources;
        Totals = totals;
        LocalPlayer = localPlayer;
    }

    public List<WaterSourceView> Sources { get; }

    public WaterView Totals { get; }

    public LocalPlayerView? LocalPlayer { get; }
}
