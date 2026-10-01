#nullable enable

using System;

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// UnityEngine.Mathf's Lerp, Clamp and Clamp01 in float, as the rocket code calls them: Lerp clamps t to 0..1, and a
/// NaN passes through Clamp (neither comparison holds), as it does in the game.
/// </summary>
internal static class UnityFloat
{
    internal static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    internal static float Clamp(float value, float minimum, float maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    internal static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
}

/// <summary>
/// The rocket's flight arithmetic, ported line by line from Objects.Rockets.Rocket (decompile Objects.Rockets/Rocket.cs).
/// Everything is float, as in the game, so a forecast and the game round the same way. The in-game self test
/// (rocket_status checks) compares these with the game's own Rocket.GetApex and RocketAvionicsDevice
/// .GetAutoLandConfidenceRatio.
/// </summary>
internal static class FlightMath
{
    /// <summary>The autopilot's abort line: a stop point at or below this many metres counts as a failed throttle step (Rocket.cs:2520).</summary>
    internal const float AbortApexMetres = 60f;

    /// <summary>Below this altitude, slower than 4 m/s down, the autopilot flies its final approach (Rocket.cs:2583, LANDING_BEGIN_ALTITUDE).</summary>
    internal const float FinalApproachAltitude = 60f;

    /// <summary>The floor of the stop point the autopilot aims for (Rocket.cs:2574, LANDING_ARREST_ALTITUDE).</summary>
    internal const float ArrestAltitude = 40f;

    /// <summary>Gravity a rocket feels, clamped as Rocket.GetGravity and GetAutoLandConfidenceRatio clamp it (Rocket.cs:2512, 3097).</summary>
    internal static float ClampGravity(float gravity) => UnityFloat.Clamp(gravity, -5.5f, -1f);

    /// <summary>Rocket.TimeToVerticalArrest (Rocket.cs:1801-1809): -1 when not falling.</summary>
    internal static float TimeToVerticalArrest(float velocity, float acceleration)
    {
        if (velocity >= 0f)
        {
            return -1f;
        }

        float half = 0.5f * (0f - acceleration);
        return 0f - (0f - velocity) / (2f * half);
    }

    /// <summary>
    /// Rocket.GetApex (Rocket.cs:1816-1832): the altitude at which a falling rocket would stop at this acceleration.
    /// NaN when rising and accelerating up; negative infinity whenever the acceleration is downward.
    /// </summary>
    internal static float Apex(float altitude, float velocity, float acceleration)
    {
        if (velocity > 0f && acceleration > 0f)
        {
            return float.NaN;
        }

        float half = 0.5f * (0f - acceleration);
        float speed = 0f - velocity;
        float depth = 0f - altitude;
        float time = TimeToVerticalArrest(velocity, acceleration);
        float reached = (float)Math.Pow(time, 2f) * half + time * speed + depth;
        if (acceleration < 0f)
        {
            return float.NegativeInfinity;
        }

        return 0f - reached;
    }

    /// <summary>Rocket.TargetApex (Rocket.cs:2572-2575): the stop point the autopilot trims toward.</summary>
    internal static float TargetApex(float altitude) => Math.Max(altitude / 50f, ArrestAltitude);

    /// <summary>
    /// Rocket.GetAutoLandConfidenceRatio (Rocket.cs:2502-2528): from 100 % throttle down to 50 %, how many 1 % steps in a
    /// row still stop the rocket more than 60 m up, over 50. Zero with automated landing off. The game calls it with
    /// deltaV = the re-entry hop's distance (the speed the landing starts at), the world's gravity and the re-entry
    /// profile's altitude, once, as the landing hop starts (Rocket.cs:2174-2187), and aborts the landing at 0.
    /// </summary>
    internal static ConfidenceReading Confidence(bool automatedLanding, float deltaV, float gravity, float altitude,
        float maxExpectedThrust, float massKg)
    {
        float minRequired = float.PositiveInfinity;
        if (!automatedLanding)
        {
            return new ConfidenceReading(0f, minRequired);
        }

        float velocity = 0f - deltaV;
        float clamped = ClampGravity(gravity);
        const int lowest = 50;
        int steps = 0;
        for (int percent = 100; percent >= lowest; percent--)
        {
            float share = percent / 100f;
            float acceleration = maxExpectedThrust * share / massKg + clamped;
            float apex = Apex(altitude, velocity, acceleration);
            if (float.IsNaN(apex) || float.IsNegativeInfinity(apex) || !(apex > AbortApexMetres))
            {
                break;
            }

            steps++;
            minRequired = maxExpectedThrust * share;
        }

        return new ConfidenceReading(steps / (float)lowest, minRequired);
    }

    /// <summary>Rocket.AutoLandConfidenceString's bands (Rocket.cs:3166-3170).</summary>
    internal static string ConfidenceBand(float ratio) =>
        ratio >= 0.8f ? "very_high" : ratio >= 0.6f ? "high" : ratio >= 0.3f ? "moderate" : ratio > 0f ? "low" : "none";
}

/// <summary>The confidence ratio and the least thrust of the steps that passed (infinite when none did).</summary>
internal readonly struct ConfidenceReading
{
    internal ConfidenceReading(float ratio, float minRequiredThrust)
    {
        Ratio = ratio;
        MinRequiredThrust = minRequiredThrust;
    }

    internal float Ratio { get; }

    internal float MinRequiredThrust { get; }
}
