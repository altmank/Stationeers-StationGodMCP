#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// 1.9.1 instrumentation: mod_info.runtime's wire, the per-method counters (thread-safe, handler / serialise / queue
/// wait / reply bytes) and the dispatcher's frame counters.
/// </summary>
public sealed class RuntimeWireTests
{
    internal static RuntimeView EmptyRuntime() =>
        new RuntimeView(0.0, 0, FrameBudget.For(4.0, false), new DispatchStats().Snapshot(),
            new MemoryView(0, 0, 0, null, null), new List<MethodTiming>(), new List<DriftCount>());

    [Fact]
    public void RuntimeWire()
    {
        MethodTimings timings = new MethodTimings();
        timings.Record("inspect_slots", true, 0.2, 0.1, 9.0);
        timings.Record("inspect_slots", false, 0.4, 0.3, 15.0);
        timings.RecordReply("inspect_slots", 1600);
        timings.RecordReply("inspect_slots", 1400);
        DispatchStats frames = new DispatchStats();
        frames.Frame(2, 1.5, false);
        frames.Frame(1, 4.5, true);
        frames.Expired();
        RuntimeView view = new RuntimeView(125.04, 2, FrameBudget.For(4.0, false), frames.Snapshot(),
            new MemoryView(31, 0, 123456789, 100000000, null), timings.Called(), new List<DriftCount>());

        Assert.Equal(
            "{\"uptime_s\":125.0,\"world_epoch\":2,\"request_budget_ms\":4.0," +
            "\"frames\":{\"busy_frames\":2,\"requests_served\":3,\"requests_per_frame_mean\":1.5," +
            "\"requests_per_frame_max\":2,\"frame_ms\":{\"total\":6.0,\"mean\":3.0,\"max\":4.5},\"budget_stops\":1," +
            "\"expired\":1}," +
            "\"memory\":{\"gc_collections\":31,\"gc_max_generation\":0,\"gc_total_memory_bytes\":123456789," +
            "\"mono_used_bytes\":100000000,\"mono_heap_bytes\":null}," +
            "\"methods\":[{\"method\":\"inspect_slots\",\"calls\":2,\"errors\":1,\"main_thread_ms\":1.0," +
            "\"handler_ms\":{\"total\":0.6,\"mean\":0.3,\"max\":0.4}," +
            "\"serialize_ms\":{\"total\":0.4,\"mean\":0.2,\"max\":0.3}," +
            "\"queue_wait_ms\":{\"total\":24.0,\"mean\":12.0,\"max\":15.0}," +
            "\"reply_bytes\":{\"total\":3000.0,\"mean\":1500.0,\"max\":1600.0}}],\"method_count\":1," +
            "\"catalogue_drift\":[],\"job_settles\":{\"run\":0,\"skipped\":0,\"unchecked\":0}," +
            "\"prefab_index\":{\"ready\":false,\"verify\":false,\"things\":0,\"names\":0,\"verified\":0," +
            "\"differences\":0}}",
            WireCheck.New(view));
    }

    [Fact]
    public void UnlimitedBudgetIsNull()
    {
        Assert.Contains("\"request_budget_ms\":null",
            WireCheck.New(new RuntimeView(0.0, 0, FrameBudget.For(0.0, false), new DispatchStats().Snapshot(),
                new MemoryView(0, 0, 0, null, null), new List<MethodTiming>(), new List<DriftCount>())));
    }

    [Fact]
    public void MethodsMostMainThreadTimeFirst()
    {
        MethodTimings timings = new MethodTimings();
        timings.Record("game_clock", true, 0.1, 0.1, 1.0);
        timings.Record("grid_survey", true, 30.0, 8.0, 1.0);
        timings.Record("atmosphere_contents", true, 0.1, 0.1, 1.0);
        List<MethodTiming> called = timings.Called();

        Assert.Equal(new[] { "grid_survey", "atmosphere_contents", "game_clock" },
            called.ConvertAll(static timing => timing.Method).ToArray());
    }

    [Fact]
    public void UncalledMethodReadsZero()
    {
        MethodTiming timing = new MethodTimings().Snapshot("planet");

        Assert.Equal(0, timing.Calls);
        Assert.Null(timing.Handler.Mean);
        Assert.Equal(0.0, timing.Handler.Maximum);
    }

    [Fact]
    public void ReplySizeDoesNotCountACall()
    {
        MethodTimings timings = new MethodTimings();
        timings.RecordReply("read_logic", 200);

        MethodTiming timing = timings.Snapshot("read_logic");
        Assert.Equal(0, timing.Calls);
        Assert.Equal(200.0, timing.ReplyBytes.Total);
    }

    // The main thread records times while listener threads record sizes: no update may be lost.
    [Fact]
    public void ConcurrentRecordsAllCount()
    {
        MethodTimings timings = new MethodTimings();
        const int Each = 2000;
        Parallel.For(0, 8, worker =>
        {
            for (int index = 0; index < Each; index++)
            {
                if (worker % 2 == 0)
                {
                    timings.Record("read_logic_many", true, 1.0, 0.5, 2.0);
                }
                else
                {
                    timings.RecordReply("read_logic_many", 10);
                }
            }
        });

        MethodTiming timing = timings.Snapshot("read_logic_many");
        Assert.Equal(4 * Each, timing.Calls);
        Assert.Equal(4 * Each * 1.0, timing.Handler.Total);
        Assert.Equal(4 * Each, timing.ReplyBytes.Count);
        Assert.Equal(4 * Each * 10.0, timing.ReplyBytes.Total);
    }

    [Fact]
    public void FramesWithoutRequestsAreNotCounted()
    {
        DispatchStats stats = new DispatchStats();
        stats.Frame(0, 0.01, false);
        stats.Frame(3, 2.0, false);

        DispatchSnapshot snapshot = stats.Snapshot();
        Assert.Equal(1, snapshot.BusyFrames);
        Assert.Equal(3, snapshot.Served);
        Assert.Equal(3, snapshot.MaxPerFrame);
        Assert.Equal(3.0, snapshot.MeanPerFrame);
    }

    [Fact]
    public void TallyMeanAndMax()
    {
        Tally tally = default(Tally).With(2.0).With(6.0).With(1.0);

        Assert.Equal(3, tally.Count);
        Assert.Equal(3.0, tally.Mean);
        Assert.Equal(6.0, tally.Maximum);
    }

    [Fact]
    public void TallyMaximumOfNegativeSamplesIsTheSample()
    {
        Assert.Equal(-2.0, default(Tally).With(-2.0).Maximum);
    }

    [Fact]
    public void CatalogueDriftCountsMissesByMethodAndName()
    {
        CatalogueDrift drift = new CatalogueDrift();
        Assert.True(drift.Miss("thing_health", "prefab"));
        Assert.False(drift.Miss("thing_health", "prefab"));
        Assert.True(drift.Miss("find_things", "zzz"));
        RuntimeView view = new RuntimeView(0.0, 0, FrameBudget.For(4.0, false), new DispatchStats().Snapshot(),
            new MemoryView(0, 0, 0, null, null), new List<MethodTiming>(), drift.Snapshot());

        Assert.EndsWith(
            "\"catalogue_drift\":[{\"method\":\"find_things\",\"argument\":\"zzz\",\"reads\":1}," +
            "{\"method\":\"thing_health\",\"argument\":\"prefab\",\"reads\":2}]," +
            "\"job_settles\":{\"run\":0,\"skipped\":0,\"unchecked\":0}," +
            "\"prefab_index\":{\"ready\":false,\"verify\":false,\"things\":0,\"names\":0,\"verified\":0," +
            "\"differences\":0}}",
            WireCheck.New(view));
    }

    [Fact]
    public void ThePrefabIndexReportsItsState()
    {
        RuntimeView view = new RuntimeView(0.0, 0, FrameBudget.For(4.0, false), new DispatchStats().Snapshot(),
            new MemoryView(0, 0, 0, null, null), new List<MethodTiming>(), new List<DriftCount>(),
            prefabIndex: new PrefabIndexView(true, true, 34012, 811, 40, 0));

        Assert.EndsWith(
            "\"prefab_index\":{\"ready\":true,\"verify\":true,\"things\":34012,\"names\":811,\"verified\":40," +
            "\"differences\":0}}",
            WireCheck.New(view));
    }
}
