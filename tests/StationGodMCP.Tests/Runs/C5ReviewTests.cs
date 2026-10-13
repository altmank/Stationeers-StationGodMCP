#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Hub;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>The C5 review's findings, each as a case that went wrong before.</summary>
[Collection(PythonCollection.Name)]
public sealed class C5ReviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sg-c5review-" + Guid.NewGuid().ToString("N"));

    // ---- X1 ------------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task RepliesKeepComingWhileARunWritesInALoopAndTheSessionStillEnds()
    {
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            CleanupWindow = TimeSpan.FromMilliseconds(300)
        });
        await bench.RunAsync("i = 0\nwhile True:\n    sg.write('logic', id='1', logic='On', value=i % 2)\n    i += 1",
            confirm: true, maxWait: 0);
        await RunBench.UntilAsync(() => bench.World.Count("write.logic") >= 5, "the loop's writes");

        Task notices = Task.Run(async () =>
        {
            for (int index = 0; index < 300; index++)
            {
                await bench.Host.NoticesAsync("read", "logic", null);
                await bench.Host.InspectJobAsync(new JsonObject());
                bench.Host.MarkSeen("script-1");
            }
        });
        await notices.WaitAsync(TimeSpan.FromSeconds(30));
        await bench.Host.EndSessionAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(bench.World.SessionEndedAt);
    }

    // ---- X2 ------------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task AWorldLoadedMidUndoRefusesTheRestAndTheUndoExpires()
    {
        await using RunBench bench = new();
        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)\nsg.write('logic', id='2', logic='On', value=1)",
            confirm: true);
        bench.World.Override = call =>
        {
            if (RunWorld.KeyOf(call) == "read.logic" && call.Stream != bench.Host.Find("script-1")!.Stream)
            {
                // The undo's first read: another save loads before the undo's write goes.
                bench.World.ChangeWorldQuietly("world-2");
            }

            return null;
        };

        VerbReply reply = (await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }))!;

        Assert.Equal("undo_expired", ((VerbReply.Failed)reply).Code);
        Assert.StartsWith("world changed", ((VerbReply.Failed)reply).Message);
        Assert.Equal(1, bench.World.Value("2", "On"));
        Assert.Equal(1, bench.World.Value("1", "On"));
        Assert.Contains(bench.World.Log, entry => entry == "refused_world write.logic");
    }

    // ---- X3 ------------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task UndoWaitsForAMaybeRanWriteToSettleThenRevertsWhatLanded()
    {
        await using RunBench bench = new();
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bench.World.Gate = call =>
        {
            if (RunWorld.KeyOf(call) != "write.logic")
            {
                return null;
            }

            arrived.TrySetResult();
            return release.Task;
        };
        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)", confirm: true, maxWait: 0);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await bench.EndedAsync("script-1");

        VerbReply early = (await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }))!;
        bench.World.Gate = null;
        release.TrySetResult();
        await RunBench.UntilAsync(() => bench.World.Value("1", "On") == 1, "the late write to land");
        VerbReply settled = (await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }))!;

        VerbReply.Failed waiting = Assert.IsType<VerbReply.Failed>(early);
        Assert.Equal("run_unsettled", waiting.Code);
        Assert.Contains("a write may still land: try again when script-1 settles", waiting.Message);
        Assert.IsType<VerbReply.Done>(settled);
        Assert.Equal(0, bench.World.Value("1", "On"));
    }

    // ---- X4 ------------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task TheHoldLineIsJournalledBeforeTheRegistrationAndAFailedOneIsCleared()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000c0", mcp: true, () => "world-1");
        await using RunBench bench = new(adjust: settings => JournalTests.WithJournal(settings, journal));
        List<string>? atRegistration = null;
        bench.World.OnHoldRestore = change =>
        {
            if (change.TryGetProperty("set", out _))
            {
                atRegistration ??= journal.Lines();
                return FakeGameLink.Refused("device_not_found", "no such device");
            }

            return null;
        };

        await bench.RunAsync("with sg.hold('7', 'On', 1):\n    pass", confirm: true);

        Assert.NotNull(atRegistration);
        Assert.Contains(atRegistration!, line => line.Contains("\"t\":\"hold\"") && line.Contains("\"id\":\"7\""));
        Assert.Contains(journal.Lines(), line => line.Contains("\"t\":\"hold_cleared\"") && line.Contains("\"id\":\"7\""));
        Assert.Equal(0, bench.World.Value("7", "On"));
    }

    [PythonFact]
    public async Task AHoldsWriteIsJournalledWithTheValueReadNotTheAfterGiven()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000c1", mcp: true, () => "world-other");
        await using RunBench bench = new(adjust: settings => JournalTests.WithJournal(settings, journal));

        await bench.RunAsync("with sg.hold('7', 'On', 1, after=3):\n    pass", confirm: true);

        JsonObject call = JournalTests.Line(journal.Lines().First(line => line.Contains("\"t\":\"call\"")));
        Assert.Equal(0, (double)call["targets"]![0]!["before"]!);
        // The call line carries the run's world, not whatever the link holds when the line is written.
        Assert.Equal("world-1", (string)call["world"]!);
    }

    [PythonFact]
    public async Task AStopWhileAHoldRegistersMeansTheHoldDoesNotStart()
    {
        await using RunBench bench = new();
        bench.World.OnHoldRestore = change =>
        {
            if (change.TryGetProperty("set", out _))
            {
                bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
            }

            return null;
        };

        await bench.RunAsync("with sg.hold('7', 'On', 1):\n    pass", confirm: true, maxWait: 0);
        await bench.EndedAsync("script-1");

        Assert.Equal(0, bench.World.Value("7", "On"));
        Assert.Contains(bench.World.Log, entry => entry.StartsWith("hold_restore {\"clear\"", StringComparison.Ordinal));
    }

    // ---- M1, M2, M5 and the adoption minors ----------------------------------------------------------------------------

    [Fact]
    public async Task TwoJournalsOfOneReusedPidNameTheirRunsApart()
    {
        Write("6000-111112222.jsonl", Hub("hub-6000000000000001") + Run1);
        Write("6000-333334444.jsonl", Hub("hub-6000000000000002") + Run1);
        RunWorld world = new();
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());

        host.StartAdoption(_folder, null, _ => new FakeTokenLink(reachable: false));
        await host.NoticesAsync("read", "logic", null);

        Assert.NotNull(host.Find("script-6000-12222.1"));
        Assert.NotNull(host.Find("script-6000-34444.1"));
    }

    [Fact]
    public async Task ATornLastLineIsSkippedCountedAndTheNextLineStartsAfreshOnItsOwnLine()
    {
        string path = Write("6100-1000.jsonl", Hub("hub-6100000000000001") + Run1 + """{"t":"call","n":2,"run":"scr""");
        RunWorld world = new();
        RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());

        host.StartAdoption(_folder, null, _ => new FakeTokenLink(reachable: true, restores: """{"applied":[],"skipped":[],"dropped":0}"""));
        List<string> first = await host.NoticesAsync("read", "logic", null);
        await host.DisposeAsync();

        Assert.Contains("1 torn journal lines skipped", Assert.Single(first));
        string[] lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        // Retired and gone, or still there with every line after the torn one readable.
        foreach (string line in lines.SkipWhile(line => !line.EndsWith("\"run\":\"scr", StringComparison.Ordinal)).Skip(1))
        {
            JsonDocument.Parse(line).Dispose();
        }
    }

    [Fact]
    public async Task ATokenLinkThatFailsAfterAResumeIsClosedEveryWay()
    {
        Write("6200-1000.jsonl", Hub("hub-6200000000000001") + HoldLine + Run1);
        RunWorld world = new();
        List<FakeTokenLink> links = [];
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(),
            new RunHostSettings { AdoptionReconnect = TimeSpan.FromMilliseconds(10) });

        host.StartAdoption(_folder, null, _ =>
        {
            FakeTokenLink link = new(reachable: links.Count == 0, resumed: true);
            links.Add(link);
            return link;
        });
        await host.NoticesAsync("read", "logic", null);

        Assert.Equal(2, links.Count);
        Assert.Equal(false, links[0].ClosedWithBye);
        Assert.Equal(false, links[1].ClosedWithBye);
    }

    [Fact]
    public async Task TheOldTokensRestoresAreReadForEveryJournalAtOnce()
    {
        for (int index = 0; index < 4; index++)
        {
            Write($"63{index}0-1000.jsonl", Hub($"hub-63{index}0000000000001") + HoldLine + Run1);
        }

        RunWorld world = new();
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());
        // Each connect waits until all four have begun: read one after another, none would get past the first.
        BarrierLinks links = new(4);

        host.StartAdoption(_folder, null, _ => links.Next());
        List<string> first = await host.NoticesAsync("read", "logic", null);

        Assert.Equal(4, first.Count);
        Assert.All(first, line => Assert.DoesNotContain("unknown", line));
    }

    [Fact]
    public async Task AJournalOfAnotherGameIsNotAdopted()
    {
        Write("6400-1000.jsonl",
            """{"t":"hub","hub":"6400-1000","token":"hub-6400000000000001","world":"world-1","mcp":true,"target":"pipe 'other'"}""" +
            "\n" + Run1);
        RunWorld world = new();
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());

        host.StartAdoption(_folder, null, _ => new FakeTokenLink(reachable: false), target: "pipe 'mine'");
        await host.NoticesAsync("read", "logic", null);

        Assert.Null(host.Find("script-6400.1"));
    }

    // ---- M3 ------------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task TwoLogicTypesOnOneIdKeepTheirOwnBeforeValues()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000c2", mcp: true, () => "world-1");
        await using RunBench bench = new(adjust: settings => JournalTests.WithJournal(settings, journal));
        bench.World.Set("1", "Lock", 5);

        await bench.RunAsync("sg.write('logic', writes=[{'id': '1', 'logic': 'On', 'value': 1}, " +
                             "{'id': '1', 'logic': 'Lock', 'value': 1}])", confirm: true);
        VerbReply undone = (await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true }))!;

        Assert.IsType<VerbReply.Done>(undone);
        Assert.Equal(0, bench.World.Value("1", "On"));
        Assert.Equal(5, bench.World.Value("1", "Lock"));
        JsonNode targets = JournalTests.Line(journal.Lines().First(line => line.Contains("\"t\":\"call\"")))["targets"]!;
        Assert.Equal(0, (double)targets[0]!["before"]!);
        Assert.Equal(5, (double)targets[1]!["before"]!);
    }

    // ---- M4 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoRunStartsWithoutAWorld()
    {
        await using RunBench bench = new();
        bench.World.ChangeWorldQuietly(null);

        VerbReply reply = await bench.Host.RunScriptAsync(new JsonObject { ["code"] = "x = 1" }, ReplyHooks.None);

        Assert.Equal("no_world", ((VerbReply.Failed)reply).Code);
        Assert.Equal("no world loaded: start the run when the game is in a world.", ((VerbReply.Failed)reply).Message);
    }

    // ---- M6 ------------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task AKeptStreamIsCountedAndWrittenBackAtTheNextRunsEnd()
    {
        // The run spins in Python, so only the kill (1 s after the stop) ends it.
        await using RunBench bench = new(adjust: settings => new RunHostSettings
        {
            SgRoot = settings.SgRoot,
            KillAfter = TimeSpan.FromSeconds(1),
            KillLatest = TimeSpan.FromSeconds(3)
        });
        bench.World.RefusesWrite = (id, _, value) => id == "7" && value == 0;
        await bench.RunAsync("with sg.hold('7', 'On', 1):\n    while True:\n        pass", confirm: true, maxWait: 0);
        await RunBench.UntilAsync(() => bench.World.Value("7", "On") == 1, "the hold's write");
        bench.Host.Stop(new JsonObject { ["id"] = "script-1" });
        await bench.EndedAsync("script-1");
        List<string> info = await bench.Host.NoticesAsync("server", "info", null);

        bench.World.RefusesWrite = null;
        await bench.RunAsync("x = 1", confirm: true);
        await RunBench.UntilAsync(() => bench.World.Value("7", "On") == 0, "the kept stream's write-back");

        Assert.Contains(info, line => line.StartsWith("kept streams: 1", StringComparison.Ordinal));
        await RunBench.UntilAsync(() => bench.World.Ended.Contains(bench.Host.Find("script-1")!.Stream), "the kept stream to end");
    }

    // ---- minors ----------------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task TwoUndosOfOneRunAtOnceOneWins()
    {
        await using RunBench bench = new();
        await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)", confirm: true);
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bench.World.Gate = call =>
        {
            if (RunWorld.KeyOf(call) != "read.logic")
            {
                return null;
            }

            arrived.TrySetResult();
            return release.Task;
        };

        Task<VerbReply?> first = bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true });
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        VerbReply? second = await bench.Host.UndoAsync(new JsonObject { ["id"] = "script-1", ["confirm"] = true });
        bench.World.Gate = null;
        release.TrySetResult();
        await first;

        Assert.Equal("undo_running", ((VerbReply.Failed)second!).Code);
        Assert.Equal(0, bench.World.Value("1", "On"));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private const string Run1 =
        """{"t":"run","run":"script-1","first_line":"x","confirm":true,"world":"world-1"}""" + "\n" +
        """{"t":"call","n":1,"run":"script-1","verb":"write","what":"logic","targets":[{"id":"1","logic":"On","before":0,"new":1}],"undo":"mixed","world":"world-1"}""" + "\n" +
        """{"t":"done","n":1,"ok":true,"after":[{"id":"1","logic":"On","value":1,"before":0}]}""" + "\n";

    private const string HoldLine = """{"t":"hold","run":"script-1","id":"7","logic":"On","after":0}""" + "\n";

    private static string Hub(string token) =>
        $$"""{"t":"hub","hub":"x","token":"{{token}}","world":"world-1","mcp":true}""" + "\n";

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
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

    /// <summary>Token links whose connects go on only once the given number of them have begun.</summary>
    private sealed class BarrierLinks(int count)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _begun;

        public ITokenLink Next() => new BarrierTokenLink(this);

        public async Task ArriveAsync(CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref _begun) >= count)
            {
                _all.TrySetResult();
            }

            await _all.Task.WaitAsync(cancellation);
        }
    }

    private sealed class BarrierTokenLink(BarrierLinks links) : ITokenLink
    {
        public JsonElement? Welcome { get; private set; }

        public async Task<bool> ConnectAsync(CancellationToken cancellation)
        {
            await links.ArriveAsync(cancellation);
            Welcome = FakeHubLink.Element("""{"type":"welcome","resume":{"resumed":false},"restores":{"applied":[],"skipped":[],"dropped":0}}""");
            return true;
        }

        public Task SessionEndAsync() => Task.CompletedTask;

        public ValueTask CloseAsync(bool bye) => ValueTask.CompletedTask;
    }
}
