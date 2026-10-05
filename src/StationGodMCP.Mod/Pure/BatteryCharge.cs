#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// The charge set_battery_charge gives a battery: full (the default), a share of its capacity, or a number of joules.
/// Full is the whole capacity, as the console's 'power chargeall' sets it (PowerCommand), not the 99.9 % the
/// 'setbatteries Full' state gives (IChargable.SetPower); the game shows both as Full.
/// </summary>
internal abstract class ChargeGoal
{
    private ChargeGoal()
    {
    }

    internal static readonly ChargeGoal Full = new FullCharge();

    /// <summary>The goal ratio or joules name; both at once, a ratio outside 0 to 1, or negative joules is null.</summary>
    internal static ChargeGoal? Of(double? ratio, double? joules, out string? problem)
    {
        problem = null;
        if (ratio.HasValue && joules.HasValue)
        {
            problem = "Pass ratio or joules, not both.";
            return null;
        }

        if (ratio.HasValue)
        {
            if (ratio.Value < 0.0 || ratio.Value > 1.0)
            {
                problem = "Argument 'ratio' must be from 0 to 1.";
                return null;
            }

            return new ShareOfCapacity(ratio.Value);
        }

        if (joules.HasValue)
        {
            if (joules.Value < 0.0)
            {
                problem = "Argument 'joules' must be 0 or more.";
                return null;
            }

            return new Joules(joules.Value);
        }

        return Full;
    }

    /// <summary>The charge in joules a store of this capacity gets, clamped to what it holds.</summary>
    internal abstract ChargeChange Apply(double before, double capacity);

    /// <summary>The goal as the reply names it: full, ratio or joules.</summary>
    internal abstract string Kind { get; }

    /// <summary>The ratio or joules asked for; null for full.</summary>
    internal abstract double? Amount { get; }

    private sealed class FullCharge : ChargeGoal
    {
        internal override string Kind => "full";

        internal override double? Amount => null;

        internal override ChargeChange Apply(double before, double capacity) =>
            new ChargeChange(before, capacity, capacity, clamped: false);
    }

    private sealed class ShareOfCapacity : ChargeGoal
    {
        private readonly double _ratio;

        internal ShareOfCapacity(double ratio)
        {
            _ratio = ratio;
        }

        internal override string Kind => "ratio";

        internal override double? Amount => _ratio;

        internal override ChargeChange Apply(double before, double capacity) =>
            new ChargeChange(before, capacity * _ratio, capacity, clamped: false);
    }

    private sealed class Joules : ChargeGoal
    {
        private readonly double _joules;

        internal Joules(double joules)
        {
            _joules = joules;
        }

        internal override string Kind => "joules";

        internal override double? Amount => _joules;

        internal override ChargeChange Apply(double before, double capacity) =>
            _joules > capacity
                ? new ChargeChange(before, capacity, capacity, clamped: true)
                : new ChargeChange(before, _joules, capacity, clamped: false);
    }
}

/// <summary>One store's charge before and after, its capacity, and whether the joules asked for exceeded it.</summary>
internal sealed class ChargeChange
{
    internal ChargeChange(double before, double after, double capacity, bool clamped)
    {
        Before = before;
        After = after;
        Capacity = capacity;
        Clamped = clamped;
    }

    internal double Before { get; }

    internal double After { get; }

    internal double Capacity { get; }

    internal bool Clamped { get; }

    internal double AddedJoules => After - Before;

    internal static double? RatioOf(double charge, double capacity) => capacity > 0.0 ? charge / capacity : null;
}

/// <summary>
/// What kind of power store a thing is, by the game's classes: a placed Battery (Station Battery, Large Station
/// Battery) or one built into a rocket (Battery with an InternalCellType or a RocketNetwork), a battery cell item
/// (BatteryCell and its subclasses, the wireless cell among them), a power pylon end's buffer
/// (PowerPylonTerminus.PowerStored), or any other IChargable a mod adds.
/// </summary>
internal enum PowerStoreKind
{
    StationBattery,
    RocketBattery,
    BatteryCell,
    PylonBuffer,
    OtherChargeable
}

internal static class PowerStoreKinds
{
    internal static string Name(PowerStoreKind kind) =>
        kind switch
        {
            PowerStoreKind.StationBattery => "station_battery",
            PowerStoreKind.RocketBattery => "rocket_battery",
            PowerStoreKind.BatteryCell => "battery_cell",
            PowerStoreKind.PylonBuffer => "pylon_buffer",
            _ => "other_chargeable"
        };
}
