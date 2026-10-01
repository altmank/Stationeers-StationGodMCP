#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>read_devices: every item's reads, all made in one frame, with the clock when asked for.</summary>
internal sealed class ReadDevicesView
{
    internal ReadDevicesView(string gatewayId, GameClockView? clock, BatchResultView batch)
    {
        GatewayId = gatewayId;
        Clock = clock;
        Results = batch.Results;
        Count = batch.Count;
        SuccessCount = batch.SuccessCount;
        ErrorCount = batch.ErrorCount;
    }

    public string GatewayId { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GameClockView? Clock { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }

    public List<BatchItemView> Results { get; }
}

/// <summary>What one item read: each part asked for, or that part's error in errors; parts not asked are left out.</summary>
internal sealed class DeviceReadParts
{
    internal DeviceReadParts(LogicReadings? logic, List<SlotReadView>? slots, AtmosphereReadView? atmosphere,
        ReagentsReadView? reagents, Dictionary<string, ErrorView>? errors)
    {
        Logic = logic;
        Slots = slots;
        Atmosphere = atmosphere;
        Reagents = reagents;
        Errors = errors;
    }

    internal LogicReadings? Logic { get; }

    internal List<SlotReadView>? Slots { get; }

    internal AtmosphereReadView? Atmosphere { get; }

    internal ReagentsReadView? Reagents { get; }

    internal Dictionary<string, ErrorView>? Errors { get; }
}

/// <summary>Values by the name the client sent, and the names that failed with their errors.</summary>
internal sealed class LogicReadings
{
    internal LogicReadings(Dictionary<string, double> values, Dictionary<string, ErrorView> errors)
    {
        Values = values;
        Errors = errors;
    }

    internal Dictionary<string, double> Values { get; }

    internal Dictionary<string, ErrorView> Errors { get; }
}

/// <summary>One item found: its parts. errors names a part that failed as a whole (the device out of scope).</summary>
internal sealed class DeviceReadItemView : BatchItemView
{
    internal DeviceReadItemView(int index, ThingId referenceId, DeviceReadParts parts) : base(index, ok: true)
    {
        ReferenceId = referenceId;
        Logic = parts.Logic?.Values;
        LogicErrors = parts.Logic != null && parts.Logic.Errors.Count > 0 ? parts.Logic.Errors : null;
        Slots = parts.Slots;
        Atmosphere = parts.Atmosphere;
        Reagents = parts.Reagents;
        Errors = parts.Errors != null && parts.Errors.Count > 0 ? parts.Errors : null;
    }

    public ThingId ReferenceId { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, double>? Logic { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, ErrorView>? LogicErrors { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SlotReadView>? Slots { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public AtmosphereReadView? Atmosphere { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ReagentsReadView? Reagents { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, ErrorView>? Errors { get; }
}

/// <summary>An item whose reference id names nothing: no thing, network or atmosphere has it.</summary>
internal sealed class DeviceReadFailedView : BatchItemView
{
    internal DeviceReadFailedView(int index, ThingId referenceId, ApiException error) : base(index, ok: false)
    {
        ReferenceId = referenceId;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId ReferenceId { get; }

    public ErrorView Error { get; }
}

/// <summary>
/// One slot: its slot logic values by the name sent (without logic: every type the slot reads, by the names
/// inspect_slots gives), the names that failed, or the slot's own error (slot_not_found).
/// </summary>
internal sealed class SlotReadView
{
    private SlotReadView(int index, LogicReadings? logic, ErrorView? error)
    {
        Index = index;
        Logic = logic?.Values;
        LogicErrors = logic != null && logic.Errors.Count > 0 ? logic.Errors : null;
        Error = error;
    }

    internal static SlotReadView Read(int index, LogicReadings logic) => new SlotReadView(index, logic, null);

    internal static SlotReadView Failed(int index, ApiException error) =>
        new SlotReadView(index, null, new ErrorView(error.Code, error.Message));

    public int Index { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, double>? Logic { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, ErrorView>? LogicErrors { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ErrorView? Error { get; }
}

/// <summary>
/// One atmosphere, compact: where it comes from, the network's id when it is a network's, its bulk figures and each
/// gas and liquid held (atmosphere_contents' figures, without owner, display names or water).
/// </summary>
internal sealed class AtmosphereReadView
{
    internal AtmosphereReadView(string source, ThingId atmosphereId, ThingId? networkId, AtmosphereState state,
        List<CompactGasView> contents)
    {
        Source = source;
        AtmosphereId = atmosphereId;
        NetworkId = networkId;
        VolumeL = state.VolumeL;
        PressureKpa = state.PressureKpa;
        TemperatureK = state.TemperatureK;
        TotalMol = state.TotalMol;
        LiquidVolumeL = state.LiquidVolumeL;
        Contents = contents;
    }

    /// <summary>internal, pipe_network or landing_pad_network.</summary>
    public string Source { get; }

    public ThingId AtmosphereId { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? NetworkId { get; }

    public double VolumeL { get; }

    public double PressureKpa { get; }

    public double TemperatureK { get; }

    public double TotalMol { get; }

    public double LiquidVolumeL { get; }

    public List<CompactGasView> Contents { get; }
}

/// <summary>A gas or liquid held: atmosphere_contents' gas, state, amount_mol and (liquids) liquid_l.</summary>
internal sealed class CompactGasView
{
    internal CompactGasView(string gas, bool liquid, double amountMol, double? liquidL)
    {
        Gas = gas;
        State = liquid ? "liquid" : "gas";
        AmountMol = amountMol;
        LiquidL = liquidL;
    }

    public string Gas { get; }

    /// <summary>gas or liquid.</summary>
    public string State { get; }

    public double AmountMol { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? LiquidL { get; }
}

/// <summary>The reagents tool's total and reagents for the item.</summary>
internal sealed class ReagentsReadView
{
    internal ReagentsReadView(double total, List<ReagentView> reagents)
    {
        Total = total;
        Reagents = reagents;
    }

    public double Total { get; }

    public List<ReagentView> Reagents { get; }
}
