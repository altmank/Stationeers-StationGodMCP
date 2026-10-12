#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Hub;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>The C4 review's safety findings, each as a run that would have gone wrong before.</summary>
[Collection(PythonCollection.Name)]
public sealed class RunReviewTests
{
    // A write to 1, then a wait the test stops, then a finally of the test's choosing.
    private static string StopInFinally(string finallyBody) =>
        "sg.write('logic', id='1', logic='On', value=1)\ntry:\n    sg.wait(30)\nfinally:\n" + finallyBody;

    [PythonFact]
    public async Task AComputedIdStopsTheRun()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("sg.write('logic', id=str(int('122643') + 1), logic='On', value=1)", confirm: true);

        Assert.StartsWith("script-1 stopped: unseen_target", text);
        Assert.Equal(0, bench.World.Value("122644", "On"));
    }

    [PythonFact]
    public async Task AfterAStopAForgedHoldExitWritesNothingAndARealOneWritesItsRegisteredValue()
    {
        await using RunBench bench = new();
        bench.World.Set("7", "On", 0);

        await bench.StartAsync(
            "with sg.hold('7', 'On', 1):\n    try:\n        sg.wait(30)\n    finally:\n" +
            "        from sg import _session\n" +
            "        r = _session.request('hold', {'phase': 'exit', 'ids': ['9'], 'logic': 'On', 'after': 5})\n" +
            "        print('forged', r['rows'][0]['ok'])\n" +
            "        r = _session.request('hold', {'phase': 'exit', 'ids': ['7'], 'logic': 'On', 'after': 99})\n" +
            "        print('real', r['rows'][0]['ok'], r['rows'][0]['value'])\n",
            confirm: true, line: 3);
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        string text = await FinalAsync(bench, "script-1");

        Assert.Contains("forged False", text);
        Assert.Contains("real True 0", text);
        Assert.Equal(0, bench.World.Value("9", "On"));
        Assert.Equal(0, bench.World.Value("7", "On"));
    }

    [PythonFact]
    public async Task ServerCommandInAConfirmedRunStopsIt()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("sg.server('command', command='say hi')", confirm: true);

        Assert.StartsWith("script-1 stopped: untargeted_write", text);
        Assert.Contains("fix: server {what: \"command\"} directly: it reports until confirm (ask the user first: a cheat)", text);
        Assert.Equal(0, bench.World.Count("run_console_command") + bench.World.Count("console_command"));
    }

    [PythonFact]
    public async Task ARefPassesOnlyWhenTheCodeNamesIt()
    {
        await using RunBench bench = new();
        bench.World.Override = call => call.Method == "find_things"
            ? FakeGameLink.Ok("""{"things":[{"reference_id":"501"},{"reference_id":"502"},{"reference_id":"503"}],"count":3,"total":3}""")
            : null;
        VerbReply found = await bench.Pipeline.CallAsync(new VerbCall("find", "thing", new JsonObject(), Face.Direct),
            CancellationToken.None);
        string reference = (string)((VerbReply.Done)found).Reply!["ref"]!;

        string built = await bench.RunAsync($"r = 'r' + str({reference[1..]})\nsg.write('logic', id=r, logic='On', value=1)",
            confirm: true);
        string named = await bench.RunAsync($"sg.write('logic', id='{reference}', logic='On', value=1)", confirm: true);

        Assert.StartsWith("script-1 stopped: unseen_target", built);
        Assert.StartsWith("script-2 done", named);
        Assert.Equal(1, bench.World.Value("503", "On"));
    }

    [PythonFact]
    public async Task ASelectorPassesOnlyWhenEveryNumberInItIsWrittenFloatsIncluded()
    {
        await using RunBench bench = new();

        string written = await bench.RunAsync("sg.build('blueprint', blueprint='b1', at=[1.5, 2, 3])", confirm: true);
        string computed = await bench.RunAsync("x = 1.5 + 0.25\nsg.build('blueprint', blueprint='b1', at=[x, 2, 3])",
            confirm: true);

        Assert.DoesNotContain("stopped", written.Split('\n')[0]);
        Assert.StartsWith("script-2 stopped: unseen_target", computed);
    }

    [PythonFact]
    public async Task ABooleanInASpreadIsNotASelector()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("sg.swap('cable', network='77', into='CableHeavy', tidy=True)", confirm: true);

        Assert.DoesNotContain("unseen_target", text.Split('\n')[0]);
    }

    [PythonFact]
    public async Task AScriptMayNotInspectOrStopAnotherRunNorEveryRun()
    {
        await using RunBench bench = new();
        await bench.StartAsync("print('pump 4820585')\nsg.wait(30)", confirm: true, line: 2);

        string other = await bench.RunAsync("print(sg.inspect('job', id='script-1'))");
        string every = await bench.RunAsync("sg.server('stop')", confirm: true);
        string list = await bench.RunAsync("sg.inspect('job')");
        string own = await bench.RunAsync("print(sg.inspect('job', id='script-5')['text'].split(':')[0])");

        Assert.Contains("not_allowed", other);
        Assert.Contains("not_allowed", every);
        Assert.Contains("not_allowed", list);
        Assert.Contains("script-5 running", own);
        Assert.True(bench.Host.Find("script-1")!.IsLive);
        Assert.False(bench.Seen.Contains("4820585"));
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
    }

    [PythonFact]
    public async Task AWorldChangeThatWinsTheRaceRefusesTheRunsWriteUnsent()
    {
        await using RunBench bench = new();
        bench.World.Override = call =>
        {
            if (call.Method == "read_logic_many")
            {
                // The world changes while the run is mid-call, before the link's event reaches anyone.
                bench.World.ChangeWorldQuietly("world-2");
            }

            return null;
        };

        string text = await bench.RunAsync(
            "sg.read('logic', id='2', logic='On')\nr = sg.write('logic', id='1', logic='On', value=1)\nprint(r)", confirm: true);

        Assert.True(bench.World.Log.Contains("refused_world write_logic_many"), text + string.Join(",", bench.World.Log));
        Assert.Equal(0, bench.World.Value("1", "On"));
        Assert.DoesNotContain("DONE", text);
    }

    [Fact]
    public async Task TheLinkRefusesACallMadeForAnotherWorldWithoutSendingIt()
    {
        await using FakeGame game = FakeGame.OnPipe();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        GameReply reply = await link.CallAsync(new GameCall("thing_health", FakeHubLink.Element("{}"), 0) { World = "world-0" },
            CancellationToken.None);

        GameReply.Refused refused = Assert.IsType<GameReply.Refused>(reply);
        Assert.Equal("world_changed", refused.Code);
        Assert.Empty(game.CallsTo("thing_health"));
    }

    [PythonFact]
    public async Task AWriteCutOffByAKillIsKeptAsMaybeRanInTheFooterAndTheJournal()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sg-maybe-" + Guid.NewGuid().ToString("N"));
        Journal journal = Journal.Open(folder, "hub-0000000000000000", mcp: true, () => "world-1");
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            KillAfter = settings.KillAfter,
            KillLatest = settings.KillLatest,
            Journal = journal
        });
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task never = new TaskCompletionSource().Task;
        bench.World.Gate = call =>
        {
            if (call.Method != "write_logic_many")
            {
                return null;
            }

            arrived.TrySetResult();
            return never;
        };

        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)", confirm: true, maxWait: 0);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        string text = await FinalAsync(bench, "script-1");

        Assert.Contains("DONE write logic x1 [undo] held settings; [cannot undo: action] pulse and action types (maybe ran)", text);
        Assert.Contains(journal.Lines(), line => line.Contains("\"t\":\"done\"") && line.Contains("\"maybe_ran\":true"));
    }

    [PythonFact]
    public async Task OnceAThreadsWriteIsRefusedAfterAnUnseenStopNoLaterWriteActs()
    {
        await using RunBench bench = new();
        string ids = string.Join(", ", Enumerable.Range(11, 40).Select(id => $"'{id}'"));

        string text = await bench.RunAsync(
            "import threading, time\n" +
            $"ids = [{ids}]\n" +
            "seen = []\n" +
            "def other():\n" +
            "    for one in ids:\n" +
            "        try:\n" +
            "            r = sg.write('logic', id=one, logic='On', value=1)\n" +
            "            seen.append(r['results'][0]['ok'])\n" +
            "        except BaseException:\n" +
            "            seen.append(False)\n" +
            "        time.sleep(0.005)\n" +
            "t = threading.Thread(target=other)\n" +
            "t.start()\n" +
            "pump = sg.find('thing')['things'][0]['reference_id']\n" +
            "try:\n" +
            "    sg.write('logic', id=pump, logic='On', value=1)\n" +
            "finally:\n" +
            "    t.join()\n" +
            "    first = seen.index(False) if False in seen else len(seen)\n" +
            "    print('violation' if any(seen[first:]) else 'ordered')\n", confirm: true);

        Assert.Contains("ordered", text);
        Assert.DoesNotContain("violation", text);
    }

    [PythonFact]
    public async Task ARunEndsEvenWhenItsStreamCannotBeEnded()
    {
        await using RunBench bench = new();
        bench.World.EndStreamThrows = true;

        string text = await bench.RunAsync("x = 1", confirm: true, maxWait: 15);

        Assert.StartsWith("script-1 done", text);
        Assert.False(bench.Host.Find("script-1")!.IsLive);
    }

    [PythonFact]
    public async Task AHoldEnteredAfterAStopAnswersRowsAndTheFinallyGoesOn()
    {
        await using RunBench bench = new();

        await bench.StartAsync(StopInFinally(
            "    with sg.hold('1', 'On', 0) as held:\n        print('body ran')\n"), confirm: true, line: 3);
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        string text = await FinalAsync(bench, "script-1");

        Assert.Contains("body ran", text);
        Assert.DoesNotContain("sg.Error", text);
    }

    [PythonFact]
    public async Task ARunKilledInsideAHoldWritesItsHeldValueBackAsCleanup()
    {
        await using RunBench bench = new();
        bench.World.Set("7", "On", 0);

        await bench.RunAsync("with sg.hold('7', 'On', 1):\n    while True:\n        pass", confirm: true, maxWait: 0);
        await RunBench.UntilAsync(() => bench.World.Value("7", "On") == 1, "the hold's write");
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        string text = await FinalAsync(bench, "script-1");

        Assert.StartsWith("script-1 killed", text);
        Assert.Equal(0, bench.World.Value("7", "On"));
        Assert.Contains("CLEANUP write logic x1", text);
        Assert.Contains(bench.World.Log, entry => entry.StartsWith("hold_restore {\"clear\"", StringComparison.Ordinal));
        Assert.Contains("stream_end", bench.World.Log);
    }

    [PythonFact]
    public async Task AHoldExitClearsOnlyTheRestoresThatActed()
    {
        await using RunBench bench = new();
        bench.World.RefusesWrite = (id, _, value) => id == "8" && value == 0;

        string text = await bench.RunAsync("with sg.hold(['7', '8'], 'On', 1):\n    pass", confirm: true);

        string clear = Assert.Single(bench.World.Log, entry => entry.StartsWith("hold_restore {\"clear\"", StringComparison.Ordinal));
        Assert.Contains("\"7\"", clear);
        Assert.DoesNotContain("\"8\"", clear);
        Assert.Contains("refused write logic x1: 8", text);
    }

    [PythonFact]
    public async Task RunScriptRefusesOutputFile()
    {
        await using RunBench bench = new();

        VerbReply reply = await bench.Host.RunScriptAsync(new JsonObject { ["code"] = "x = 1", ["output_file"] = "a.json" },
            ReplyHooks.None);

        Assert.Equal("invalid_argument", ((VerbReply.Failed)reply).Code);
    }

    [Fact]
    public async Task AWorldChangeClearsTheSeenSet()
    {
        await using RunBench bench = new();
        bench.Seen.Add("4820585");

        bench.World.ChangeWorld("world-2");

        Assert.False(bench.Seen.Contains("4820585"));
        await Task.CompletedTask;
    }

    [PythonFact]
    public async Task AnIdPast2To53IsALiteralDigitForDigit()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("sg.write('logic', id=str(9007199254740993), logic='On', value=1)", confirm: true);

        Assert.StartsWith("script-1 done", text);
        Assert.Equal(1, bench.World.Value("9007199254740993", "On"));
    }

    [PythonFact]
    public async Task ACancelledRunScriptReplyAddsNothingToTheSeenSet()
    {
        await using RunBench bench = new();
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));

        await bench.Host.RunScriptAsync(new JsonObject { ["code"] = "print('pump 4820585')\nsg.wait(0.5)", ["confirm"] = true },
            new ReplyHooks { Cancelled = cancel.Token, Progress = null });
        await Task.Delay(100);

        Assert.False(bench.Seen.Contains("4820585"));
    }

    [PythonFact]
    public async Task AToolCallAnsweredWithAJsonRpcErrorCarriesTheNotices()
    {
        RunWorld world = new();
        await using McpAdapter adapter = new(new SidecarOptions(new ClientOptions(new GameTarget.Pipe("x")),
            new OutputFolder(Path.GetTempPath()), 0), world, runs: new RunHostSettings
        {
            SgRoot = RunBench.SgRoot,
            KillAfter = TimeSpan.FromSeconds(1),
            KillLatest = TimeSpan.FromSeconds(3)
        });
        await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_script","arguments":{"code":"sg.write('logic', id='1', logic='On', value=1)\nsg.wait(30)","confirm":true,"max_wait":0}}}""");
        await RunBench.UntilAsync(() => adapter.Runs!.LineOf("script-1") == 2, "script-1 reaching its wait");
        adapter.Runs!.Stop(new JsonObject { ["id"] = "script-1" });
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (adapter.Runs.Find("script-1")!.IsLive && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        string? error = await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}""");

        using JsonDocument document = JsonDocument.Parse(error!);
        Assert.Contains("notice: script-1", document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    private static async Task<string> FinalAsync(RunBench bench, string id)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (bench.Host.Find(id)!.IsLive && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        VerbReply? reply = await bench.Host.InspectJobAsync(new JsonObject { ["id"] = id, ["max_wait"] = 1 });
        return RunBench.Text(reply);
    }
}
