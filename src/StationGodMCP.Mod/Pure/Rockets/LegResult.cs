#nullable enable

namespace StationGodMCP.Pure.Rockets;

/// <summary>How a leg ended. Only Arrived, Parked and a clean landing let the plan go on.</summary>
internal abstract class LegOutcome
{
    private protected LegOutcome()
    {
    }

    /// <summary>Short machine name.</summary>
    internal abstract string Name { get; }

    /// <summary>One sentence saying what happens in the game.</summary>
    internal abstract string Detail { get; }

    /// <summary>Whether the next leg can follow.</summary>
    internal virtual bool Continues => false;

    internal sealed class Arrived : LegOutcome
    {
        internal static readonly Arrived Instance = new Arrived();

        internal override string Name => "arrived";

        internal override string Detail => "Progress reached 1: the rocket is at the next node.";

        internal override bool Continues => true;
    }

    internal sealed class Parked : LegOutcome
    {
        internal static readonly Parked Instance = new Parked();

        internal override string Name => "parked";

        internal override string Detail => "The stop's time passed with its power draw.";

        internal override bool Continues => true;
    }

    /// <summary>The rocket stops making progress: no thrust (engine unpowered, out of fuel) or thrust below its weight on the pad.</summary>
    internal sealed class Stalled : LegOutcome
    {
        internal Stalled(string reason)
        {
            Reason = reason;
        }

        internal string Reason { get; }

        internal override string Name => "stalled";

        internal override string Detail => Reason;
    }

    /// <summary>
    /// Launching, the net acceleration turned downward (GetApex is -infinity then) and Rocket.PhysicsUpdate turned the
    /// rocket round to land on its own pad (Rocket.cs:2069-2086).
    /// </summary>
    internal sealed class LaunchAborted : LegOutcome
    {
        internal LaunchAborted(float altitude, float velocity, float acceleration)
        {
            Altitude = altitude;
            Velocity = velocity;
            Acceleration = acceleration;
        }

        internal float Altitude { get; }

        internal float Velocity { get; }

        internal float Acceleration { get; }

        internal override string Name => "launch_aborted";

        internal override string Detail =>
            "Thrust fell below the rocket's weight while launching: the game turns it round to land back on its pad " +
            "(the landing that follows is forecast too).";
    }

    /// <summary>The landing's confidence was 0: Rocket.EvaluateLaunchOrLandState sends the rocket back to the node it came from (Rocket.cs:2181-2185).</summary>
    internal sealed class LandingAborted : LegOutcome
    {
        internal LandingAborted(LandingDetail landing)
        {
            Landing = landing;
        }

        internal LandingDetail Landing { get; }

        internal override string Name => "landing_aborted";

        internal override string Detail =>
            "Auto-land confidence 0 as the re-entry hop starts: the game logs 'landing aborted' and turns back to " +
            "orbit; no harm done, but the rocket is not home.";
    }

    /// <summary>
    /// Down on the mount: Rocket.SoftLanding (altitude under 0.1 m slower than 2 m/s, Rocket.cs:2232-2241) or a
    /// CrashLanding slower than 4 m/s (Rocket.cs:2417-2434), both harmless; 4 to 25 m/s damages every engine by
    /// |v| x 10 (DamageRocket, Rocket.cs:2462-2468).
    /// </summary>
    internal sealed class Landed : LegOutcome
    {
        internal Landed(LandingDetail landing, bool soft, bool enginesDamaged)
        {
            Landing = landing;
            Soft = soft;
            EnginesDamaged = enginesDamaged;
        }

        internal LandingDetail Landing { get; }

        internal bool Soft { get; }

        internal bool EnginesDamaged { get; }

        internal override string Name => EnginesDamaged ? "hard_landing" : "landed";

        internal override string Detail =>
            EnginesDamaged
                ? "Touched down at 4 to 25 m/s: the rocket stands but every engine takes |v| x 10 damage."
                : Soft ? "Soft landing on the mount." : "Touched down under 4 m/s: no damage.";

        internal override bool Continues => !EnginesDamaged;
    }

    /// <summary>Hit the mount faster than 25 m/s: Rocket.Explode and AbandonRocket (Rocket.cs:2427-2430).</summary>
    internal sealed class Crashed : LegOutcome
    {
        internal Crashed(LandingDetail landing)
        {
            Landing = landing;
        }

        internal LandingDetail Landing { get; }

        internal override string Name => "crashed";

        internal override string Detail => "Hit the mount faster than 25 m/s: the rocket explodes.";
    }

    /// <summary>The simulation's own limit: the leg did not end within the time allowed.</summary>
    internal sealed class TimedOut : LegOutcome
    {
        internal TimedOut(float seconds)
        {
            Seconds = seconds;
        }

        internal float Seconds { get; }

        internal override string Name => "timed_out";

        internal override string Detail => $"Still going after {Seconds:0} s of simulated time (the forecast's own limit).";
    }
}

/// <summary>What the landing autopilot saw and did on one landing.</summary>
internal sealed class LandingDetail
{
    internal LandingDetail(float confidence, float minRequiredThrust, float maxExpectedThrust, float startAltitude,
        float startMassKg)
    {
        Confidence = confidence;
        MinRequiredThrust = minRequiredThrust;
        MaxExpectedThrust = maxExpectedThrust;
        StartAltitude = startAltitude;
        StartMassKg = startMassKg;
    }

    /// <summary>The confidence ratio the game computed at the start (NaN when the landing was already under way).</summary>
    internal float Confidence { get; }

    internal float MinRequiredThrust { get; }

    internal float MaxExpectedThrust { get; }

    internal float StartAltitude { get; }

    internal float StartMassKg { get; }

    /// <summary>The lowest stop point the autopilot saw while arresting the fall (Rocket.GetApex each tick, final approach left out).</summary>
    internal float LowestApex { get; set; } = float.PositiveInfinity;

    /// <summary>The velocity when the landing ended (0 for an abort).</summary>
    internal float TouchdownVelocity { get; set; }

    internal float Seconds { get; set; }

    internal FlightRule LastRule { get; set; }

    /// <summary>Whether the batteries ran short during the landing (the engines stop: crash).</summary>
    internal bool PowerLost { get; set; }

    /// <summary>Whether the fuel line ran dry before touchdown.</summary>
    internal bool FuelRanOut { get; set; }
}

/// <summary>One leg's numbers.</summary>
internal sealed class LegResult
{
    internal LegResult(FlightLeg leg, LegOutcome outcome, LegTally tally)
    {
        Leg = leg;
        Outcome = outcome;
        Tally = tally;
    }

    internal FlightLeg Leg { get; }

    internal LegOutcome Outcome { get; }

    internal LegTally Tally { get; }
}

/// <summary>What a leg used and left: time, burn, Δv, fuel, mass, power, thrust.</summary>
internal sealed class LegTally
{
    internal double Seconds { get; set; }

    internal double BurnSeconds { get; set; }

    /// <summary>The engines' Δv spent: thrust / mass over time, gravity not taken off.</summary>
    internal double DeltaV { get; set; }

    internal double FuelStartMol { get; set; }

    internal double FuelEndMol { get; set; }

    internal double FuelUsedMol => FuelStartMol - FuelEndMol;

    internal double MassStartKg { get; set; }

    internal double MassEndKg { get; set; }

    internal double BatteryStartJ { get; set; }

    internal double BatteryEndJ { get; set; }

    internal double ThrustStartN { get; set; }

    internal double ThrustEndN { get; set; }

    internal double PeakThrustN { get; set; }

    internal double PressureEndKpa { get; set; }

    internal double MaxRecordedThrustN { get; set; }
}
