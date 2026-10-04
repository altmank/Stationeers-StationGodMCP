#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>set_ic_pins: the pins written, and all the housing's pins afterwards.</summary>
internal sealed class SetIcPinsView
{
    internal SetIcPinsView(string gatewayId, ThingId referenceId, bool hasDataNetwork, List<PinChangeView> changes,
        List<IcPinView> pins)
    {
        GatewayId = gatewayId;
        ReferenceId = referenceId;
        HasDataNetwork = hasDataNetwork;
        Changes = changes;
        Pins = pins;
    }

    /// <summary>The gateway's reference id, or "world".</summary>
    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public bool HasDataNetwork { get; }

    public List<PinChangeView> Changes { get; }

    public List<IcPinView> Pins { get; }
}

internal sealed class PinChangeView
{
    internal PinChangeView(int index, ThingId? previousReferenceId, ThingId? referenceId, bool changed)
    {
        Index = index;
        PreviousReferenceId = previousReferenceId;
        ReferenceId = referenceId;
        Changed = changed;
    }

    public int Index { get; }

    public string Name => IcPinView.PinName(Index);

    public ThingId? PreviousReferenceId { get; }

    public ThingId? ReferenceId { get; }

    public bool Changed { get; }
}

/// <summary>One pin d0..dN of a circuit holder, as get_ic_status and set_ic_pins list it.</summary>
internal sealed class IcPinView
{
    internal IcPinView(int index, ThingView? device, string? label, bool reachable)
    {
        Index = index;
        ReferenceId = device?.ReferenceId;
        PrefabName = device?.PrefabName;
        DisplayName = device?.DisplayName;
        Label = string.IsNullOrEmpty(label) ? null : label;
        Reachable = reachable;
    }

    public int Index { get; }

    public string Name => PinName(Index);

    public ThingId? ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The alias the chip gave the pin; null when none.</summary>
    public string? Label { get; }

    /// <summary>Whether the chip reaches the device through the pin right now.</summary>
    public bool Reachable { get; }

    /// <summary>No device and no alias: a pin replies leave out.</summary>
    internal bool HoldsNothing => ReferenceId == null && Label == null;

    internal static string PinName(int index) =>
        "d" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
