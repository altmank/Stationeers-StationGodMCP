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

    // ---- session end ----------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task AtEofCleanupWritesGoThenSessionEndWithinTheWindowAndOtherWritesAreRefused()
    {
        await using RunBench bench = new();
        await bench.StartAsync("sg.write('logic', id='1', logic='On', value=1)\ntry:\n    sg.wait(30)\nfinally:\n" +
                               "    sg.write('logic', id='1', logic='On', value=0)", confirm: true, line: 3);
        await bench.StartAsync("with sg.hold('7', 'On', 1):\n    sg.wait(30)", confirm: true, line: 2);
        await bench.StartAsync("try:\n    sg.wait(30)\nfinally:\n    sg.write('logic', id='9', logic='On', value=1)",
            confirm: true, line: 2);

        Stopwatch eof = Stopwatch.StartNew();
        await bench.Host.EndSessionAsync();
        TimeSpan toSessionEnd = eof.Elapsed;

        Assert.True(toSessionEnd < TimeSpan.FromMilliseconds(350), $"session_end went {toSessionEnd.TotalMilliseconds} ms after EOF");
        var log = bench.World.Log;
        int sessionEnd = log.IndexOf("session_end");
        Assert.True(sessionEnd > 0);
        Assert.Equal(0, bench.World.Value("1", "On"));
        Assert.Equal(0, bench.World.Value("7", "On"));
        Assert.Equal(0, bench.World.Value("9", "On"));
        Assert.True(log.LastIndexOf("write_logic_many", sessionEnd) > 0);
        Assert.Equal(SessionPhase.Ended, bench.Host.Phase);
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
