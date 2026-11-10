#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>server info's runtime.game_frame_ms: the game's frame ms over the last 60 s of real time.</summary>
public sealed class GameFrameWindowTests
{
    [Fact]
    public void An_empty_window_reads_nulls()
    {
        GameFrameReading reading = new GameFrameWindow().Read(10.0);
        Assert.Equal(0, reading.Frames);
        Assert.Null(reading.Mean);
        Assert.Null(reading.P50);
        Assert.Null(reading.P95);
        Assert.Null(reading.P99);
        Assert.Null(reading.Max);
    }

    [Fact]
    public void Frames_older_than_sixty_seconds_are_dropped()
    {
        GameFrameWindow window = new GameFrameWindow();
        window.Add(100.0, 0.0);
        window.Add(10.0, 30.0);
        window.Add(20.0, 70.0);
        GameFrameReading reading = window.Read(70.0);
        Assert.Equal(2, reading.Frames);
        Assert.Equal(20.0, reading.Max);
        Assert.Equal(15.0, reading.Mean);
        Assert.Equal(1, window.Read(100.0).Frames);
        Assert.Equal(0, window.Read(131.0).Frames);
    }

    [Fact]
    public void Percentiles_are_nearest_rank_on_a_known_series()
    {
        GameFrameWindow window = new GameFrameWindow();
        for (int ms = 100; ms >= 1; ms--)
        {
            window.Add(ms, 5.0);
        }

        GameFrameReading reading = window.Read(6.0);
        Assert.Equal(100, reading.Frames);
        Assert.Equal(50.5, reading.Mean);
        Assert.Equal(50.0, reading.P50);
        Assert.Equal(95.0, reading.P95);
        Assert.Equal(99.0, reading.P99);
        Assert.Equal(100.0, reading.Max);
    }

    [Fact]
    public void The_ring_wraps_at_capacity_and_keeps_the_newest()
    {
        GameFrameWindow window = new GameFrameWindow(4);
        for (int ms = 1; ms <= 6; ms++)
        {
            window.Add(ms, ms);
        }

        GameFrameReading reading = window.Read(6.0);
        Assert.Equal(4, reading.Frames);
        Assert.Equal(4.5, reading.Mean);
        Assert.Equal(6.0, reading.Max);
        Assert.Equal(4.0, reading.P50);
    }

    [Fact]
    public void A_negative_frame_counts_as_zero()
    {
        GameFrameWindow window = new GameFrameWindow();
        window.Add(-5.0, 1.0);
        window.Add(double.NaN, 1.0);
        Assert.Equal(0.0, window.Read(2.0).Max);
    }
}
