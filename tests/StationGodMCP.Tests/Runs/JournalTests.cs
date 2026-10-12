#nullable enable

using System;
using System.Collections.Generic;
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

/// <summary>The journal (hub.md, The journal): lines and their order, the lock, adoption, restores, retirement.</summary>
[Collection(PythonCollection.Name)]
public sealed class JournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sg-journal-" + Guid.NewGuid().ToString("N"));

    [PythonFact]
    public async Task ACallLineIsOnDiskBeforeTheWriteReachesTheGameAndTheLinesComeInOrder()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000aa", mcp: true, () => "world-1");
        await using RunBench bench = new(adjust: settings => WithJournal(settings, journal));
        List<string>? atWrite = null;
        bench.World.Override = call =>
        {
            if (call.Method == "write_logic_many")
            {
                atWrite ??= journal.Lines();
            }

            return null;
        };

        string text = await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1)", confirm: true);
        List<string> lines = journal.Lines();

        Assert.StartsWith("script-1 done", text);
        Assert.NotNull(atWrite);
        JsonObject call = Line(atWrite!.Last());
        Assert.Equal("call", (string)call["t"]!);
        Assert.Equal("script-1", (string)call["run"]!);
        Assert.Equal("world-1", (string)call["world"]!);
        Assert.Equal("mixed", (string)call["undo"]!);
        JsonNode target = call["targets"]![0]!;
        Assert.Equal("1", (string)target["id"]!);
        Assert.Equal("On", (string)target["logic"]!);
        Assert.Equal(0, (double)target["before"]!);
        Assert.Equal(1, (double)target["new"]!);
        Assert.Equal(new[] { "hub", "run", "call", "done", "end" }, lines.Select(line => (string)Line(line)["t"]!));
        Assert.Equal("hub-00000000000000aa", (string)Line(lines[0])["token"]!);
        Assert.Equal(1, (double)Line(lines[3])["after"]![0]!["value"]!);
    }

    [PythonFact]
    public async Task AHoldAndAWorldChangeAreJournalled()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000ab", mcp: true, () => "world-1");
        await using RunBench bench = new(adjust: settings => WithJournal(settings, journal));

        await bench.StartAsync("with sg.hold('7', 'On', 1):\n    sg.wait(30)", confirm: true, line: 2);
        bench.World.ChangeWorld("world-2");
        await EndedAsync(bench, "script-1");
        List<string> kinds = journal.Lines().Select(line => (string)Line(line)["t"]!).ToList();

        Assert.Contains("hold", kinds);
        JsonObject world = Line(journal.Lines().First(line => line.Contains("\"t\":\"world\"")));
        Assert.Equal("script-1", (string)world["holds_dropped"]![0]!);
        Assert.Contains(bench.World.Log, entry => entry == "hold_restore {\"clear_all\":true}");
    }

    [Fact]
    public void ALiveHubsJournalCannotBeTakenByAnother()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000ac", mcp: true, () => "world-1");

        AdoptedJournal? whileLive = AdoptedJournal.TryTake(journal.FilePath);
        journal.Dispose();
        AdoptedJournal? afterwards = AdoptedJournal.TryTake(journal.FilePath);

        Assert.Null(whileLive);
        Assert.NotNull(afterwards);
        afterwards!.Dispose();
        Assert.Empty(AdoptedJournal.TakeAll(_folder, journal.FilePath));
    }

    [Fact]
    public void AJournalThatCannotBeWrittenTurnsItselfOffAndSaysSoOnce()
    {
        string blocked = Path.Combine(_folder, "file");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(blocked, "a file where the folder should be");
        StringWriter log = new();

        Journal journal = Journal.Open(blocked, "hub-00000000000000ad", mcp: true, () => null, log);

        Assert.True(journal.IsOff);
        Assert.StartsWith("journal off: ", journal.TakeNotice());
        Assert.Null(journal.TakeNotice());
        Assert.Contains("the journal is off", log.ToString());
    }

    [Fact]
    public async Task AdoptedRunsAreNamedWithTheOldPidAndTheFirstReplyCarriesWhatTheyLeft()
    {
        string path = OldJournal("4242-1000", "hub-1111111111111111", end: true);
        RunWorld world = new();
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());

        host.StartAdoption(_folder, null, _ => new FakeTokenLink(reachable: false));
        List<string> first = await host.NoticesAsync("read", "logic", null);
        List<string> second = await host.NoticesAsync("read", "logic", null);
        VerbReply? listed = await host.InspectJobAsync(new JsonObject());

        string line = Assert.Single(first);
        Assert.StartsWith("notice: script-4242.3 (ff = '1') stopped in an earlier session; left: 1 On=1 (was 0)", line);
        Assert.Contains("undo it with undo {id: \"script-4242.3\"}", line);
        Assert.Empty(second);
        Assert.Equal("script-4242.3", (string)((VerbReply.Done)listed!).Reply!["runs"]![0]!["id"]!);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AResumedOldTokenIsEndedClosedWithoutByeAndReadAgainForItsRestores()
    {
        string path = OldJournal("4243-1000", "hub-2222222222222222", end: false);
        RunWorld world = new();
        List<FakeTokenLink> links = [];
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(),
            new RunHostSettings { AdoptionReconnect = TimeSpan.FromMilliseconds(10) });

        host.StartAdoption(_folder, null, token =>
        {
            Assert.Equal("hub-2222222222222222", token);
            FakeTokenLink link = new(reachable: true, resumed: links.Count == 0,
                restores: links.Count == 0 ? null : """{"applied":[{"reference_id":"7","logic_type":{"name":"On"},"value":0}],"skipped":[],"dropped":0}""");
            links.Add(link);
            return link;
        });
        List<string> first = await host.NoticesAsync("read", "logic", null);
        await host.DisposeAsync();

        Assert.Equal(2, links.Count);
        Assert.True(links[0].SessionEnded);
        Assert.Equal(false, links[0].ClosedWithBye);
        Assert.False(links[1].SessionEnded);
        Assert.Equal(true, links[1].ClosedWithBye);
        string line = Assert.Single(first);
        Assert.Contains("killed in an earlier session", line);
        Assert.Contains("held restores applied: 7 On=0", line);
        Assert.DoesNotContain("7 On=1", line);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AnOldTokenOutsideItsWindowGivesItsRestoresInTheFirstWelcome()
    {
        OldJournal("4244-1000", "hub-3333333333333333", end: false);
        RunWorld world = new();
        List<FakeTokenLink> links = [];
        await using RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());

        host.StartAdoption(_folder, null, _ =>
        {
            FakeTokenLink link = new(reachable: true, resumed: false,
                restores: """{"applied":[],"skipped":[{"reference_id":"7","logic_type":{"name":"On"},"reason":"changed"}],"dropped":0}""");
            links.Add(link);
            return link;
        });
        List<string> first = await host.NoticesAsync("read", "logic", null);

        FakeTokenLink only = Assert.Single(links);
        Assert.False(only.SessionEnded);
        Assert.Equal(true, only.ClosedWithBye);
        Assert.Contains("skipped: 7 On (changed)", Assert.Single(first));
    }

    [Fact]
    public async Task AdoptedJournalsRetireAfterTheFirstReplyAndRetiredOnesAreNotTakenAgain()
    {
        string path = OldJournal("4245-1000", "hub-4444444444444444", end: true);
        RunWorld world = new();
        RunHost host = new(world, new VerbPipeline(world), new SeenSet(), new RunHostSettings());
        host.StartAdoption(_folder, null, _ => new FakeTokenLink(reachable: false));
        await host.NoticesAsync("read", "logic", null);

        // Still held by this hub: read the lines once it lets go of it.
        await host.DisposeAsync();

        Assert.False(File.Exists(path));
    }

    [PythonFact]
    public async Task ThisHubsJournalRetiresAtTheEndWhenEveryRunWasReported()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000ae", mcp: true, () => "world-1");
        RunBench bench = new(adjust: settings => WithJournal(settings, journal));
        await bench.RunAsync("x = 1", confirm: true);

        await bench.DisposeAsync();

        Assert.False(File.Exists(journal.FilePath));
    }

    [PythonFact]
    public async Task ThisHubsJournalStaysWhenARunWasNotReported()
    {
        Journal journal = Journal.Open(_folder, "hub-00000000000000af", mcp: true, () => "world-1");
        RunBench bench = new(adjust: settings => WithJournal(settings, journal));
        await bench.RunAsync("sg.wait(1)", confirm: true, maxWait: 0.1);
        await EndedAsync(bench, "script-1");

        await bench.DisposeAsync();

        Assert.True(File.Exists(journal.FilePath));
        Assert.DoesNotContain("\"retired\"", File.ReadAllText(journal.FilePath));
    }

    internal static RunHostSettings WithJournal(RunHostSettings settings, IRunJournal journal) => new()
    {
        SgRoot = settings.SgRoot,
        KillAfter = settings.KillAfter,
        KillLatest = settings.KillLatest,
        Journal = journal
    };

    // An earlier hub's journal: script-3 wrote 1 On 0 -> 1 and held 7 On (after 0); ended stopped, or not ended at all.
    private string OldJournal(string name, string token, bool end)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name + ".jsonl");
        List<string> lines =
        [
            $$"""{"t":"hub","hub":"{{name}}","token":"{{token}}","world":"world-1","mcp":true}""",
            """{"t":"run","run":"script-3","first_line":"ff = '1'","confirm":true,"world":"world-1"}""",
            """{"t":"hold","run":"script-3","id":"7","logic":"On","after":0}""",
            """{"t":"call","n":1,"run":"script-3","verb":"write","what":"logic","targets":[{"id":"7","logic":"On","before":0,"new":1}],"undo":"mixed","world":"world-1"}""",
            """{"t":"done","n":1,"ok":true,"after":[{"id":"7","logic":"On","value":1,"before":0}]}""",
            """{"t":"call","n":2,"run":"script-3","verb":"write","what":"logic","targets":[{"id":"1","logic":"On","before":0,"new":1}],"undo":"mixed","world":"world-1"}""",
            """{"t":"done","n":2,"ok":true,"after":[{"id":"1","logic":"On","value":1,"before":0}]}"""
        ];
        if (end)
        {
            lines.Add("""{"t":"end","run":"script-3","state":"stopped","left":[{"id":"1","logic":"On","value":1,"before":0}]}""");
        }

        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    internal static JsonObject Line(string line) => JsonNode.Parse(line)!.AsObject();

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

/// <summary>A link with an old resume token, as adoption sees it: its welcome, and what it was asked to do.</summary>
internal sealed class FakeTokenLink(bool reachable, bool resumed = false, string? restores = null) : ITokenLink
{
    public bool SessionEnded { get; private set; }

    public bool? ClosedWithBye { get; private set; }

    public JsonElement? Welcome { get; private set; }

    public Task<bool> ConnectAsync(CancellationToken cancellation)
    {
        if (reachable)
        {
            Welcome = FakeHubLink.Element("{\"type\":\"welcome\",\"resume\":{\"resumed\":" + (resumed ? "true" : "false") + "}" +
                                          (restores != null ? ",\"restores\":" + restores : "") + "}");
        }

        return Task.FromResult(reachable);
    }

    public Task SessionEndAsync()
    {
        SessionEnded = true;
        return Task.CompletedTask;
    }

    public ValueTask CloseAsync(bool bye)
    {
        ClosedWithBye = bye;
        return ValueTask.CompletedTask;
    }
}
