#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Profiling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The runtime profiler without the game: the frame ring's percentiles and wrap-around, the worst frame and its
/// breakdown, heap deltas, timed methods, tick holds, the slow-frame warning's rate limit, the CSV writer, the
/// profiling method's arguments, and the cost of a scope with profiling off.
/// </summary>
public sealed class ProfilingTests
{
    // One tick per millisecond, a heap the test sets.
    private sealed class ScriptedClock : ProfileClock
    {
        internal long Now { get; set; } = 1000;

        internal long Heap { get; set; } = 1_000_000;

        internal override long Frequency => 1000;

        internal override long Timestamp() => Now;

        internal override long HeapBytes() => Heap;

        internal override DateTime UtcNow => new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    }

    private static ProfileRecorder Recorder(ScriptedClock clock, double slowMs = 1000, IReadOnlyList<string>? methods = null,
        SlowFrameGate? gate = null) =>
        new ProfileRecorder(slowMs, methods ?? Array.Empty<string>(), clock, gate);

    // One frame of ms milliseconds, with the heap changed by heapDelta.
    private static void Frame(ProfileRecorder recorder, ScriptedClock clock, long ms, long frame, long heapDelta = 0,
        Action? inside = null)
    {
        long started = recorder.BeginFrame();
        inside?.Invoke();
        clock.Now = started + ms;
        clock.Heap += heapDelta;
        recorder.EndFrame(started, frame);
    }

    private static void Scope(ProfileRecorder recorder, ScriptedClock clock, ProfId id, long ms, Action? inside = null)
    {
        long started = recorder.Enter((int)id);
        inside?.Invoke();
        clock.Now = Math.Max(clock.Now, started) + ms;
        recorder.Exit((int)id, started);
    }

    [Fact]
    public void PercentilesAreNearestRankOnKnownData()
    {
        double[] values = Enumerable.Range(1, 100).Select(value => (double)(101 - value)).ToArray();

        Spread spread = Spread.Of(values, values.Length);

        Assert.Equal(50.0, spread.P50);
        Assert.Equal(95.0, spread.P95);
        Assert.Equal(100.0, spread.Max);
        Assert.Null(Spread.Of(new double[1], 0).P50);
        Spread one = Spread.Of(new[] { 7.0 }, 1);
        Assert.Equal(7.0, one.P50);
        Assert.Equal(7.0, one.P95);
    }

    [Fact]
    public void TheRingKeepsTheLast600FramesAndWrapsAround()
    {
        ScriptedClock clock = new ScriptedClock();
        ProfileRecorder recorder = Recorder(clock);
        // 600 frames of 50 ms, then 600 frames of 1..600 ms: only the second set is in the window.
        for (int frame = 0; frame < ProfileRecorder.WindowFrames; frame++)
        {
            Frame(recorder, clock, 50, frame);
        }

        for (int frame = 1; frame <= ProfileRecorder.WindowFrames; frame++)
        {
            Frame(recorder, clock, frame, 1000 + frame);
        }

        ProfileSnapshot report = recorder.Report(true);

        Assert.Equal(1200, report.FramesRecorded);
        Assert.Equal(600, report.WindowFrames);
        Assert.Equal(300.0, report.FrameMs.P50);
        Assert.Equal(570.0, report.FrameMs.P95);
        Assert.Equal(600.0, report.FrameMs.Max);
    }

    [Fact]
    public void TheWorstFrameKeepsItsScopesCallsAndUnscopedRest()
    {
        ScriptedClock clock = new ScriptedClock();
        ProfileRecorder recorder = Recorder(clock);
        Frame(recorder, clock, 5, 1);
        Frame(recorder, clock, 20, 2, inside: () =>
        {
            Scope(recorder, clock, ProfId.RunFrame, 3, () =>
            {
                long call = recorder.Now();
                Scope(recorder, clock, ProfId.CallExecute, 9);
                recorder.Call("read_devices", call);
            });
            Scope(recorder, clock, ProfId.HeldTickJobs, 2);
        });
        Frame(recorder, clock, 4, 3);

        ProfileSnapshot report = recorder.Report(true);
        WorstFrame worst = report.Worst!;

        Assert.Equal(2, worst.Frame);
        Assert.Equal(20.0, worst.Ms);
        Assert.Equal(new[] { ("call_execute", 9.0), ("unscoped", 6.0), ("run_frame", 3.0), ("held_tick_jobs", 2.0) },
            worst.Scopes.Select(scope => (scope.Name, scope.Ms)));
        Assert.Equal(("read_devices", 9.0), (worst.Calls.Single().Name, worst.Calls.Single().Ms));
        SlotStat runFrame = report.Slots.Single(slot => slot.Name == "run_frame");
        Assert.Equal(12.0, runFrame.TotalMs);
        Assert.Equal(3.0, runFrame.SelfMs);
        Assert.Equal(1, runFrame.Count);
        Assert.Equal(new[] { "unscoped", "call_execute", "run_frame", "held_tick_jobs" }, report.Slots.Select(slot => slot.Name));
    }

    [Fact]
    public void ANegativeHeapDeltaIsDroppedAndCounted()
    {
        ScriptedClock clock = new ScriptedClock();
        ProfileRecorder recorder = Recorder(clock);
        Frame(recorder, clock, 1, 1, heapDelta: 100);
        Frame(recorder, clock, 1, 2, heapDelta: -5000);
        Frame(recorder, clock, 1, 3, heapDelta: 300);

        ProfileSnapshot report = recorder.Report(true);

        Assert.Equal(1, report.HeapDeltasDropped);
        Assert.Equal(100.0, report.HeapDeltaBytes.P50);
        Assert.Equal(300.0, report.HeapDeltaBytes.Max);
    }

    [Fact]
    public void ResetClearsTheWindowButNotAnOpenTickHold()
    {
        ScriptedClock clock = new ScriptedClock();
        ProfileRecorder recorder = Recorder(clock);
        Frame(recorder, clock, 30, 1);
        recorder.TickHeld("upgrade-1", "upgrade_cables", 10);
        clock.Now += 40;
        recorder.TickReleased(12);
        recorder.TickHeld("upgrade-2", "upgrade_pipes", 20);
        recorder.Reset();
        clock.Now += 25;
        recorder.TickReleased(23);
        recorder.TickHeld("upgrade-2", null, 24);

        ProfileSnapshot report = recorder.Report(true);

        Assert.Equal(0, report.FramesRecorded);
        Assert.Null(report.Worst);
        Assert.Equal(1, report.TickHolds.Count);
        Assert.Equal(25.0, report.TickHolds.MaxMs);
        Assert.Equal(new[] { ("upgrade-2", "upgrade_pipes", true), ("upgrade-2", "upgrade_pipes", false) },
            report.TickHolds.Recent.Select(hold => (hold.Job, hold.Tool, hold.Holding)));
        Assert.Equal(3, report.TickHolds.Recent[1].Frames);
    }

    [Fact]
    public void ATimedMethodCountsOnlyItsOutermostRunOnTheMainThread()
    {
        Prof.MainThreadId = Environment.CurrentManagedThreadId;
        ScriptedClock clock = new ScriptedClock();
        ProfileRecorder recorder = Recorder(clock, methods: new[] { "ListDevicesApi.Handle" });
        Frame(recorder, clock, 10, 1, inside: () =>
        {
            long outer = recorder.MethodEnter(0);
            long inner = recorder.MethodEnter(0);
            clock.Now += 4;
            recorder.MethodExit(0, inner);
            recorder.MethodExit(0, outer);
        });
        long elsewhere = 0;
        System.Threading.Thread thread = new System.Threading.Thread(() => elsewhere = recorder.MethodEnter(0));
        thread.Start();
        thread.Join();

        SlotStat method = recorder.Report(true).Slots.Single(slot => slot.Kind == "method");

        Assert.Equal(0, elsewhere);
        Assert.Equal("ListDevicesApi.Handle", method.Name);
        Assert.Equal(1, method.Count);
        Assert.Equal(4.0, method.TotalMs);
    }

    [Fact]
    public void AScopeWithProfilingOffRecordsAndAllocatesNothing()
    {
        Prof.Stop();
        OffPath(10);
        long before = GC.GetAllocatedBytesForCurrentThread();
        OffPath(1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(default(ProfScope), Prof.Scope(ProfId.JobApply));
        Assert.Equal(0, Prof.BeginFrame());
    }

    private static void OffPath(int runs)
    {
        for (int run = 0; run < runs; run++)
        {
            using (Prof.Scope(ProfId.RunFrame))
            {
            }

            Prof.EndFrame(Prof.BeginFrame(), run);
            Prof.CallEnded("read_logic", Prof.CallStarted());
            Prof.TickHeld("job-1", "tool", run);
            Prof.TickReleased(run);
            Prof.CallTimes("read_logic", true, 1.0, 1.0, 1.0);
            Prof.MethodExit(0, Prof.MethodEnter(0));
        }
    }

    [Fact]
    public void SlowFramesWarnAtMostOncePerIntervalAndCountTheRest()
    {
        SlowFrameGate gate = new SlowFrameGate(10.0);

        Assert.True(gate.Allow(100.0, out int first));
        Assert.Equal(0, first);
        Assert.False(gate.Allow(101.0, out _));
        Assert.False(gate.Allow(109.9, out _));
        Assert.True(gate.Allow(110.0, out int passed));
        Assert.Equal(2, passed);
        Assert.Equal(0, gate.Suppressed);
    }

    [Fact]
    public void ASlowFrameWarningNamesItsTopThreeAndItsCalls()
    {
        List<string> warnings = new List<string>();
        Action<string>? sink = Prof.WarningSink;
        Prof.WarningSink = warnings.Add;
        try
        {
            ScriptedClock clock = new ScriptedClock();
            ProfileRecorder recorder = Recorder(clock, slowMs: 8);
            Frame(recorder, clock, 5, 1);
            Frame(recorder, clock, 12, 2, inside: () =>
            {
                long call = recorder.Now();
                Scope(recorder, clock, ProfId.CallExecute, 7);
                recorder.Call("grid_survey", call);
                Scope(recorder, clock, ProfId.JobStep, 3);
                Scope(recorder, clock, ProfId.PublishFacts, 1);
            });
            Frame(recorder, clock, 9, 3);

            Assert.Single(warnings);
            Assert.Equal(
                "Slow frame 2: StationGod took 12.00 ms on the main thread (slow_frame_ms 8.00); top: call_execute 7.00 ms, " +
                "job_step 3.00 ms, publish_facts 1.00 ms; calls: grid_survey 7.00 ms.", warnings[0]);
            Assert.Equal(2, recorder.Report(true).SlowFrames);
        }
        finally
        {
            Prof.WarningSink = sink;
        }
    }

    [Fact]
    public void TheCsvWritesItsRowsRotatesAtItsCapAndCountsDrops()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sg-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProfileCsvWriter writer = new ProfileCsvWriter(directory, "20260102-030405", capBytes: 400, keptFiles: 2,
                queueRows: 4);
            ProfileRow row = new ProfileRow(ProfileRowKind.Frame, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks,
                42, 1, 12.3456, 12.3456, null, 2, new NamedSlot("call_execute", 7), new NamedSlot("job,step", 3), default,
                new NamedSlot("grid_survey", 6.5));

            Assert.Equal(
                "frame,2026-01-02T03:04:05.000Z,42,1,12.346,12.346,,2,call_execute,7,?,3,,,grid_survey,6.5",
                writer.Format(in row));
            for (int index = 0; index < 6; index++)
            {
                writer.TryEnqueue(in row);
            }

            Assert.Equal(2, writer.Dropped);
            Assert.Equal(4, writer.Drain());
            for (int round = 0; round < 3; round++)
            {
                for (int index = 0; index < 4; index++)
                {
                    writer.TryEnqueue(in row);
                }

                writer.Drain();
            }

            writer.Close();

            string[] files = Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(name => name).ToArray()!;
            Assert.Equal(2, files.Length);
            Assert.All(files, name => Assert.StartsWith("profile-20260102-030405-", name));
            foreach (string file in Directory.GetFiles(directory))
            {
                string[] lines = File.ReadAllLines(file);
                Assert.Equal(ProfileCsvWriter.Header, lines[0]);
                Assert.True(new FileInfo(file).Length <= 400 + 120);
            }

            Assert.Equal(16, writer.Written);
            Assert.Null(writer.Failure);
            writer.Stop();
            Assert.False(writer.TryEnqueue(in row));
            Assert.Equal(2, writer.Dropped);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void ASecondSummaryRowGoesToTheCsvOncePerSecond()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sg-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScriptedClock clock = new ScriptedClock();
            ProfileRecorder recorder = Recorder(clock, slowMs: 8);
            ProfileCsvWriter writer = new ProfileCsvWriter(directory, "x");
            recorder.Csv = writer;
            for (int frame = 1; frame <= 600; frame++)
            {
                Frame(recorder, clock, frame == 50 ? 20 : 2, frame, inside: () => Scope(recorder, clock, ProfId.RunFrame, 1));
            }

            writer.Drain();
            writer.Close();

            string[] lines = File.ReadAllLines(writer.Path);
            Assert.Equal(ProfileCsvWriter.Header, lines[0]);
            Assert.Single(lines, line => line.StartsWith("frame,", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("second,", StringComparison.Ordinal) && line.Contains(",unscoped,"));
            Assert.Equal(recorder.Report(true).Csv!.Path, writer.Path);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void TheWorstFrameViewShowsItsCostliestPiecesAndSlowestCallsAndCountsTheRest()
    {
        ScriptedClock clock = new ScriptedClock();
        ProfileRecorder recorder = Recorder(clock);
        Frame(recorder, clock, 200, 1, inside: () =>
        {
            for (int id = 0; id < ProfIds.Count; id++)
            {
                Scope(recorder, clock, (ProfId)id, 1 + id % 3);
            }

            for (int call = 0; call < ProfileRecorder.CallsPerFrame + 5; call++)
            {
                long started = recorder.Now();
                clock.Now += call % 7;
                recorder.Call("read_logic", started);
            }
        });

        WorstFrameView view = new WorstFrameView(recorder.Report(true).Worst!);

        Assert.Equal(WorstFrameView.Shown, view.Scopes.Count);
        Assert.Equal(ProfIds.Count + 1 - WorstFrameView.Shown, view.ScopesNotListed);
        Assert.Equal(WorstFrameView.Shown, view.Calls.Count);
        Assert.Equal(ProfileRecorder.CallsPerFrame + 5 - WorstFrameView.Shown, view.CallsNotListed);
        Assert.Equal(6.0, view.Calls[0].Ms);
        Assert.Equal(view.Calls.Select(call => call.Ms).OrderByDescending(ms => ms), view.Calls.Select(call => call.Ms));
    }

    [Fact]
    public void ResetAfterOffDropsTheStoredReport()
    {
        ScriptedClock clock = new ScriptedClock();
        Prof.Start(Recorder(clock));
        Prof.Forget();
        Assert.NotNull(Prof.Recorder);

        Prof.Stop();
        Prof.Forget();

        Assert.Null(Prof.Recorder);
        Assert.False(Prof.Enabled);
    }

    [Fact]
    public void TheCsvNeverWritesOverAFileAndOutlivesALockedOldPart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sg-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string earlier = Path.Combine(directory, "profile-s.csv");
        File.WriteAllText(earlier, "an earlier session");
        try
        {
            ProfileCsvWriter writer = new ProfileCsvWriter(directory, "s", capBytes: 200, keptFiles: 1, queueRows: 8);
            ProfileRow row = new ProfileRow(ProfileRowKind.Second, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks,
                1, 60, 30, 2, 100, 3, default, default, default, default);
            writer.TryEnqueue(in row);
            writer.Drain();
            string first = writer.Path;
            Assert.EndsWith("profile-s-2.csv", first);
            Assert.Equal("an earlier session", File.ReadAllText(earlier));

            // The first part is closed at its cap; held open elsewhere, it cannot be deleted when the next part opens.
            writer.TryEnqueue(in row);
            writer.Drain();
            using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                for (int index = 0; index < 4; index++)
                {
                    writer.TryEnqueue(in row);
                }

                writer.Drain();
            }

            writer.Close();
            Assert.Null(writer.Failure);
            Assert.Equal(6, writer.Written);
            Assert.NotEqual(first, writer.Path);
            Assert.True(File.Exists(first));
            writer.Stop();
            writer.Stop();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AStartedCsvWriterWritesItsRowsAndEndsOnStop()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sg-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProfileCsvWriter writer = new ProfileCsvWriter(directory, "t");
            writer.Start();
            ProfileRow row = new ProfileRow(ProfileRowKind.Frame, DateTime.UtcNow.Ticks, 7, 1, 9, 9, null, 0, default,
                default, default, default);
            writer.TryEnqueue(in row);
            writer.Stop();
            writer.Stop();
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (writer.Written < 1 && DateTime.UtcNow < until)
            {
                System.Threading.Thread.Sleep(10);
            }

            Assert.Equal(1, writer.Written);
            Assert.False(writer.State().Writing);
        }
        finally
        {
            for (int attempt = 0; attempt < 50 && Directory.Exists(directory); attempt++)
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(20);
                }
            }
        }
    }

    [Theory]
    [InlineData("{\"action\": \"on\"}", "On", null, null)]
    [InlineData("{\"action\": \"ON\", \"slow_frame_ms\": 12.5, \"csv\": true}", "On", 12.5, true)]
    [InlineData("{\"action\": \"off\"}", "Off", null, null)]
    [InlineData("{\"action\": \" reset \"}", "Reset", null, null)]
    [InlineData("{\"action\": \"report\"}", "Report", null, null)]
    [InlineData("{\"action\": \"on\", \"slow_frame_ms\": 1}", "On", 1.0, null)]
    [InlineData("{\"action\": \"on\", \"slow_frame_ms\": 1000}", "On", 1000.0, null)]
    public void ProfilingTakesItsActions(string json, string action, double? slowFrameMs, bool? csv)
    {
        ProfilingRequest request = ProfilingRequest.Of(new Args(JObject.Parse(json)));

        Assert.Equal(action, request.Action.ToString());
        Assert.Equal(slowFrameMs, request.SlowFrameMs);
        Assert.Equal(csv, request.Csv);
    }

    [Theory]
    [InlineData("{}", "action")]
    [InlineData("{\"action\": \"start\"}", "action")]
    [InlineData("{\"action\": 1}", "action")]
    [InlineData("{\"action\": \"on\", \"slow_frame_ms\": 0.5}", "slow_frame_ms")]
    [InlineData("{\"action\": \"on\", \"slow_frame_ms\": 1000.5}", "slow_frame_ms")]
    [InlineData("{\"action\": \"on\", \"slow_frame_ms\": \"8\"}", "slow_frame_ms")]
    [InlineData("{\"action\": \"report\", \"csv\": true}", "csv")]
    [InlineData("{\"action\": \"off\", \"slow_frame_ms\": 8}", "slow_frame_ms")]
    public void ProfilingRefusesABadArgument(string json, string named)
    {
        ApiException refusal = Assert.Throws<ApiException>(() => ProfilingRequest.Of(new Args(JObject.Parse(json))));

        Assert.Equal(ApiErrors.InvalidArgumentCode, refusal.Code);
        Assert.Contains(named, refusal.Message);
    }

    [Fact]
    public void TheReplyCarriesTheStateAndTheReport()
    {
        ScriptedClock clock = new ScriptedClock();
        ProfileRecorder recorder = Recorder(clock);
        Frame(recorder, clock, 3, 1, inside: () => Scope(recorder, clock, ProfId.WorldStores, 1));

        ProfilingView view = new ProfilingView(recorder.Report(true), true, 19, new List<string>(), null);
        JObject json = JObject.Parse(ApiJson.WriteFresh(view));

        Assert.True((bool)json["enabled"]!);
        Assert.Equal(JTokenType.Null, json["csv_path"]!.Type);
        Assert.Equal(3.0, (double)json["frames"]!["stationgod_ms"]!["max"]!);
        Assert.Equal("world_stores", (string?)json["scopes"]![1]!["name"]);
        Assert.Equal(19, (int)json["timed_methods"]!);
        Assert.Null(new ProfilingView(null, false, 0, new List<string>(), null).Frames);
    }
}
