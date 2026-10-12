#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>undo script-N, the session-end order and the kill's timing, with the real worker.</summary>
[Collection(PythonCollection.Name)]
public sealed class RunEndAndUndoTests : IDisposable
{
    private const string ThreeWrites =
        "sg.write('logic', id='1', logic='On', value=1)\n" +
        "sg.write('logic', id='2', logic='On', value=1)\n" +
        "sg.write('logic', id='1', logic='On', value=2)";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sg-end-" + Guid.NewGuid().ToString("N"));

    // ---- undo ---------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task UndoReportsUntilConfirmThenRevertsNewestFirstToTheValuesBeforeTheRun()
    {
        await using RunBench bench = new();
        await bench.RunAsync(ThreeWrites, confirm: true);

        JsonNode report = Done(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1" }));
        JsonNode acted = Done(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));

        Assert.True((bool)report["report_only"]!);
        Assert.Equal(new[] { "1", "2" }, report["targets"]!.AsArray().Select(row => (string)row!["id"]!));
        Assert.Equal("[1, ?, 2 -> 0] [undo]", (string)report["targets"]![0]!["text"]!);
        Assert.Equal(new[] { "1", "2" }, acted["results"]!.AsArray().Select(row => (string)row!["id"]!));
        Assert.Equal(0, bench.World.Value("1", "On"));
        Assert.Equal(0, bench.World.Value("2", "On"));
        VerbReply.Failed again = Failed(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));
        Assert.Equal("already_undone", again.Code);
    }

    [PythonFact]
    public async Task ATargetChangedSinceTheRunIsLeftUnlessOverwriteIsAllowed()
    {
        await using RunBench bench = new();
        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)", confirm: true);
        bench.World.Set("1", "On", 5);

        JsonNode refused = Done(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));
        Assert.False((bool)refused["results"]![0]!["ok"]!);
        Assert.Contains("changed since the run", (string)refused["results"]![0]!["note"]!);
        Assert.Equal(5, bench.World.Value("1", "On"));

        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=6)", confirm: true);
        bench.World.Set("1", "On", 7);
        JsonNode allowed = Done(await bench.Host.UndoAsync(new JsonObject
        {
            ["id"] = "script-2",
            ["confirm"] = true,
            ["allow"] = new JsonArray("overwrite")
        }));
        Assert.True((bool)allowed["results"]![0]!["ok"]!);
        Assert.Equal(5, bench.World.Value("1", "On"));
    }

    [PythonFact]
    public async Task ARunningRunIsNotUndoneAndSaysHowToStopIt()
    {
        await using RunBench bench = new();
        await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.wait(30)", confirm: true, line: 2);

        VerbReply.Failed live = Failed(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));

        Assert.Equal("run_live", live.Code);
        Assert.Equal("stop it first: server {what: \"stop\", id: \"script-1\"}", live.Fix);
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
    }

    [PythonFact]
    public async Task UndoOfARunOfAnotherWorldExpires()
    {
        await using RunBench bench = new();
        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)", confirm: true);
        bench.World.ChangeWorld("world-2");

        VerbReply.Failed expired = Failed(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));

        Assert.Equal("undo_expired", expired.Code);
        Assert.StartsWith("world changed", expired.Message);
    }

    [PythonFact]
    public async Task PastTheMemoryBoundUndoExpiresNamingWhatItCanNoLongerRevert()
    {
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            UndoMemoryTargets = 1
        });
        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)", confirm: true);
        await bench.RunAsync("sg.write('logic', id='2', logic='On', value=1)", confirm: true);

        VerbReply.Failed expired = Failed(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));

        Assert.Equal("undo_expired", expired.Code);
        Assert.Contains("write logic x1 (1 On=1)", expired.Message);
    }

    [Fact]
    public async Task AnAdoptedRunIsUndoneFromItsJournalInTheSameWorld()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "5150-1000.jsonl"), string.Join("\n",
            """{"t":"hub","hub":"5150-1000","token":"hub-5555555555555555","world":"world-1","mcp":true}""",
            """{"t":"run","run":"script-2","first_line":"x","confirm":true,"world":"world-1"}""",
            """{"t":"call","n":1,"run":"script-2","verb":"write","what":"logic","targets":[{"id":"1","logic":"On","before":0,"new":1}],"undo":"mixed","world":"world-1"}""") + "\n");
        RunWorld world = new();
        world.Set("1", "On", 1);
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());
        host.StartAdoption(_folder, null, _ => new FakeTokenLink(reachable: false));

        JsonNode acted = Done(await host.UndoAsync(new JsonObject { ["id"] = "script-5150.2", ["confirm"] = true }));

        Assert.True((bool)acted["results"]![0]!["ok"]!);
        Assert.Equal(0, world.Value("1", "On"));
    }

    [PythonFact]
    public async Task ATargetUndoLeftStaysUndoableWithOverwrite()
    {
        await using RunBench bench = new();
        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.write('logic', id='2', logic='On', value=1)",
            confirm: true);
        bench.World.Set("1", "On", 5);

        JsonNode first = Done(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));
        JsonNode second = Done(await bench.Host.UndoAsync(new JsonObject
        {
            ["id"] = "script-1",
            ["confirm"] = true,
            ["allow"] = new JsonArray("overwrite")
        }));
        VerbReply.Failed third = Failed(await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }));

        Assert.Equal(new[] { "2", "1" }, first["results"]!.AsArray().Select(row => (string)row!["id"]!).OrderByDescending(id => id));
        Assert.Equal("1", (string)Assert.Single(second["results"]!.AsArray())!["id"]!);
        Assert.Equal(0, bench.World.Value("1", "On"));
        Assert.Equal(0, bench.World.Value("2", "On"));
        Assert.Equal("already_undone", third.Code);
    }

    // ---- session end ----------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task AtEofCleanupWritesGoThenSessionEndAndOtherWritesAreRefused()
    {
        // A long window, so the order does not depend on the runs beating a clock: session_end goes when they have ended.
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            CleanupWindow = TimeSpan.FromSeconds(20)
        });
        await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\ntry:\n    sg.wait(30)\nfinally:\n" +
                               "    sg.write('logic', id='1', logic='On', value=0)", confirm: true, line: 3);
        await bench.StartAsync("with sg.hold('7', 'On', 1):\n    sg.wait(30)", confirm: true, line: 2);
        await bench.StartAsync("try:\n    sg.wait(30)\nfinally:\n    sg.write('logic', id='9', logic='On', value=1)",
            confirm: true, line: 2);
        int atEof = bench.World.Log.Count;

        await bench.Host.EndSessionAsync();

        var log = bench.World.Log;
        int sessionEnd = log.IndexOf("session_end");
        Assert.True(sessionEnd > atEof);
        int writesAfterEof = log.Skip(atEof).Take(sessionEnd - atEof).Count(entry => entry == "write_logic_many");
        Assert.Equal(2, writesAfterEof);
        Assert.Equal(0, bench.World.Value("1", "On"));
        Assert.Equal(0, bench.World.Value("7", "On"));
        Assert.Equal(0, bench.World.Value("9", "On"));
        Assert.Equal(SessionPhase.Ended, bench.Host.Phase);
        Assert.False(bench.Host.Find("script-3")!.IsLive);
    }

    [PythonFact]
    public async Task AtEofSessionEndGoesWhenTheWindowEndsThoughACleanupIsStillWaiting()
    {
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            CleanupWindow = TimeSpan.FromMilliseconds(300)
        });
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task never = new TaskCompletionSource().Task;
        bench.World.Gate = call =>
        {
            if (call.Method != "write_logic_many" || !call.Parameters.GetRawText().Contains("\"value\":0"))
            {
                return null;
            }

            arrived.TrySetResult();
            return never;
        };
        await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\ntry:\n    sg.wait(30)\nfinally:\n" +
                               "    sg.write('logic', id='1', logic='On', value=0)", confirm: true, line: 3);

        Stopwatch eof = Stopwatch.StartNew();
        await bench.Host.EndSessionAsync();

        // The cleanup write never ends; session_end still goes once the window is over.
        Assert.True(eof.Elapsed >= TimeSpan.FromMilliseconds(300), "session_end went before the window ended");
        Assert.NotNull(bench.World.SessionEndedAt);
        Assert.True(arrived.Task.IsCompleted);
    }

    [PythonFact]
    public async Task ACleanupWriteStillWaitingAtSessionEndIsJournalledNotSent()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000b0", mcp: true, () => "world-1");
        await using RunBench bench = new(adjust: settings => JournalTests.WithJournal(settings, journal));
        // The cleanup write reaches the game and waits there until session_end cancels it.
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task never = new TaskCompletionSource().Task;
        bench.World.Gate = call =>
        {
            if (call.Method != "write_logic_many" || !call.Parameters.GetRawText().Contains("\"value\":0"))
            {
                return null;
            }

            arrived.TrySetResult();
            return never;
        };
        string id = await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\ntry:\n    sg.wait(30)\nfinally:\n" +
                                           "    sg.write('logic', id='1', logic='On', value=0)", confirm: true, line: 3);
        bench.Host.Stop(new JsonObject { ["id"] = id });
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await bench.Host.EndSessionAsync();
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!journal.Lines().Any(line => line.Contains("\"t\":\"not_sent\"")) && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.Contains(journal.Lines(), line => line.Contains("\"t\":\"not_sent\""));
    }

    // ---- kill timing ------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task ACallInFlightAtTheKillDelaysItUntilItReturns()
    {
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            KillAfter = TimeSpan.FromSeconds(1),
            KillLatest = TimeSpan.FromSeconds(8)
        });
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bench.World.Gate = call =>
        {
            if (call.Method != "read_logic_many")
            {
                return null;
            }

            arrived.TrySetResult();
            return release.Task;
        };

        await bench.RunAsync("try:\n    sg.read('logic', id='1', logic='On')\n    sg.wait(30)\nexcept BaseException:\n    pass\n" +
                             "while True:\n    pass", confirm: true, maxWait: 0);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        // Past KillAfter (1 s) with the read still held in flight: the run must still be alive.
        await Task.Delay(TimeSpan.FromSeconds(2));
        bool aliveWhileInFlight = bench.Host.Find("script-1")!.IsLive;
        release.TrySetResult();
        await EndedAsync(bench, "script-1");

        Assert.True(aliveWhileInFlight, "the kill came while a call was in flight, before KillLatest");
        Assert.Equal(RunState.Killed, bench.Host.Find("script-1")!.State);
    }

    [PythonFact]
    public async Task ACallInFlightNeverHoldsTheKillPastTheLatest()
    {
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            KillAfter = TimeSpan.FromSeconds(1),
            KillLatest = TimeSpan.FromSeconds(3)
        });
        // The read is held in flight for good: only KillLatest can end the run.
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task never = new TaskCompletionSource().Task;
        bench.World.Gate = call =>
        {
            if (call.Method != "read_logic_many")
            {
                return null;
            }

            arrived.TrySetResult();
            return never;
        };

        await bench.RunAsync("sg.read('logic', id='1', logic='On')", confirm: true, maxWait: 0);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Stopwatch since = Stopwatch.StartNew();
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await EndedAsync(bench, "script-1");

        Assert.Equal(RunState.Killed, bench.Host.Find("script-1")!.State);
        Assert.True(since.Elapsed >= TimeSpan.FromSeconds(3), "killed before KillLatest while a call was in flight");
    }

    [PythonFact]
    public async Task ARunThatSwallowsItsStopAndKeepsCallingIsKilledAtKillAfterNotAtTheLatest()
    {
        // KillAfter 1 s, KillLatest 30 s: a loop of cleanup writes always has a call in flight, so before the fix only
        // KillLatest ended it. From KillAfter its new calls are refused and the call in flight is the last.
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            KillAfter = TimeSpan.FromSeconds(1),
            KillLatest = TimeSpan.FromSeconds(30)
        });

        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)\nwhile True:\n    try:\n" +
                             "        sg.write('logic', id='1', logic='On', value=1)\n        sg.wait(0.05)\n" +
                             "    except BaseException:\n        pass", confirm: true, maxWait: 0);
        await RunBench.UntilAsync(() => bench.World.Count("write_logic_many") >= 3, "the loop's writes");
        Stopwatch since = Stopwatch.StartNew();
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await bench.EndedAsync("script-1");

        Assert.Equal(RunState.Killed, bench.Host.Find("script-1")!.State);
        Assert.True(since.Elapsed < TimeSpan.FromSeconds(20), $"the kill took {since.Elapsed.TotalSeconds} s");
    }

    private static JsonNode Done(VerbReply? reply) =>
        reply is VerbReply.Done { Reply: { } body }
            ? body
            : throw new Xunit.Sdk.XunitException("Expected a reply, got " + ((VerbReply.Failed?)reply)?.ToJson().ToJsonString());

    private static VerbReply.Failed Failed(VerbReply? reply) =>
        reply as VerbReply.Failed ?? throw new Xunit.Sdk.XunitException("Expected a failure, got " +
                                                                         ((VerbReply.Done?)reply)?.Reply?.ToJsonString());

    private static async Task EndedAsync(RunBench bench, string id)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (bench.Host.Find(id)!.IsLive)
        {
            Assert.True(DateTime.UtcNow < until, $"{id} did not end in time.");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
