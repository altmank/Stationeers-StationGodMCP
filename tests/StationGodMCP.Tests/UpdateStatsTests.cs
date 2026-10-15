#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// server info's update_ms with include_counters (cost build step 1): StationGod's whole Update per frame, job steps
/// included; the longest frame, the frames over the budget, the buckets, and the recent ring the cost gate reads a
/// call's worst frame from.
/// </summary>
public sealed class UpdateStatsTests
{
    [Fact]
    public void TheWorstFrameAndItsNumberAreKept()
    {
        UpdateStats stats = new UpdateStats();
        stats.Frame(10, 0.5, 4.0);
        stats.Frame(11, 7.25, 4.0);
        stats.Frame(12, 3.0, 4.0);

        UpdateReading reading = stats.Read(13);

        Assert.Equal(13, reading.Frame);
        Assert.Equal(7.25, reading.WorstMs);
        Assert.Equal(11, reading.WorstFrame);
        Assert.Equal(3, reading.Histogram.Frames);
    }

    [Fact]
    public void WithNoFramesThereIsNoWorstFrame()
    {
        UpdateReading reading = new UpdateStats().Read(1);

        Assert.Null(reading.WorstMs);
        Assert.Null(reading.WorstFrame);
        Assert.Empty(reading.RecentFrames);
    }

    [Fact]
    public void OverBudgetCountsFramesPastTheBudgetOnly()
    {
        UpdateStats stats = new UpdateStats();
        stats.Frame(1, 4.0, 4.0);
        stats.Frame(2, 4.01, 4.0);
        stats.Frame(3, 100.0, 0.0);

        Assert.Equal(1, stats.Read(4).OverBudget);
    }

    [Fact]
    public void RecentKeepsFramesOverOneMsOldestFirst()
    {
        UpdateStats stats = new UpdateStats();
        stats.Frame(1, 0.9, 4.0);
        stats.Frame(2, 1.0, 4.0);
        stats.Frame(3, 6.5, 4.0);
        stats.Frame(4, 1.5, 4.0);

        UpdateReading reading = stats.Read(5);

        Assert.Equal(new long[] { 3, 4 }, reading.RecentFrames);
        Assert.Equal(new[] { 6.5, 1.5 }, reading.RecentMs);
    }

    [Fact]
    public void RecentWrapsAtItsCapacity()
    {
        UpdateStats stats = new UpdateStats();
        int frames = UpdateStats.RecentCapacity + 10;
        for (int frame = 1; frame <= frames; frame++)
        {
            stats.Frame(frame, 2.0, 4.0);
        }

        UpdateReading reading = stats.Read(frames + 1);

        Assert.Equal(UpdateStats.RecentCapacity, reading.RecentFrames.Length);
        Assert.Equal(11, reading.RecentFrames[0]);
        Assert.Equal(frames, reading.RecentFrames[UpdateStats.RecentCapacity - 1]);
    }

    [Fact]
    public void UpdateMsIsWrittenWithIncludeCountersOnly()
    {
        UpdateStats stats = new UpdateStats();
        stats.Frame(41, 5.555, 4.0);

        Assert.Contains(
            "\"update_ms\":{\"frame\":42,\"frames\":1,\"worst_ms\":5.56,\"worst_frame\":41,\"over_budget\":1," +
            "\"buckets\":[0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0],\"recent\":[{\"frame\":41,\"ms\":5.56}]}",
            WireCheck.New(Runtime(stats.Read(42), counters: true)));
        Assert.DoesNotContain("update_ms", WireCheck.New(Runtime(stats.Read(42), counters: false)));
    }

    private static RuntimeView Runtime(UpdateReading updates, bool counters) =>
        new RuntimeView(0.0, 0, FrameBudget.For(4.0, false), new DispatchStats().Snapshot(),
            new MemoryView(0, 0, 0, null, null), new List<MethodTiming>(),
            prefabIndex: new PrefabIndexView(true, false, 1, 1, 0, 0),
            counters: counters
                ? new GameCountersView(new JobHoldsView(new long[5]), new PrintLogView(0, 0), 0, new BatchConsoleView(0, 0))
                : null,
            updates: updates);
}
