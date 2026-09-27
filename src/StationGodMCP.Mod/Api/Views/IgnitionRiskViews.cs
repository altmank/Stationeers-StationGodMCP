#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>ignition_risk: the air at the local player and every burnable thing they carry.</summary>
internal sealed class IgnitionRiskView
{
    internal IgnitionRiskView(LocalPlayerView? player, IgnitionCellView? cell, List<CarriedIgnitionView> items,
        List<PrefabIgnitionView>? prefabs)
    {
        Player = player;
        Cell = cell;
        Items = items;
        Prefabs = prefabs;
    }

    public LocalPlayerView? Player { get; }

    public IgnitionCellView? Cell { get; }

    public double AutoignitionMinEnergyJ => IgnitionRule.MinimumAutoignitionEnergyJ;

    public double MinimumIgnitionPressureKpa => IgnitionRule.MinimumIgnitionPressureKpa;

    public List<CarriedIgnitionView> Items { get; }

    /// <summary>Every prefab that can burn; only with include_prefabs.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PrefabIgnitionView>? Prefabs { get; }
}

/// <summary>The air of one cell, as the fire checks read it.</summary>
internal sealed class IgnitionCellView
{
    internal IgnitionCellView(double temperatureK, double pressureKpa, double energyJ, bool inflamed, double oxygenMol,
        bool inRoom)
    {
        TemperatureK = temperatureK;
        PressureKpa = pressureKpa;
        EnergyJ = energyJ;
        Inflamed = inflamed;
        OxygenMol = oxygenMol;
        InRoom = inRoom;
    }

    public double TemperatureK { get; }

    public double PressureKpa { get; }

    public double EnergyJ { get; }

    public bool Inflamed { get; }

    public double OxygenMol { get; }

    public bool InRoom { get; }
}

/// <summary>One burnable thing the player carries and whether it lights now.</summary>
internal sealed class CarriedIgnitionView
{
    internal CarriedIgnitionView(ThingView thing, CarriedPlaceView place, IgnitionTemperaturesView temperatures,
        IgnitionStateView state)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Slot = place.Slot;
        InHand = place.InHand;
        Holder = place.Holder;
        Atmosphere = place.Atmosphere;
        Burning = state.Burning;
        Hidden = state.Hidden;
        FlashpointK = temperatures.FlashpointK;
        FlashpointEffectiveK = temperatures.FlashpointEffectiveK;
        AutoignitionK = temperatures.AutoignitionK;
        IgnitesNow = state.IgnitesNow;
        HealthRatio = state.HealthRatio;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The name of the slot it sits in.</summary>
    public string? Slot { get; }

    /// <summary>It sits directly in one of the player's hands.</summary>
    public bool InHand { get; }

    /// <summary>The thing whose slot holds it: the player, a suit, a backpack, a belt.</summary>
    public ThingView? Holder { get; }

    /// <summary>The air its fire checks read: "world" (the cell), "internal" (its holder's own air) or "none".</summary>
    public string Atmosphere { get; }

    public bool Burning { get; }

    /// <summary>In a slot that hides it (Slot.HidesOccupant, at any depth): it never burns there.</summary>
    public bool Hidden { get; }

    public double? FlashpointK { get; }

    /// <summary>The flashpoint over the air's one-atmosphere ratio: what burning air must pass; null without either.</summary>
    public double? FlashpointEffectiveK { get; }

    public double? AutoignitionK { get; }

    public bool IgnitesNow { get; }

    /// <summary>1 - DamageState.TotalRatio; null for a thing without damage.</summary>
    public double? HealthRatio { get; }
}

/// <summary>Where a carried thing sits, for CarriedIgnitionView.</summary>
internal sealed class CarriedPlaceView
{
    internal CarriedPlaceView(string? slot, bool inHand, ThingView? holder, string atmosphere)
    {
        Slot = slot;
        InHand = inHand;
        Holder = holder;
        Atmosphere = atmosphere;
    }

    internal string? Slot { get; }

    internal bool InHand { get; }

    internal ThingView? Holder { get; }

    internal string Atmosphere { get; }
}

/// <summary>A thing's ignition temperatures, for CarriedIgnitionView.</summary>
internal sealed class IgnitionTemperaturesView
{
    internal IgnitionTemperaturesView(double? flashpointK, double? flashpointEffectiveK, double? autoignitionK)
    {
        FlashpointK = flashpointK;
        FlashpointEffectiveK = flashpointEffectiveK;
        AutoignitionK = autoignitionK;
    }

    internal double? FlashpointK { get; }

    internal double? FlashpointEffectiveK { get; }

    internal double? AutoignitionK { get; }
}

/// <summary>A thing's fire state, for CarriedIgnitionView.</summary>
internal sealed class IgnitionStateView
{
    internal IgnitionStateView(bool burning, bool hidden, bool ignitesNow, double? healthRatio)
    {
        Burning = burning;
        Hidden = hidden;
        IgnitesNow = ignitesNow;
        HealthRatio = healthRatio;
    }

    internal bool Burning { get; }

    internal bool Hidden { get; }

    internal bool IgnitesNow { get; }

    internal double? HealthRatio { get; }
}

/// <summary>One prefab's ignition temperatures.</summary>
internal sealed class PrefabIgnitionView
{
    internal PrefabIgnitionView(string? prefabName, string? displayName, double? flashpointK, double? autoignitionK)
    {
        PrefabName = prefabName;
        DisplayName = displayName;
        FlashpointK = flashpointK;
        AutoignitionK = autoignitionK;
    }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public double? FlashpointK { get; }

    public double? AutoignitionK { get; }
}
