#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>read_logic: one logic value of one device.</summary>
internal sealed class LogicReadView
{
    internal LogicReadView(string gatewayId, LogicRead read)
    {
        GatewayId = gatewayId;
        ReferenceId = read.ReferenceId;
        LogicType = read.LogicType;
        Value = read.Value;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    /// <summary>A number; NaN and the infinities as the strings "NaN", "Infinity", "-Infinity".</summary>
    public double Value { get; }
}

/// <summary>A value read from a device.</summary>
internal sealed class LogicRead
{
    internal LogicRead(ThingId referenceId, LogicTypeView logicType, double value)
    {
        ReferenceId = referenceId;
        LogicType = logicType;
        Value = value;
    }

    internal ThingId ReferenceId { get; }

    internal LogicTypeView LogicType { get; }

    internal double Value { get; }
}

/// <summary>write_logic: one value written, with what the device read before and after.</summary>
internal sealed class LogicWriteView
{
    internal LogicWriteView(string gatewayId, LogicWrite write)
    {
        GatewayId = gatewayId;
        ReferenceId = write.ReferenceId;
        LogicType = write.LogicType;
        RequestedValue = write.RequestedValue;
        PreviousValue = write.PreviousValue;
        CurrentValue = write.CurrentValue;
        DrivenBy = write.DrivenBy;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double RequestedValue { get; }

    /// <summary>Null when the type is write-only.</summary>
    public double? PreviousValue { get; }

    public double? CurrentValue { get; }

    /// <summary>The controllers that write the same logic type on this device; left out when none does.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ControllerView>? DrivenBy { get; }
}

/// <summary>
/// A value written to a device, what it read before and after (null when write-only), and the controllers that write
/// the same logic type on it (null when none).
/// </summary>
internal sealed class LogicWrite
{
    internal LogicWrite(LogicRead requested, double? previousValue, double? currentValue,
        List<ControllerView>? drivenBy = null)
    {
        ReferenceId = requested.ReferenceId;
        LogicType = requested.LogicType;
        RequestedValue = requested.Value;
        PreviousValue = previousValue;
        CurrentValue = currentValue;
        DrivenBy = drivenBy;
    }

    internal ThingId ReferenceId { get; }

    internal LogicTypeView LogicType { get; }

    internal double RequestedValue { get; }

    internal double? PreviousValue { get; }

    internal double? CurrentValue { get; }

    internal List<ControllerView>? DrivenBy { get; }
}

/// <summary>read_logic_many and write_logic_many: each operation's result in order, and the counts.</summary>
internal sealed class LogicBatchView
{
    internal LogicBatchView(string gatewayId, BatchResultView results)
    {
        GatewayId = gatewayId;
        Results = results.Results;
        Count = results.Count;
        SuccessCount = results.SuccessCount;
        ErrorCount = results.ErrorCount;
    }

    public string GatewayId { get; }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }
}

internal sealed class LogicReadItemView : BatchItemView
{
    internal LogicReadItemView(int index, LogicRead read) : base(index, ok: true)
    {
        ReferenceId = read.ReferenceId;
        LogicType = read.LogicType;
        Value = read.Value;
    }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double Value { get; }
}

internal sealed class LogicWriteItemView : BatchItemView
{
    internal LogicWriteItemView(int index, LogicWrite write) : base(index, ok: true)
    {
        ReferenceId = write.ReferenceId;
        LogicType = write.LogicType;
        RequestedValue = write.RequestedValue;
        PreviousValue = write.PreviousValue;
        CurrentValue = write.CurrentValue;
        DrivenBy = write.DrivenBy;
    }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double RequestedValue { get; }

    public double? PreviousValue { get; }

    public double? CurrentValue { get; }

    /// <summary>As write_logic's: left out when no controller writes the same logic type on the device.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ControllerView>? DrivenBy { get; }
}

/// <summary>
/// A read or write of a batch that failed: the device and logic type it named, each null when it could not be read
/// from the entry, and why it failed. Callers match results by index; the names are there to read a failure alone.
/// </summary>
internal sealed class LogicFailedItemView : BatchItemView
{
    internal LogicFailedItemView(int index, ThingId? referenceId, LogicTypeView? logicType, ApiException error)
        : base(index, ok: false)
    {
        ReferenceId = referenceId;
        LogicType = logicType;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId? ReferenceId { get; }

    public LogicTypeView? LogicType { get; }

    public ErrorView Error { get; }
}
