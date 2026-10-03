#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// What owns an atmosphere (an item, a structure or a pipe network) and what it holds, for atmosphere_contents and
/// water_sources.
/// </summary>
internal static class AtmosphereOwners
{
    /// <summary>An ItemOwnerView for an item, else a StructureOwnerView of its kind (structure, dynamic...).</summary>
    internal static object OwnerOf(Thing thing, PlayerOrigin origin)
    {
        if (thing is Item item)
        {
            return new ItemOwnerView(WorldItems.Describe(item, origin).Fields(), thing.GetType().Name);
        }

        return new StructureOwnerView(ThingKinds.Of(thing), GameLookup.ViewOf(thing), thing.GetType().Name,
            GameLookup.ViewOf(thing.Position), origin.DistanceTo(thing.Position));
    }

    /// <summary>What a network atmosphere is reported as: landing_pad_network or pipe_network.</summary>
    internal static string SourceOf(AtmosphericsNetwork network) =>
        network is LandingPadNetwork ? AtmosphereSource.LandingPadNetwork : AtmosphereSource.PipeNetwork;

    /// <summary>A pipe network, its devices, and where it is: at its first device, else its first pipe.</summary>
    internal static NetworkOwnerView OwnerOf(AtmosphericsNetwork network, PlayerOrigin origin)
    {
        List<Device> devices = new List<Device>();
        int pipes = 0;
        Thing? anchor = null;
        try
        {
            if (network is PipeNetwork pipeNetwork)
            {
                devices = LiveDevices(pipeNetwork);
            }

            lock (network.StructureList)
            {
                pipes = network.StructureList.Count;
                anchor = FirstThing(network.StructureList);
            }
        }
        catch (Exception)
        {
            // PipeNetwork.DeviceList and AtmosphericsNetwork.StructureList belong to the game; a snapshot that fails
            // leaves the counts empty rather than failing the call.
        }

        if (devices.Count > 0)
        {
            anchor = devices[0];
        }

        List<ThingView> deviceViews = new List<ThingView>(devices.Count);
        foreach (Device device in devices)
        {
            deviceViews.Add(GameLookup.ViewOf(device));
        }

        NetworkKind kind = new NetworkKind(network.GetType().Name, network.NetworkContentType.ToString(), pipes);
        return new NetworkOwnerView(new ThingId(network.ReferenceId), kind, deviceViews,
            anchor != null ? GameLookup.ViewOf(anchor.Position) : null,
            anchor != null ? origin.DistanceTo(anchor.Position) : null);
    }

    private static List<Device> LiveDevices(PipeNetwork network)
    {
        List<Device> devices = new List<Device>(network.DeviceList.Count);
        foreach (Device device in network.DeviceList)
        {
            if (device != null)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    private static Thing? FirstThing(List<INetworkedStructure> structures)
    {
        foreach (INetworkedStructure structure in structures)
        {
            if (structure is Thing thing)
            {
                return thing;
            }
        }

        return null;
    }

    /// <summary>Each gas and liquid an atmosphere holds (above Chemistry.MINIMUM_QUANTITY_MOLES), and water.</summary>
    internal static HeldAtmosphereView? ContentsOf(Atmosphere? atmosphere)
    {
        if (atmosphere == null)
        {
            return null;
        }

        GasMixture mixture = atmosphere.GasMixture;
        List<HeldGasView> contents = new List<HeldGasView>();
        foreach (HeldGas held in Held(mixture))
        {
            contents.Add(new HeldGasView(held.Name, Text.PlainName(held.Mole.DisplayName), held.Liquid, held.Moles,
                held.LiquidL));
        }

        return new HeldAtmosphereView(new ThingId(atmosphere.ReferenceId), StateOf(atmosphere), contents,
            WaterOf(mixture));
    }

    /// <summary>
    /// The same figures as ContentsOf, compact: no display names, no water; source and network id as given.
    /// </summary>
    internal static AtmosphereReadView CompactOf(Atmosphere atmosphere, string source, ThingId? networkId)
    {
        List<CompactGasView> contents = new List<CompactGasView>();
        foreach (HeldGas held in Held(atmosphere.GasMixture))
        {
            contents.Add(new CompactGasView(held.Name, held.Liquid, held.Moles, held.LiquidL));
        }

        return new AtmosphereReadView(source, new ThingId(atmosphere.ReferenceId), networkId, StateOf(atmosphere),
            contents);
    }

    private static AtmosphereState StateOf(Atmosphere atmosphere)
    {
        GasMixture mixture = atmosphere.GasMixture;
        return new AtmosphereState(atmosphere.Volume.ToDouble(), atmosphere.PressureGassesAndLiquids.ToDouble(),
            atmosphere.Temperature.ToDouble(), mixture.GetTotalMolesGassesAndLiquids.ToDouble(),
            mixture.VolumeLiquids.ToDouble());
    }

    // Each gas type in the game's order held above Chemistry.MINIMUM_QUANTITY_MOLES.
    private static List<HeldGas> Held(GasMixture mixture)
    {
        double minimum = Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
        List<HeldGas> held = new List<HeldGas>();
        foreach (Chemistry.GasType type in GasTypes.All)
        {
            Mole mole = mixture.GetMoleValue(type);
            double moles = mole.Quantity.ToDouble();
            if (moles > minimum)
            {
                bool liquid = Mole.MatterState(type) == AtmosphereHelper.MatterState.Liquid;
                held.Add(new HeldGas(EnumNames<Chemistry.GasType>.Of(type) ?? type.ToString(), mole, liquid, moles,
                    liquid ? mole.Volume.ToDouble() : null));
            }
        }

        return held;
    }

    private readonly struct HeldGas
    {
        internal HeldGas(string name, Mole mole, bool liquid, double moles, double? liquidL)
        {
            Name = name;
            Mole = mole;
            Liquid = liquid;
            Moles = moles;
            LiquidL = liquidL;
        }

        internal string Name { get; }

        internal Mole Mole { get; }

        internal bool Liquid { get; }

        internal double Moles { get; }

        internal double? LiquidL { get; }
    }

    /// <summary>The water in a mixture: liquid (and the hydration drinking it gives), polluted, and steam.</summary>
    internal static WaterView WaterOf(GasMixture mixture)
    {
        double liquid = mixture.Water.Quantity.ToDouble();
        double steam = mixture.Steam.Quantity.ToDouble();
        return new WaterView(
            new WaterAmount(liquid, mixture.Water.Volume.ToDouble()),
            liquid * HydrationBase.HydrationPerMole,
            new WaterAmount(mixture.PollutedWater.Quantity.ToDouble(), mixture.PollutedWater.Volume.ToDouble()),
            new WaterAmount(steam, steam * Mole.MolarVolume(Chemistry.GasType.Water).ToDouble()));
    }
}
