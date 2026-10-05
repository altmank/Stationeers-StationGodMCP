#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>
/// set_battery_charge's reply: the goal, where the batteries were found (rocket and in_id forms), one result per
/// battery, and the joules the run adds (negative when it drains).
/// </summary>
internal sealed class SetBatteryChargeView
{
    internal SetBatteryChargeView(bool dryRun, ChargeGoal goal, ThingView? foundIn, BatchResultView batch,
        double addedJ)
    {
        DryRun = dryRun;
        Goal = new ChargeGoalView(goal);
        FoundIn = foundIn;
        Results = batch.Results;
        Count = batch.Count;
        SuccessCount = batch.SuccessCount;
        ErrorCount = batch.ErrorCount;
        AddedJ = Math.Round(addedJ, 1);
    }

    public bool DryRun { get; }

    public ChargeGoalView Goal { get; }

    /// <summary>The rocket or the holder searched; absent in the reference_ids form.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingView? FoundIn { get; }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }

    public double AddedJ { get; }
}

/// <summary>full, or ratio / joules with the value asked for.</summary>
internal sealed class ChargeGoalView
{
    internal ChargeGoalView(ChargeGoal goal)
    {
        Kind = goal.Kind;
        Value = goal.Amount;
    }

    public string Kind { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Value { get; }
}

/// <summary>One battery's charge before and after (predicted on a dry run, read back on a real run).</summary>
internal sealed class BatteryChargedView : BatchItemView
{
    private const int JouleDecimals = 1;
    private const int RatioDecimals = 4;

    internal BatteryChargedView(int index, ThingView battery, PowerStoreKind kind, ThingView? heldBy,
        ChargeChange change) : base(index, ok: true)
    {
        ReferenceId = battery.ReferenceId;
        PrefabName = battery.PrefabName;
        DisplayName = battery.DisplayName;
        Kind = PowerStoreKinds.Name(kind);
        HeldBy = heldBy;
        CapacityJ = Math.Round(change.Capacity, JouleDecimals);
        BeforeJ = Math.Round(change.Before, JouleDecimals);
        AfterJ = Math.Round(change.After, JouleDecimals);
        BeforeRatio = Ratio(change.Before, change.Capacity);
        AfterRatio = Ratio(change.After, change.Capacity);
        Clamped = change.Clamped ? true : null;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public string Kind { get; }

    /// <summary>The thing whose slot holds a battery cell; absent for a loose cell or a placed battery.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingView? HeldBy { get; }

    public double CapacityJ { get; }

    public double BeforeJ { get; }

    public double AfterJ { get; }

    public double? BeforeRatio { get; }

    public double? AfterRatio { get; }

    /// <summary>True when the joules asked for were more than the capacity, so the battery was set full.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Clamped { get; }

    private static double? Ratio(double charge, double capacity)
    {
        double? ratio = ChargeChange.RatioOf(charge, capacity);
        return ratio.HasValue ? Math.Round(ratio.Value, RatioDecimals) : null;
    }
}

/// <summary>A target that was not set: its id when it read as one, and why.</summary>
internal sealed class BatteryRefusedView : BatchItemView
{
    internal BatteryRefusedView(int index, ThingId? referenceId, ApiException error) : base(index, ok: false)
    {
        ReferenceId = referenceId;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId? ReferenceId { get; }

    public ErrorView Error { get; }
}
