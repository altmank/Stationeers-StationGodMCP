#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Scheduling;

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
        Answer<FlightRun> answer = new Answer<FlightRun>();
        foreach (object? _ in Running(simulator, rocket, plan, answer))
        {
        }

        return answer.Value;
    }

    /// <summary>Run as a Work body: each leg in steps of its own (FlightLeg.Flying).</summary>
    internal static IEnumerable<object?> Running(FlightSimulator simulator, RocketCraft rocket,
        IReadOnlyList<FlightLeg> plan, Answer<FlightRun> answer)
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

            Answer<LegResult> flown = new Answer<LegResult>();
            foreach (object? step in leg.Flying(simulator, craft, flown))
            {
                yield return step;
            }

            LegResult result = flown.Value;
            results.Add(result);
            if (result.Outcome is LegOutcome.LaunchAborted aborted && leg is LaunchLeg launch)
            {
                // ChangeTarget(CurrentNode) then InitAutomatedLanding (Rocket.cs:2076-2083): it lands on its own pad.
                LandingLeg back = new LandingLeg("launch abort", launch.From, launch.Distance, launch.Gravity,
                    new LandingStart.Midway(aborted.Altitude, aborted.Velocity, aborted.Acceleration,
                        Math.Min(aborted.Altitude, 1000f), true));
                beforeLanding = craft.Copy();
                landing = back;
                Answer<LegResult> landed = new Answer<LegResult>();
                foreach (object? step in back.Flying(simulator, craft, landed))
                {
                    yield return step;
                }

                results.Add(landed.Value);
                break;
            }

            if (!result.Outcome.Continues)
            {
                break;
            }
        }

        answer.Value = new FlightRun(results, craft, beforeLanding, landing);
    }

    /// <summary>The least fuel (mol, the rocket's mix) the whole plan succeeds with, by bisection; null when even a full load fails.</summary>
    internal static double? LeastFuel(FlightSimulator simulator, RocketCraft rocket, IReadOnlyList<FlightLeg> plan,
        double ceilingMol)
    {
        Answer<double?> answer = new Answer<double?>();
        foreach (object? _ in LeastFuelSteps(simulator, rocket, plan, ceilingMol, answer))
        {
        }

        return answer.Value;
    }

    /// <summary>LeastFuel as a Work body: each trial flight in steps of its own.</summary>
    internal static IEnumerable<object?> LeastFuelSteps(FlightSimulator simulator, RocketCraft rocket,
        IReadOnlyList<FlightLeg> plan, double ceilingMol, Answer<double?> answer)
    {
        IEnumerable<object?> Works(double moles, Answer<bool> works)
        {
            RocketCraft trial = rocket.Copy();
            trial.SetFuelMoles(moles);
            Answer<FlightRun> run = new Answer<FlightRun>();
            foreach (object? step in Running(simulator, trial, plan, run))
            {
                yield return step;
            }

            works.Value = run.Value.Succeeded;
        }

        return Bisect.LowestPassingSteps(0.0, ceilingMol, Works, 1.0, answer);
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

    /// <summary>LowestPassing over a judgement that is itself a Work body: each judgement in steps of its own.</summary>
    internal static IEnumerable<object?> LowestPassingSteps(double low, double high,
        Func<double, Answer<bool>, IEnumerable<object?>> passes, double resolution, Answer<double?> answer)
    {
        Answer<bool> passed = new Answer<bool>();
        foreach (object? step in passes(high, passed))
        {
            yield return step;
        }

        if (!passed.Value)
        {
            answer.Value = null;
            yield break;
        }

        foreach (object? step in passes(low, passed))
        {
            yield return step;
        }

        if (passed.Value)
        {
            answer.Value = low;
            yield break;
        }

        for (int step = 0; step < MaximumSteps && high - low > resolution; step++)
        {
            double middle = (low + high) / 2.0;
            foreach (object? judged in passes(middle, passed))
            {
                yield return judged;
            }

            if (passed.Value)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        answer.Value = high;
    }

    /// <summary>HighestPassing over a judgement that is itself a Work body: each judgement in steps of its own.</summary>
    internal static IEnumerable<object?> HighestPassingSteps(double low, double high,
        Func<double, Answer<bool>, IEnumerable<object?>> passes, double resolution, Answer<double?> answer)
    {
        Answer<bool> passed = new Answer<bool>();
        foreach (object? step in passes(low, passed))
        {
            yield return step;
        }

        if (!passed.Value)
        {
            answer.Value = null;
            yield break;
        }

        foreach (object? step in passes(high, passed))
        {
            yield return step;
        }

        if (passed.Value)
        {
            answer.Value = high;
            yield break;
        }

        for (int step = 0; step < MaximumSteps && high - low > resolution; step++)
        {
            double middle = (low + high) / 2.0;
            foreach (object? judged in passes(middle, passed))
            {
                yield return judged;
            }

            if (passed.Value)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        answer.Value = low;
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
        Answer<LandingLimits> answer = new Answer<LandingLimits>();
        foreach (object? _ in Finding(simulator, before, leg, confidenceFloor, distance, gravity, altitude, answer))
        {
        }

        return answer.Value;
    }

    /// <summary>Of as a Work body: each trial landing in steps of its own.</summary>
    internal static IEnumerable<object?> Finding(FlightSimulator simulator, RocketCraft before, LandingLeg leg,
        float confidenceFloor, float distance, float gravity, float altitude, Answer<LandingLimits> answer)
    {
        IEnumerable<object?> Lands(RocketCraft trial, Answer<bool> lands)
        {
            Answer<LegResult> flown = new Answer<LegResult>();
            foreach (object? step in leg.Flying(simulator, trial, flown))
            {
                yield return step;
            }

            lands.Value = Clean(flown.Value);
        }

        IEnumerable<object?> LandsAtMass(double massKg, Answer<bool> lands)
        {
            RocketCraft trial = before.Copy();
            trial.StructureMassKg = (float)(massKg - trial.GasMassKg);
            return Lands(trial, lands);
        }

        IEnumerable<object?> LandsWithFuel(double moles, Answer<bool> lands)
        {
            RocketCraft trial = before.Copy();
            trial.SetFuelMoles(moles);
            return Lands(trial, lands);
        }

        IEnumerable<object?> LandsAtScale(double scale, Answer<bool> lands)
        {
            RocketCraft trial = before.Copy();
            trial.ThrustScale = (float)scale;
            return Lands(trial, lands);
        }

        double fuel = before.FuelMoles;
        Answer<bool> now = new Answer<bool>();
        foreach (object? step in LandsWithFuel(fuel, now))
        {
            yield return step;
        }

        bool landsNow = now.Value;
        Answer<double?> found = new Answer<double?>();
        foreach (object? step in Bisect.HighestPassingSteps(before.GasMassKg + 1.0, MassCeilingKg, LandsAtMass, 1.0,
                     found))
        {
            yield return step;
        }

        double? maxMass = found.Value;
        double? minFuel = null;
        double? maxFuel = null;
        if (landsNow)
        {
            foreach (object? step in Bisect.LowestPassingSteps(0.0, fuel, LandsWithFuel, 1.0, found))
            {
                yield return step;
            }

            minFuel = found.Value;
            foreach (object? step in Bisect.HighestPassingSteps(fuel, Math.Max(fuel * 20.0, 100000.0), LandsWithFuel,
                         1.0, found))
            {
                yield return step;
            }

            maxFuel = found.Value;
        }

        foreach (object? step in Bisect.LowestPassingSteps(0.0, before.ThrustScale, LandsAtScale, 0.001, found))
        {
            yield return step;
        }

        double? lowestScale = found.Value;
        double? drop = lowestScale.HasValue ? before.ThrustScale - lowestScale.Value : null;
        double? confident = Bisect.HighestPassing(1.0, MassCeilingKg,
            mass => FlightMath.Confidence(before.AutomatedLanding, distance, gravity, altitude, before.MaxExpectedThrust,
                (float)mass).Ratio >= confidenceFloor, 1.0);
        answer.Value = new LandingLimits(maxMass, minFuel, maxFuel, drop, confident);
    }
}
