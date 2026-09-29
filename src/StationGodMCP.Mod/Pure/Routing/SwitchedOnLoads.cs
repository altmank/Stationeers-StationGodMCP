#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// The most a device that is off now can move through a port once it is switched on, for would_overload_when_on.
/// Its numbers now depend on a charge the game keeps changing (a battery near full asks for a few kW one second and
/// twice that the next, as it drains), so a warning priced from them comes and goes between a dry run and the real run.
/// These are the game's own ceilings instead, the same whatever the charge (decompile Battery, AreaPowerControl,
/// PowerTransmitter): a station battery has no rate limit, GetGeneratedPower gives all of PowerStored and GetUsedPower
/// asks for all of PowerMaximum - PowerStored in one power tick, so its ceiling both ways is PowerMaximum (full, or
/// empty); an APC's output gives AvailablePower (its input network's potential plus its cell's charge), whose ceiling
/// is the game's MaximumPower (that potential plus the cell's PowerMaximum); a power transmitter moves at most
/// MaxPowerTransmission each way. The flow is min(potential, required), so a ceiling on one side is bounded by the
/// other: a battery's input beside generators still carries only their rate, its output only its consumers' demand.
/// </summary>
internal static class SwitchedOnLoads
{
    /// <summary>A station battery's output switched on: all it can hold (PowerMaximum), full or not.</summary>
    internal static double BatteryOutput(double powerMaximum) => Math.Max(0.0, powerMaximum);

    /// <summary>A station battery's input switched on: all it can hold (PowerMaximum), as if empty.</summary>
    internal static double BatteryInput(double powerMaximum) => Math.Max(0.0, powerMaximum);

    /// <summary>An APC's output switched on: its input network's potential plus a full cell (MaximumPower).</summary>
    internal static double ApcOutput(double inputPotential, double? cellMaximum) =>
        Math.Max(0.0, inputPotential) + Math.Max(0.0, cellMaximum ?? 0.0);

    /// <summary>A power transmitter's input switched on: MaxPowerTransmission.</summary>
    internal static double TransmitterInput(double maxTransmission) => Math.Max(0.0, maxTransmission);

    /// <summary>A power transmitter's output switched on: its input network's potential, at most MaxPowerTransmission.</summary>
    internal static double TransmitterOutput(double inputPotential, double maxTransmission) =>
        Math.Max(0.0, Math.Min(maxTransmission, inputPotential));
}
