#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// mod_info's frame_ms with include_counters: the busy frames' request ms in FrameHistogram's fixed buckets, the
/// nearest-rank percentiles read from them, the frames over 8 ms, the raw bucket counts, the frames DispatchStats
/// counts, the wire, and the edges the catalogue and the docs state.
/// </summary>
public sealed class FrameHistogramTests
{
    [Theory]
    [InlineData(-1.0, 0)]
    [InlineData(0.0, 0)]
    [InlineData(0.1, 0)]
    [InlineData(0.1001, 1)]
    [InlineData(1.0, 3)]
    [InlineData(4.0, 7)]
    [InlineData(4.0001, 8)]
    [InlineData(8.0, 10)]
    [InlineData(8.0001, 11)]
    [InlineData(250.0, 18)]
    [InlineData(250.0001, 19)]
    [InlineData(10_000.0, 19)]
    public void EachEdgeBelongsToTheBucketItEnds(double ms, int bucket) =>
        Assert.Equal(bucket, FrameHistogram.BucketOf(ms));

    [Fact]
    public void TheBucketsAreFixedAndEightMsIsAnEdge()
    {
        Assert.Equal(20, FrameHistogram.BucketCount);
        Assert.Contains(FrameHistogram.OverMs, FrameHistogram.EdgesMs);
        for (int index = 1; index < FrameHistogram.EdgesMs.Length; index++)
        {
            Assert.True(FrameHistogram.EdgesMs[index] > FrameHistogram.EdgesMs[index - 1]);
        }
    }

    [Fact]
    public void WithNoFramesThePercentilesAreNullAndTheCountsZero()
    {
        FrameHistogramReading reading = new FrameHistogram().Read();

        Assert.Equal(0, reading.Frames);
        Assert.Null(reading.P50);
        Assert.Null(reading.P90);
        Assert.Null(reading.P99);
        Assert.Equal(0, reading.Over8Ms);
    }

    [Fact]
    public void FramesInOneBucketReportItsUpperEdgeForEveryPercentile()
    {
        FrameHistogram histogram = Of(100, 3.5);

        FrameHistogramReading reading = histogram.Read();

        Assert.Equal((100L, 4.0, 4.0, 4.0), (reading.Frames, reading.P50!.Value, reading.P90!.Value, reading.P99!.Value));
    }

    [Fact]
    public void OneFrameIsEveryPercentile()
    {
        FrameHistogram histogram = Of(1, 0.3);

        Assert.Equal((0.5, 0.5, 0.5), (histogram.Percentile(50)!.Value, histogram.Percentile(90)!.Value,
            histogram.Percentile(99)!.Value));
    }

    // Ten frames: p50 is the 5th, p90 the 9th and p99 the 10th (ceil(9.9)).
    [Fact]
    public void PercentilesTakeTheNearestRank()
    {
        FrameHistogram histogram = Of(5, 0.2);
        Add(histogram, 4, 1.2);
        Add(histogram, 1, 7.0);

        FrameHistogramReading reading = histogram.Read();

        Assert.Equal((0.25, 1.5, 8.0), (reading.P50!.Value, reading.P90!.Value, reading.P99!.Value));
    }

    [Fact]
    public void TheRankRoundsUp()
    {
        // 99 of 100 frames short: the 99th frame is short. One more long frame moves the 99th of 101 (ceil(99.99) =
        // 100) into the long bucket.
        FrameHistogram histogram = Of(99, 1.0);
        Add(histogram, 1, 20.0);
        Assert.Equal(1.0, histogram.Percentile(99));

        Add(histogram, 1, 20.0);
        Assert.Equal(25.0, histogram.Percentile(99));
    }

    [Fact]
    public void TheOpenBucketReportsTheLongestFrame()
    {
        FrameHistogram histogram = Of(10, 2.0);
        Add(histogram, 1, 400.0);
        Add(histogram, 1, 1_234.567);

        Assert.Equal(1_234.567, histogram.Percentile(99));
        Assert.Equal(2.0, histogram.Percentile(50));
    }

    [Fact]
    public void OverEightCountsFramesStrictlyAboveEight()
    {
        FrameHistogram histogram = Of(3, 8.0);
        Add(histogram, 2, 8.01);
        Add(histogram, 1, 300.0);
        Add(histogram, 4, 7.9);

        Assert.Equal(3, histogram.Read().Over8Ms);
    }

    [Fact]
    public void NotANumberAndNegativeCountInTheFirstBucket()
    {
        FrameHistogram histogram = new FrameHistogram();
        histogram.Add(double.NaN);
        histogram.Add(-3.0);

        FrameHistogramReading reading = histogram.Read();

        Assert.Equal((2L, 0.1, 0L), (reading.Frames, reading.P99!.Value, reading.Over8Ms));
    }

    [Fact]
    public void AReadingHasOneCountPerBucket()
    {
        Assert.Equal(FrameHistogram.BucketCount, new FrameHistogram().Read().Buckets.Length);
        Assert.Equal(FrameHistogram.BucketCount, Of(5, 3.5).Read().Buckets.Length);
    }

    [Fact]
    public void EachFrameCountsInTheBucketItsTimeEnds()
    {
        FrameHistogram histogram = new FrameHistogram();
        Add(histogram, 3, 0.05);
        Add(histogram, 2, 4.0);
        Add(histogram, 4, 8.0001);
        Add(histogram, 1, 300.0);

        long[] expected = new long[FrameHistogram.BucketCount];
        expected[0] = 3;
        expected[7] = 2;
        expected[11] = 4;
        expected[19] = 1;
        Assert.Equal(expected, histogram.Read().Buckets);
    }

    [Fact]
    public void TwoReadingsSubtractToTheWindowsCounts()
    {
        FrameHistogram histogram = new FrameHistogram();
        Add(histogram, 10, 0.3);
        Add(histogram, 2, 12.0);
        long[] before = histogram.Read().Buckets;
        Add(histogram, 5, 0.3);
        Add(histogram, 1, 3.2);
        Add(histogram, 2, 60.0);
        long[] after = histogram.Read().Buckets;

        long[] window = new long[FrameHistogram.BucketCount];
        for (int bucket = 0; bucket < window.Length; bucket++)
        {
            window[bucket] = after[bucket] - before[bucket];
        }

        long[] expected = new long[FrameHistogram.BucketCount];
        expected[FrameHistogram.BucketOf(0.3)] = 5;
        expected[FrameHistogram.BucketOf(3.2)] = 1;
        expected[FrameHistogram.BucketOf(60.0)] = 2;
        Assert.Equal(expected, window);
        Assert.Equal(12, before.Sum());
    }

    /// <summary>
    /// The edges FrameHistogram counts in are the ones the docs state, in the same order and form.
    /// </summary>
    [Fact]
    public void TheDocsStateTheEdges()
    {
        string edges = string.Join(", ", FrameHistogram.EdgesMs.Take(FrameHistogram.EdgesMs.Length - 1).Select(Ms)) +
                       " and " + Ms(FrameHistogram.EdgesMs[FrameHistogram.EdgesMs.Length - 1]) + " ms";
        string docs = File.ReadAllText(Path.Combine(CatalogueFiles.RepositoryRoot(), "docs", "devices-and-logic.md"));

        Assert.Contains("upper edges " + edges, Regex.Replace(docs, @"\s+", " "));
    }

    [Fact]
    public void DispatchStatsCountsTheFramesItCallsBusy()
    {
        DispatchStats stats = new DispatchStats();
        stats.Frame(0, 0.5, false);
        stats.Frame(2, 3.0, false);
        stats.Frame(0, 9.0, true);
        stats.Frame(1, 0.4, false);

        DispatchSnapshot snapshot = stats.Snapshot();

        Assert.Equal(3, snapshot.BusyFrames);
        Assert.Equal(snapshot.BusyFrames, snapshot.Histogram.Frames);
        Assert.Equal(1, snapshot.Histogram.Over8Ms);
        Assert.Equal(3.0, snapshot.Histogram.P50);
    }

    [Fact]
    public void AFrameAllocatesNothing()
    {
        DispatchStats stats = new DispatchStats();
        for (int warm = 0; warm < 1_000; warm++)
        {
            stats.Frame(1 + warm % 3, warm % 300 * 0.1, warm % 7 == 0);
        }

        long allocated = ThreadAllocations.Of(() =>
        {
            for (int measured = 0; measured < 10_000; measured++)
            {
                stats.Frame(1 + measured % 3, measured % 300 * 0.1, measured % 7 == 0);
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FrameMsComesLastAmongTheCountersOnTheWire()
    {
        DispatchStats stats = new DispatchStats();
        stats.Frame(1, 0.3, false);
        stats.Frame(1, 3.2, false);
        stats.Frame(1, 12.5, true);

        Assert.EndsWith(
            "\"batch_console\":{\"lines\":0,\"lines_per_minute\":0.0}," +
            "\"frame_ms\":{\"frames\":3,\"p50\":4.0,\"p90\":16.0,\"p99\":16.0,\"over_8ms\":1," +
            "\"buckets\":[0,0,1,0,0,0,0,1,0,0,0,0,0,1,0,0,0,0,0,0]}}",
            WireCheck.New(Runtime(stats, counters: true)));
    }

    [Fact]
    public void WithNoFramesFrameMsHasNullPercentiles() =>
        Assert.EndsWith(
            "\"frame_ms\":{\"frames\":0,\"p50\":null,\"p90\":null,\"p99\":null,\"over_8ms\":0," +
            "\"buckets\":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]}}",
            WireCheck.New(Runtime(new DispatchStats(), counters: true)));

    [Fact]
    public void WithoutIncludeCountersFrameMsIsNotWritten()
    {
        DispatchStats stats = new DispatchStats();
        stats.Frame(1, 3.2, false);

        Assert.DoesNotContain("\"frame_ms\":{\"frames\"", WireCheck.New(Runtime(stats, counters: false)));
    }

    private static RuntimeView Runtime(DispatchStats stats, bool counters) =>
        new RuntimeView(0.0, 0, FrameBudget.For(4.0, false), stats.Snapshot(), new MemoryView(0, 0, 0, null, null),
            new List<MethodTiming>(), worldIndex: new WorldIndexView(true, false, 1, 1, WorldIndexView.NoClasses, 0, 0),
            counters: counters
                ? new GameCountersView(new JobHoldsView(new long[5]), new PrintLogView(0, 0), 0, new BatchConsoleView(0, 0))
                : null);

    private static string Ms(double edge) => edge.ToString(CultureInfo.InvariantCulture);

    private static FrameHistogram Of(int frames, double ms)
    {
        FrameHistogram histogram = new FrameHistogram();
        Add(histogram, frames, ms);
        return histogram;
    }

    private static void Add(FrameHistogram histogram, int frames, double ms)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            histogram.Add(ms);
        }
    }
}
