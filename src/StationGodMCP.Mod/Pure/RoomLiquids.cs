#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A liquid a move adds to a room: how much, and the gas pressure it needs around it to stay liquid.</summary>
internal sealed class LiquidArrival
{
    internal LiquidArrival(string liquid, double addedMol, double minLiquidPressureKpa)
    {
        Liquid = liquid;
        AddedMol = addedMol;
        MinLiquidPressureKpa = minLiquidPressureKpa;
    }

    internal string Liquid { get; }

    internal double AddedMol { get; }

    internal double MinLiquidPressureKpa { get; }
}

/// <summary>The room's air after a move, as far as its liquids go.</summary>
internal sealed class RoomAirAfter
{
    internal RoomAirAfter(double gasPressureKpa, double temperatureK, double frozenBeforeMol, double frozenAfterMol,
        bool everyLiquidBoils)
    {
        GasPressureKpa = gasPressureKpa;
        TemperatureK = temperatureK;
        FrozenBeforeMol = frozenBeforeMol;
        FrozenAfterMol = frozenAfterMol;
        EveryLiquidBoils = everyLiquidBoils;
    }

    internal double GasPressureKpa { get; }

    internal double TemperatureK { get; }

    internal double FrozenBeforeMol { get; }

    internal double FrozenAfterMol { get; }

    /// <summary>Whether the room has the heat to boil every liquid in it into its gas.</summary>
    internal bool EveryLiquidBoils { get; }
}

/// <summary>
/// Whether a room's air would lose liquid a move brings in. A room's cells freeze any amount out of their air
/// (Atmosphere.StateChange in World mode: into the room's frozen contents, or ice once a cell holds 50 mol), unlike a
/// network, which tolerates some. A liquid under its minimum liquid pressure keeps evaporating (Mole.ChangeState clamps
/// its evaporation pressure to at least that minimum), and each mole that turns to gas takes its latent heat out of the
/// room, so unless the room can boil all of it, what is left cools to its freezing point and freezes.
/// </summary>
internal static class RoomLiquids
{
    private const double FrozenToleranceMol = 1e-6;

    /// <summary>Why the room would lose liquid the move brings in, or null when it keeps it.</summary>
    internal static string? Loss(RoomAirAfter air, IReadOnlyList<LiquidArrival> arrivals)
    {
        if (air.FrozenAfterMol > air.FrozenBeforeMol + FrozenToleranceMol)
        {
            return $"{air.FrozenAfterMol:0.###} mol would freeze at {air.TemperatureK:0.#} K, and a room's air loses " +
                   "whatever freezes in it";
        }

        if (air.EveryLiquidBoils)
        {
            return null;
        }

        foreach (LiquidArrival arrival in arrivals)
        {
            if (arrival.AddedMol > 0.0 && air.GasPressureKpa < arrival.MinLiquidPressureKpa)
            {
                return $"{arrival.Liquid} would sit in air at {air.GasPressureKpa:0.###} kPa, under the " +
                       $"{arrival.MinLiquidPressureKpa:0.#} kPa it needs to stay liquid: it keeps evaporating and " +
                       "cooling the room, which has not the heat to boil it all, so the rest freezes, and a " +
                       "room's air loses whatever freezes in it";
            }
        }

        return null;
    }
}
