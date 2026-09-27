#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Trading;
using Weather;

namespace StationGodMCP.Api;

/// <summary>
/// weather: the storm schedule (Weather.WeatherManager), the world's weather events and the season. Read only.
///
/// How the game schedules (CODE WeatherManager.ManagerUpdate): with no event current, it picks one at random
/// (WorldSetting.WeatherEvents.Pick) as soon as CanScheduleWeatherEvent holds, i.e. DaysSinceLastWeatherEvent &gt;
/// LastEventCoolDown and (float)DaysPast &gt; 7 * DifficultySetting.StartingWeatherMultiplier. Scheduling sets
/// WeatherStartTime = GameTime + the event's start delay and WeatherEventLength, both drawn then, so from that moment
/// the start is known to the second. When it ends (StopWeatherEventServer) DaysSinceLastWeatherEvent goes back to 0
/// and a new cooldown is drawn from the ended event's range; OnNextDay adds one a day. While an event is current
/// nothing new schedules, so schedulable_in_days counts the day rules only.
///
/// The season: the player body's orbit angle (CelestialBody._trueAnomaly, which Orbit.GetLocalPosition treats as
/// the mean anomaly) advances evenly with time and is 0 at perihelion. RotatingCelestialBody.AccumulatedAngle counts
/// solar days from it with SolDegreesPerDay (TidallyLockedMoonSolarDegreesPerDay for a moon, against its planet's
/// angle), which gives the year length and the day since perihelion (WeatherMath).
/// </summary>
internal static class WeatherApi
{
    private const float StartDelayDaysPerMultiplier = 7f;

    internal static WeatherView Handle(Args args)
    {
        WorldSetting? world = WorldSetting.Current;
        DifficultySetting? difficulty = DifficultySetting.Current;
        if (world == null || difficulty == null)
        {
            throw ApiErrors.Refused("not_ready", "No world is loaded.");
        }

        double startDelayDays = StartDelayDaysPerMultiplier * (float)difficulty.StartingWeatherMultiplier;
        uint daysPast = WorldManager.DaysPast;
        int daysSince = WeatherManager.DaysSinceLastWeatherEvent;
        int cooldown = WeatherManager.LastEventCoolDown;
        WeatherCountersView counters = new WeatherCountersView(daysPast, daysSince, cooldown, startDelayDays,
            WeatherManager.CanScheduleWeatherEvent(),
            WeatherMath.SchedulableInDays(daysPast, startDelayDays, daysSince, cooldown));
        return new WeatherView(WeatherManager.WorldHasWeather, WeatherManager.WeatherState.ToString(), Current(),
            counters, Events(world), Season());
    }

    private static CurrentWeatherView? Current()
    {
        WeatherEvent current = WeatherManager.CurrentWeatherEvent;
        if (current == null)
        {
            return null;
        }

        return new CurrentWeatherView(current.Id, NameOf(current), WeatherManager.IsWeatherEventRunning,
            WeatherManager.WeatherStartTime - GameManager.GameTime, WeatherManager.WeatherEventLength);
    }

    private static List<WeatherEventView> Events(WorldSetting world)
    {
        List<WeatherEventView> views = new List<WeatherEventView>();
        foreach (WeatherEvent weather in world.WeatherEvents)
        {
            if (weather != null)
            {
                views.Add(ViewOf(weather));
            }
        }

        return views;
    }

    private static WeatherEventView ViewOf(WeatherEvent weather)
    {
        (int coolMin, int coolMax) = weather.CoolDownDays == null
            ? (0, 0)
            : WeatherMath.DrawnRange(weather.CoolDownDays.Value, weather.CoolDownDays.Min, weather.CoolDownDays.Max);
        (double delayMin, double delayMax) = RangeOf(weather.EventStartDelaySeconds);
        (double lengthMin, double lengthMax) = RangeOf(weather.EventDurationSeconds);
        GlobalTemperatureFloatOffset? flat = weather.TemperatureOffset as GlobalTemperatureFloatOffset;
        return new WeatherEventView(weather.Id, NameOf(weather),
            new WeatherRangesView(coolMin, coolMax, delayMin, delayMax, lengthMin, lengthMax),
            flat?.FloatOffsetDayData?.Value, flat?.FloatOffsetNightData?.Value, weather.SolarRatio?.Value,
            weather.ActiveInOrbit);
    }

    private static (double Min, double Max) RangeOf(FloatRangeData? range) =>
        range == null ? (0.0, 0.0) : WeatherMath.DrawnRange(range.Value, range.Min, range.Max);

    private static string? NameOf(WeatherEvent weather) =>
        weather.Name == null ? weather.Id : Text.Plain((string)weather.Name);

    private static SeasonView? Season()
    {
        if (!OrbitalSimulation.IsValid)
        {
            return null;
        }

        OrbitalSimulation orbit = OrbitalSimulation.System;
        RotatingCelestialBody body = orbit.PlayerBody;
        RotatingCelestialBody reference = body.GetRotatingReferenceBody();
        double degreesPerDay = reference == body ? body.SolDegreesPerDay : body.TidallyLockedMoonSolarDegreesPerDay();
        double angle = WeatherMath.WrapDegrees(reference.GetWrappedTrueAnomaly());
        float energy = orbit.GetSolarEnergyPercentClamped(orbit.GetSolarEnergy(), orbit.CalculateSolarIrradiance());
        return new SeasonView(angle, WeatherMath.YearLengthDays(degreesPerDay), energy,
            WeatherMath.DayOfYear(angle, degreesPerDay));
    }
}
