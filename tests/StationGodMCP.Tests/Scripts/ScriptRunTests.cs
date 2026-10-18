#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Server;
using StationGodMCP.Tests.Runs;
using Xunit;

namespace StationGodMCP.Tests.Scripts;

/// <summary>
/// Saved scripts through the hub: run_script {name, args} (the arguments checked before the run, the current version
/// run), composition (a run imports a saved script and calls it, the whole call tree one run) and the four hub whats
/// served by the pipeline on the direct and the script faces (write, find, inspect, remove script). The real Python
/// worker runs the scripts.
/// </summary>
[Collection(PythonCollection.Name)]
public sealed class ScriptRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sg-run-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptStore _store;

    public ScriptRunTests()
    {
        _store = new ScriptStore(Path.Combine(_folder, "scripts"),
            new ScriptCode(ScriptCode.DefaultPython, RunBench.SgRoot));
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

    private async Task<VerbReply> SaveAsync(RunBench bench, string name, string code, string scope = "shared")
    {
        return await bench.Pipeline.CallAsync(new VerbCall("write", "script", new JsonObject
        {
            ["name"] = name,
            ["code"] = code,
            ["description"] = "a test script",
            ["scope"] = scope
        }, Face.Direct), CancellationToken.None);
    }

    private static JsonObject Done(VerbReply reply) =>
        Assert.IsType<VerbReply.Done>(reply).Reply!.AsObject();

    private static VerbReply.Failed Failed(VerbReply reply) =>
        Assert.IsType<VerbReply.Failed>(reply);

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    // ---- run_script {name, args} ------------------------------------------------------------------------------------

    [PythonFact]
    public async Task ASavedScriptRunsByNameWithItsArgumentsAndTheyCountAsWritten()
    {
        await using RunBench bench = new(scripts: _store);
        string code = "def main(target: str, level: int) -> None:\n" +
                      "    sg.write('logic', id=target, logic='On', value=level)\n" +
                      "    print('did', target, level)\n";
        JsonObject saved = Done(await SaveAsync(bench, "turn_on", code));
        Assert.Equal(1, saved["version"]!.GetValue<int>());

        VerbReply reply = await bench.Host.RunScriptAsync(
            new JsonObject { ["name"] = "turn_on", ["args"] = Args("""{"target": "7", "level": 1}"""), ["confirm"] = true },
            ReplyHooks.None);
        string text = RunBench.Text(reply);

        Assert.StartsWith("script-1 done", text);
        Assert.Contains("did 7 1", text);
        Assert.Equal(1, bench.World.Value("7", "On"));
    }

    [PythonFact]
    public async Task TheRunIsTheCurrentVersion()
    {
        await using RunBench bench = new(scripts: _store);
        await SaveAsync(bench, "say", "def main() -> None:\n    print('v1 here')\n");
        Assert.Contains("v1 here", RunBench.Text(await bench.Host.RunScriptAsync(
            new JsonObject { ["name"] = "say" }, ReplyHooks.None)));
        await SaveAsync(bench, "say", "def main() -> None:\n    print('v2 here')\n");

        VerbReply reply = await bench.Host.RunScriptAsync(new JsonObject { ["name"] = "say" }, ReplyHooks.None);

        Assert.Contains("v2 here", RunBench.Text(reply));
        Assert.DoesNotContain("v1 here", RunBench.Text(reply));
    }

    [PythonFact]
    public async Task ABadArgumentIsRefusedBeforeTheRunStarts()
    {
        await using RunBench bench = new(scripts: _store);
        await SaveAsync(bench, "turn_on", "def main(target: str, level: int = 1) -> None:\n    print(target, level)\n");

        VerbReply missing = await bench.Host.RunScriptAsync(
            new JsonObject { ["name"] = "turn_on", ["args"] = Args("""{"level": 2}""") }, ReplyHooks.None);
        VerbReply unknown = await bench.Host.RunScriptAsync(
            new JsonObject { ["name"] = "turn_on", ["args"] = Args("""{"target": "7", "levels": 2}""") }, ReplyHooks.None);
        VerbReply wrongType = await bench.Host.RunScriptAsync(
            new JsonObject { ["name"] = "turn_on", ["args"] = Args("""{"target": "7", "level": "two"}""") }, ReplyHooks.None);

        Assert.Equal("invalid_argument", Failed(missing).Code);
        Assert.Contains("target: required", Failed(missing).Message);
        Assert.Contains("levels: not a parameter", Failed(unknown).Message);
        Assert.Contains("level: must be a whole number", Failed(wrongType).Message);
        Assert.Null(bench.Host.Find("script-1"));
        Assert.Equal(0, bench.World.Count("verb"));
    }

    [PythonFact]
    public async Task BothRoutesOrNeitherAndArgsWithCodeAreRefused()
    {
        await using RunBench bench = new(scripts: _store);
        await SaveAsync(bench, "say", "def main() -> None:\n    print('hi')\n");

        VerbReply both = await bench.Host.RunScriptAsync(
            new JsonObject { ["code"] = "print(1)", ["name"] = "say" }, ReplyHooks.None);
        VerbReply neither = await bench.Host.RunScriptAsync(new JsonObject(), ReplyHooks.None);
        VerbReply argsWithCode = await bench.Host.RunScriptAsync(
            new JsonObject { ["code"] = "print(1)", ["args"] = Args("{}") }, ReplyHooks.None);

        Assert.Equal("invalid_argument", Failed(both).Code);
        Assert.Contains("not with code", Failed(both).Message);
        Assert.Equal("invalid_argument", Failed(neither).Code);
        Assert.Contains("exactly one of code or name", Failed(neither).Message);
        Assert.Equal("invalid_argument", Failed(argsWithCode).Code);
        Assert.Contains("args: with name only", Failed(argsWithCode).Message);
        Assert.Null(bench.Host.Find("script-1"));
    }

    [PythonFact]
    public async Task AnUnknownNameIsRefusedWithTheNearestAndAWorldScriptOnlyRunsInItsWorld()
    {
        await using RunBench bench = new(scripts: _store);
        await SaveAsync(bench, "greet", "def main() -> None:\n    print('hi')\n");
        await SaveAsync(bench, "mine", "def main() -> None:\n    print('mine')\n", scope: "world");

        VerbReply unknown = await bench.Host.RunScriptAsync(new JsonObject { ["name"] = "grete" }, ReplyHooks.None);
        Assert.Equal("script_not_found", Failed(unknown).Code);
        Assert.Contains("nearest: greet", Failed(unknown).Message);

        Assert.StartsWith("script-1 done", RunBench.Text(
            await bench.Host.RunScriptAsync(new JsonObject { ["name"] = "mine" }, ReplyHooks.None)));

        bench.World.ChangeWorldQuietly("world-2");
        VerbReply elsewhere = await bench.Host.RunScriptAsync(new JsonObject { ["name"] = "mine" }, ReplyHooks.None);
        Assert.Equal("script_world", Failed(elsewhere).Code);
        Assert.Null(bench.Host.Find("script-2"));
    }

    // ---- composition (B-7) ------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task AnImportedSavedScriptRunsInTheSameRunAndItsLiteralsCountAsWritten()
    {
        await using RunBench bench = new(scripts: _store);
        // The id is a literal of the saved script alone (and an id a shared script may not carry): were the loaded
        // script's analysis not merged into the run, the gate would stop the run on an unseen target.
        Done(await SaveAsync(bench, "switch_on", "def main(level: int = 1) -> int:\n" +
                                                "    sg.write('logic', id='7', logic='On', value=level)\n" +
                                                "    return level * 2\n", scope: "world"));

        string text = RunBench.Text(await bench.Host.RunScriptAsync(new JsonObject
        {
            ["code"] = "from sg.lib import switch_on\nsg.script('switch_on')(level=3)",
            ["confirm"] = true
        }, ReplyHooks.None));

        Assert.StartsWith("script-1 done", text);
        Assert.Contains("returned: 6", text);
        Assert.Equal(3, bench.World.Value("7", "On"));
    }

    [PythonFact]
    public async Task ALoadedScriptThatDoesNotWriteDoesNotMakeTheRunReadOnly()
    {
        // The run's own analysis says it writes, but its first write comes after a long wait. The loaded script's
        // analysis writes nothing: merged, the run stays a writing run, whose reply waits the confirmed ceiling
        // rather than the read-only run's limit.
        await using RunBench bench = new(settings: new RunHostSettings
        {
            SgRoot = RunBench.SgRoot,
            ConfirmedCeiling = TimeSpan.FromSeconds(0.5),
            KillAfter = TimeSpan.FromSeconds(30),
            KillLatest = TimeSpan.FromSeconds(60)
        }, scripts: _store);
        await SaveAsync(bench, "quiet", "def main() -> None:\n    print('quiet')\n");

        string text = await bench.RunAsync("from sg.lib import quiet\nsg.wait(3)\n" +
                                           "sg.write('logic', id='7', logic='On', value=1)\n", confirm: true,
            maxWait: 5);

        Assert.StartsWith("script-1 running:", text);
        await bench.EndedAsync("script-1");
        Assert.Equal(1, bench.World.Value("7", "On"));
    }

    [PythonFact]
    public async Task AReportRunOfALoadedWaitingLoopEndsItsPass()
    {
        await using RunBench bench = new(scripts: _store);
        // The waiting loop is the loaded script's own: its lines are its own file's, so the waits inside it are loop
        // waits and the report pass ends there rather than running the loop on to the client's limit. The script is a
        // world script: its id literal is one a shared script may not carry.
        Done(await SaveAsync(bench, "poll", "def main() -> None:\n" +
           "    sg.write('logic', id='1', logic='On', value=1)\n" +
           "    while True:\n" +
           "        sg.wait(1)\n", scope: "world"));

        string text = await bench.RunAsync("from sg.lib import poll\npoll.main()\n");

        Assert.StartsWith("script-1 REPORT\n", text);
        Assert.Contains("the pass ended at line 2", text);
        Assert.Contains("REPORTED write logic x1", text);
        Assert.Equal(0, bench.World.Count("write.logic"));
    }

    [PythonFact]
    public async Task ARunFromAHubWithoutAStoreCannotImportASavedScript()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync("from sg.lib import anything\n", confirm: true);

        Assert.StartsWith("script-1 failed", text);
        Assert.Contains("no script store here", text);
    }

    // ---- the four whats ---------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task FindInspectAndRemoveServeTheStoreAndTheScriptFaceTakesWrite()
    {
        await using RunBench bench = new(scripts: _store);
        JsonObject written = Done(await SaveAsync(bench, "greet",
            "def main(who: str) -> str:\n    return 'hi ' + who\n"));
        Assert.Equal("greet", (string)written["name"]!);
        Assert.Equal("shared", (string)written["scope"]!);
        Assert.Equal(1, written["version"]!.GetValue<int>());
        Assert.True(File.Exists((string)written["file"]!));

        JsonObject found = Done(await bench.Pipeline.CallAsync(
            new VerbCall("find", "script", new JsonObject { ["text"] = "gree" }, Face.Direct), CancellationToken.None));
        Assert.Equal(1, found["count"]!.GetValue<int>());
        JsonObject entry = found["scripts"]!.AsArray()[0]!.AsObject();
        Assert.Equal("greet", (string)entry["name"]!);
        Assert.Equal("a test script", (string)entry["description"]!);
        Assert.Equal(new[] { "who: str" }, entry["parameters"]!.AsArray().Select(node => (string)node!).ToArray());

        JsonObject inspected = Done(await bench.Pipeline.CallAsync(
            new VerbCall("inspect", "script", new JsonObject { ["name"] = "greet" }, Face.Direct), CancellationToken.None));
        Assert.Contains("def main(who: str)", (string)inspected["source"]!);
        Assert.Null(inspected["last_used"]);
        Assert.Equal(1, inspected["version"]!.GetValue<int>());

        // The script face takes write script too (a run may save one): the same store, the run's world.
        VerbReply fromScript = await bench.Pipeline.CallAsync(new VerbCall("write", "script", new JsonObject
        {
            ["name"] = "from_script",
            ["code"] = "def main() -> None:\n    print('hi')\n",
            ["description"] = "saved by a run",
            ["scope"] = "shared"
        }, Face.Script), CancellationToken.None);
        Assert.IsType<VerbReply.Done>(fromScript);

        JsonObject removed = Done(await bench.Pipeline.CallAsync(
            new VerbCall("remove", "script", new JsonObject { ["name"] = "greet" }, Face.Direct), CancellationToken.None));
        Assert.True(removed["removed"]!.GetValue<bool>());
        Assert.False(File.Exists((string)written["file"]!));
        Assert.DoesNotContain("greet", _store.Find(null, null).Select(script => script.Name));
    }

    [Fact]
    public async Task TheFourWhatsOnAFaceWithoutAStoreAreNotAvailable()
    {
        // A hub built without --scripts-dir (a plain VerbPipeline): the whats name the missing store.
        RunWorld world = new();
        VerbPipeline bare = new(world);

        VerbReply reply = await bare.CallAsync(
            new VerbCall("find", "script", new JsonObject(), Face.Direct), CancellationToken.None);

        Assert.Equal("not_available", Failed(reply).Code);
    }

    // ---- the MCP face -----------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task TheMcpFaceCarriesTheFourWhatsInTheToolListAndAnswersThem()
    {
        RunWorld world = new();
        SidecarOptions options = new(new StationGodMCP.Client.ClientOptions(new StationGodMCP.Client.GameTarget.Pipe("x")),
            new StationGodMCP.Client.OutputFolder(Path.GetTempPath()), 0)
        {
            ScriptsDirectory = Path.Combine(_folder, "scripts"),
            JournalDirectory = Path.Combine(_folder, "journal"),
            Python = ScriptCode.DefaultPython
        };
        await using McpAdapter adapter = new(options, world, runs: new RunHostSettings
        {
            SgRoot = RunBench.SgRoot,
            KillAfter = TimeSpan.FromSeconds(30),
            KillLatest = TimeSpan.FromSeconds(60)
        });

        using JsonDocument listed = JsonDocument.Parse((await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"""))!);
        JsonElement tools = listed.RootElement.GetProperty("result").GetProperty("tools");
        JsonElement write = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "write");
        Assert.Contains("script", write.GetProperty("inputSchema").GetProperty("properties").GetProperty("what")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

        string saved = (await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write","arguments":{"what":"script","name":"turn_on","code":"def main() -> None:\n    print('hi')\n","description":"d","scope":"shared"}}}"""))!;
        Assert.DoesNotContain("isError\":true", saved);
        Assert.Contains("turn_on", saved);

        string found = (await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"find","arguments":{"what":"script"}}}"""))!;
        Assert.Contains("turn_on", found);

        string ran = (await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_script","arguments":{"name":"turn_on","max_wait":20}}}"""))!;
        Assert.Contains("script-1 done", ran);
        Assert.Contains("hi", ran);
    }

    // ---- code_file, and a saved file edited where it lies -----------------------------------------------------------

    [PythonFact]
    public async Task WriteScriptReadsItsSourceFromCodeFile()
    {
        await using RunBench bench = new(scripts: _store);
        Directory.CreateDirectory(_folder);
        string source = Path.Combine(_folder, "greet_source.py");
        File.WriteAllText(source, "def main(who: str) -> str:\n    return 'hi ' + who\n");

        JsonObject saved = Done(await bench.Pipeline.CallAsync(new VerbCall("write", "script", new JsonObject
        {
            ["name"] = "greet_file",
            ["code_file"] = source,
            ["description"] = "greet, saved from a file",
            ["scope"] = "shared"
        }, Face.Direct), CancellationToken.None));
        VerbReply neither = await bench.Pipeline.CallAsync(new VerbCall("write", "script", new JsonObject
        {
            ["name"] = "greet_none",
            ["description"] = "no source",
            ["scope"] = "shared"
        }, Face.Direct), CancellationToken.None);

        Assert.Equal(new[] { "who: str" }, saved["parameters"]!.AsArray().Select(node => (string)node!).ToArray());
        Assert.EndsWith("return 'hi ' + who\n", File.ReadAllText((string)saved["file"]!));
        Assert.Contains("code_file", Failed(neither).Message);
    }

    [PythonFact]
    public async Task AScriptFileEditedOnDiskTakesItsNewParametersAtTheNextRun()
    {
        await using RunBench bench = new(scripts: _store);
        JsonObject saved = Done(await SaveAsync(bench, "greet_edit", "def main(who: str) -> None:\n    print('hi', who)\n"));
        string file = (string)saved["file"]!;
        string text = File.ReadAllText(file);
        File.WriteAllText(file, text.Replace("def main(who: str) -> None:\n    print('hi', who)\n",
            "def main(who: str, times: int = 1) -> None:\n    print('hi', who, times)\n"));

        string ran = RunBench.Text(await bench.Host.RunScriptAsync(
            new JsonObject { ["name"] = "greet_edit", ["args"] = Args("""{"who": "LU", "times": 3}""") }, ReplyHooks.None));

        Assert.True(ran.Contains("hi LU 3"), ran);
        SavedScript now = Assert.IsType<ScriptOutcome<SavedScript>.Done>(_store.Open("greet_edit", null)).Value;
        Assert.Equal(2, now.Version);
        Assert.Equal(new[] { "who", "times" }, now.Parameters.Select(parameter => parameter.Name).ToArray());
        Assert.Contains("# version: 2", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "greet_edit", "v2.py")));
    }
}
