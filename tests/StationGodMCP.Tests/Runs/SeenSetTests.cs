#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>The seen set's extraction rules (hub.md, The seen set), and the MCP adapter feeding it and serving runs.</summary>
public sealed class SeenSetTests
{
    [Fact]
    public void JsonIdsComeFromIdKeysOnly()
    {
        List<string> ids = SeenSet.IdsInJson("""
            {"id":"12","ids":["13",14],"network_id":"15","reference_ids":[16],"placed":["17"],"removed":[18],
             "created_ids":["19"],"targets":["20",{"id":"21","name":"Pump 22"}],"count":23,"name":"24",
             "things":[{"reference_id":"25","position":{"x":26}}],"other_id":"abc","big_id":"12345678901234567890",
             "neg_id":-5,"frac_id":2.5}
            """);

        Assert.Equal(new[] { "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "25" }, Sorted(ids));
    }

    [Fact]
    public void TextThatIsNotJsonGivesNoIds() => Assert.Empty(SeenSet.IdsInJson("script-1 done 4820585"));

    [Fact]
    public void PlainTextIdsAreFreeStandingDigitRuns()
    {
        List<string> ids = SeenSet.IdsInPlainText(
            "pump 4820585, [122643] x 12a b34 1.5 -7 7- 3.x id=99 (100) 12345678901234567890 '5'\n6");

        Assert.Equal(new[] { "4820585", "122643", "99", "100", "5", "6" }, ids);
    }

    [Fact]
    public void ShownTextIsAddedAndContainsMatchesStrings()
    {
        SeenSet seen = new();

        seen.Shown("""{"things":[{"reference_id":"77"}]}""");
        seen.ShownPlain("left 88.");

        Assert.True(seen.Contains("77"));
        Assert.False(seen.Contains("88"));
    }

    [PythonFact]
    public async Task TheAdapterServesRunScriptAsTextAndFeedsTheSeenSet()
    {
        RunWorld world = new();
        await using McpAdapter adapter = new(Options(), world, runs: new RunHostSettings { SgRoot = RunBench.SgRoot });

        string? found = await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find","arguments":{"what":"thing"}}}""");
        string? run = await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"run_script","arguments":{"code":"sg.write('logic', id='999', logic='On', value=1)","confirm":true}}}""");

        Assert.Contains("999", found);
        Assert.True(adapter.Seen.Contains("999"));
        string text = TextOf(run!);
        Assert.StartsWith("script-1 done\n", text);
        Assert.Equal(1, world.Value("999", "On"));
    }

    [PythonFact]
    public async Task ACancelledRunScriptRequestStopsItsRun()
    {
        RunWorld world = new();
        await using McpAdapter adapter = new(Options(), world, runs: new RunHostSettings { SgRoot = RunBench.SgRoot });

        Task<string?> pending = adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":"r9","method":"tools/call","params":{"name":"run_script","arguments":{"code":"sg.wait(30)","confirm":true}}}""");
        await Task.Delay(500);
        await adapter.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"r9"}}""");
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        RunRecord record = adapter.Runs!.Find("script-1")!;
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (record.IsLive && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.Equal(RunState.Stopped, record.State);
        Assert.Equal("the agent cancelled the run_script request", record.StopMessage);
    }

    [PythonFact]
    public async Task ALongRunScriptWaitSendsProgressWhenTheRequestAskedForIt()
    {
        RunWorld world = new();
        await using McpAdapter adapter = new(Options(), world, runs: new RunHostSettings
        {
            SgRoot = RunBench.SgRoot,
            ProgressEvery = TimeSpan.FromMilliseconds(300)
        });
        List<string> sent = new();
        adapter.Notify = line =>
        {
            lock (sent)
            {
                sent.Add(line);
            }

            return Task.CompletedTask;
        };

        await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_script","_meta":{"progressToken":"p1"},"arguments":{"code":"sg.wait(1.2)"}}}""");

        Assert.NotEmpty(sent);
        Assert.Contains("\"method\":\"notifications/progress\"", sent[0]);
        Assert.Contains("\"progressToken\":\"p1\"", sent[0]);
    }

    private static SidecarOptions Options() =>
        new(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(System.IO.Path.GetTempPath()), 0);

    private static string TextOf(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static List<string> Sorted(List<string> ids)
    {
        ids.Sort((left, right) => long.Parse(left).CompareTo(long.Parse(right)));
        return ids;
    }
}
