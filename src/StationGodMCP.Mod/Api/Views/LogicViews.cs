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

/// <summary>
/// write_logic: one value written, with what the device read before and after, the warnings its checks raised (absent
/// when there are none), and the controllers that write the same logic type on the device (absent when none does).
/// </summary>
internal sealed class LogicWriteView
{
    internal LogicWriteView(string gatewayId, LogicWrite write)
    {
        GatewayId = gatewayId;
        ReferenceId = write.Checked.ReferenceId;
        LogicType = write.Checked.LogicType;
        RequestedValue = write.Checked.RequestedValue;
        PreviousValue = write.Checked.PreviousValue;
        CurrentValue = write.CurrentValue;
        Warnings = write.Checked.ReplyWarnings;
        DrivenBy = write.Checked.DrivenBy;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double RequestedValue { get; }

    /// <summary>Null when the type is write-only.</summary>
    public double? PreviousValue { get; }

    public double? CurrentValue { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<BuildIssueView>? Warnings { get; }

    /// <summary>The controllers that write the same logic type on this device; left out when none does.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ControllerView>? DrivenBy { get; }
}

/// <summary>
/// write_logic's report (dry_run): the write its checks passed, the value there now, whether it opens the device (so
/// the write rules ran), their warnings, and the controllers that write the same logic type on it; nothing written.
/// </summary>
internal sealed class LogicWriteReportView
{
    internal LogicWriteReportView(string gatewayId, LogicWriteChecked write)
    {
        GatewayId = gatewayId;
        ReferenceId = write.ReferenceId;
        LogicType = write.LogicType;
        RequestedValue = write.RequestedValue;
        PreviousValue = write.PreviousValue;
        Opens = write.Opens;
        Warnings = write.Warnings;
        DrivenBy = write.DrivenBy;
    }

    public bool ReportOnly => true;

    /// <summary>Always empty: a refused check answers ReportRefusedView instead.</summary>
    public List<ProblemView> Problems { get; } = new List<ProblemView>();

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double RequestedValue { get; }

    /// <summary>The value there now; null when the type is write-only.</summary>
    public double? PreviousValue { get; }

    public bool Opens { get; }

    public List<BuildIssueView> Warnings { get; }

    /// <summary>The controllers that write the same logic type on this device; left out when none does.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ControllerView>? DrivenBy { get; }
}

/// <summary>
/// A logic write its checks passed: the device, type and value, the value there before, whether it opens the device
/// (OpeningTransition), the warnings the checks raised, and the controllers that write the same logic type on the
/// device (DrivenBy; null when none). Every write_logic and write_logic_many reply and report is assembled from one,
/// so a fact the checks find goes here and into the views that show it.
/// </summary>
internal sealed class LogicWriteChecked
{
    internal LogicWriteChecked(LogicRead requested, double? previousValue, bool opens, List<BuildIssueView> warnings,
        List<ControllerView>? drivenBy)
    {
        ReferenceId = requested.ReferenceId;
        LogicType = requested.LogicType;
        RequestedValue = requested.Value;
        PreviousValue = previousValue;
        Opens = opens;
        Warnings = warnings;
        DrivenBy = drivenBy;
    }

    internal ThingId ReferenceId { get; }

    internal LogicTypeView LogicType { get; }

    internal double RequestedValue { get; }

    internal double? PreviousValue { get; }

    internal bool Opens { get; }

    internal List<BuildIssueView> Warnings { get; }

    internal List<ControllerView>? DrivenBy { get; }

    /// <summary>The warnings as an acting reply carries them: null for none, so the key is left out.</summary>
    internal List<BuildIssueView>? ReplyWarnings => Warnings.Count > 0 ? Warnings : null;
}

/// <summary>A logic write applied: its checked write and what the device read after (null when write-only).</summary>
internal sealed class LogicWrite
{
    internal LogicWrite(LogicWriteChecked written, double? currentValue)
    {
        Checked = written;
        CurrentValue = currentValue;
    }

    internal LogicWrite(LogicRead requested, double? previousValue, double? currentValue,
        List<ControllerView>? drivenBy = null)
        : this(new LogicWriteChecked(requested, previousValue, false, new List<BuildIssueView>(), drivenBy),
            currentValue)
    {
    }

    internal LogicWriteChecked Checked { get; }

    internal double? CurrentValue { get; }
}

/// <summary>
/// write_logic_many: each write's result in order, the counts, and the warnings the writes' checks raised, each with
/// its entry's index (absent when there are none).
/// </summary>
internal sealed class LogicWriteBatchView
{
    internal LogicWriteBatchView(string gatewayId, BatchResultView results, List<BuildIssueView> warnings)
    {
        GatewayId = gatewayId;
        Results = results.Results;
        Count = results.Count;
        SuccessCount = results.SuccessCount;
        ErrorCount = results.ErrorCount;
        Warnings = warnings.Count > 0 ? warnings : null;
    }

    public string GatewayId { get; }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<BuildIssueView>? Warnings { get; }
}

/// <summary>
/// write_logic_many's report (dry_run): each write as its checks passed it (with the value there now and whether it
/// opens its device) or refused, each refusal also a problem with its index, and the warnings with theirs; nothing
/// written.
/// </summary>
internal sealed class LogicBatchReportView
{
    internal LogicBatchReportView(string gatewayId, BatchResultView results, List<BuildIssueView> warnings)
    {
        Problems = ProblemView.Of(results.Results);
        GatewayId = gatewayId;
        Results = results.Results;
        Count = results.Count;
        SuccessCount = results.SuccessCount;
        ErrorCount = results.ErrorCount;
        Warnings = warnings;
    }

    public bool ReportOnly => true;

    public List<ProblemView> Problems { get; }

    public string GatewayId { get; }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }

    public List<BuildIssueView> Warnings { get; }
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
        ReferenceId = write.Checked.ReferenceId;
        LogicType = write.Checked.LogicType;
        RequestedValue = write.Checked.RequestedValue;
        PreviousValue = write.Checked.PreviousValue;
        CurrentValue = write.CurrentValue;
        DrivenBy = write.Checked.DrivenBy;
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

/// <summary>One write of write_logic_many's report, as its checks passed it; its warnings are the report's.</summary>
internal sealed class LogicWritePlannedItemView : BatchItemView
{
    internal LogicWritePlannedItemView(int index, LogicWriteChecked write) : base(index, ok: true)
    {
        ReferenceId = write.ReferenceId;
        LogicType = write.LogicType;
        RequestedValue = write.RequestedValue;
        PreviousValue = write.PreviousValue;
        Opens = write.Opens;
        DrivenBy = write.DrivenBy;
    }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double RequestedValue { get; }

    /// <summary>The value there now; null when the type is write-only.</summary>
    public double? PreviousValue { get; }

    public bool Opens { get; }

    /// <summary>As write_logic's: left out when no controller writes the same logic type on the device.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ControllerView>? DrivenBy { get; }
}

/// <summary>
/// A read or write of a batch that failed: the device and logic type it named, each null when it could not be read
/// from the entry, and why it failed. Callers match results by index; the names are there to read a failure alone.
/// </summary>
internal sealed class LogicFailedItemView : BatchItemView, IBatchFailure
{
    internal LogicFailedItemView(int index, ThingId? referenceId, LogicTypeView? logicType, ApiException error)
        : base(index, ok: false)
    {
        ReferenceId = referenceId;
        LogicType = logicType;
        Error = new ErrorView(error.Code, error.Message, error.Details);
    }

    public ThingId? ReferenceId { get; }

    public LogicTypeView? LogicType { get; }

    public ErrorView Error { get; }
}

/// <summary>lint_refused's data: every write rule that found a problem on the networks the write opens to.</summary>
internal sealed class LintRefusedView
{
    internal LintRefusedView(List<LintRefusedRuleView> rules)
    {
        Rules = rules;
    }

    public List<LintRefusedRuleView> Rules { get; }
}

/// <summary>One write rule's problem: the rule, its message, and the thing it is about.</summary>
internal sealed class LintRefusedRuleView
{
    internal LintRefusedRuleView(string rule, string message, ThingId? referenceId)
    {
        Rule = rule;
        Message = message;
        ReferenceId = referenceId;
    }

    public string Rule { get; }

    public string Message { get; }

    public ThingId? ReferenceId { get; }
}
