#nullable enable

using System;
using StationGodMCP.Pure.DeviceReads;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>
/// How often a devices subscription reads, in game seconds: 0.5 (one game tick) to 3600, 1 by default. Game time stops
/// while the game is paused, so a paused game samples nothing.
/// </summary>
internal readonly struct SamplingInterval
{
    internal const double MinimumSeconds = 0.5;
    internal const double MaximumSeconds = 3600.0;
    internal const double DefaultSeconds = 1.0;

    private SamplingInterval(double seconds)
    {
        Seconds = seconds;
    }

    internal double Seconds { get; }

    internal static SamplingInterval Default => new SamplingInterval(DefaultSeconds);

    /// <summary>The interval, or false when seconds is not a finite number from MinimumSeconds to MaximumSeconds.</summary>
    internal static bool TryOf(double seconds, out SamplingInterval interval)
    {
        bool valid = !double.IsNaN(seconds) && seconds >= MinimumSeconds && seconds <= MaximumSeconds;
        interval = valid ? new SamplingInterval(seconds) : Default;
        return valid;
    }
}

/// <summary>
/// What one sample of a devices subscription reads and is expected to cost: the values read_devices counts (a slot
/// without logic counts DeviceReadBounds.AllSlotValuesWeight), the items, and the main-thread milliseconds per sample
/// from the starting figures of scheduling.md (GUESS until measured).
/// </summary>
internal readonly struct SubscriptionPlan
{
    /// <summary>Per logic or slot logic value.</summary>
    internal const double ValueCostMs = 0.005;

    internal const double AtmosphereCostMs = 0.08;

    internal const double ReagentsCostMs = 0.05;

    internal SubscriptionPlan(int items, int values, double costMs)
    {
        Items = items;
        Values = values;
        CostMs = costMs;
    }

    internal int Items { get; }

    internal int Values { get; }

    internal double CostMs { get; }

    internal static SubscriptionPlan Of(DeviceReadRequest request)
    {
        int values = 0;
        int atmospheres = 0;
        int reagents = 0;
        foreach (DeviceReadItem item in request.Items)
        {
            values += item.Weight;
            atmospheres += item.Atmosphere != null ? 1 : 0;
            reagents += item.Reagents ? 1 : 0;
        }

        return new SubscriptionPlan(request.Items.Count, values,
            values * ValueCostMs + atmospheres * AtmosphereCostMs + reagents * ReagentsCostMs);
    }

    /// <summary>Main-thread milliseconds per game second at the interval.</summary>
    internal double ProjectedMsPerSecond(SamplingInterval interval) => CostMs / interval.Seconds;
}

/// <summary>
/// The admission limits: per subscription (one read_devices call), per connection (subscriptions and values), and
/// across the mod the projected sampling load, half the subscription lane's share of a frame at 30 frames a second
/// (scheduling.md, Subscriptions). A lane share of 0 turns subscriptions off.
/// </summary>
internal sealed class SubscriptionLimits
{
    internal const int DefaultMaxSubscriptions = 64;
    internal const int DefaultMaxConnectionValues = 8192;
    internal const double DefaultSubscriptionBudgetMs = 1.5;
    internal const double FramesPerSecond = 30.0;

    internal SubscriptionLimits(int maxSubscriptions, int maxConnectionValues, double laneShareMs)
    {
        MaxSubscriptions = maxSubscriptions;
        MaxConnectionValues = maxConnectionValues;
        LaneShareMs = laneShareMs;
    }

    internal int MaxItemsPerSubscription => DeviceReadBounds.MaximumItems;

    internal int MaxValuesPerSubscription => DeviceReadBounds.MaximumValues;

    internal int MaxSubscriptions { get; }

    internal int MaxConnectionValues { get; }

    /// <summary>The subscription lane's share of one frame, in milliseconds.</summary>
    internal double LaneShareMs { get; }

    internal bool Enabled => LaneShareMs > 0.0;

    /// <summary>The projected sampling load every subscription together may reach, in ms per game second.</summary>
    internal double MaxProjectedMsPerSecond => LaneShareMs / 2.0 * FramesPerSecond;

    internal static SubscriptionLimits Default => From(DefaultSubscriptionBudgetMs, FrameBudget.DefaultMs);

    /// <summary>
    /// From [Performance] SubscriptionBudgetMs and RequestBudgetMs (0 = unlimited): the lane's share is
    /// SubscriptionBudgetMs, at most half the request budget.
    /// </summary>
    internal static SubscriptionLimits From(double subscriptionBudgetMs, double requestBudgetMs)
    {
        double share = requestBudgetMs > 0.0
            ? Math.Min(subscriptionBudgetMs, requestBudgetMs / 2.0)
            : subscriptionBudgetMs;
        return new SubscriptionLimits(DefaultMaxSubscriptions, DefaultMaxConnectionValues, Math.Max(0.0, share));
    }
}

/// <summary>Which limit a subscription would pass; Name is its key in welcome.limits where it has one.</summary>
internal enum SubscriptionLimitKind
{
    SubscriptionsOff,
    ItemsPerSubscription,
    ValuesPerSubscription,
    SubscriptionsPerConnection,
    ValuesPerConnection,
    ProjectedLoad,
}

/// <summary>
/// A subscribe refused with subscription_limit: the limit passed, its maximum, what the subscription would have made it,
/// and the projected load of every subscription with this one. The code is the client's signal to poll the same
/// items with read_devices at the same interval instead.
/// </summary>
internal sealed class SubscriptionRefusal
{
    internal const string ErrorCode = "subscription_limit";

    internal SubscriptionRefusal(SubscriptionLimitKind kind, double maximum, double requested,
        double projectedMsPerSecond)
    {
        Kind = kind;
        Maximum = maximum;
        Requested = requested;
        ProjectedMsPerSecond = projectedMsPerSecond;
    }

    internal SubscriptionLimitKind Kind { get; }

    internal double Maximum { get; }

    internal double Requested { get; }

    internal double ProjectedMsPerSecond { get; }

    internal string Name => Kind switch
    {
        SubscriptionLimitKind.SubscriptionsOff => "subscription_budget_ms",
        SubscriptionLimitKind.ItemsPerSubscription => "max_items_per_subscription",
        SubscriptionLimitKind.ValuesPerSubscription => "max_values_per_subscription",
        SubscriptionLimitKind.SubscriptionsPerConnection => "max_subscriptions",
        SubscriptionLimitKind.ValuesPerConnection => "max_subscription_values",
        SubscriptionLimitKind.ProjectedLoad => "max_projected_ms_per_s",
        _ => throw new InvalidOperationException($"Unknown subscription limit {Kind}."),
    };

    internal string Message => Kind switch
    {
        SubscriptionLimitKind.SubscriptionsOff =>
            "Subscriptions are off on this game ([Performance] SubscriptionBudgetMs is 0); poll with read_devices.",
        SubscriptionLimitKind.ProjectedLoad =>
            $"Sampling every subscription with this one would cost {Format(Requested)} ms per game second; at most " +
            $"{Format(Maximum)}. Poll with read_devices at the same interval instead.",
        _ => $"The subscription would make {Name} {Format(Requested)}; at most {Format(Maximum)}. Poll with " +
             "read_devices at the same interval instead.",
    };

    internal SubscriptionLimitData Data => new SubscriptionLimitData(Name, Maximum, Requested, ProjectedMsPerSecond);

    private static string Format(double value) =>
        Math.Round(value, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>subscription_limit's error data on the wire.</summary>
internal sealed class SubscriptionLimitData
{
    internal SubscriptionLimitData(string name, double limit, double requested, double projectedMsPerS)
    {
        Name = name;
        Limit = limit;
        Requested = requested;
        ProjectedMsPerS = Math.Round(projectedMsPerS, 3);
    }

    /// <summary>The limit passed, as welcome.limits names it.</summary>
    public string Name { get; }

    public double Limit { get; }

    public double Requested { get; }

    public double ProjectedMsPerS { get; }
}
