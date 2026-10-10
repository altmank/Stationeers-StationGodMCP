#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Profiling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// mod_info's game counters: the job tick-hold histogram (HoldLengths), the event rate behind batch_console
/// (EventRate), the prefab index's arrived and left totals, and their place in runtime's wire with include_counters.
/// </summary>
public sealed class GameCountersTests
{
    // One tick is one millisecond.
    private const long Millis = 1000;

    // One tick is one second: EventRate's slots are 10 ticks.
    private const long Seconds = 1;

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(99.999, 0)]
    [InlineData(100.0, 1)]
    [InlineData(999.999, 1)]
    [InlineData(1_000.0, 2)]
    [InlineData(9_999.999, 2)]
    [InlineData(10_000.0, 3)]
    [InlineData(59_999.999, 3)]
    [InlineData(60_000.0, 4)]
    [InlineData(3_600_000.0, 4)]
    public void HoldBucketEdgesBelongToTheBucketAbove(double ms, int bucket) =>
        Assert.Equal(bucket, HoldLengths.BucketOf(ms));

    [Fact]
    public void HoldCountsOnceInItsLengthsBucket()
    {
        HoldLengths lengths = new HoldLengths();

        lengths.Held(5_000);
        lengths.Released(5_250, Millis);

        Assert.Equal(new long[] { 0, 1, 0, 0, 0 }, lengths.Counts());
    }

    [Fact]
    public void ReleaseWithNoHoldCountsNothing()
    {
        HoldLengths lengths = new HoldLengths();

        lengths.Released(9_000, Millis);
        lengths.Held(10_000);
        lengths.Released(10_050, Millis);
        lengths.Released(99_000, Millis);

        Assert.Equal(new long[] { 1, 0, 0, 0, 0 }, lengths.Counts());
    }

    [Fact]
    public void HoldAskedForAgainKeepsTheFirstStart()
    {
        HoldLengths lengths = new HoldLengths();

        lengths.Held(1_000);
        lengths.Held(70_000);
        lengths.Released(71_000, Millis);

        Assert.Equal(new long[] { 0, 0, 0, 0, 1 }, lengths.Counts());
    }

    [Fact]
    public void RateCountsTheFullSlotsBeforeTheOneUnderWay()
    {
        EventRate rate = new EventRate(Seconds);
        NoteAt(rate, 5, 3);
        NoteAt(rate, 15, 2);
        NoteAt(rate, 21, 4);

        Assert.Equal(5.0, rate.PerMinute(25));
        Assert.Equal(9, rate.Total);
    }

    [Fact]
    public void RateLeavesOutSlotsOlderThanTheWindow()
    {
        EventRate rate = new EventRate(Seconds);
        NoteAt(rate, 5, 3);
        NoteAt(rate, 25, 2);

        // Slot 8 is under way; the window is slots 2 to 7.
        Assert.Equal(2.0, rate.PerMinute(80));
        Assert.Equal(5, rate.Total);
    }

    [Fact]
    public void ReusedSlotStartsItsCountAgain()
    {
        EventRate rate = new EventRate(Seconds);
        NoteAt(rate, 5, 3);
        NoteAt(rate, 75, 1);

        Assert.Equal(1.0, rate.PerMinute(85));
    }

    [Fact]
    public void RateOfAQuietWindowIsZero() =>
        Assert.Equal(0.0, new EventRate(Seconds).PerMinute(1_000));

    // Console lines arrive from the game's worker threads as well as the main thread: none may be lost.
    [Fact]
    public void ConcurrentNotesAllCount()
    {
        EventRate rate = new EventRate(Seconds);
        const int Each = 5000;

        Parallel.For(0, 8, worker =>
        {
            for (int index = 0; index < Each; index++)
            {
                rate.Note(worker % 2 == 0 ? 12 : 17);
            }
        });

        Assert.Equal(8 * Each, rate.Total);
        Assert.Equal(8.0 * Each, rate.PerMinute(20));
    }

    [Fact]
    public void PrefabIndexArrivalsAndLeavesSurviveAReset()
    {
        PrefabIndex<string> index = new PrefabIndex<string>(static name => name);
        index.Arrived("a");
        index.Arrived("b");
        index.Left("a");

        index.Reset();

        Assert.Equal((2L, 1L), (index.ArrivedTotal, index.LeftTotal));
    }

    [Fact]
    public void JobGasWalkIsAProfilingScope() =>
        Assert.Equal("job_gas_walk", ProfIds.Name(ProfId.JobGasWalk));

    [Fact]
    public void WithoutIncludeCountersNoCounterIsWritten()
    {
        string wire = WireCheck.New(new RuntimeView(0.0, 0, FrameBudget.For(4.0, false), new DispatchStats().Snapshot(),
            new MemoryView(0, 0, 0, null, null), new List<MethodTiming>(), new List<DriftCount>(),
            prefabIndex: new PrefabIndexView(true, false, 10, 3, 0, 0)));

        Assert.EndsWith("\"differences\":0}}", wire);
    }

    [Fact]
    public void TheSidecarTakesIncludeCounters()
    {
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse("""{"include_counters":true}""");
        Assert.Empty(StationGodMCP.Server.ArgumentCheck.Problems(
            StationGodMCP.Server.Program.InputSchemas["mod_info"], document.RootElement));
    }

    [Fact]
    public void CountersSitBesidePrefabIndexOnTheWire()
    {
        GameCountersView counters = new GameCountersView(new JobHoldsView(new long[] { 7, 3, 2, 1, 0 }),
            new PrintLogView(120, PrintLog.DefaultCapacity), 4, new BatchConsoleView(250, 12.345));
        RuntimeView view = new RuntimeView(0.0, 0, FrameBudget.For(4.0, false), new DispatchStats().Snapshot(),
            new MemoryView(0, 0, 0, null, null), new List<MethodTiming>(), new List<DriftCount>(),
            prefabIndex: new PrefabIndexView(true, false, 10, 3, 0, 0, 40, 30), counters: counters);

        Assert.EndsWith(
            "\"prefab_index\":{\"ready\":true,\"verify\":false,\"things\":10,\"names\":3,\"verified\":0," +
            "\"differences\":0,\"arrived\":40,\"left\":30}," +
            "\"job_holds\":{\"under_100ms\":7,\"under_1s\":3,\"under_10s\":2,\"under_60s\":1,\"over_60s\":0}," +
            "\"print_log\":{\"records\":120,\"capacity\":2048},\"lint_chip_programs\":4," +
            "\"batch_console\":{\"lines\":250,\"lines_per_minute\":12.3}}",
            WireCheck.New(view));
    }

    private static void NoteAt(EventRate rate, long timestamp, int times)
    {
        for (int time = 0; time < times; time++)
        {
            rate.Note(timestamp);
        }
    }
}
