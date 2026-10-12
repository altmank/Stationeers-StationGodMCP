#nullable enable

using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>Stops, kills, world changes, reply waits and notices, with the real worker.</summary>
public sealed class RunStopTests
{
    // Writes 1 On=1, waits, and in its finally writes 1 back (written: acts) and 2 (never written: refused).
    private const string WriteWaitFinally =
        "sg.write('logic', id='1', logic='On', value=1)\n" +
        "try:\n    sg.wait(30)\nfinally:\n" +
        "    sg.write('logic', id='1', logic='On', value=0)\n" +
        "    r = sg.write('logic', id='2', logic='On', value=1)\n" +
        "    print('second', r['results'][0]['ok'])\n";

    [PythonFact]
    public async Task AfterAStopAFinallyWriteToAWrittenTargetActsAsCleanupAndAnUnwrittenOneAnswersAnErrorRow()
    {
        await using RunBench bench = new();

        string running = await bench.RunAsync(WriteWaitFinally, confirm: true, maxWait: 0.5);
        VerbReply? stopped = bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        string text = await FinalAsync(bench, "script-1");

        Assert.StartsWith("script-1 running: line 3", running);
        Assert.Contains("+1 write logic", running);
        Assert.Contains("follow it with inspect {what: \"job\", id: \"script-1\"}", running);
        Assert.Equal("stopping", (string)((VerbReply.Done)stopped!).Reply!["state"]!);
        Assert.StartsWith("script-1 stopped: stopped (stopped by server stop)", text);
        Assert.Contains("second False", text);
        Assert.Contains("CLEANUP write logic x1 [undo] held settings; [cannot undo: action] pulse and action types: 1 On=0", text);
        Assert.Contains("refused write logic x1: 2 (this run was stopped;", text);
        Assert.Equal(0, bench.World.Value("1", "On"));
        Assert.Equal(0, bench.World.Value("2", "On"));
        Assert.DoesNotContain("left:", text);
    }

    [PythonFact]
    public async Task AWorldChangeStopsTheRunAndRefusesItsFinallysWrites()
    {
        await using RunBench bench = new();

        await bench.RunAsync(WriteWaitFinally, confirm: true, maxWait: 0.5);
        bench.World.ChangeWorld("world-2");
        string text = await FinalAsync(bench, "script-1");

        Assert.StartsWith("script-1 stopped: world_changed", text);
        Assert.Equal(1, bench.World.Value("1", "On"));
        Assert.Contains("refused write logic x2", text);
        Assert.Contains(bench.World.Log, entry => entry == "hold_restore {\"clear_all\":true}");
        Assert.DoesNotContain("CLEANUP", text);
    }

    [PythonFact]
    public async Task AStoppedRunThatLeftSomethingIsAStickyNoticeUntilInspected()
    {
        await using RunBench bench = new();

        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(30)", confirm: true, maxWait: 0.5);
        bench.Host.Stop(new JsonObject());
        await EndedAsync(bench, "script-1");
        var first = await bench.Host.NoticesAsync("read", "logic", null);
        var second = await bench.Host.NoticesAsync("find", "thing", null);
        string inspected = await FinalAsync(bench, "script-1");
        var after = await bench.Host.NoticesAsync("read", "logic", null);

        string notice = Assert.Single(first);
        Assert.Equal("notice: script-1 (sg.write('logic', id='1', logic='On', value=1)) stopped with something left " +
                     "(left: 1 On=1 (was 0)); inspect or undo it by id; if you did not start it, leave it", notice);
        Assert.Equal(first, second);
        Assert.Contains("left: 1 On=1 (was 0)", inspected);
        Assert.Empty(after);
    }

    [PythonFact]
    public async Task ADoneNoticeComesOnceOnTheNextRunReply()
    {
        await using RunBench bench = new();

        await bench.RunAsync("sg.wait(1)\nx = 1", confirm: true, maxWait: 0.2);
        await EndedAsync(bench, "script-1");
        var direct = await bench.Host.NoticesAsync("read", "logic", null);
        var first = await bench.Host.NoticesAsync("run_script", null, "script-2");
        var again = await bench.Host.NoticesAsync("inspect", "job", null);

        Assert.Empty(direct);
        Assert.Equal("notice: script-1 (sg.wait(1)) done; if you did not start it, leave it", Assert.Single(first));
        Assert.Empty(again);
    }

    [PythonFact]
    public async Task StopWithNoIdStopsEveryLiveRun()
    {
        await using RunBench bench = new();

        await bench.RunAsync("sg.wait(30)", confirm: true, maxWait: 0.3);
        await bench.RunAsync("sg.wait(30)", confirm: true, maxWait: 0.3);
        JsonNode reply = ((VerbReply.Done)bench.Host.Stop(new JsonObject())!).Reply!;
        string first = await FinalAsync(bench, "script-1");
        string second = await FinalAsync(bench, "script-2");

        Assert.Equal(2, (int)reply["count"]!);
        Assert.Equal("sg.wait(30)", (string)reply["runs"]![0]!["first_line"]!);
        Assert.StartsWith("script-1 stopped", first);
        Assert.StartsWith("script-2 stopped", second);
    }

    [PythonFact]
    public async Task ASwallowedStopEndsAtTheKillAfterTheStop()
    {
        await using RunBench bench = new();

        await bench.RunAsync("try:\n    sg.wait(30)\nexcept BaseException:\n    pass\nwhile True:\n    pass",
            confirm: true, maxWait: 0.3);
        Stopwatch since = Stopwatch.StartNew();
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await EndedAsync(bench, "script-1");

        Assert.Equal(RunState.Killed, bench.Host.Find("script-1")!.State);
        Assert.InRange(since.Elapsed.TotalSeconds, 0.8, 2.9);
    }

    [PythonFact]
    public async Task ACallInFlightHoldsTheKillOffButNotPastTheLatest()
    {
        await using RunBench bench = new();

        await bench.RunAsync("while True:\n    try:\n        sg.wait(0.01)\n    except BaseException:\n        pass",
            confirm: true, maxWait: 0.3);
        Stopwatch since = Stopwatch.StartNew();
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await EndedAsync(bench, "script-1");

        Assert.Equal(RunState.Killed, bench.Host.Find("script-1")!.State);
        Assert.InRange(since.Elapsed.TotalSeconds, 0.8, 3.9);
    }

    [PythonFact]
    public async Task ASecondStopKillsAtOnce()
    {
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            KillAfter = TimeSpan.FromSeconds(20),
            KillLatest = TimeSpan.FromSeconds(30)
        });

        await bench.RunAsync("try:\n    sg.wait(30)\nexcept BaseException:\n    pass\nwhile True:\n    pass",
            confirm: true, maxWait: 0.3);
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await Task.Delay(300);
        Stopwatch since = Stopwatch.StartNew();
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await EndedAsync(bench, "script-1");

        Assert.Equal(RunState.Killed, bench.Host.Find("script-1")!.State);
        Assert.True(since.Elapsed < TimeSpan.FromSeconds(5));
    }

    [PythonFact]
    public async Task AConfirmedRunAnswersRunningAtTheCeilingWhateverMaxWaitSays()
    {
        await using RunBench bench = new(adjust: Ceiling(1));
        Stopwatch since = Stopwatch.StartNew();

        string text = await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(3)", confirm: true,
            maxWait: 120);

        Assert.StartsWith("script-1 running:", text);
        Assert.InRange(since.Elapsed.TotalSeconds, 0.9, 2.5);
        // inspect job on a live confirmed run has the same ceiling.
        Assert.StartsWith("script-1 running:", await FinalAsync(bench, "script-1"));
        await EndedAsync(bench, "script-1");
        Assert.StartsWith("script-1 done", await FinalAsync(bench, "script-1"));
    }

    [PythonFact]
    public async Task ACeilingOfZeroWaitsForTheRun()
    {
        await using RunBench bench = new(adjust: Ceiling(0));

        string text = await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(1.5)", confirm: true);

        Assert.StartsWith("script-1 done", text);
    }

    [PythonFact]
    public async Task AReadOnlyConfirmedRunWaitsPastTheCeiling()
    {
        await using RunBench bench = new(adjust: Ceiling(1));

        string text = await bench.RunAsync("sg.wait(1.8)\nsg.read('logic', id='1', logic='On')", confirm: true);

        Assert.StartsWith("script-1 done", text);
    }

    [PythonFact]
    public async Task TheFirstWriteMovesAReadOnlyRunToTheCeiling()
    {
        await using RunBench bench = new(adjust: Ceiling(1));
        Stopwatch since = Stopwatch.StartNew();

        // getattr hides the write from the analysis, so the run starts read-only.
        string text = await bench.RunAsync("getattr(sg, 'wr' + 'ite')('logic', id='1', logic='On', value=1)\nsg.wait(4)",
            confirm: true);

        Assert.StartsWith("script-1 running:", text);
        Assert.True(since.Elapsed < TimeSpan.FromSeconds(3.5));
        Assert.Equal(1, bench.World.Value("1", "On"));
    }

    [PythonFact]
    public async Task AReportRunWaitsForItsEnd()
    {
        await using RunBench bench = new(adjust: Ceiling(1));

        string text = await bench.RunAsync("sg.wait(1.5)\nsg.write('logic', id='1', logic='On', value=1)");

        Assert.StartsWith("script-1 REPORT", text);
    }

    [PythonFact]
    public async Task AFinishedRunsReturnedValueIsItsRef()
    {
        await using RunBench bench = new();
        await bench.RunAsync("print('hi')\n{'a': 1}");

        string text = await bench.RunAsync("r = sg.ref('script-1')\nprint(r['returned']['a'], r['printed'].strip())");

        Assert.Contains("1 hi", text);
    }

    private static Func<RunHostSettings, RunHostSettings> Ceiling(int seconds) => settings => new RunHostSettings
    {
        SgRoot = settings.SgRoot,
        KillAfter = settings.KillAfter,
        KillLatest = settings.KillLatest,
        ConfirmedCeiling = TimeSpan.FromSeconds(seconds)
    };

    private static async Task<string> FinalAsync(RunBench bench, string id)
    {
        VerbReply? reply = await bench.Host.InspectJobAsync(new JsonObject { ["id"] = id, ["max_wait"] = 15 });
        return RunBench.Text(reply);
    }

    private static async Task EndedAsync(RunBench bench, string id)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (bench.Host.Find(id)!.IsLive)
        {
            Assert.True(DateTime.UtcNow < until, $"{id} did not end in time.");
            await Task.Delay(20);
        }
    }
}
