#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>read logic: one logic value of one device.</summary>
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
/// A logic write its checks passed: the device, type and value, the value there before, whether it opens the device
/// (OpeningTransition), the warnings the checks raised, the controllers that write the same logic type on the device
/// (DrivenBy; null when none), and whether allow: ["lint"] let a lint problem through (LintPassed). Every write logic
/// reply and report is assembled from one, so a fact the checks find goes here and into the replies that show it.
/// </summary>
internal sealed class LogicWriteChecked
{
    internal LogicWriteChecked(LogicRead requested, double? previousValue, bool opens, List<BuildIssueView> warnings,
        List<ControllerView>? drivenBy, bool lintPassed = false)
    {
        ReferenceId = requested.ReferenceId;
        LogicType = requested.LogicType;
        RequestedValue = requested.Value;
        PreviousValue = previousValue;
        Opens = opens;
        Warnings = warnings;
        DrivenBy = drivenBy;
        LintPassed = lintPassed;
    }

    internal ThingId ReferenceId { get; }

    internal LogicTypeView LogicType { get; }

    internal double RequestedValue { get; }

    internal double? PreviousValue { get; }

    internal bool Opens { get; }

    internal List<BuildIssueView> Warnings { get; }

    internal List<ControllerView>? DrivenBy { get; }

    /// <summary>A lint problem would have refused the write, and allow: ["lint"] let it through.</summary>
    internal bool LintPassed { get; }
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
