#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// Refs and the cap on the direct face: the 12 KB boundary and the 4 KB cut, refs minted for lists, reports and cuts,
/// a ref as a target (and with a path), repeat (overrides, whole from the store), the cut and re-ask recording, and
/// the shown-text hook. Script and program replies are never cut and mint no ref.
/// </summary>
public sealed class RefsAndCapTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sgm-refs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // A find thing reply whose JSON is exactly bytes long (things padded through the last entry's name).
    private static string ReplyOfBytes(int bytes)
    {
        JsonArray things = [];
        for (int index = 0; index < 60; index++)
        {
            things.Add(new JsonObject { ["reference_id"] = (1000 + index).ToString(), ["prefab_name"] = "StructureLight" });
        }

        JsonObject reply = new() { ["things"] = things, ["has_more"] = false };
        int missing = bytes - ReplyCap.Bytes(reply);
        things[^1]!["prefab_name"] = "StructureLight" + new string('x', missing);
        Assert.Equal(bytes, ReplyCap.Bytes(reply));
        return reply.ToJsonString();
    }

    [Theory]
    [InlineData(ReplyCap.Threshold, false)]
    [InlineData(ReplyCap.Threshold + 1, true)]
    public void ARepliesOver12KbIsCutToAbout4Kb(int bytes, bool cut)
    {
        FakeGameLink game = new();
        string answer = ReplyOfBytes(bytes);
        game.Answer = _ => FakeGameLink.Ok(answer);

        JsonNode reply = game.Pipeline().Call("find", "thing", "{}").Done()!;

        Assert.Equal(cut, reply["cut"] != null);
        Assert.StartsWith("r", (string)reply["ref"]!);
        if (cut)
        {
            Assert.True(ReplyCap.Bytes(reply) <= ReplyCap.Target);
            int shown = reply["things"]!.AsArray().Count;
            Same($$"""{"shown":{{shown}},"total":60}""", reply["cut"]);
            Assert.Equal($"whole: repeat '{reply["ref"]}' with whole: true | page: repeat '{reply["ref"]}' with offset: {shown}" +
                         $" | narrower: add a filter | as a target, {reply["ref"]} is the shown entries", (string)reply["next"]!);
        }
    }

    [Fact]
    public void WholeAListLimitOrAReportIsNeverCut()
    {
        FakeGameLink game = new();
        string answer = ReplyOfBytes(20000);
        game.Answer = _ => FakeGameLink.Ok(answer);

        Assert.Null(game.Pipeline().Call("find", "thing", """{"whole":true}""").Done()!["cut"]);
        Assert.Null(game.Pipeline().Call("find", "thing", """{"list_limits":{"things":100}}""").Done()!["cut"]);

        // A report over 12 KB (a hub report of 300 logic writes) is answered whole.
        string ids = string.Join(",", Enumerable.Range(100000, 300).Select(n => $"\"{n}\""));
        JsonNode report = new FakeGameLink().Pipeline()
            .Call("write", "logic", $$"""{"id":[{{ids}}],"logic":"On","value":1,"confirm":false}""").Done()!;
        Assert.True(ReplyCap.Bytes(report) > ReplyCap.Threshold);
        Assert.Null(report["cut"]);
        Assert.Equal(300, report["targets"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(Face.Script)]
    [InlineData(Face.Program)]
    public void ScriptAndProgramRepliesAreNeverCutAndMintNoRef(Face face)
    {
        FakeGameLink game = new();
        string answer = ReplyOfBytes(20000);
        game.Answer = _ => FakeGameLink.Ok(answer);

        JsonNode reply = game.Pipeline().Call("find", "thing", """{"limit":60}""", face).Done()!;

        Assert.Null(reply["cut"]);
        Assert.Null(reply["ref"]);
    }

    [Fact]
    public void AShortListMintsNoRefAndThreeIdsDo()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"},{"reference_id":"2"}],"has_more":false}""");
        Assert.Null(game.Pipeline().Call("find", "thing", "{}").Done()!["ref"]);

        game.Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}]}""");
        Assert.Equal("r1", (string)game.Pipeline().Call("find", "thing", "{}").Done()!["ref"]!);
    }

    [Fact]
    public void ARefAsATargetIsItsShownIdsAndAPathWalksIntoThem()
    {
        FakeGameLink game = new();
        string big = ReplyOfBytes(20000);
        game.Answer = call => call.Method == "find" ? FakeGameLink.Ok(big) : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline();

        JsonNode cut = pipeline.Call("find", "thing", "{}").Done()!;
        string reference = (string)cut["ref"]!;
        int shown = cut["things"]!.AsArray().Count;
        pipeline.Call("remove", "cable", $$"""{"id":"{{reference}}"}""").Done();
        pipeline.Call("inspect", "device", $$"""{"id":"{{reference}}.things.1"}""").Done();

        JsonArray removed = game.Single("remove").Params()["id"]!.AsArray();
        Assert.Equal(shown, removed.Count);
        Assert.Equal("1000", (string)removed[0]!);
        Same("""{"what":"device","id":["1001"]}""", game.Single("inspect").Params());
    }

    [Fact]
    public void AWriteOnARefReportsWithWhereItsTargetsCameFrom()
    {
        FakeGameLink game = new();
        string big = ReplyOfBytes(20000);
        game.Answer = call => call.Method == "find" ? FakeGameLink.Ok(big) : FakeGameLink.Ok("{}");
        VerbPipeline pipeline = game.Pipeline();
        string reference = (string)pipeline.Call("find", "thing", "{}").Done()!["ref"]!;

        JsonNode report = pipeline.Call("write", "logic", $$"""{"id":"{{reference}}","logic":"On","value":1}""").Done()!;

        Assert.True((bool)report["report_only"]!);
        Assert.Equal(reference, (string)report["targets_from"]!["from"]!);
        Assert.True((bool)report["targets_from"]!["more_in_source"]!);
        Assert.All(game.CallsTo("write"), call => Assert.Equal(false, (bool?)call.Params()["confirm"]));
    }

    [Fact]
    public void RepeatSendsTheCallAgainWithTheGivenOptionsAndWholeComesFromTheStore()
    {
        FakeGameLink game = new();
        string big = ReplyOfBytes(20000);
        game.Answer = _ => FakeGameLink.Ok(big);
        VerbPipeline pipeline = game.Pipeline();
        string reference = (string)pipeline.Call("find", "thing", """{"name":"Light","limit":60}""").Done()!["ref"]!;

        pipeline.Call("find", null, $$"""{"repeat":"{{reference}}","offset":8}""").Done();
        JsonNode whole = pipeline.Call("find", null, $$"""{"repeat":"{{reference}}","whole":true}""").Done()!;

        Assert.Equal(2, game.CallsTo("find").Count);
        Same("""{"what":"thing","name":"Light","limit":60,"offset":8}""", game.CallsTo("find")[1].Params());
        Assert.True((bool)whole["from_store"]!);
        Assert.Equal(60, whole["things"]!.AsArray().Count);
    }

    [Fact]
    public void RepeatOfAnUnknownRefOrAnotherVerbIsRefused()
    {
        VerbPipeline pipeline = new FakeGameLink().Pipeline();

        Assert.Equal("repeat: no ref 'r9' (refs last until the world changes)",
            Assert.Single(pipeline.Call("find", null, """{"repeat":"r9"}""").Failed().Problems()));
    }

    [Fact]
    public void AWorldChangeForgetsTheRefs()
    {
        FakeGameLink link = new();
        link.Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}]}""");
        VerbPipeline pipeline = link.Pipeline();
        string reference = (string)pipeline.Call("find", "thing", "{}").Done()!["ref"]!;

        link.ChangeWorld("world-2");

        Assert.Equal("invalid_argument", pipeline.Call("inspect", "device", $$"""{"id":"{{reference}}"}""").Failed().Code);
    }

    [Fact]
    public void CutsAndReAsksInTheNextThreeCallsAreRecorded()
    {
        string path = Path.Combine(_folder, "rec.jsonl");
        FakeGameLink game = new();
        string big = ReplyOfBytes(20000);
        game.Answer = call => FakeGameLink.Ok(call.Method == "find" ? big : "{}");
        using (CallRecorder recorder = new(path))
        {
            VerbPipeline pipeline = game.Pipeline(recorder: recorder);
            pipeline.Call("find", "thing", """{"name":"A"}""");
            pipeline.Call("read", "clock", "{}");
            pipeline.Call("find", "thing", """{"name":"A","whole":true}""");
            pipeline.Call("find", "thing", """{"name":"B"}""");
            pipeline.Call("read", "clock", "{}");
            pipeline.Call("read", "clock", "{}");
            pipeline.Call("read", "clock", "{}");
            pipeline.Call("find", "thing", """{"name":"B"}""");
        }

        List<JsonNode> lines = File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!).ToList();
        List<JsonNode> cuts = lines.Where(line => line["cut"] != null).ToList();
        List<JsonNode> reAsks = lines.Where(line => line["re_ask"] != null).ToList();
        Assert.Equal(3, cuts.Count);
        Same("""{"ref":"r1","verb":"find","what":"thing","bytes":20000}""",
            Without(cuts[0]["cut"]!, "sent"));
        Assert.True((int)cuts[0]["cut"]!["sent"]! <= ReplyCap.Target);
        JsonNode reAsk = Assert.Single(reAsks)["re_ask"]!;
        Same("""{"ref":"r1","calls_after":2,"how":"whole"}""", reAsk);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheShownTextHookHearsExactlyTheTextSent()
    {
        ShownTexts shown = new();
        FakeGameLink game = new();
        string big = ReplyOfBytes(20000);
        game.Answer = _ => FakeGameLink.Ok(big);
        McpAdapter adapter = new(new SidecarOptions(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(_folder),
            200 * 1024), game, shown: shown);

        string line = (await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find","arguments":{"what":"thing"}}}"""))!;

        // The text heard is the cut reply as sent, not the whole one.
        string text = (string)JsonNode.Parse(line)!["result"]!["content"]![0]!["text"]!;
        Assert.Equal(text, Assert.Single(shown.Texts));
        Assert.NotNull(JsonNode.Parse(text)!["cut"]);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= ReplyCap.Target);
    }

    private static JsonObject Without(JsonNode node, string key)
    {
        JsonObject copy = (JsonObject)node.DeepClone();
        copy.Remove(key);
        return copy;
    }

    private sealed class ShownTexts : IShownText
    {
        public List<string> Texts { get; } = new();

        public void Shown(string text) => Texts.Add(text);
    }
}
