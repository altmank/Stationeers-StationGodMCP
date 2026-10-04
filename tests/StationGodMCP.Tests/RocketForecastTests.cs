using System.Collections.Generic;
using StationGodMCP.Pure.Rockets;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The rocket forecast's port of the game's flight rules (Rocket.cs) and its simulator. Reference numbers: LU's Mk II
/// (wiki rockets.md section 5 correction 2026-10-01): one Pumped Gas Engine giving 16,390 N at 18 mol a tick, dry
/// 1,730 kg, premix about 16.7 g/mol, Vulcan gravity -5.5, re-entry 25 km at 400 m/s.
/// </summary>
public sealed class RocketForecastTests
{
    private const float LiveThrust = 16390f;
    private const float Step = 0.02f;

    private static FlightSimulator Simulator => new FlightSimulator(Step, 0.5f);

    private static RocketCraft MkII(double fuelMol, float dryKg = 1730f, double batteryJ = 3600000.0)
    {
        FuelBurn burn = new FuelBurn(LiveThrust / 18f, 0.0167, 0.0167 * 7.25, 1.0, 437.0, 8.3144 * 437.0);
        FuelLine line = new FuelLine(120.0, fuelMol * 120.0 / 3120.0,
            new List<FuelTank> { new FuelTank(3000.0, fuelMol * 3000.0 / 3120.0, true) }, 1, burn);
        PowerBank power = new PowerBank(batteryJ, 3600000.0, 10.0, 200.0);
        return new RocketCraft(dryKg, new List<FuelLine> { line }, 15800f, LiveThrust, true, 100f, false, 0f, power);
    }

    private static LandingLeg ReEntry(float profile = 25000f) =>
        new LandingLeg("Vulcan Orbit", "Pad", 400f, -5.5f, new LandingStart.ReEntry(profile, false, 200f, -5.5f));

    [Fact]
    public void ApexIsTheStopAltitude()
    {
        // 400 m/s down at +1 m/s² stops 80 km lower.
        Assert.Equal(25000f - 80000f, FlightMath.Apex(25000f, -400f, 1f), 1);
        Assert.True(float.IsNegativeInfinity(FlightMath.Apex(1000f, -10f, -0.5f)));
        Assert.True(float.IsNaN(FlightMath.Apex(10f, 5f, 2f)));
    }

    [Fact]
    public void ConfidenceTurnsZeroAtTheMassTheWikiDerived()
    {
        // thrust / (5.5 + 400² / (2 x 24,940)) = 1,882 kg (rockets.md section 5).
        ConfidenceReading light = FlightMath.Confidence(true, 400f, -5.5f, 25000f, LiveThrust, 1875f);
        ConfidenceReading heavy = FlightMath.Confidence(true, 400f, -5.5f, 25000f, LiveThrust, 1890f);
        ConfidenceReading manual = FlightMath.Confidence(false, 400f, -5.5f, 25000f, LiveThrust, 1000f);

        Assert.True(light.Ratio > 0f);
        Assert.Equal(LiveThrust, light.MinRequiredThrust, 1);
        Assert.Equal(0f, heavy.Ratio);
        Assert.Equal(0f, manual.Ratio);
        Assert.Equal("none", FlightMath.ConfidenceBand(heavy.Ratio));
    }

    [Fact]
    public void TheMkIILandsFromLowReEntry()
    {
        LegResult result = ReEntry().Fly(Simulator, MkII(6000.0));

        LegOutcome.Landed landed = Assert.IsType<LegOutcome.Landed>(result.Outcome);
        Assert.False(landed.EnginesDamaged);
        Assert.InRange(landed.Landing.Seconds, 100f, 260f);
        Assert.InRange(result.Tally.FuelUsedMol, 3000.0, 6000.0);
        Assert.True(landed.Landing.LowestApex >= 0f);
    }

    [Fact]
    public void TooHeavyAbortsInsteadOfFalling()
    {
        LegResult result = ReEntry().Fly(Simulator, MkII(6000.0, dryKg: 2000f));

        LegOutcome.LandingAborted aborted = Assert.IsType<LegOutcome.LandingAborted>(result.Outcome);
        Assert.Equal(0f, aborted.Landing.Confidence);
    }

    [Fact]
    public void LostThrustCrashesBecauseTheCheckSeesTheRecordedPeak()
    {
        RocketCraft weak = MkII(6000.0, dryKg: 1700f);
        weak.ThrustScale = 0.85f;

        LegResult result = ReEntry().Fly(Simulator, weak);

        Assert.True(result.Outcome is LegOutcome.Crashed || result.Outcome is LegOutcome.Landed { EnginesDamaged: true });
    }

    [Fact]
    public void RunningDryMidLandingIsNotClean()
    {
        LegResult result = ReEntry().Fly(Simulator, MkII(1500.0));

        Assert.False(LandingLimits.Clean(result));
    }

    [Fact]
    public void LimitsBracketTheRocketAsItIs()
    {
        RocketCraft rocket = MkII(6000.0);
        LandingLimits limits = LandingLimits.Of(Simulator, rocket, ReEntry(), 0.25f, 400f, -5.5f, 25000f);

        Assert.NotNull(limits.MaxLandingMassKg);
        Assert.InRange(limits.MaxLandingMassKg!.Value, rocket.MassKg, 1883.0);
        Assert.NotNull(limits.MinFuelMol);
        Assert.InRange(limits.MinFuelMol!.Value, 2000.0, 6000.0);
        Assert.NotNull(limits.ThrustDrop);
        Assert.InRange(limits.ThrustDrop!.Value, 0.0, 0.2);
        Assert.NotNull(limits.ConfidentMassKg);
        Assert.True(limits.ConfidentMassKg!.Value < limits.MaxLandingMassKg!.Value + 50.0);
    }

    [Fact]
    public void LaunchHopAndLandingSpendTheirDistanceInDeltaV()
    {
        List<FlightLeg> plan = new List<FlightLeg>
        {
            new LaunchLeg("Pad", "Vulcan Orbit", 400f, -5.5f, LaunchStart.Pad),
            new SpaceHop("Vulcan Orbit", "Sparse Vapors", 250f, 0f),
            new ParkLeg("Sparse Vapors", 600f, 120.0, 20, 0.0),
            new SpaceHop("Sparse Vapors", "Vulcan Orbit", 250f, 0f),
            ReEntry()
        };

        FlightRun run = FlightForecast.Run(Simulator, MkII(11500.0), plan);

        Assert.True(run.Succeeded, run.Legs[run.Legs.Count - 1].Outcome.Detail);
        Assert.InRange(run.Legs[1].Tally.DeltaV, 249.0, 252.0);
        Assert.InRange(run.Legs[0].Tally.Seconds, 60.0, 250.0);
        Assert.Equal(1730f + 20f, run.Final.StructureMassKg, 1);
        Assert.NotNull(run.BeforeLanding);
    }

    [Fact]
    public void AnEngineWithoutPowerStallsTheHop()
    {
        RocketCraft flat = MkII(6000.0, batteryJ: 100.0);

        LegResult result = new SpaceHop("A", "B", 250f, 0f).Fly(Simulator, flat);

        LegOutcome.Stalled stalled = Assert.IsType<LegOutcome.Stalled>(result.Outcome);
        Assert.Contains("batteries are flat", stalled.Reason);
    }

    [Fact]
    public void ARocketWithoutABatteryStallsUnpoweredWhateverItsLoadReads()
    {
        FuelBurn burn = new FuelBurn(LiveThrust / 18f, 0.0167, 0.0167 * 7.25, 1.0, 437.0, 8.3144 * 437.0);
        FuelLine line = new FuelLine(120.0, 6000.0 * 120.0 / 3120.0,
            new List<FuelTank> { new FuelTank(3000.0, 6000.0 * 3000.0 / 3120.0, true) }, 1, burn);
        RocketCraft craft = new RocketCraft(1730f, new List<FuelLine> { line }, 15800f, LiveThrust, true, 100f, false,
            0f, new PowerBank(0.0, 0.0, 0.0, 0.0));

        LegResult result = new SpaceHop("A", "B", 250f, 0f).Fly(Simulator, craft);

        LegOutcome.Stalled stalled = Assert.IsType<LegOutcome.Stalled>(result.Outcome);
        Assert.Contains("Engine unpowered: no battery", stalled.Reason);
    }

    [Fact]
    public void TheGovernedEngineDrawsEighteenTimesThrottleAndTanksRefillThePipe()
    {
        FuelBurn burn = new FuelBurn(900f, 0.0167, 0.12, 1.0, 300.0, 2494.0);
        FuelLine line = new FuelLine(100.0, 50.0, new List<FuelTank> { new FuelTank(1500.0, 1000.0, true) }, 1, burn);

        double drawn = line.Draw(50f);
        line.Mix();

        Assert.Equal(9.0, drawn, 6);
        Assert.Equal(1041.0, line.Moles, 6);
        Assert.Equal(1041.0 * 100.0 / 1600.0, line.PipeMoles, 6);
    }

    [Fact]
    public void FinalApproachSetsThrottleToHoverAtTouchdown()
    {
        AutopilotReading reading = new AutopilotReading(0.3f, -1f, 0f, 9000f, 50f, 1800f, -5.5f, 400f, 16390f, 16390f);

        AutopilotDecision decision = LandingAutopilot.Decide(reading);

        Assert.Equal(FlightRule.FinalApproach, decision.Rule);
        // Below 0.5 m slower than 2 m/s the share is 1: thrust = weight, 1800 x 5.5 / (9000 / 50).
        Assert.Equal(1800f * 5.5f / 180f, decision.Command.Apply(50f), 3);
    }

    [Fact]
    public void FlightLogKeepsTheNewestAndCountsEverything()
    {
        FlightLogBuffer buffer = new FlightLogBuffer(3);
        for (int second = 0; second < 5; second++)
        {
            buffer.Add(new FlightSample(second, second < 3 ? "InSpace" : "Landing", "Orbit", "Pad", 0.5, 100 - second,
                -10 * second, 0, 1800, 5000, 437, 900, 100, 16000, 16390, 0.4, 1000, 3, 120, "Normal"));
        }

        List<FlightSample> page = buffer.Page(0, 10, 1);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(5, buffer.Recorded);
        Assert.Equal(2.0, page[0].GameSeconds);
        Assert.Equal(-40.0, buffer.FastestDescent);
        Assert.Single(buffer.Events);
        Assert.StartsWith("0,InSpace,Orbit,Pad", buffer.First!.ToCsv());
    }

    [Fact]
    public void RunningDryWhileLaunchingTurnsRoundAndFallsBack()
    {
        List<FlightLeg> plan = new List<FlightLeg> { new LaunchLeg("Pad", "Vulcan Orbit", 400f, -5.5f, LaunchStart.Pad) };

        FlightRun run = FlightForecast.Run(Simulator, MkII(2000.0), plan);

        Assert.Equal(2, run.Legs.Count);
        Assert.IsType<LegOutcome.LaunchAborted>(run.Legs[0].Outcome);
        Assert.Equal("landing", run.Legs[1].Leg.Kind);
        Assert.False(run.Succeeded);
    }

    [Fact]
    public void TooLittleThrustStaysOnThePad()
    {
        List<FlightLeg> plan = new List<FlightLeg> { new LaunchLeg("Pad", "Vulcan Orbit", 400f, -5.5f, LaunchStart.Pad) };

        FlightRun run = FlightForecast.Run(Simulator, MkII(6000.0, dryKg: 3500f), plan);

        LegOutcome.Stalled stalled = Assert.IsType<LegOutcome.Stalled>(Assert.Single(run.Legs).Outcome);
        Assert.Contains("below the rocket's weight", stalled.Reason);
    }

    [Fact]
    public void LeastFuelForAPlanLiesBetweenFailingAndFlying()
    {
        List<FlightLeg> plan = new List<FlightLeg> { ReEntry() };

        double? least = FlightForecast.LeastFuel(Simulator, MkII(6000.0), plan, 6000.0);

        Assert.NotNull(least);
        Assert.InRange(least!.Value, 3000.0, 6000.0);
        RocketCraft below = MkII(6000.0);
        below.SetFuelMoles(least.Value - 50.0);
        Assert.False(FlightForecast.Run(Simulator, below, plan).Succeeded);
    }

    [Fact]
    public void FlightLogPagesEveryNthRow()
    {
        FlightLogBuffer buffer = new FlightLogBuffer(100);
        for (int second = 0; second < 10; second++)
        {
            buffer.Add(new FlightSample(second, "InSpace", "Orbit", "Pad", 0, -1, 0, 0, 1800, 5000, 437, 900, 100,
                16000, 16390, 0.4, 1000, 3, 120, "Normal"));
        }

        List<FlightSample> page = buffer.Page(1, 3, 3);

        Assert.Equal(new[] { 1.0, 4.0, 7.0 }, new[] { page[0].GameSeconds, page[1].GameSeconds, page[2].GameSeconds });
    }
}
