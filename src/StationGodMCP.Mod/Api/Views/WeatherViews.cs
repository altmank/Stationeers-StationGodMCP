#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Api.Views;

/// <summary>weather: the storm schedule, the world's weather events and the season.</summary>
internal sealed class WeatherView
{
    internal WeatherView(bool worldHasWeather, string state, CurrentWeatherView? current, WeatherCountersView counters,
        List<WeatherEventView> events, SeasonView? season)
    {
        WorldHasWeather = worldHasWeather;
        State = state;
        Current = current;
        DaysPast = counters.DaysPast;
        DaysSinceLast = counters.DaysSinceLast;
        CooldownDays = counters.CooldownDays;
        WorldStartDelayDays = counters.WorldStartDelayDays;
        CanScheduleNow = counters.CanScheduleNow;
        SchedulableInDays = counters.SchedulableInDays;
        Events = events;
        Season = season;
    }

    public bool WorldHasWeather { get; }

    /// <summary>WeatherManager.WeatherState: None, StormScheduled, Storm, RainScheduled, Rain, SnowScheduled, Snow.</summary>
    public string State { get; }

    /// <summary>The scheduled or running event; null when none is.</summary>
    public CurrentWeatherView? Current { get; }

    public uint DaysPast { get; }

    public int DaysSinceLast { get; }

    public int CooldownDays { get; }

    public double WorldStartDelayDays { get; }

    public bool CanScheduleNow { get; }

    public int SchedulableInDays { get; }

    public List<WeatherEventView> Events { get; }

    /// <summary>Null while the orbit is not loaded.</summary>
    public SeasonView? Season { get; }
}

/// <summary>The day counts the schedule turns on, for WeatherView.</summary>
internal sealed class WeatherCountersView
{
    internal WeatherCountersView(uint daysPast, int daysSinceLast, int cooldownDays, double worldStartDelayDays,
        bool canScheduleNow, int schedulableInDays)
    {
        DaysPast = daysPast;
        DaysSinceLast = daysSinceLast;
        CooldownDays = cooldownDays;
        WorldStartDelayDays = worldStartDelayDays;
        CanScheduleNow = canScheduleNow;
        SchedulableInDays = schedulableInDays;
    }

    internal uint DaysPast { get; }

    internal int DaysSinceLast { get; }

    internal int CooldownDays { get; }

    internal double WorldStartDelayDays { get; }

    internal bool CanScheduleNow { get; }

    internal int SchedulableInDays { get; }
}

/// <summary>The scheduled or running weather event.</summary>
internal sealed class CurrentWeatherView
{
    internal CurrentWeatherView(string? id, string? name, bool running, double startsInS, double lengthS)
    {
        Id = id;
        Name = name;
        Running = running;
        StartsInS = startsInS;
        LengthS = lengthS;
    }

    public string? Id { get; }

    public string? Name { get; }

    public bool Running { get; }

    /// <summary>WeatherStartTime - GameManager.GameTime: negative once it runs.</summary>
    public double StartsInS { get; }

    public double LengthS { get; }

    /// <summary>Seconds of game time left; null unless it is running.</summary>
    public double? EndsInS => Running ? StartsInS + LengthS : null;
}

/// <summary>One of the world's weather events and the ranges the game draws from.</summary>
internal sealed class WeatherEventView
{
    internal WeatherEventView(string? id, string? name, WeatherRangesView ranges, double? temperatureOffsetDayK,
        double? temperatureOffsetNightK, double? solarRatio, bool activeInOrbit)
    {
        Id = id;
        Name = name;
        CooldownDaysMin = ranges.CooldownDaysMin;
        CooldownDaysMax = ranges.CooldownDaysMax;
        StartDelaySMin = ranges.StartDelaySMin;
        StartDelaySMax = ranges.StartDelaySMax;
        DurationSMin = ranges.DurationSMin;
        DurationSMax = ranges.DurationSMax;
        TemperatureOffsetDayK = temperatureOffsetDayK;
        TemperatureOffsetNightK = temperatureOffsetNightK;
        SolarRatio = solarRatio;
        ActiveInOrbit = activeInOrbit;
    }

    public string? Id { get; }

    public string? Name { get; }

    public int CooldownDaysMin { get; }

    public int CooldownDaysMax { get; }

    public double StartDelaySMin { get; }

    public double StartDelaySMax { get; }

    public double DurationSMin { get; }

    public double DurationSMax { get; }

    /// <summary>A flat day offset; null when the event has none or uses a curve.</summary>
    public double? TemperatureOffsetDayK { get; }

    public double? TemperatureOffsetNightK { get; }

    public double? SolarRatio { get; }

    public bool ActiveInOrbit { get; }
}

/// <summary>The drawn ranges of one weather event, for WeatherEventView.</summary>
internal sealed class WeatherRangesView
{
    internal WeatherRangesView(int cooldownDaysMin, int cooldownDaysMax, double startDelaySMin, double startDelaySMax,
        double durationSMin, double durationSMax)
    {
        CooldownDaysMin = cooldownDaysMin;
        CooldownDaysMax = cooldownDaysMax;
        StartDelaySMin = startDelaySMin;
        StartDelaySMax = startDelaySMax;
        DurationSMin = durationSMin;
        DurationSMax = durationSMax;
    }

    internal int CooldownDaysMin { get; }

    internal int CooldownDaysMax { get; }

    internal double StartDelaySMin { get; }

    internal double StartDelaySMax { get; }

    internal double DurationSMin { get; }

    internal double DurationSMax { get; }
}

/// <summary>Where the world is in its year.</summary>
internal sealed class SeasonView
{
    internal SeasonView(double trueAnomalyDeg, double yearLengthDays, double solarEnergyPercent, double dayOfYear)
    {
        TrueAnomalyDeg = trueAnomalyDeg;
        YearLengthDays = yearLengthDays;
        SolarEnergyPercent = solarEnergyPercent;
        DayOfYear = dayOfYear;
    }

    /// <summary>The game's orbit angle, 0 at perihelion (CelestialBody.GetWrappedTrueAnomaly, wrapped to 0..360).</summary>
    public double TrueAnomalyDeg { get; }

    public double YearLengthDays { get; }

    public double SolarEnergyPercent { get; }

    /// <summary>Solar days since perihelion, fractional.</summary>
    public double DayOfYear { get; }
}
