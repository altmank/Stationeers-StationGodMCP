#nullable enable

using System;

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// Flies a RocketCraft leg by leg, tick by tick, as the game does: Rocket.PhysicsUpdate every physics step
/// (Time.fixedDeltaTime) and, every 0.5 s game tick (GameManager.GameTickSpeedSeconds), the atmospheric tick in the
/// order GameManager runs it (GameManager.cs:750-790): Rocket.RocketAtmospherics (gas mass, then the landing
/// autopilot, Rocket.cs:2867-2879), the engines' OnPreAtmosphere (draw and burn, RocketEngineBase.cs:436-459), the
/// tanks' OnAtmosphericTick (mix with the pipe), ElectricityTick (the batteries pay the load).
/// The two clocks run apart in the game (physics on the main thread, the tick on a pool thread); here a tick falls
/// every round(0.5 / dt) physics steps.
/// </summary>
internal sealed class FlightSimulator
{
    /// <summary>The longest simulated time one leg may take.</summary>
    internal const float LegLimitSeconds = 7200f;

    // Above this the parked rocket's parent transform stays put (MoveToTarget clamps to mount + 1000, Rocket.cs:2293).
    private const float VisualCeiling = 1000f;

    internal FlightSimulator(float physicsStep, float tickSeconds)
    {
        PhysicsStep = physicsStep;
        TickSeconds = tickSeconds;
        StepsPerTick = Math.Max(1, (int)Math.Round(tickSeconds / physicsStep));
    }

    internal float PhysicsStep { get; }

    internal float TickSeconds { get; }

    internal int StepsPerTick { get; }

    internal LegResult Launch(LaunchLeg leg, RocketCraft craft)
    {
        LegTally tally = Begin(craft);
        craft.EnginesOn = true;
        bool onPad = leg.Start.OnPad;
        float altitude = leg.Start.Altitude;
        float velocity = leg.Start.Velocity;
        float progress = leg.Start.Progress;
        float limit = LegLimitSeconds / PhysicsStep;
        for (int step = 0; step < limit; step++)
        {
            Tick(craft, step, tally, false);
            RecordThrust(craft);
            if (onPad && progress > 0f)
            {
                onPad = false;
            }

            if (!onPad)
            {
                altitude += velocity * PhysicsStep;
            }

            if (progress >= 1f)
            {
                return End(leg, LegOutcome.Arrived.Instance, craft, tally);
            }

            float engine = craft.Force / craft.MassKg;
            float change = engine * PhysicsStep + leg.Gravity * PhysicsStep;
            float acceleration = change / PhysicsStep;
            Accumulate(tally, craft, engine);
            if (onPad)
            {
                if (acceleration < 0f)
                {
                    // Rocket.PhysicsUpdate on the mount (Rocket.cs:2055-2067): too little thrust and nothing moves.
                    if (step % StepsPerTick == StepsPerTick - 1)
                    {
                        return End(leg, new LegOutcome.Stalled(StallReason(craft, "on the pad")), craft, tally);
                    }

                    continue;
                }

                velocity += change;
                progress += change / leg.Distance;
                continue;
            }

            velocity += change;
            float apex = FlightMath.Apex(altitude, velocity, acceleration);
            if ((velocity < 0f && float.IsNegativeInfinity(apex)) || apex < 0f)
            {
                return End(leg, new LegOutcome.LaunchAborted(altitude, velocity, acceleration), craft, tally);
            }

            progress += change / leg.Distance;
        }

        return End(leg, new LegOutcome.TimedOut(LegLimitSeconds), craft, tally);
    }

    internal LegResult Hop(SpaceHop leg, RocketCraft craft)
    {
        LegTally tally = Begin(craft);
        craft.EnginesOn = true;
        float progress = leg.StartProgress;
        float limit = LegLimitSeconds / PhysicsStep;
        for (int step = 0; step < limit; step++)
        {
            Tick(craft, step, tally, false);
            RecordThrust(craft);
            if (progress >= 1f)
            {
                return End(leg, LegOutcome.Arrived.Instance, craft, tally);
            }

            float engine = craft.Force / craft.MassKg;
            Accumulate(tally, craft, engine);
            progress += engine * PhysicsStep / leg.Distance;
            if (!Burning(craft) && step % StepsPerTick == StepsPerTick - 1)
            {
                return End(leg, new LegOutcome.Stalled(StallReason(craft, "in space")), craft, tally);
            }
        }

        return End(leg, new LegOutcome.TimedOut(LegLimitSeconds), craft, tally);
    }

    internal LegResult Park(ParkLeg leg, RocketCraft craft)
    {
        LegTally tally = Begin(craft);
        craft.EnginesOn = false;
        craft.Force = 0f;
        double previous = craft.Power.OtherLoadW;
        craft.Power.OtherLoadW = leg.LoadW;
        int ticks = (int)Math.Ceiling(leg.Seconds / TickSeconds);
        for (int tick = 0; tick < ticks; tick++)
        {
            craft.Power.Tick(false);
        }

        craft.Power.OtherLoadW = previous;
        craft.Power.Transfer(leg.BatteryJ);
        for (int index = 0; index < leg.Fuel.Count; index++)
        {
            LineTransfer transfer = leg.Fuel[index];
            if (transfer.Line >= 0 && transfer.Line < craft.Lines.Count)
            {
                craft.TransferFuel(transfer.Line, transfer.Moles);
            }
        }

        craft.StructureMassKg = Math.Max(0f, craft.StructureMassKg + leg.AddCargoSlots + (float)leg.AddCargoKg);
        craft.GasMassKg = craft.CountedGasKg();
        tally.Seconds = leg.Seconds;
        return End(leg, LegOutcome.Parked.Instance, craft, tally);
    }

    internal LegResult Land(LandingLeg leg, RocketCraft craft)
    {
        LegTally tally = Begin(craft);
        float altitude;
        float velocity;
        float acceleration;
        float visual;
        LandingDetail detail;
        int step = 0;
        if (leg.Start is LandingStart.ReEntry entry)
        {
            craft.EnginesOn = true;
            // The re-entry hop's first physics step in space: Progress must pass 0 before the state can change.
            for (; ; step++)
            {
                Tick(craft, step, tally, false);
                RecordThrust(craft);
                float engine = craft.Force / craft.MassKg;
                Accumulate(tally, craft, engine);
                if (engine * PhysicsStep / leg.Distance > 0f)
                {
                    step++;
                    break;
                }

                if (!Burning(craft) && step % StepsPerTick == StepsPerTick - 1)
                {
                    return End(leg, new LegOutcome.Stalled(StallReason(craft, "at the start of the re-entry hop")),
                        craft, tally);
                }
            }

            float checkGravity = FlightMath.ClampGravity(entry.Orbital ? -1f : entry.WorldGravity);
            ConfidenceReading check = FlightMath.Confidence(craft.AutomatedLanding, leg.Distance, checkGravity,
                entry.ProfileAltitude, craft.MaxExpectedThrust, craft.MassKg);
            altitude = entry.Orbital ? OrbitalAltitude(craft, leg, entry) : entry.ProfileAltitude;
            detail = new LandingDetail(check.Ratio, check.MinRequiredThrust, craft.MaxExpectedThrust, altitude,
                craft.MassKg);
            if (craft.AutomatedLanding && check.Ratio <= 0f)
            {
                return End(leg, new LegOutcome.LandingAborted(detail), craft, tally);
            }

            velocity = 0f - leg.Distance;
            acceleration = 0f;
            visual = Math.Min(altitude, VisualCeiling);
            InitAutomatedLanding(craft, velocity, leg.Distance);
        }
        else
        {
            LandingStart.Midway midway = (LandingStart.Midway)leg.Start;
            altitude = midway.Altitude;
            velocity = midway.Velocity;
            acceleration = midway.Acceleration;
            visual = midway.VisualAltitude;
            detail = new LandingDetail(float.NaN, float.PositiveInfinity, craft.MaxExpectedThrust, altitude,
                craft.MassKg);
            if (midway.Initialise)
            {
                InitAutomatedLanding(craft, velocity, leg.Distance);
            }
        }

        int start = step;
        float limit = step + LegLimitSeconds / PhysicsStep;
        for (; step < limit; step++)
        {
            bool tick = step % StepsPerTick == 0;
            if (tick && craft.AutomatedLanding)
            {
                craft.GasMassKg = craft.CountedGasKg();
                craft.HighestRecordedThrust = Math.Max(craft.HighestRecordedThrust, craft.Force);
                AutopilotDecision decision = LandingAutopilot.Decide(new AutopilotReading(altitude, velocity,
                    acceleration, craft.Force, craft.Throttle, craft.MassKg, leg.Gravity, leg.Distance,
                    craft.MaxExpectedThrust, craft.HighestRecordedThrust));
                craft.EnginesOn = true;
                craft.Throttle = decision.Command.Apply(craft.Throttle);
                detail.LastRule = decision.Rule;
                if (decision.Rule != FlightRule.FinalApproach && !float.IsNaN(decision.Apex) &&
                    !float.IsNegativeInfinity(decision.Apex) && velocity < 0f)
                {
                    detail.LowestApex = Math.Min(detail.LowestApex, decision.Apex);
                }
            }

            Tick(craft, step, tally, tick && craft.AutomatedLanding);
            RecordThrust(craft);
            if (tick)
            {
                detail.PowerLost |= !craft.Power.Powered;
                detail.FuelRanOut |= craft.OutOfFuel;
            }

            // Rocket.SetRocketTargetPosition: the target moves at last step's velocity; a soft landing ends it.
            float before = velocity;
            altitude += velocity * PhysicsStep;
            if (altitude < 0.1f && velocity > -2f)
            {
                return Touchdown(leg, craft, tally, detail, before, (step - start) * PhysicsStep, soft: true);
            }

            // Rocket.MoveToTarget: the parent transform lerps after the target; below the mount it is a crash landing.
            visual += (Math.Min(altitude, VisualCeiling) - visual) * UnityFloat.Clamp01(PhysicsStep * 2f);
            if (visual < 0f)
            {
                return Touchdown(leg, craft, tally, detail, before, (step - start) * PhysicsStep, soft: false);
            }

            float engine = craft.Force / craft.MassKg;
            Accumulate(tally, craft, engine);
            float change = engine * PhysicsStep + leg.Gravity * PhysicsStep;
            acceleration = change / PhysicsStep;
            velocity += change;
        }

        return End(leg, new LegOutcome.TimedOut(LegLimitSeconds), craft, tally);
    }

    // Rocket.InitAutomatedLanding (Rocket.cs:2530-2541).
    private static void InitAutomatedLanding(RocketCraft craft, float velocity, float distance)
    {
        if (!craft.AutomatedLanding)
        {
            return;
        }

        craft.HighestRecordedThrust = 0f;
        craft.EnginesOn = true;
        if (velocity <= 0f - distance)
        {
            craft.Throttle = new ThrottleCommand.Trim(100f).Apply(craft.Throttle);
        }
    }

    // The orbital pad's altitude search in Rocket.OnRocketStateUpdated (Rocket.cs:1665-1681).
    private static float OrbitalAltitude(RocketCraft craft, LandingLeg leg, LandingStart.ReEntry entry)
    {
        float altitude = entry.ProfileAltitude;
        float baseline = FlightMath.Confidence(craft.AutomatedLanding, entry.DistanceToOrbit * 2f, entry.WorldGravity,
            altitude, craft.MaxExpectedThrust, craft.MassKg).Ratio;
        for (int index = (int)entry.DistanceToOrbit - 1; index >= 0; index--)
        {
            float trial = altitude * (index / entry.DistanceToOrbit);
            if (FlightMath.Confidence(craft.AutomatedLanding, leg.Distance, -1f, trial, craft.MaxExpectedThrust,
                    craft.MassKg).Ratio <= baseline)
            {
                return (index + 1) / entry.DistanceToOrbit * altitude;
            }
        }

        return altitude;
    }

    private LegResult Touchdown(LandingLeg leg, RocketCraft craft, LegTally tally, LandingDetail detail,
        float velocity, float seconds, bool soft)
    {
        detail.TouchdownVelocity = velocity;
        detail.Seconds = seconds;
        LegOutcome outcome = soft || velocity > -4f
            ? new LegOutcome.Landed(detail, soft, false)
            : velocity > -25f
                ? new LegOutcome.Landed(detail, false, true)
                : new LegOutcome.Crashed(detail);
        return End(leg, outcome, craft, tally);
    }

    // One physics step's share of the atmospheric tick, when the step falls on one.
    private void Tick(RocketCraft craft, int step, LegTally tally, bool gasMassMeasured)
    {
        if (step % StepsPerTick != 0)
        {
            return;
        }

        if (!gasMassMeasured)
        {
            craft.GasMassKg = craft.CountedGasKg();
        }

        bool burn = craft.EnginesOn && craft.Power.Powered;
        float force = 0f;
        // Every engine's OnPreAtmosphere (draw and burn), then every tank's OnAtmosphericTick (GameManager.cs:750-790).
        for (int index = 0; index < craft.Engines.Count; index++)
        {
            EngineUnit engine = craft.Engines[index];
            if (burn)
            {
                force += (float)engine.Burn(craft.Throttle, craft.Lines) * craft.ThrustScale;
            }
            else
            {
                engine.Idle();
            }
        }

        for (int index = 0; index < craft.Lines.Count; index++)
        {
            craft.Lines[index].Mix();
        }

        craft.Force = force;
        craft.Power.Tick(craft.EnginesOn);
        tally.PeakThrustN = Math.Max(tally.PeakThrustN, force);
        if (force > 0f)
        {
            tally.BurnSeconds += TickSeconds;
        }
    }

    private static void RecordThrust(RocketCraft craft)
    {
        if (craft.Engines.Count > 0)
        {
            craft.MaxRecordedThrust = Math.Max(craft.Force, craft.MaxRecordedThrust);
        }
    }

    private void Accumulate(LegTally tally, RocketCraft craft, float engineAcceleration)
    {
        tally.Seconds += PhysicsStep;
        tally.DeltaV += engineAcceleration * PhysicsStep;
    }

    private static bool Burning(RocketCraft craft) => craft.Force > 0f;

    private static string StallReason(RocketCraft craft, string where)
    {
        if (craft.OutOfFuel)
        {
            return $"No fuel left {where} that the engines' feed can take: they make no thrust.";
        }

        if (!craft.Power.Powered)
        {
            return $"The batteries are flat {where}: an unpowered engine burns nothing (RocketEngineBase.cs:450-458).";
        }

        if (craft.Throttle <= 0f)
        {
            return $"Throttle 0 {where}: the engines draw nothing.";
        }

        if (craft.Force <= 0f)
        {
            return $"The engines draw {where} but what they draw does not burn (no fuel and oxidiser together, or only " +
                   "pressurant left): no thrust.";
        }

        return $"Thrust is below the rocket's weight {where}: it does not move, and burns fuel while it waits.";
    }

    private static LegTally Begin(RocketCraft craft) => new LegTally
    {
        FuelStartMol = craft.FuelMoles,
        MassStartKg = craft.MassKg,
        BatteryStartJ = craft.Power.ChargeJ,
        ThrustStartN = craft.Force
    };

    private static LegResult End(FlightLeg leg, LegOutcome outcome, RocketCraft craft, LegTally tally)
    {
        craft.GasMassKg = craft.CountedGasKg();
        tally.FuelEndMol = craft.FuelMoles;
        tally.MassEndKg = craft.MassKg;
        tally.BatteryEndJ = craft.Power.ChargeJ;
        tally.ThrustEndN = craft.Force;
        tally.MaxRecordedThrustN = craft.MaxRecordedThrust;
        tally.PressureEndKpa = craft.Lines.Count > 0 ? craft.Lines[0].PressureKpa : 0.0;
        return new LegResult(leg, outcome, tally);
    }
}
