#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>weather: the wire shape of WeatherView, and WeatherMath's schedule and season arithmetic.</summary>
public sealed class WeatherWireTests
{
    [Fact]
    public void WireShape()
    {
        var expected = new
        {
            world_has_weather = true,
            state = "StormScheduled",
            current = new
            {
                id = "VulcanAshStorm", name = "Ash Storm", running = false, starts_in_s = 300.0, length_s = 240.0,
                ends_in_s = (double?)null
            },
            days_past = 59u,
            days_since_last = 4,
            cooldown_days = 3,
            world_start_delay_days = 7.0,
            can_schedule_now = false,
            schedulable_in_days = 0,
            events = new List<object>
            {
                new
                {
                    id = "VulcanSolarStorm", name = "Solar Storm", cooldown_days_min = 3, cooldown_days_max = 11,
                    start_delay_s_min = 30.0, start_delay_s_max = 60.0, duration_s_min = 600.0,
                    duration_s_max = 900.0, temperature_offset_day_k = (double?)500.0,
                    temperature_offset_night_k = (double?)100.0, solar_ratio = (double?)4.0, active_in_orbit = true
                }
            },
            season = new { true_anomaly_deg = 180.0, year_length_days = 124.0, solar_energy_percent = 28.2, day_of_year = 62.0 }
        };
        WeatherView view = new WeatherView(true, "StormScheduled",
            new CurrentWeatherView("VulcanAshStorm", "Ash Storm", false, 300.0, 240.0),
            new WeatherCountersView(59u, 4, 3, 7.0, false, 0),
            new List<WeatherEventView>
            {
                new WeatherEventView("VulcanSolarStorm", "Solar Storm",
                    new WeatherRangesView(3, 11, 30.0, 60.0, 600.0, 900.0), 500.0, 100.0, 4.0, true)
            },
            new SeasonView(180.0, 124.0, 28.2, 62.0));
        WireCheck.Same(expected, view);
    }

    [Fact]
    public void RunningEventCountsDownToItsEnd()
    {
        CurrentWeatherView running = new CurrentWeatherView("VulcanAshStorm", "Ash Storm", true, -100.0, 240.0);
        Assert.Equal(140.0, running.EndsInS);
    }

    [Fact]
    public void ScheduleWaitsForBothDayRules()
    {
        // days_since_last must pass the cooldown: 4 > 3 already.
        Assert.Equal(0, WeatherMath.SchedulableInDays(59u, 7.0, 4, 3));
        // 1 > 3 needs 3 more days.
        Assert.Equal(3, WeatherMath.SchedulableInDays(59u, 7.0, 1, 3));
        // A new world: days_past must pass 7 * multiplier; day 5 of a 7-day start delay waits 3 days.
        Assert.Equal(3, WeatherMath.SchedulableInDays(5u, 7.0, 10, 0));
        // A fractional delay (multiplier 1.5 gives 10.5): day 10 already passes 10.5? No: 10 > 10.5 is false, 11 is.
        Assert.Equal(1, WeatherMath.SchedulableInDays(10u, 10.5, 10, 0));
    }

    [Fact]
    public void RangesAreWhatTheGameCanDraw()
    {
        Assert.Equal((3, 11), WeatherMath.DrawnRange(1, 3, 12));        // random.Next(3, 12) never gives 12
        Assert.Equal((1, 1), WeatherMath.DrawnRange(1, 1, -1));         // unset range falls back to Value
        Assert.Equal((900.0, 900.0), WeatherMath.DrawnRange(float.NaN, 900f, 900f));
        Assert.Equal((5.0, 5.0), WeatherMath.DrawnRange(5f, 0f, 1f));   // a set Value wins
    }

    [Fact]
    public void SeasonCountsSolarDaysLikeTheGame()
    {
        // Vulcan: 125 rotations a year, so 360 / 125 solar degrees a day and 124 solar days.
        double degreesPerDay = 360.0 / 125.0;
        Assert.Equal(124.0, WeatherMath.YearLengthDays(degreesPerDay), 9);
        Assert.Equal(62.0, WeatherMath.DayOfYear(180.0, degreesPerDay), 9);
        Assert.Equal(WeatherMath.DayOfYear(270.0, degreesPerDay), WeatherMath.DayOfYear(-90.0, degreesPerDay), 9);
    }
}
