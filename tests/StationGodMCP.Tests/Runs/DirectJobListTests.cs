#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Hub;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>
/// inspect job on the direct face (the MCP adapter's, with the runs served): a list of ids answers one D13 row per id
/// - a run's its text view, a mod id's the pipeline's answer for it, each failing alone - and one id keeps the single
/// {text} shape.
/// </summary>
[Collection(PythonCollection.Name)]
public sealed class DirectJobListTests
{
    private static SidecarOptions Options() =>
        new(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(System.IO.Path.GetTempPath()), 0);

    // A finished run (script-1), started and answered through the adapter itself.
    private static async Task<McpAdapter> RunningAsync(RunWorld world)
    {
        McpAdapter adapter = new(Options(), world, runs: new RunHostSettings { SgRoot = RunBench.SgRoot });
        string? reply = await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_script","arguments":{"code":"x = 1","confirm":true}}}""");
        Assert.StartsWith("script-1 done", Text(reply!));
        return adapter;
    }

    [PythonFact]
    public async Task ADirectJobListAnswersARunRowAndAModRow()
    {
        RunWorld world = new();
        await using McpAdapter adapter = await RunningAsync(world);
        world.Override = call => RunWorld.KeyOf(call) == "inspect.job"
            ? FakeGameLink.Ok("""{"job_id":"place-2","status":"applied"}""")
            : null;

        string? listed = await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"inspect","arguments":{"what":"job","id":["script-1","place-2"]}}}""");

        JsonElement rows = Structured(listed!);
        Assert.Equal(2, rows.GetProperty("count").GetInt32());
        Assert.Equal(2, rows.GetProperty("success_count").GetInt32());
        Assert.Equal(0, rows.GetProperty("error_count").GetInt32());
        JsonElement run = rows.GetProperty("results")[0];
        Assert.Equal("script-1", run.GetProperty("reference_id").GetString());
        Assert.True(run.GetProperty("ok").GetBoolean());
        Assert.StartsWith("script-1 ", run.GetProperty("payload").GetProperty("text").GetString());
        JsonElement mod = rows.GetProperty("results")[1];
        Assert.Equal("place-2", mod.GetProperty("reference_id").GetString());
        Assert.True(mod.GetProperty("ok").GetBoolean());
        Assert.Equal("place-2", mod.GetProperty("payload").GetProperty("job_id").GetString());
        Assert.Equal("applied", mod.GetProperty("payload").GetProperty("status").GetString());
    }

    [PythonFact]
    public async Task ADirectJobListLetsOneIdFailAlone()
    {
        RunWorld world = new();
        await using McpAdapter adapter = await RunningAsync(world);
        world.Override = call => RunWorld.KeyOf(call) == "inspect.job"
            ? FakeGameLink.Refused("job_not_found", "No job place-404 in this world.")
            : null;

        string? listed = await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"inspect","arguments":{"what":"job","id":["place-404","script-1"]}}}""");

        JsonElement rows = Structured(listed!);
        Assert.Equal(2, rows.GetProperty("count").GetInt32());
        Assert.Equal(1, rows.GetProperty("success_count").GetInt32());
        Assert.Equal(1, rows.GetProperty("error_count").GetInt32());
        JsonElement refused = rows.GetProperty("results")[0];
        Assert.Equal("place-404", refused.GetProperty("reference_id").GetString());
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("job_not_found", refused.GetProperty("error").GetProperty("code").GetString());
        JsonElement run = rows.GetProperty("results")[1];
        Assert.Equal("script-1", run.GetProperty("reference_id").GetString());
        Assert.True(run.GetProperty("ok").GetBoolean());
        Assert.StartsWith("script-1 ", run.GetProperty("payload").GetProperty("text").GetString());
    }

    [PythonFact]
    public async Task ADirectJobOfASingleRunIdKeepsTheSingleTextShape()
    {
        RunWorld world = new();
        await using McpAdapter adapter = await RunningAsync(world);

        string? answer = await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"inspect","arguments":{"what":"job","id":"script-1"}}}""");

        JsonElement single = Structured(answer!);
        Assert.StartsWith("script-1 ", single.GetProperty("text").GetString());
        Assert.Equal(new[] { "text" }, single.EnumerateObject().Select(property => property.Name));
    }

    [PythonFact]
    public async Task InspectJobOfARunTakesWholeAndOutputFile()
    {
        RunWorld world = new();
        await using McpAdapter adapter = new(Options(), world, runs: new RunHostSettings { SgRoot = RunBench.SgRoot });
        await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_script","arguments":{"code":"return 'v' * 10000","confirm":true}}}""");
        string file = "job-" + Guid.NewGuid().ToString("N")[..8] + ".json";

        string cut = Text((await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"inspect","arguments":{"what":"job","id":"script-1"}}}"""))!);
        string whole = Text((await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"inspect","arguments":{"what":"job","id":"script-1","whole":true}}}"""))!);
        JsonElement pointer = Structured((await adapter.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"inspect\",\"arguments\":" +
            "{\"what\":\"job\",\"id\":\"script-1\",\"output_file\":\"" + file + "\"}}}"))!);

        Assert.Contains("cut at 4 KB", cut);
        Assert.Contains(new string('v', 10000), whole);
        Assert.DoesNotContain("cut at 4 KB", whole);
        string path = pointer.GetProperty("output_file").GetString()!;
        using JsonDocument written = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        Assert.Equal(new string('v', 10000), written.RootElement.GetProperty("returned").GetString());
        Assert.Equal("done", written.RootElement.GetProperty("state").GetString());
        System.IO.File.Delete(path);
    }

    private static JsonElement Structured(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").GetProperty("structuredContent").Clone();
    }

    private static string Text(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}
