#nullable enable

using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>Report and confirmed runs with the real worker: what acts, what is reported, what stops a run.</summary>
[Collection(PythonCollection.Name)]
public sealed class RunRouteTests
{
    private const string WriteOne = "sg.write('logic', id='122643', logic='Open', value=1)";

    [PythonFact]
    public async Task AReportRunListsItsRowsAndChangesNothing()
    {
        await using RunBench bench = new();
        bench.World.Set("122643", "Open", 0);

        string text = await bench.RunAsync(WriteOne);

        Assert.StartsWith("script-1 REPORT\n", text);
        Assert.Contains("limits: reads after a reported write show the old value", text);
        Assert.Contains("to act, send the same code again with confirm: true", text);
        Assert.Contains("REPORTED write logic x1", text);
        Assert.Contains("[122643, Device 122643, 0 -> 1]", text);
        Assert.Equal(0, bench.World.Count("write_logic_many") + bench.World.Count("write_logic"));
        Assert.Equal(0, bench.World.Value("122643", "Open"));
    }

    [PythonFact]
    public async Task ARunWithoutConfirmThatOnlyReadsIsDoneNotReport()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("for i in range(3):\n    sg.read('logic', id='1', logic='On')\n    sg.wait(0.01)");

        Assert.StartsWith("script-1 done\n", text);
        Assert.DoesNotContain("limits:", text);
        Assert.DoesNotContain("confirm: true", text);
    }

    [PythonFact]
    public async Task AConfirmedRunActsOnALiteralId()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync(WriteOne, confirm: true);

        Assert.StartsWith("script-1 done\n", text);
        Assert.Equal(1, bench.World.Value("122643", "Open"));
        Assert.Contains("DONE write logic x1", text);
        Assert.Contains("122643 Open=1", text);
        Assert.Contains("undo script-1 (classes: mixed)", text);
        RunRecord record = bench.Host.Find("script-1")!;
        WriteEntry write = Assert.Single(record.Writes);
        Assert.Equal(0, (double)write.Targets[0].Before!);
        Assert.Equal(1, (double)write.Targets[0].After!);
        Assert.Equal(WriteGroup.Done, write.Group);
        Assert.Equal(1, write.Line);
    }

    [PythonFact]
    public async Task ALiteralHeldInAVariableActs()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("ff = '122643'\nsg.write('logic', id=ff, logic='Open', value=1)", confirm: true);

        Assert.StartsWith("script-1 done\n", text);
        Assert.Equal(1, bench.World.Value("122643", "Open"));
    }

    [PythonFact]
    public async Task AnIntLiteralMatchesTheStringIdItWrites()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("sg.write('logic', id=str(122643), logic='Open', value=1)", confirm: true);

        Assert.StartsWith("script-1 done\n", text);
    }

    [PythonFact]
    public async Task AnIdFoundInsideTheRunStopsItAndTheSameCodeSentAgainActs()
    {
        await using RunBench bench = new();
        const string code = "pump = sg.find('thing', name='Pump')['things'][0]['reference_id']\n" +
                            "sg.write('logic', id=pump, logic='On', value=1)";

        string first = await bench.RunAsync(code, confirm: true);
        string second = await bench.RunAsync(code, confirm: true);

        Assert.StartsWith("script-1 stopped: unseen_target (line 2: write logic names targets not seen; nothing acted.)", first);
        Assert.Contains("these targets were refused (now seen):\n  [999, ?, ? -> 1]", first);
        Assert.Contains("fix: nothing acted: send it again with confirm: true", first);
        Assert.DoesNotContain("DONE", first);
        Assert.StartsWith("script-2 done\n", second);
        Assert.Equal(1, bench.World.Value("999", "On"));
    }

    [PythonFact]
    public async Task AStopAfterAnActNamesWhatActed()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync(WriteOne + "\n" +
                                           "pump = sg.find('thing')['things'][0]['reference_id']\n" +
                                           "sg.write('logic', id=pump, logic='On', value=1)", confirm: true);

        Assert.Contains("stopped: unseen_target", text);
        Assert.Contains("fix: these acted: [122643 logic Open]; undo script-1 first, then send it again", text);
        Assert.Equal(0, bench.World.Value("999", "On"));
    }

    [PythonFact]
    public async Task ARefusalCaughtByExceptExceptionOrBareExceptStillStopsAndLaterWritesAreRefused()
    {
        // Two benches: the first run's stop rows make 999 seen for the hub that showed them.
        await using RunBench bench = new();
        await using RunBench other = new();
        const string find = "pump = sg.find('thing')['things'][0]['reference_id']\n";

        string caughtAsException = await other.RunAsync(find +
            "try:\n    sg.write('logic', id=pump, logic='On', value=1)\nexcept Exception:\n    print('caught')\n" +
            "sg.write('logic', id='5', logic='On', value=1)", confirm: true);
        string caughtBare = await bench.RunAsync(find +
            "try:\n    sg.write('logic', id=pump, logic='On', value=1)\nexcept:\n    print('swallowed')\n" +
            "r = sg.write('logic', id='5', logic='On', value=1)\nprint(r['results'][0]['ok'])", confirm: true);

        Assert.StartsWith("script-1 stopped: unseen_target", caughtAsException);
        Assert.DoesNotContain("caught", caughtAsException);
        Assert.Equal(0, other.World.Value("5", "On"));
        Assert.StartsWith("script-1 stopped: unseen_target", caughtBare);
        Assert.Contains("swallowed\nFalse", caughtBare);
        Assert.Contains("refused write logic x1: 5 (this run was stopped;", caughtBare);
        Assert.Equal(0, bench.World.Value("5", "On"));
    }

    [PythonFact]
    public async Task ConfirmInsideTheCodeIsRefused()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("sg.write('logic', id='1', logic='On', value=1, confirm=True)", confirm: true);

        Assert.StartsWith("script-1 failed", text);
        Assert.Contains("confirm belongs to the run", text);
        Assert.Equal(0, bench.World.Value("1", "On"));
    }

    [PythonFact]
    public async Task APerIdWhatAnswersResultsForOneId()
    {
        await using RunBench bench = new();
        bench.World.Set("1", "On", 1);

        string text = await bench.RunAsync("r = sg.read('logic', id='1', logic='On')\nprint('results' in r, r['results'][0]['value'])");

        Assert.Contains("True 1", text);
    }

    [PythonFact]
    public async Task AReportPassEndsAtTheSecondLoopWaitAndItsFinallyIsReported()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync(
            "sg.write('logic', id='1', logic='On', value=1)\ntry:\n    while True:\n        sg.wait(1)\n" +
            "finally:\n    sg.write('logic', id='1', logic='On', value=0)");

        Assert.StartsWith("script-1 REPORT\n", text);
        Assert.Contains("the pass ended at line 4", text);
        Assert.Contains("REPORTED write logic x2", text);
        Assert.Equal(0, bench.World.Count("write_logic_many"));
    }

    [PythonFact]
    public async Task AWatchInAReportRunAfterAWriteReturnsAtOnce()
    {
        await using RunBench bench = new();

        // until never holds, so only the hub's instant answer makes the watch report met (a timeout reports not met).
        string text = await bench.RunAsync(
            "sg.write('logic', id='1', logic='On', value=1)\n" +
            "rows, met = sg.watch([{'id': '1', 'logic': 'On'}], lambda rows: False, 300)\nprint('met', met)");

        Assert.Contains("met True", text);
    }

    [PythonFact]
    public async Task AHoldRegistersBeforeItWritesAndItsExitRestoresAndClears()
    {
        await using RunBench bench = new();
        bench.World.Set("7", "On", 0);

        string text = await bench.RunAsync(
            "with sg.hold('7', 'On', 1) as held:\n    print('in', sg.read('logic', id='7', logic='On')['results'][0]['value'])\n" +
            "print('restored', held.rows[0]['restored'])", confirm: true);

        Assert.StartsWith("script-1 done\n", text);
        Assert.Contains("in 1", text);
        Assert.Contains("restored True", text);
        Assert.Equal(0, bench.World.Value("7", "On"));
        var log = bench.World.Log;
        int set = log.FindIndex(entry => entry.StartsWith("hold_restore {\"set\"", StringComparison.Ordinal));
        int firstWrite = log.IndexOf("write_logic_many");
        int restore = log.LastIndexOf("write_logic_many");
        int clear = log.FindIndex(entry => entry.StartsWith("hold_restore {\"clear\"", StringComparison.Ordinal));
        Assert.True(set >= 0 && set < firstWrite, string.Join(" | ", log));
        Assert.True(firstWrite < restore && restore < clear, string.Join(" | ", log));
        Assert.Contains("\"value\":0", log[set]);
        Assert.Contains("HELD write logic x2", text);
    }

    [PythonFact]
    public async Task AModMethodCalledStraightFromTheWorkerIsRefused()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync(
            "from sg import _session\n" +
            "r = _session.current_transport().call('write_logic', {'reference_id': '1', 'logic_type': 'On', 'value': 1})\n" +
            "print(r['error']['code'])", confirm: true);

        Assert.Contains("not_allowed", text);
        Assert.Equal(0, bench.World.Count("write_logic"));
        Assert.Equal(0, bench.World.Value("1", "On"));
    }

    [PythonFact]
    public async Task TheCostLineCarriesCallsPerSecondAndMsPerSecondFromStreamCosts()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("x = 1", confirm: true);

        Assert.Matches(@"cost script-1 \(x = 1\): 12 calls, [0-9.]+ calls/s, 0 writes, 48 game ms, [0-9.]+ ms/s, " +
                       @"2 frames over budget, tick held 0 ms, [0-9.]+ s wall$", text);
        var log = bench.World.Log;
        Assert.True(log.LastIndexOf("stream_costs") < log.IndexOf("stream_end"));
    }

    [PythonFact]
    public async Task AWorkerThatDiesMidRunIsKilled()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("import os\nos._exit(3)", confirm: true);

        Assert.StartsWith("script-1 killed", text);
    }

    [Fact]
    public async Task AWorkerThatCannotStartFailsTheRunSayingWhy()
    {
        await using RunBench bench = new(new RunHostSettings { Python = ["stationgod-no-such-python"], SgRoot = RunBench.SgRoot });

        string text = await bench.RunAsync("x = 1", confirm: true);

        Assert.StartsWith("script-1 failed\n", text);
        Assert.Contains("the script worker did not start: ", text);
    }

    [Fact]
    public void TheBootLineIsTheWorkersOwn()
    {
        string worker = System.IO.File.ReadAllText(System.IO.Path.Combine(RunBench.SgRoot, "sg", "worker.py"));

        Assert.Contains("BOOT = \"" + WorkerPool.Boot + "\"", worker);
    }

    [PythonFact]
    public async Task ASyntaxErrorEndsTheRunFailedWithItsText()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("x = (\n", confirm: true);

        Assert.StartsWith("script-1 failed\n", text);
        Assert.Contains("SyntaxError", text);
    }

    [PythonFact]
    public async Task AFinishedRunsPrintedIdsAreSeen()
    {
        await using RunBench bench = new();

        await bench.RunAsync("print('pump 4820585 at 1.5')");

        Assert.True(bench.Seen.Contains("4820585"));
        Assert.False(bench.Seen.Contains("1"));
    }
}
