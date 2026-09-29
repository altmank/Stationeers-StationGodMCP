#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// water_sources: every thing and pipe network in the world whose atmosphere holds water, polluted water or steam.
/// Read only.
///
/// Found through AtmosphericsManager.AllAtmospheres, the game's own list of every atmosphere: Thing-mode atmospheres
/// (a thing's own) and Network-mode ones (a pipe network's), and PipeNetwork.AllPipeNetworks for a network whose
/// atmosphere the game has not listed yet. Room and world air is left out, and so are organs and
/// bodies (a stomach holds water too). Largest liquid water first.
/// </summary>
internal static class WaterSourcesApi
{
    private const double DefaultMinimumMol = 1.0;

    internal static WaterSourcesView Handle(Args args)
    {
        double minimum = args.OptionalPositiveDouble("min_mol") ?? args.OptionalPositiveDouble("min_moles") ??
            DefaultMinimumMol;
        PlayerOrigin origin = PlayerOrigin.Current();
        List<Atmosphere> atmospheres = Atmospheres();
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
        }

        // Summed in the game's list order, then sorted.
        WaterSum sum = new WaterSum();
        foreach (WaterRow row in rows)
        {
            sum.Add(row);
        }

        rows.Sort(static (a, b) => WaterRow.LargestFirst(a, b));
        List<WaterSourceView> sources = new List<WaterSourceView>(rows.Count);
        foreach (WaterRow row in rows)
        {
            sources.Add(row.ToView(origin));
        }

        return new WaterSourcesView(sources, sum.ToView(), minimum, origin.View);
    }

    // AtmosphericsManager.AllAtmospheres, plus every pipe network's atmosphere: a network made since the last
    // atmospherics tick (or while paused) has its atmosphere only queued (AtmosphericsManager.RegisterFromMainThread)
    // until HandleMainThreadRegistrations adds it to the list on the next tick.
    private static List<Atmosphere> Atmospheres()
    {
        List<Atmosphere> atmospheres = Pools.Snapshot(AtmosphericsManager.AllAtmospheres);
        HashSet<Atmosphere> listed = new HashSet<Atmosphere>(atmospheres);
        foreach (PipeNetwork network in Pools.Snapshot(PipeNetwork.AllPipeNetworks))
        {
            Atmosphere? atmosphere = network?.Atmosphere;
            if (atmosphere != null && listed.Add(atmosphere))
            {
                atmospheres.Add(atmosphere);
            }
        }

        return atmospheres;
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
