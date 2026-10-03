#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>atmosphere_contents: every atmosphere one thing, pipe network or atmosphere id holds.</summary>
internal sealed class AtmosphereContentsView
{
    private const string NoteText =
        "amount_mol per gas or liquid; liquid_l for liquids comes from each liquid's molar volume (Mole.Volume). " +
        "water.hydration is what drinking that liquid water would give (HydrationBase: 5 per litre, 55.56 mol per " +
        "litre). Polluted water needs a water purifier first and steam must condense before it can be drunk.";

    internal AtmosphereContentsView(ThingId referenceId, object subject, List<HeldAtmosphereEntryView> atmospheres)
    {
        ReferenceId = referenceId;
        Subject = subject;
        Atmospheres = atmospheres;
        Count = atmospheres.Count;
    }

    public ThingId ReferenceId { get; }

    /// <summary>An ItemOwnerView, StructureOwnerView or NetworkOwnerView.</summary>
    public object Subject { get; }

    public List<HeldAtmosphereEntryView> Atmospheres { get; }

    public int Count { get; }

    public string Note => NoteText;
}

/// <summary>Where an atmosphere_contents entry comes from: the reply's source values.</summary>
internal static class AtmosphereSource
{
    internal const string Internal = "internal";
    internal const string PipeNetwork = "pipe_network";
    internal const string LandingPadNetwork = "landing_pad_network";
    internal const string MountedPipeNetwork = "mounted_pipe_network";
    internal const string ConnectedNetwork = "connected_network";
    internal const string Slot = "slot";

    internal static readonly string[] All =
        { Internal, PipeNetwork, LandingPadNetwork, MountedPipeNetwork, ConnectedNetwork, Slot };
}

/// <summary>One atmosphere of the subject: where it comes from, who owns it, and what it holds.</summary>
internal sealed class HeldAtmosphereEntryView
{
    internal HeldAtmosphereEntryView(string source, object owner, SlotRef? slot, HeldAtmosphereView? atmosphere)
    {
        Source = source;
        Owner = owner;
        SlotIndex = slot?.Index;
        SlotName = slot?.Name;
        Atmosphere = atmosphere;
    }

    /// <summary>One of AtmosphereSource.All.</summary>
    public string Source { get; }

    public object Owner { get; }

    public int? SlotIndex { get; }

    public string? SlotName { get; }

    /// <summary>Null for a pipe network that has no atmosphere yet.</summary>
    public HeldAtmosphereView? Atmosphere { get; }
}

/// <summary>A slot by index and display name.</summary>
internal sealed class SlotRef
{
    internal SlotRef(int index, string? name)
    {
        Index = index;
        Name = name;
    }

    internal int Index { get; }

    internal string? Name { get; }
}

/// <summary>water_sources: every canister, tank and pipe network holding water, largest first.</summary>
internal sealed class WaterSourcesView
{
    private const string NoteText =
        "Water in canisters, tanks and pipe networks, not in bottles or packets (see consumables). It is drunk " +
        "through a water bottle filler or a drinking fountain on a pipe network. Polluted water needs a water " +
        "purifier first; steam has to condense. Room and world air, bodies and organs are left out. Sources under " +
        "min_mol (default 1 mol, 0.018 L) are skipped.";

    internal WaterSourcesView(List<WaterSourceView> sources, WaterView totals, double minMol,
        LocalPlayerView? localPlayer)
    {
        Sources = sources;
        Count = sources.Count;
        Totals = totals;
        MinMol = minMol;
        LocalPlayer = localPlayer;
    }

    public List<WaterSourceView> Sources { get; }

    public int Count { get; }

    public WaterView Totals { get; }

    public double MinMol { get; }

    public LocalPlayerView? LocalPlayer { get; }

    public string Note => NoteText;
}

internal sealed class WaterSourceView
{
    internal WaterSourceView(bool isThing, object owner, ThingId atmosphereId, AtmosphereState state, WaterView water)
    {
        Kind = isThing ? "thing" : "pipe_network";
        Owner = owner;
        AtmosphereId = atmosphereId;
        VolumeL = state.VolumeL;
        PressureKpa = state.PressureKpa;
        TemperatureK = state.TemperatureK;
        Water = water;
    }

    /// <summary>thing or pipe_network.</summary>
    public string Kind { get; }

    /// <summary>An ItemOwnerView, StructureOwnerView or NetworkOwnerView.</summary>
    public object Owner { get; }

    public ThingId AtmosphereId { get; }

    public double VolumeL { get; }

    public double PressureKpa { get; }

    public double TemperatureK { get; }

    public WaterView Water { get; }
}
