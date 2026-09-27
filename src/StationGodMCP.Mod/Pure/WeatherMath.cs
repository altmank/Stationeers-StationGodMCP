#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// The storm schedule and season arithmetic, from the game's own rules (Weather.WeatherManager,
/// RotatingCelestialBody), kept free of game types so it can be tested.
/// </summary>
internal static class WeatherMath
{
    private const double FullTurnDeg = 360.0;

    /// <summary>
    /// Whole days until WeatherManager.CanScheduleWeatherEvent's two day counts both pass: DaysSinceLastWeatherEvent
    /// &gt; LastEventCoolDown and (float)DaysPast &gt; 7 * StartingWeatherMultiplier. Both counters go up by one a
    /// day, so the wait is the larger of the two shortfalls; 0 once both pass.
    /// </summary>
    internal static int SchedulableInDays(uint daysPast, double worldStartDelayDays, int daysSinceLast, int cooldownDays)
    {
        int cooldownWait = Math.Max(0, cooldownDays + 1 - daysSinceLast);
        int startWait = Math.Max(0, (int)Math.Floor(worldStartDelayDays) + 1 - (int)daysPast);
        return Math.Max(cooldownWait, startWait);
    }

    /// <summary>
    /// The largest cooldown IntRangeData.GenerateValue can draw: random.Next(Min, Max) never returns Max, and an unset
    /// or inverted range falls back to Value.
    /// </summary>
    internal static (int Min, int Max) DrawnRange(int value, int min, int max) =>
        max < 1 || min < 1 || min > max ? (value, value) : (min, Math.Max(min, max - 1));

    /// <summary>FloatRangeData.GenerateValue: a set Value wins; otherwise Min to Max.</summary>
    internal static (double Min, double Max) DrawnRange(float value, float min, float max) =>
        !float.IsNaN(value) ? (value, value) : (Math.Min(min, max), Math.Max(min, max));

    /// <summary>
    /// Solar days in one orbit, as RotatingCelestialBody.AccumulatedAngle counts them: the orbit angle over the solar
    /// degrees per day, times (360 - that) / 360 days per step. Vulcan: 125 rotations a year are 124 solar days.
    /// </summary>
    internal static double YearLengthDays(double solarDegreesPerDay) =>
        (FullTurnDeg - solarDegreesPerDay) / solarDegreesPerDay;

    /// <summary>Days since the orbit angle was 0 (perihelion); the game advances that angle evenly in time.</summary>
    internal static double DayOfYear(double wrappedOrbitDeg, double solarDegreesPerDay) =>
        WrapDegrees(wrappedOrbitDeg) / FullTurnDeg * YearLengthDays(solarDegreesPerDay);

    /// <summary>An angle in 0 to 360; C#'s % keeps the sign of a negative angle.</summary>
    internal static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % FullTurnDeg;
        return wrapped < 0.0 ? wrapped + FullTurnDeg : wrapped;
    }
}
