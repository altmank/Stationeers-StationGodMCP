#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>An item that owns an atmosphere: the item as find_items lists it, then kind and runtime type.</summary>
internal sealed class ItemOwnerView : ItemFieldsView
{
    internal ItemOwnerView(ItemFields fields, string runtimeType) : base(fields)
    {
        RuntimeType = runtimeType;
    }

    public string Kind => "item";

    public string RuntimeType { get; }
}

/// <summary>A structure or device that owns an atmosphere.</summary>
internal sealed class StructureOwnerView
{
    internal StructureOwnerView(ThingView thing, string runtimeType, PositionView position, double? distanceM)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        RuntimeType = runtimeType;
        Position = position;
        DistanceM = distanceM;
    }

    [JsonProperty(Order = -2)]
    public string Kind => "structure";

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public string RuntimeType { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }
}

/// <summary>A pipe network that owns an atmosphere, placed at its first device (else its first pipe).</summary>
internal sealed class NetworkOwnerView
{
    internal NetworkOwnerView(ThingId referenceId, NetworkKind kind, List<ThingView> devices, PositionView? position,
        double? distanceM)
    {
        ReferenceId = referenceId;
        NetworkType = kind.NetworkType;
        ContentType = kind.ContentType;
        PipeCount = kind.PipeCount;
        Devices = devices;
        Position = position;
        DistanceM = distanceM;
    }

    [JsonProperty(Order = -2)]
    public string Kind => "pipe_network";

    public ThingId ReferenceId { get; }

    public string NetworkType { get; }

    public string ContentType { get; }

    public int PipeCount { get; }

    public List<ThingView> Devices { get; }

    public PositionView? Position { get; }

    public double? DistanceM { get; }
}

/// <summary>A pipe network's runtime type, what it carries, and how many pipes it has.</summary>
internal sealed class NetworkKind
{
    internal NetworkKind(string networkType, string contentType, int pipeCount)
    {
        NetworkType = networkType;
        ContentType = contentType;
        PipeCount = pipeCount;
    }

    internal string NetworkType { get; }

    internal string ContentType { get; }

    internal int PipeCount { get; }
}

/// <summary>An atmosphere's gases and liquids, volume, pressure and temperature, and its water.</summary>
internal sealed class HeldAtmosphereView
{
    internal HeldAtmosphereView(ThingId referenceId, AtmosphereState state, List<HeldGasView> contents,
        WaterView water)
    {
        ReferenceId = referenceId;
        VolumeL = state.VolumeL;
        PressureKpa = state.PressureKpa;
        TemperatureK = state.TemperatureK;
        TotalMol = state.TotalMol;
        LiquidVolumeL = state.LiquidVolumeL;
        Contents = contents;
        Water = water;
    }

    public ThingId ReferenceId { get; }

    public double VolumeL { get; }

    public double PressureKpa { get; }

    public double TemperatureK { get; }

    public double TotalMol { get; }

    public double LiquidVolumeL { get; }

    public List<HeldGasView> Contents { get; }

    public WaterView Water { get; }
}

/// <summary>An atmosphere's bulk figures.</summary>
internal sealed class AtmosphereState
{
    internal AtmosphereState(double volumeL, double pressureKpa, double temperatureK, double totalMol,
        double liquidVolumeL)
    {
        VolumeL = volumeL;
        PressureKpa = pressureKpa;
        TemperatureK = temperatureK;
        TotalMol = totalMol;
        LiquidVolumeL = liquidVolumeL;
    }

    internal double VolumeL { get; }

    internal double PressureKpa { get; }

    internal double TemperatureK { get; }

    internal double TotalMol { get; }

    internal double LiquidVolumeL { get; }
}

internal sealed class HeldGasView
{
    internal HeldGasView(string gas, string? displayName, bool liquid, double amountMol, double? liquidL)
    {
        Gas = gas;
        DisplayName = displayName;
        State = liquid ? "liquid" : "gas";
        AmountMol = amountMol;
        LiquidL = liquidL;
    }

    public string Gas { get; }

    public string? DisplayName { get; }

    /// <summary>gas or liquid.</summary>
    public string State { get; }

    public double AmountMol { get; }

    /// <summary>For a liquid, its volume from the molar volume (Mole.Volume); null for a gas.</summary>
    public double? LiquidL { get; }
}

/// <summary>Water in an atmosphere: drinkable liquid, polluted liquid and steam.</summary>
internal sealed class WaterView
{
    internal WaterView(WaterAmount liquid, double hydration, WaterAmount polluted, WaterAmount steam)
    {
        LiquidMol = liquid.Mol;
        LiquidL = liquid.Litres;
        Hydration = hydration;
        PollutedMol = polluted.Mol;
        PollutedL = polluted.Litres;
        SteamMol = steam.Mol;
        SteamIfCondensedL = steam.Litres;
    }

    public double LiquidMol { get; }

    public double LiquidL { get; }

    /// <summary>What drinking the liquid water would give (HydrationBase.HydrationPerMole).</summary>
    public double Hydration { get; }

    public double PollutedMol { get; }

    public double PollutedL { get; }

    public double SteamMol { get; }

    /// <summary>The steam's volume once condensed to water.</summary>
    public double SteamIfCondensedL { get; }
}

/// <summary>An amount of water in moles and litres.</summary>
internal sealed class WaterAmount
{
    internal WaterAmount(double mol, double litres)
    {
        Mol = mol;
        Litres = litres;
    }

    internal double Mol { get; }

    internal double Litres { get; }
}
