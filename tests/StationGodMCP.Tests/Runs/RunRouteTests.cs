#nullable enable

using System;
using System.Threading.Tasks;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>Report and confirmed runs with the real worker: what acts, what is reported, what stops a run.</summary>
public sealed class RunRouteTests
{
    [PythonFact]
    public async Task AReportRunListsItsRowsAndChangesNothing()
    {
        await using RunBench bench = new();
        bench.World.Set("122643", "Open", 0);

        string text = await bench.RunAsync("sg.write('logic', id='122643', logic='Open', value=1)");

        Assert.StartsWith("script-1 REPORT\n", text);
        Assert.Contains("limits: reads after a reported write show the old value", text);
        Assert.Contains("to act, send the same code again with confirm: true", text);
        Assert.Contains("REPORTED write logic x1", text);
        Assert.Contains("[122643, Device 122643, 0 -> 1]", text);
        Assert.Equal(0, bench.World.Count("write_logic_many") + bench.World.Count("write_logic"));
        Assert.Equal(0, bench.World.Value("122643", "Open"));
        Assert.Contains("cost script-1 (sg.write('logic', id='122643', logic='Open', value=1)): 12 calls", text);
    }

    [PythonFact]
    public async Task AConfirmedRunActsOnALiteralId()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("sg.write('logic', id='122643', logic='Open', value=1)", confirm: true);

        Assert.StartsWith("script-1 done\n", text);
        Assert.Equal(1, bench.World.Value("122643", "Open"));
        Assert.Contains("DONE write logic x1", text);
        Assert.Contains("122643 Open=1", text);
        Assert.Contains("undo script-1 (classes: mixed)", text);
    }
}
