#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.HandlerSelectorTests;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>The C2a review's findings, each held by the params sent (or the text written).</summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sgm-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void RadiusWithoutNearIsRefusedNotDropped()
    {
        FakeGameLink game = new();

        VerbReply.Failed deposit = game.Pipeline().Call("find", "container", """{"radius":10}""").Failed();
        VerbReply.Failed things = game.Pipeline().Call("find", "thing", """{"radius":10}""").Failed();

        Assert.Equal("radius: needs near: \"player\" here", Assert.Single(deposit.Problems()));
        Assert.Equal("radius: needs near", Assert.Single(things.Problems()));
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void ToPlayerIsThePlayersIdForASlotMoveAndLeftOutOfAWithdraw()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "player_vitals" => FakeGameLink.Ok("""{"reference_id":"88"}"""),
            "thing_health" when call.Params()["reference_id"] != null => FakeGameLink.Ok("""{"prefab_name":"StructureSDBSilo"}"""),
            _ => FakeGameLink.Ok("{}")
        };

        game.Pipeline().Call("move", "item", """{"id":"1","to":"player"}""").Done();
        game.Pipeline().Call("move", "item", """{"moves":[{"id":"2","to":"player"}]}""").Done();
        game.Pipeline().Call("move", "item", """{"source":"21","prefab":"ItemIronOre","quantity":3,"to":"player","confirm":true}""")
            .Done();

        Same("""{"reference_id":"1","to_id":"88","to_slot":"auto","dry_run":false}""", game.CallsTo("move_item")[0].Params());
        Same("""{"moves":[{"reference_id":"2","to_id":"88","to_slot":"auto"}],"dry_run":false}""",
            game.CallsTo("move_item")[1].Params());
        Same("""{"silo_id":"21","prefab_name":"ItemIronOre","quantity":3,"dry_run":false,"confirm":true}""",
            game.Single("silo_withdraw").Params());
    }

    [Fact]
    public void EveryMoveNamesItsSlotAndForceTakesOneItem()
    {
        Same("""{"moves":[{"reference_id":"1","to_id":"9","to_slot":"auto"},{"reference_id":"2","to_id":"9","to_slot":"auto"}],"dry_run":false}""",
            Sent("move", "item", """{"id":["1","2"],"to":"9"}""", "move_item").Params());
        Same("""{"moves":[{"reference_id":"1","to_id":"9","to_slot":"auto"}],"dry_run":false}""",
            Sent("move", "item", """{"moves":[{"id":"1","to":"9"}]}""", "move_item").Params());
        Same("""{"moves":[{"reference_id":"1","to_id":"9","to_slot":"auto"}],"dry_run":false}""",
            Sent("move", "item", """{"id":"1","to":"9"}""", "move_item", Face.Script, new ScriptedGate(new WriteDecision.Act()))
                .Params());
        Assert.Equal("force: takes one item (one id, no moves): each forced move is its own call", Assert.Single(
            new FakeGameLink().Pipeline().Call("move", "item", """{"id":["1","2"],"to":"9","slot":0,"force":true,"confirm":true}""")
                .Failed().Problems()));
    }

    [Fact]
    public void AWholePrefabNameIsNotWidenedToAPart()
    {
        FakeGameLink game = new();

        VerbReply.Failed failed = game.Pipeline().Call("find", "container", """{"prefab":"StructureLocker"}""").Failed();
        game.Pipeline().Call("find", "container", """{"prefab":"*Locker*"}""").Done();
        game.Pipeline().Call("survey", "network", """{"network":"5","prefab":"Vent"}""").Done();

        Assert.Equal("prefab: a whole name is not matched exactly here; give a part (nearest: *StructureLocker*)",
            failed.Problems()[0] + " (nearest: " + (string)failed.Data!["problems"]![0]!["nearest"]![0]! + ")");
        Same("""{"prefab_contains":"Locker"}""", game.Single("list_containers").Params());
        Same("""{"network_id":"5","prefab_contains":"Vent"}""", game.Single("connections").Params());
    }

    [Fact]
    public void UnknownWhatIfKeysAndHalfAReadSampleAreRefused()
    {
        VerbPipeline pipeline = new FakeGameLink().Pipeline();

        Assert.Equal("what_if.fuel_mols: not a what_if of plan flight",
            Assert.Single(pipeline.Call("plan", "flight", """{"id":"4","what_if":{"fuel_mols":1}}""").Failed().Problems()));
        Assert.Equal("logic: required with id (or reads)",
            Assert.Single(pipeline.Call("read", "sample", """{"id":"1"}""").Failed().Problems()));
    }

    [Fact]
    public void InspectControllersGoesToTheProtocolMethod()
    {
        FakeGameLink game = new();

        game.Pipeline().Call("inspect", "controllers", """{"id":["4","5"]}""").Done();

        Assert.Equal(new[] { """{"reference_id":"4"}""", """{"reference_id":"5"}""" },
            game.CallsTo("inspect_controllers").Select(call => call.Params().ToJsonString()));
    }

    [Fact]
    public void NoTextThePipelineWritesNamesAHandlerOrAHandlersOwnArgument()
    {
        Regex names = HandlerNames();
        List<string> texts = new();
        FakeGameLink game = new();
        game.Answer = call => new GameReply.Unreachable(
            $"The connection broke while {call.Method} was in flight; it may have reached the game.", maybeRan: true);
        foreach (object[] row in VerbExampleTests.Whats())
        {
            WhatEntry what = VerbExampleTests.WhatOf((string)row[0]);
            string? whatName = what.Name == "-" ? null : what.Name;
            JsonObject example = (JsonObject)(what.Example ?? new JsonObject()).DeepClone();
            example.Remove("code_file");
            foreach (VerbReply reply in new[]
                     {
                         game.Pipeline().Call(what.Verb, whatName, example.ToJsonString()),
                         game.Pipeline().Call(what.Verb, whatName, With(example, "bogus_option", 1)),
                         game.Pipeline().Call(what.Verb, whatName, With(example, "radius", 3)),
                         game.Pipeline().Call(what.Verb, whatName, With(example, "id", "r99"))
                     })
            {
                if (reply is VerbReply.Failed failed)
                {
                    texts.Add(failed.ToJson().ToJsonString());
                }
            }
        }

        Assert.NotEmpty(texts);
        foreach (string text in texts)
        {
            Assert.False(names.IsMatch(text), $"{names.Match(text).Value} in {text}");
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AnInternalErrorReachesTheAgentWithoutItsDetails()
    {
        McpAdapter adapter = new(new SidecarOptions(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(_folder),
            200 * 1024), new ThrowingLink());

        string line = (await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"what":"clock"}}}"""))!;

        Assert.Equal("Internal error in the MCP server; its log has the details.",
            (string)JsonNode.Parse(line)!["error"]!["message"]!);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheAdapterRecordsCallsItRefusesItself()
    {
        string path = Path.Combine(_folder, "rec.jsonl");
        SidecarOptions options = new(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(_folder), 200 * 1024, path);
        McpAdapter adapter = new(options, new FakeGameLink());

        await adapter.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"finde","arguments":{"x":1}}}""");
        await adapter.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find","arguments":{"what":"thing","output_file":"../x"}}}""");
        await adapter.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read","arguments":{"what":"clock"}}}""");
        await adapter.DisposeAsync();

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(new[] { "finde", "find", "read" }, lines.Select(line => (string)JsonNode.Parse(line)!["verb"]!));
        Assert.Equal("""["x"]""", JsonNode.Parse(lines[0])!["options"]!.ToJsonString());
    }

    [Fact]
    public void ARecordingThatCannotBeWrittenNeverFailsTheCall()
    {
        string path = Path.Combine(_folder, "rec.jsonl");
        CallRecorder recorder = new(path);
        recorder.Dispose();

        new FakeGameLink().Pipeline(recorder: recorder).Call("read", "clock", "{}").Done();
    }

    [Fact]
    public void EveryHelperEnvelopeIsWithinTheModsBoundsToo()
    {
        LegacyVerbMap map = new FakeGameLink().Pipeline().Map;
        foreach (VerbEntry verb in VerbCatalogue.Embedded.Verbs)
        {
            foreach (WhatEntry what in verb.WhatNames.Select(name => verb.Whats[name]))
            {
                foreach (string helper in new[] { "thing_health", "player_vitals", "read_logic_many", "place_structure",
                             "place_cables", "replace_walls", "upgrade_cables", "paste_blueprint" })
                {
                    JsonObject envelope = map.Rename.Build(what, helper);
                    Assert.True(envelope["args"]!.AsObject().Count + envelope["tools"]!.AsObject().Count <= RenameEnvelope.MostEntries,
                        $"{what.Key} {helper}");
                }
            }
        }
    }

    private static string With(JsonObject options, string name, JsonNode value)
    {
        JsonObject copy = (JsonObject)options.DeepClone();
        copy[name] = value.DeepClone();
        return copy.ToJsonString();
    }

    private static string With(JsonObject options, string name, int value) => With(options, name, JsonValue.Create(value));

    private static string With(JsonObject options, string name, string value) => With(options, name, JsonValue.Create(value));

    // Handler names, and handler arguments no verb option is named, from the generated LegacyVerbMap table.
    private static Regex HandlerNames()
    {
        JsonNode map = JsonNode.Parse(File.ReadAllText(Path.Combine(
            StationGodMCP.Tests.CatalogueChecks.CatalogueFiles.RepositoryRoot(), "generated", "verbs", "legacy-verb-map.json")))!;
        HashSet<string> options = new();
        foreach (VerbEntry verb in VerbCatalogue.Embedded.Verbs)
        {
            foreach (WhatEntry what in verb.WhatNames.Select(name => verb.Whats[name]))
            {
                options.UnionWith(what.OptionNames);
                options.Add(what.Name);
            }
        }

        options.UnionWith(VerbCatalogue.Shaping);
        HashSet<string> names = new();
        foreach (JsonNode? entry in map["entries"]!.AsArray())
        {
            string handler = (string)entry!["handler"]!;
            // One-word handlers (connections, label, weather) are ordinary words and what names too.
            if (!handler.StartsWith("hub:", StringComparison.Ordinal) && handler.Contains('_') && !options.Contains(handler))
            {
                names.Add(handler);
            }

            foreach (JsonNode? arg in entry["args"]!.AsArray())
            {
                string name = (string)arg!["arg"]!;
                if (name.Contains('_') && !options.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        return new Regex(@"(?<![A-Za-z0-9_])(" + string.Join("|", names.OrderByDescending(name => name.Length).Select(Regex.Escape)) +
                         @")(?![A-Za-z0-9_])");
    }

    private sealed class ThrowingLink : IGameLink
    {
        public string? WorldId => null;

        public event Action<string?>? WorldChanged
        {
            add { }
            remove { }
        }

        public long NewStream() => throw new NotSupportedException();

        public System.Threading.Tasks.Task<GameReply> CallAsync(GameCall call, System.Threading.CancellationToken cancellation) =>
            throw new InvalidOperationException("game_clock exploded in describe_device");

        public System.Threading.Tasks.Task EndStreamAsync(long stream) => throw new NotSupportedException();

        public System.Threading.Tasks.Task SessionEndAsync() => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task<GameReply> HoldRestoreAsync(long stream, System.Text.Json.JsonElement change,
            System.Threading.CancellationToken cancellation) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams,
            System.Threading.CancellationToken cancellation) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<System.Text.Json.JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout,
            System.Threading.CancellationToken cancellation) => throw new NotSupportedException();
    }
}
