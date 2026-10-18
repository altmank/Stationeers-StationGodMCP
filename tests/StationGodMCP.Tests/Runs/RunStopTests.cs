#nullable enable

using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Server;
using StationGodMCP.Tests.Hub;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>
/// Stops, kills, world changes, reply waits and notices, with the real worker. Each test meets the run at a known line
/// (RunBench.StartAsync) rather than racing a reply wait against the worker's start; time is measured only where a
/// bound is the point, and then only one-sided in the direction that cannot flake (a kill can not come early).
/// </summary>
[Collection(PythonCollection.Name)]
public sealed class RunStopTests
{
    // Writes 1 On=1, waits (line 3), and in its finally writes 1 back (written: acts) and 2 (never written: refused).
    private const string WriteWaitFinally =
        "sg.write('logic', id='1', logic='On', value=1)\n" +
        "try:\n    sg.wait(30)\nfinally:\n" +
        "    sg.write('logic', id='1', logic='On', value=0)\n" +
        "    r = sg.write('logic', id='2', logic='On', value=1)\n" +
        "    print('second', r['results'][0]['ok'])\n";

    // A run that swallows its stop and then spins in Python, making no sg call (the wait is line 2).
    private const string Swallows = "try:\n    sg.wait(30)\nexcept BaseException:\n    pass\nwhile True:\n    pass";

    [PythonFact]
    public async Task AfterAStopAFinallyWriteToAWrittenTargetActsAsCleanupAndAnUnwrittenOneAnswersAnErrorRow()
    {
        await using RunBench bench = new();

        string id = await bench.StartAsync(WriteWaitFinally, confirm: true, line: 3);
        string running = await TextAsync(bench, id);
        VerbReply? stopped = await bench.Host.StopAsync(new JsonObject { ["id"] = id });
        string text = await FinalAsync(bench, id);

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

        string id = await bench.StartAsync(WriteWaitFinally, confirm: true, line: 3);
        bench.World.ChangeWorld("world-2");
        string text = await FinalAsync(bench, id);

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

        string id = await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(30)", confirm: true, line: 2);
        await bench.Host.StopAsync(new JsonObject());
        await bench.EndedAsync(id);
        var first = await bench.Host.NoticesAsync("read", "logic", null);
        var second = await bench.Host.NoticesAsync("find", "thing", null);
        string inspected = await FinalAsync(bench, id);
        var after = await bench.Host.NoticesAsync("read", "logic", null);

        string notice = Assert.Single(first);
        Assert.Equal("notice: script-1 (sg.write('logic', id='1', logic='On', value=1)) stopped with something left " +
                     "(left: 1 On=1 (was 0)); inspect or undo it by id; if you did not start it, leave it", notice);
        Assert.Equal(first, second);
        Assert.Contains("left: 1 On=1 (was 0)", inspected);
        Assert.Empty(after);
    }

    [PythonFact]
    public async Task AStoppedRunsStickyNoticeRidesOnlyTheRepliesOfItsWorld()
    {
        await using RunBench bench = new();

        string id = await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(30)", confirm: true, line: 2);
        await bench.Host.StopAsync(new JsonObject());
        await bench.EndedAsync(id);
        bench.World.ChangeWorldQuietly("world-2");
        var elsewhere = await bench.Host.NoticesAsync("read", "logic", null);
        bench.World.ChangeWorldQuietly("world-1");
        var back = await bench.Host.NoticesAsync("read", "logic", null);

        Assert.Empty(elsewhere);
        Assert.StartsWith("notice: script-1 (", Assert.Single(back));
    }

    [PythonFact]
    public async Task ADoneNoticeComesOnceOnTheNextRunReply()
    {
        await using RunBench bench = new();

        string id = await bench.StartAsync("sg.wait(0.2)\nx = 1", confirm: true, line: 1);
        await bench.EndedAsync(id);
        var direct = await bench.Host.NoticesAsync("read", "logic", null);
        var first = await bench.Host.NoticesAsync("run_script", null, "script-2");
        var again = await bench.Host.NoticesAsync("inspect", "job", null);

        Assert.Empty(direct);
        Assert.Equal("notice: script-1 (sg.wait(0.2)) done; if you did not start it, leave it", Assert.Single(first));
        Assert.Empty(again);
    }

    [PythonFact]
    public async Task TheStopReplyListsWhatTheRunLeftChangedTheLineOfThatWriteAndItsUndo()
    {
        await using RunBench bench = new();

        string id = await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(30)", confirm: true, line: 2);
        JsonObject reply = (JsonObject)((VerbReply.Done)await bench.Host.StopAsync(new JsonObject { ["id"] = id })).Reply!;
        await bench.EndedAsync(id);

        Assert.Equal("script-1", (string)reply["id"]!);
        Assert.Equal("stopping", (string)reply["state"]!);
        Assert.Equal(2, (int)reply["line"]!);
        JsonObject left = (JsonObject)Assert.Single((JsonArray)reply["left"]!)!;
        Assert.Equal("1", (string)left["id"]!);
        Assert.Equal("On", (string)left["logic"]!);
        Assert.Equal(1.0, (double)left["value"]!);
        Assert.Equal(1, (int)left["line"]!);
        Assert.Empty((JsonArray)reply["in_flight"]!);
        Assert.Equal("undo script-1", (string)reply["undo"]!);
    }

    [PythonFact]
    public async Task StopWithAJobIdDropsAWaitingJobAndAStartedOneRunsToItsEnd()
    {
        await using RunBench bench = new();
        bench.World.Override = call => call.Method != "drop_job"
            ? null
            : call.Parameters.GetProperty("job_id").GetString() == "place-41"
                ? FakeGameLink.Ok("""{"job_id":"place-41","dropped":true}""")
                : FakeGameLink.Refused("job_not_waiting",
                    "Job place-42 has started; it cannot be dropped. Follow it with inspect job {id} to its end.");

        VerbReply dropped = await bench.Host.StopAsync(new JsonObject { ["id"] = "place-41" });
        VerbReply started = await bench.Host.StopAsync(new JsonObject { ["id"] = "place-42" });

        JsonObject reply = (JsonObject)((VerbReply.Done)dropped).Reply!;
        Assert.Equal("place-41", (string)reply["id"]!);
        Assert.Equal("dropped", (string)reply["state"]!);
        Assert.Empty((JsonArray)reply["left"]!);
        Assert.Null(reply["undo"]);
        Assert.Equal("job_not_waiting", Assert.IsType<VerbReply.Failed>(started).Code);
        Assert.Equal(2, bench.World.Count("drop_job"));
    }

    [PythonFact]
    public async Task StopWithNoIdStopsEveryLiveRun()
    {
        await using RunBench bench = new();

        string one = await bench.StartAsync("sg.wait(30)", confirm: true, line: 1);
        string two = await bench.StartAsync("sg.wait(30)", confirm: true, line: 1);
        JsonNode reply = ((VerbReply.Done)await bench.Host.StopAsync(new JsonObject())!).Reply!;
        string first = await FinalAsync(bench, one);
        string second = await FinalAsync(bench, two);

        Assert.Equal(2, (int)reply["count"]!);
        Assert.Equal("sg.wait(30)", (string)reply["runs"]![0]!["first_line"]!);
        Assert.StartsWith("script-1 stopped", first);
        Assert.StartsWith("script-2 stopped", second);
    }

    [PythonFact]
    public async Task ASwallowedStopEndsAtTheKillAfterTheStop()
    {
        // KillAfter 1 s, KillLatest 20 s: with no call in flight the kill comes at the first, far from the second.
        await using RunBench bench = new(adjust: Kill(after: 1, latest: 20));

        string id = await bench.StartAsync(Swallows, confirm: true, line: 2);
        Stopwatch since = Stopwatch.StartNew();
        await bench.Host.StopAsync(new JsonObject { ["id"] = id });
        await bench.EndedAsync(id);

        Assert.Equal(RunState.Killed, bench.Host.Find(id)!.State);
        Assert.True(since.Elapsed >= TimeSpan.FromSeconds(1), "killed before KillAfter");
        Assert.True(since.Elapsed < TimeSpan.FromSeconds(15), "the kill waited for KillLatest though nothing was in flight");
    }

    [PythonFact]
    public async Task ACallsInFlightHoldTheKillOffButNotPastTheLatest()
    {
        // A loop of short calls keeps one in flight most of the time; KillLatest (3 s) ends it whatever.
        await using RunBench bench = new(adjust: Kill(after: 1, latest: 3));

        string id = await bench.StartAsync("while True:\n    try:\n        sg.wait(0.01)\n    except BaseException:\n        pass",
            confirm: true, line: 3);
        Stopwatch since = Stopwatch.StartNew();
        await bench.Host.StopAsync(new JsonObject { ["id"] = id });
        await bench.EndedAsync(id);

        Assert.Equal(RunState.Killed, bench.Host.Find(id)!.State);
        Assert.True(since.Elapsed >= TimeSpan.FromSeconds(1), "killed before KillAfter");
    }

    [PythonFact]
    public async Task ASecondStopKillsAtOnce()
    {
        await using RunBench bench = new(adjust: Kill(after: 60, latest: 120));

        string id = await bench.StartAsync(Swallows, confirm: true, line: 2);
        await bench.Host.StopAsync(new JsonObject { ["id"] = id });
        await RunBench.UntilAsync(() => bench.Host.Find(id)!.StopCode != null, "the first stop");
        await bench.Host.StopAsync(new JsonObject { ["id"] = id });
        await bench.EndedAsync(id);

        // Without the second stop the run would live another minute (KillAfter 60 s).
        Assert.Equal(RunState.Killed, bench.Host.Find(id)!.State);
    }

    [PythonFact]
    public async Task AConfirmedRunAnswersRunningAtTheCeilingWhateverMaxWaitSays()
    {
        await using RunBench bench = new(adjust: Ceiling(1));
        Stopwatch since = Stopwatch.StartNew();

        string text = await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(30)", confirm: true,
            maxWait: 120);
        TimeSpan answered = since.Elapsed;
        // inspect job on a live confirmed run has the same ceiling.
        string inspected = await TextAsync(bench, "script-1", maxWait: 120);
        await bench.Host.StopAsync(new JsonObject { ["id"] = "script-1" });

        Assert.StartsWith("script-1 running:", text);
        Assert.True(answered >= TimeSpan.FromSeconds(1) && answered < TimeSpan.FromSeconds(25), answered.ToString());
        Assert.StartsWith("script-1 running:", inspected);
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

        // getattr hides the write from the analysis, so the run starts read-only; it then waits 30 s, so only the
        // ceiling can answer it.
        string text = await bench.RunAsync("getattr(sg, 'wr' + 'ite')('logic', id='1', logic='On', value=1)\nsg.wait(30)",
            confirm: true);
        await bench.Host.StopAsync(new JsonObject { ["id"] = "script-1" });

        Assert.StartsWith("script-1 running:", text);
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

    private static Func<RunHostSettings, RunHostSettings> Kill(int after, int latest) => settings => new RunHostSettings
    {
        SgRoot = settings.SgRoot,
        KillAfter = TimeSpan.FromSeconds(after),
        KillLatest = TimeSpan.FromSeconds(latest)
    };

    // inspect job's text now (a live run's running line, at once).
    private static async Task<string> TextAsync(RunBench bench, string id, double maxWait = 0) =>
        RunBench.Text(await bench.Host.InspectJobAsync(new JsonObject { ["id"] = id, ["max_wait"] = maxWait }));

    // The run's final text, once it has ended.
    private static async Task<string> FinalAsync(RunBench bench, string id)
    {
        await bench.EndedAsync(id);
        return await TextAsync(bench, id);
    }
}
