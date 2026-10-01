#nullable enable

using System;

namespace StationGodMCP.Pure.Rockets;

/// <summary>Objects.Rockets.FlightControlRule, by the same names and order (FlightControlRule.cs).</summary>
internal enum FlightRule
{
    None,
    Normal,
    Alternate,
    Alternate2,
    FinalApproach
}

/// <summary>What the landing autopilot reads at one atmospheric tick (Rocket.HandleAutomatedLanding, Rocket.cs:2543-2570).</summary>
internal readonly struct AutopilotReading
{
    internal AutopilotReading(float altitude, float velocity, float acceleration, float thrust, float throttle,
        float massKg, float gravity, float transitDistance, float maxExpectedThrust, float highestRecordedThrust)
    {
        Altitude = altitude;
        Velocity = velocity;
        Acceleration = acceleration;
        Thrust = thrust;
        Throttle = throttle;
        MassKg = massKg;
        Gravity = gravity;
        TransitDistance = transitDistance;
        MaxExpectedThrust = maxExpectedThrust;
        HighestRecordedThrust = highestRecordedThrust;
    }

    /// <summary>Rocket.GetAltitude: metres above the destination's launch mount.</summary>
    internal float Altitude { get; }

    internal float Velocity { get; }

    /// <summary>Rocket.Acceleration: the last physics step's net acceleration, gravity included.</summary>
    internal float Acceleration { get; }

    /// <summary>Rocket.GetThrust: every engine's Force from its last atmospheric tick.</summary>
    internal float Thrust { get; }

    /// <summary>The first engine's Throttle, 0 to 100 (Rocket.GetThrottle).</summary>
    internal float Throttle { get; }

    /// <summary>Rocket.TotalMass: RocketNetwork.CombinedMass.</summary>
    internal float MassKg { get; }

    /// <summary>Rocket.GetGravity while landing: -1 at an orbital pad, else the world's, clamped to -5.5..-1.</summary>
    internal float Gravity { get; }

    /// <summary>The re-entry hop's NodeTransit.GetDistance.</summary>
    internal float TransitDistance { get; }

    /// <summary>Rocket.GetMaxExpectedThrust.</summary>
    internal float MaxExpectedThrust { get; }

    /// <summary>Rocket._highestRecordedThrust, already raised to this tick's thrust.</summary>
    internal float HighestRecordedThrust { get; }

    /// <summary>Rocket.Weight: |mass x gravity|.</summary>
    internal float Weight => Math.Abs(MassKg * Gravity);
}

/// <summary>A throttle change the autopilot makes: Rocket.TrimThrottle adds to every engine's throttle, SetThrottle sets it.</summary>
internal abstract class ThrottleCommand
{
    private ThrottleCommand()
    {
    }

    /// <summary>The throttle an engine is left at, through RocketEngineBase.Throttle's setter (clamp 0 to 100, RocketEngineBase.cs:188-196).</summary>
    internal abstract float Apply(float current);

    internal sealed class Trim : ThrottleCommand
    {
        internal Trim(float amount)
        {
            Amount = amount;
        }

        internal float Amount { get; }

        internal override float Apply(float current) =>
            UnityFloat.Clamp((float)(current + (double)Amount), 0f, LandingAutopilot.MaxThrottle);
    }

    internal sealed class Set : ThrottleCommand
    {
        internal Set(float value)
        {
            Value = value;
        }

        internal float Value { get; }

        internal override float Apply(float current) => UnityFloat.Clamp(Value, 0f, LandingAutopilot.MaxThrottle);
    }
}

/// <summary>One autopilot decision: the rule it chose, the stop point it saw, and the throttle change.</summary>
internal readonly struct AutopilotDecision
{
    internal AutopilotDecision(FlightRule rule, float apex, ThrottleCommand command)
    {
        Rule = rule;
        Apex = apex;
        Command = command;
    }

    internal FlightRule Rule { get; }

    /// <summary>Rocket.GetApex at the reading: the altitude the rocket would stop at.</summary>
    internal float Apex { get; }

    internal ThrottleCommand Command { get; }
}

/// <summary>
/// The landing autopilot, ported from Objects.Rockets.Rocket (Rocket.cs:2543-2686). Every rule turns the engines on
/// (TurnOnEngines) before it trims, so a decision always leaves them on.
/// </summary>
internal static class LandingAutopilot
{
    /// <summary>Rocket.MAX_THROTTLE (Rocket.cs:215), also RocketEngineBase._maxThrottle (RocketEngineBase.cs:73).</summary>
    internal const float MaxThrottle = 100f;

    /// <summary>Rocket.HandleAutomatedLanding's choice and throttle change for one reading.</summary>
    internal static AutopilotDecision Decide(AutopilotReading reading)
    {
        FlightRule rule = RuleFor(reading, out float apex);
        ThrottleCommand command = rule switch
        {
            FlightRule.Normal => Normal(reading, apex),
            FlightRule.Alternate => new ThrottleCommand.Trim(10f),
            FlightRule.Alternate2 => Alternate2(reading, 2f),
            FlightRule.FinalApproach => FinalDescent(reading, apex),
            _ => new ThrottleCommand.Trim(0f)
        };
        return new AutopilotDecision(rule, apex, command);
    }

    /// <summary>Rocket.GetFlightControlRule (Rocket.cs:2577-2596).</summary>
    internal static FlightRule RuleFor(AutopilotReading reading, out float apex)
    {
        float velocity = reading.Velocity;
        float altitude = reading.Altitude;
        apex = FlightMath.Apex(altitude, velocity, reading.Acceleration);
        if (altitude < FlightMath.FinalApproachAltitude && velocity > -4f)
        {
            return FlightRule.FinalApproach;
        }

        bool finite = !float.IsNaN(apex) && !float.IsNegativeInfinity(apex);
        if (finite && (FlightMath.TargetApex(altitude) - apex < altitude / 2f ||
                       (apex > 0f && velocity < (0f - reading.TransitDistance) / 2f)))
        {
            return FlightRule.Normal;
        }

        if ((!float.IsNegativeInfinity(apex) && !float.IsNaN(apex)) ||
            (!float.IsNegativeInfinity(apex) && apex < 0f - altitude))
        {
            return FlightRule.Alternate;
        }

        return FlightRule.Alternate2;
    }

    // Rocket.NormalRuleThrottle (Rocket.cs:2598-2616).
    private static ThrottleCommand Normal(AutopilotReading reading, float arrestAltitude)
    {
        float targetAltitude = FlightMath.TargetApex(reading.Altitude);
        float strong = 1f;
        float gentle = 1f;
        if (reading.Throttle >= 1f && reading.Thrust >= 1f)
        {
            float perThrottle = reading.Thrust / reading.Throttle;
            strong = 0.25f * reading.Weight / perThrottle;
            gentle = strong / 200f;
        }

        float t = Math.Abs((arrestAltitude - targetAltitude) / reading.Altitude) * 1f;
        float amount = UnityFloat.Lerp(gentle, strong, t);
        return new ThrottleCommand.Trim(arrestAltitude < targetAltitude ? amount : 0f - amount);
    }

    // Rocket.FinalDecent (Rocket.cs:2618-2641).
    private static ThrottleCommand FinalDescent(AutopilotReading reading, float arrestAltitude)
    {
        float altitude = reading.Altitude;
        float velocity = reading.Velocity;
        float t = altitude / 60f;
        float fastest = UnityFloat.Lerp(-1f, -4f, t);
        float slowest = UnityFloat.Lerp(-0.5f, -2f, t);
        float perThrottle = reading.Thrust / reading.Throttle;
        float share = 0.999f;
        if (arrestAltitude < -2f || velocity < fastest)
        {
            share = UnityFloat.Lerp(1f, 1.2f, (velocity - fastest) / UnityFloat.Clamp(fastest, -4f, -1f));
        }
        else if (arrestAltitude > -0.1f || velocity > slowest)
        {
            share = UnityFloat.Lerp(0.799f, 0.999f, (velocity - slowest) / UnityFloat.Clamp(slowest, -2f, -1f));
        }

        if (altitude < 0.5f && velocity > -2f)
        {
            share = 1f;
        }

        return new ThrottleCommand.Set(share * reading.Weight / perThrottle);
    }

    // Rocket.Alternate2ThrottleControl (Rocket.cs:2649-2686) with its TargetDecentVelocity (2752-2758).
    private static ThrottleCommand Alternate2(AutopilotReading reading, float safetyMargin)
    {
        float step = 1f;
        if (reading.Thrust > 1f && reading.Throttle > 1f)
        {
            step = reading.Weight * 0.25f / (reading.Thrust / reading.Throttle);
        }

        float thrust = Math.Min(reading.MaxExpectedThrust, reading.HighestRecordedThrust) / safetyMargin;
        float target = TargetDescentVelocity(reading, thrust, FlightMath.TargetApex(reading.Altitude));
        float band = Math.Abs(target) * safetyMargin;
        if (reading.Velocity > 0f)
        {
            return new ThrottleCommand.Trim(-100f);
        }

        if (reading.Throttle < 1f)
        {
            return new ThrottleCommand.Set(1f);
        }

        float throttle = reading.Throttle;
        if (reading.Velocity > target)
        {
            float t = (reading.Velocity - target) / band;
            throttle = new ThrottleCommand.Trim(0f - UnityFloat.Lerp(0.05f, step, t)).Apply(throttle);
        }

        if (reading.Velocity < target)
        {
            float t = (target - reading.Velocity) / band;
            throttle = new ThrottleCommand.Trim(UnityFloat.Lerp(0.05f, step, t * 2f)).Apply(throttle);
        }

        return new ThrottleCommand.Set(throttle);
    }

    private static float TargetDescentVelocity(AutopilotReading reading, float thrust, float altitude)
    {
        float weight = reading.Weight;
        float ratio = Math.Max(1.01f, thrust / weight);
        float spare = weight * ratio - weight;
        return 0f - (float)Math.Sqrt(altitude / (reading.MassKg / (2f * spare)));
    }
}
