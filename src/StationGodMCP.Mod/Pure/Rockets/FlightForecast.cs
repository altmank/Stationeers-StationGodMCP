#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Rockets;

/// <summary>A flight plan flown on a copy of the rocket: every leg's result, and the rocket as the last leg left it.</summary>
internal sealed class FlightRun
{
    internal FlightRun(List<LegResult> legs, RocketCraft final, RocketCraft? beforeLanding, LandingLeg? landing)
    {
        Legs = legs;
        Final = final;
        BeforeLanding = beforeLanding;
        Landing = landing;
    }

    internal List<LegResult> Legs { get; }

    internal RocketCraft Final { get; }

    /// <summary>The rocket as the last landing leg began (for the landing limits); null when the plan has no landing.</summary>
    internal RocketCraft? BeforeLanding { get; }

    internal LandingLeg? Landing { get; }

    /// <summary>Whether every leg ended so the next could follow (a final hard landing counts as a failure).</summary>
    internal bool Succeeded
    {
        get
        {
            for (int index = 0; index < Legs.Count; index++)
            {
                if (!Legs[index].Outcome.Continues)
                {
                    return false;
                }
            }

            return Legs.Count > 0;
        }
    }
}

/// <summary>Flies a plan on a copy of the rocket. The live rocket is never touched.</summary>
internal static class FlightForecast
{
    internal static FlightRun Run(FlightSimulator simulator, RocketCraft rocket, IReadOnlyList<FlightLeg> plan)
    {
        RocketCraft craft = rocket.Copy();
        List<LegResult> results = new List<LegResult>(plan.Count + 1);
        RocketCraft? beforeLanding = null;
        LandingLeg? landing = null;
        for (int index = 0; index < plan.Count; index++)
        {
            FlightLeg leg = plan[index];
            if (leg is LandingLeg landingLeg)
            {
                beforeLanding = craft.Copy();
                landing = landingLeg;
            }

            LegResult result = leg.Fly(simulator, craft);
            results.Add(result);
            if (result.Outcome is LegOutcome.LaunchAborted aborted && leg is LaunchLeg launch)
            {
                // ChangeTarget(CurrentNode) then InitAutomatedLanding (Rocket.cs:2076-2083): it lands on its own pad.
                LandingLeg back = new LandingLeg("launch abort", launch.From, launch.Distance, launch.Gravity,
                    new LandingStart.Midway(aborted.Altitude, aborted.Velocity, aborted.Acceleration,
                        Math.Min(aborted.Altitude, 1000f), true));
                beforeLanding = craft.Copy();
                landing = back;
                results.Add(back.Fly(simulator, craft));
                break;
            }

            if (!result.Outcome.Continues)
            {
                break;
            }
        }

        return new FlightRun(results, craft, beforeLanding, landing);
    }

    /// <summary>The least fuel (mol, the rocket's mix) the whole plan succeeds with, by bisection; null when even a full load fails.</summary>
    internal static double? LeastFuel(FlightSimulator simulator, RocketCraft rocket, IReadOnlyList<FlightLeg> plan,
        double ceilingMol)
    {
        bool Works(double moles)
        {
            RocketCraft trial = rocket.Copy();
            trial.SetFuelMoles(moles);
            return Run(simulator, trial, plan).Succeeded;
        }

        return Bisect.LowestPassing(0.0, ceilingMol, Works, 1.0);
    }
}

/// <summary>Bisection over a pass/fail judgement assumed to switch once in the range.</summary>
internal static class Bisect
{
    private const int MaximumSteps = 40;

    /// <summary>The lowest value that passes, to within resolution; null when the top fails; the bottom when it passes.</summary>
    internal static double? LowestPassing(double low, double high, Func<double, bool> passes, double resolution)
    {
        if (!passes(high))
        {
            return null;
        }

        if (passes(low))
        {
            return low;
        }

        for (int step = 0; step < MaximumSteps && high - low > resolution; step++)
        {
            double middle = (low + high) / 2.0;
            if (passes(middle))
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return high;
    }

    /// <summary>The highest value that passes; null when the bottom fails; the top when it passes.</summary>
    internal static double? HighestPassing(double low, double high, Func<double, bool> passes, double resolution)
    {
        if (!passes(low))
        {
            return null;
        }

        if (passes(high))
        {
            return high;
        }

        for (int step = 0; step < MaximumSteps && high - low > resolution; step++)
        {
            double middle = (low + high) / 2.0;
            if (passes(middle))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

/// <summary>
/// How much room one landing has: the heaviest rocket it still brings down undamaged, the fuel range, the thrust loss it
/// survives (the confidence check keeps seeing the recorded peak, so lost thrust shows as a crash, not an abort), the
/// heaviest rocket the game's own confidence keeps at or above a chosen floor, and the same landing at each re-entry
/// profile. All flown from the rocket as the landing leg begins.
/// </summary>
internal sealed class LandingLimits
{
    private const double MassCeilingKg = 100000.0;

    private LandingLimits(double? maxLandingMassKg, double? minFuelMol, double? maxFuelMol, double? thrustDrop,
        double? confidentMassKg)
    {
        MaxLandingMassKg = maxLandingMassKg;
        MinFuelMol = minFuelMol;
        MaxFuelMol = maxFuelMol;
        ThrustDrop = thrustDrop;
        ConfidentMassKg = confidentMassKg;
    }

    /// <summary>The heaviest total mass at the start of the landing that still lands undamaged (fuel as it is, cargo or other mass added).</summary>
    internal double? MaxLandingMassKg { get; }

    /// <summary>The least fuel at the start of the landing that still lands undamaged.</summary>
    internal double? MinFuelMol { get; }

    /// <summary>The most fuel the landing still survives (more fuel is more mass).</summary>
    internal double? MaxFuelMol { get; }

    /// <summary>The share of thrust that may be lost (0 to 1) and still land undamaged.</summary>
    internal double? ThrustDrop { get; }

    /// <summary>The heaviest rocket whose confidence ratio stays at or above the floor asked for.</summary>
    internal double? ConfidentMassKg { get; }

    internal static bool Clean(LegResult result) => result.Outcome is LegOutcome.Landed { EnginesDamaged: false };

    internal static LandingLimits Of(FlightSimulator simulator, RocketCraft before, LandingLeg leg,
        float confidenceFloor, float distance, float gravity, float altitude)
    {
        bool LandsAtMass(double massKg)
        {
            RocketCraft trial = before.Copy();
            trial.StructureMassKg = (float)(massKg - trial.GasMassKg);
            return Clean(leg.Fly(simulator, trial));
        }

        bool LandsWithFuel(double moles)
        {
            RocketCraft trial = before.Copy();
            trial.SetFuelMoles(moles);
            return Clean(leg.Fly(simulator, trial));
        }

        bool LandsAtScale(double scale)
        {
            RocketCraft trial = before.Copy();
            trial.ThrustScale = (float)scale;
            return Clean(leg.Fly(simulator, trial));
        }

        double fuel = before.FuelMoles;
        bool landsNow = LandsWithFuel(fuel);
        double? maxMass = Bisect.HighestPassing(before.GasMassKg + 1.0, MassCeilingKg, LandsAtMass, 1.0);
        double? minFuel = landsNow ? Bisect.LowestPassing(0.0, fuel, LandsWithFuel, 1.0) : null;
        double? maxFuel = landsNow ? Bisect.HighestPassing(fuel, Math.Max(fuel * 20.0, 100000.0), LandsWithFuel, 1.0) : null;
        double? lowestScale = Bisect.LowestPassing(0.0, before.ThrustScale, LandsAtScale, 0.001);
        double? drop = lowestScale.HasValue ? before.ThrustScale - lowestScale.Value : null;
        double? confident = Bisect.HighestPassing(1.0, MassCeilingKg,
            mass => FlightMath.Confidence(before.AutomatedLanding, distance, gravity, altitude, before.MaxExpectedThrust,
                (float)mass).Ratio >= confidenceFloor, 1.0);
        return new LandingLimits(maxMass, minFuel, maxFuel, drop, confident);
    }
}
