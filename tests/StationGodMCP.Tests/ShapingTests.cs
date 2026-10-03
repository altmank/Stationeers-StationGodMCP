#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Server;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Shaping in the mod: fields (single names as the reference FieldSelection's, and paths), limit, max_bytes, the
/// lenient reading that never refuses, and the sidecar handing fields to the mod and not applying them a second time.
/// </summary>
public sealed class ShapingTests
{
    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Shaping");

    public static IEnumerable<object[]> Fixtures()
    {
        foreach (string file in Directory.GetFiles(FixtureFolder, "*.json"))
        {
            yield return new object[] { Path.GetFileName(file) };
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SingleNamesGiveTheSidecarsResult(string fixture)
    {
        string reply = File.ReadAllText(Path.Combine(FixtureFolder, fixture));
        List<string> keys = ShapingChecks.EntryKeys(reply);
        string first = keys.Count > 0 ? keys[0] : "reference_id";
        List<string> hundred = new List<string>(keys);
        for (int index = hundred.Count; index < 100; index++)
        {
            hundred.Add("name_" + index);
        }

        List<string[]> selections = new List<string[]>
        {
            keys.ToArray(),
            new[] { first },
            new[] { first.ToUpperInvariant(), MixedCase(first) },
            new[] { "no_such_key" },
            new[] { "  " + first + " ", "\t" + first },
            hundred.ToArray(),
            new[] { first, first, "x", "x" },
            new[] { "" }
        };
        foreach (string[] fields in selections)
        {
            ShapingChecks.SameJson(ShapingChecks.Sidecar(reply, fields), ShapingChecks.ModFields(reply, fields));
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void NothingToLeaveOutWritesTheSameBytes(string fixture)
    {
        JToken reply = ShapingChecks.Parse(File.ReadAllText(Path.Combine(FixtureFolder, fixture)));

        Assert.Equal(ApiJson.WriteFresh(reply), ShapingChecks.Mod(reply, ShapingChecks.Nothing).Json);
    }

    [Fact]
    public void TheSidecarsOwnCasesGiveTheSameResult()
    {
        (string Reply, string[] Fields)[] cases =
        {
            ("""{"count":2,"things":[{"reference_id":"1","prefab_name":"Ore","position":{"x":1}},{"reference_id":"2","held_in":[]}],"local_player":{"reference_id":"9","name":"LU"}}""",
                new[] { "reference_id", "position" }),
            ("""{"things":[{"reference_id":"1","position":{"x":1}}]}""", new[] { "reference_id", "positon" }),
            ("""{"things":[]}""", new[] { "anything" })
        };
        foreach ((string reply, string[] fields) in cases)
        {
            ShapingChecks.SameJson(ShapingChecks.Sidecar(reply, fields), ShapingChecks.ModFields(reply, fields));
        }
    }

    [Fact]
    public void APathKeepsANestedKey()
    {
        string shaped = ShapingChecks.ModFields(
            """{"count":2,"things":[{"reference_id":"1","position":{"x":1,"y":2}},{"reference_id":"2","position":{"x":3,"y":4}}]}""",
            new[] { "things.position.x" });

        Assert.Equal("""{"count":2,"things":[{"position":{"x":1}},{"position":{"x":3}}]}""", shaped);
    }

    [Fact]
    public void APathReachesDictionaryKeysWithCapitals()
    {
        string shaped = ShapingChecks.ModFields(
            """{"results":[{"reference_id":"5","logic":{"Temperature":300.0,"Pressure":50.0}}]}""",
            new[] { "reference_id", "results.logic.Temperature" });

        Assert.Equal("""{"results":[{"reference_id":"5","logic":{"Temperature":300.0}}]}""", shaped);
    }

    [Fact]
    public void APathThroughAListAppliesToEachEntryOfIt()
    {
        string shaped = ShapingChecks.ModFields(
            """{"rooms":[{"room_id":"r1","cells":[{"x":1,"y":2},"odd",{"x":3,"y":4}]}]}""",
            new[] { "rooms.cells.x" });

        Assert.Equal("""{"rooms":[{"cells":[{"x":1},"odd",{"x":3}]}]}""", shaped);
    }

    [Fact]
    public void APathReadFromEachEntryReachesANestedObject()
    {
        string shaped = ShapingChecks.ModFields(
            """{"slots":[{"index":0,"type":"Tool","occupant":{"reference_id":"9","prefab_name":"ItemDrill","quantity":1}},{"index":1,"type":"Tool","occupant":null}]}""",
            new[] { "index", "occupant.prefab_name" });

        Assert.Equal("""{"slots":[{"index":0,"occupant":{"prefab_name":"ItemDrill"}},{"index":1,"occupant":null}]}""", shaped);
    }

    [Fact]
    public void APathReadFromEachEntryWalksAnyDepthAndThroughLists()
    {
        string shaped = ShapingChecks.ModFields(
            """{"vaults":[{"id":"1","stock":{"rows":[{"ore":"Iron","grams":{"total":5,"by_slot":[1,4]}},{"ore":"Gold","grams":{"total":2}}]}}]}""",
            new[] { "stock.rows.grams.total" });

        Assert.Equal("""{"vaults":[{"stock":{"rows":[{"grams":{"total":5}},{"grams":{"total":2}}]}}]}""", shaped);
    }

    [Fact]
    public void AnEntryPathReachesIntoANestedListOfEachEntry()
    {
        string shaped = ShapingChecks.ModFields(
            """{"items":[{"reference_id":"1","held_in":[{"reference_id":"5","prefab_name":"ItemBackpack"},{"reference_id":"9","prefab_name":"Human"}]}]}""",
            new[] { "reference_id", "held_in.reference_id" });

        Assert.Equal("""{"items":[{"reference_id":"1","held_in":[{"reference_id":"5"},{"reference_id":"9"}]}]}""", shaped);
    }

    [Fact]
    public void AnEntryPathAndAListPathTogether()
    {
        string shaped = ShapingChecks.ModFields(
            """{"things":[{"reference_id":"1","position":{"x":1,"y":2},"held_in":{"reference_id":"5","name":"Locker"}}]}""",
            new[] { "things.position.x", "held_in.name" });

        Assert.Equal("""{"things":[{"position":{"x":1},"held_in":{"name":"Locker"}}]}""", shaped);
    }

    [Fact]
    public void AnEntryPathNoEntryHasIsUnmatched()
    {
        string shaped = ShapingChecks.ModFields(
            """{"slots":[{"index":0,"occupant":{"prefab_name":"A"}}]}""", new[] { "index", "occupant.colour" });

        Assert.Equal("""{"slots":[{"index":0,"occupant":{}}],"fields_unmatched":["occupant.colour"]}""", shaped);
    }

    [Fact]
    public void OmitDropsTopLevelKeysAndNestedPaths()
    {
        string reply = """{"reference_id":"5","source":"print(1)","source_length":8,"runtime":{"registers":[{"index":0,"value":0}],"register_count":18,"stack":{"values":[1,2]}},"lua":{"running":true,"log":{"lines":["a"]}},"pins":[{"index":0,"reference_id":"7"}]}""";

        Assert.Equal(
            """{"reference_id":"5","source_length":8,"runtime":{"register_count":18},"lua":{"running":true},"pins":[{"index":0,"reference_id":"7"}]}""",
            ShapingChecks.ModOmit(reply, new[] { "source", "runtime.registers", "runtime.stack", "lua.log" }));
    }

    [Fact]
    public void OmitThroughAListAppliesToEachEntry()
    {
        string reply = """{"count":2,"members":[{"reference_id":"1","position":{"x":1},"display_name":"Cable"},{"reference_id":"2","position":{"x":2}}]}""";

        Assert.Equal("""{"count":2,"members":[{"reference_id":"1","display_name":"Cable"},{"reference_id":"2"}]}""",
            ShapingChecks.ModOmit(reply, new[] { "members.position" }));
    }

    [Fact]
    public void OmitNamesWhatMatchedNothing()
    {
        string reply = """{"source":"x","runtime":{"registers":[]}}""";

        Assert.Equal("""{"omit_unmatched":["sourc"]}""",
            ShapingChecks.ModOmit(reply, new[] { "source", "sourc", "runtime", "runtime.registers", "runtime.stack" }));
    }

    [Fact]
    public void OmitWinsOverFields()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"fields":["a","b"],"omit":["things.b","total"]}"""))!;

        Assert.Equal("""{"things":[{"a":1}]}""",
            ShapingChecks.Mod(ShapingChecks.Parse("""{"total":1,"things":[{"a":1,"b":2,"c":3}]}"""), shape).Json);
    }

    [Fact]
    public void OmitInTheEnvelopeAppliesToTheResultOnly()
    {
        CallReplyView reply = CallReplyView.Of("r8", ShapingChecks.Parse("""{"id":"x","source":"long"}"""), true, 0.5, 0.25, 3);
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"omit":["source","id"]}"""))!;

        string text = ApiJson.WriteShaped(ApiJson.Fresh(), reply, shape, ShapingRoot.Envelope).Json;

        Assert.Equal("""{"type":"reply","id":"r8","ok":true,"shaped":true,"result":{},"elapsed_ms":0.5,"queue_ms":0.25,"frame":3}""", text);
    }

    [Fact]
    public void TheStrictReadingTakesOmitAndRefusesABadOne()
    {
        List<string> problems = new List<string>();
        ShapeRequest? good = ShapeRequest.Strict(JObject.Parse("""{"omit":["source","runtime.registers"]}"""), null, problems);

        Assert.Empty(problems);
        Assert.Equal(2, good!.Omit!.Count);
        Assert.True(good.Omits("source"));
        Assert.False(good.Omits("runtime"));

        Assert.Null(ShapeRequest.Strict(JObject.Parse("""{"omit":["a-b"]}"""), null, problems));
        Assert.Contains(problems, problem => problem.StartsWith("shape.omit:", StringComparison.Ordinal));
        problems.Clear();
        Assert.Null(ShapeRequest.Strict(JObject.Parse("""{"omit":[]}"""), null, problems));
        Assert.Contains("shape.omit must be an array of 1 to 256 selectors.", problems);
    }

    [Fact]
    public void TwoSelectorsOnOneKeyKeepTheUnion()
    {
        string reply = """{"things":[{"position":{"x":1,"y":2,"z":3},"id":"1"}]}""";

        Assert.Equal("""{"things":[{"position":{"x":1,"z":3}}]}""",
            ShapingChecks.ModFields(reply, new[] { "things.position.x", "things.position.z" }));
        Assert.Equal("""{"things":[{"position":{"x":1,"y":2,"z":3}}]}""",
            ShapingChecks.ModFields(reply, new[] { "things.position.x", "things.position" }));
    }

    [Fact]
    public void ASingleNameBesideAPathOnTheSameList()
    {
        string shaped = ShapingChecks.ModFields(
            """{"things":[{"reference_id":"1","position":{"x":1,"y":2},"name":"a"}],"pieces":[{"reference_id":"7","position":{"x":9}}]}""",
            new[] { "reference_id", "things.position.y" });

        Assert.Equal(
            """{"things":[{"reference_id":"1","position":{"y":2}}],"pieces":[{"reference_id":"7"}]}""", shaped);
    }

    [Fact]
    public void AWholeKeyStillCountsADeeperPathAsMatched()
    {
        string shaped = ShapingChecks.ModFields(
            """{"things":[{"position":{"x":1}}]}""", new[] { "position", "things.position.x", "things.position.w" });

        Assert.Equal("""{"things":[{"position":{"x":1}}],"fields_unmatched":["things.position.w"]}""", shaped);
    }

    [Theory]
    [InlineData("count.x")]
    [InlineData("missing.x")]
    [InlineData("prefab-name")]
    [InlineData("things..x")]
    [InlineData("things.")]
    [InlineData(".x")]
    public void ASelectorThatCannotMatchIsReportedNotRefused(string selector)
    {
        string shaped = ShapingChecks.ModFields(
            """{"count":1,"things":[{"x":1,"prefab-name":"a"}]}""", new[] { "x", selector });

        Assert.Equal($$"""{"count":1,"things":[{"x":1}],"fields_unmatched":["{{selector}}"]}""", shaped);
    }

    [Fact]
    public void ASelectorThatIsNotTextIsReportedByItsJson()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"fields":["x",7,{"a":1}]}"""))!;

        Assert.Equal("""{"things":[{"x":1}],"fields_unmatched":["7","{\"a\":1}"]}""",
            ShapingChecks.Mod(ShapingChecks.Parse("""{"things":[{"x":1,"y":2}]}"""), shape).Json);
    }

    [Fact]
    public void LimitCutsAListAndSaysHowLongItWas()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"limit":{"things":2,"count":1,"absent":3,"pieces":5}}"""))!;

        ShapedText shaped = ShapingChecks.Mod(
            ShapingChecks.Parse("""{"count":4,"things":[{"a":1},{"a":2},{"a":3},{"a":4}],"pieces":[1,2]}"""), shape);

        Assert.Equal("""{"count":4,"things":[{"a":1},{"a":2}],"pieces":[1,2],"shape_truncated":{"things":4}}""", shaped.Json);
        Assert.Equal(new[] { new KeyValuePair<string, int>("things", 4), new KeyValuePair<string, int>("pieces", 2) },
            shaped.Outcome.Lists);
    }

    [Fact]
    public void LimitAndFieldsTogether()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"fields":["a"],"limit":{"things":0}}"""))!;

        Assert.Equal("""{"things":[],"shape_truncated":{"things":2}}""",
            ShapingChecks.Mod(ShapingChecks.Parse("""{"things":[{"a":1,"b":1},{"a":2}]}"""), shape).Json);
    }

    [Fact]
    public void ReplyTooLargeCarriesTheSizesAndEveryListsLength()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse("""{"max_bytes":1024,"limit":{"things":1}}"""))!;
        ShapedText shaped = ShapingChecks.Mod(
            ShapingChecks.Parse("""{"things":[{"a":1},{"a":2},{"a":3}],"cells":[],"count":3}"""), shape);

        ErrorView error = ReplyTooLarge.Of(2048, shape.MaxBytes!.Value, shaped.Outcome);

        Assert.Equal(
            """{"code":"reply_too_large","message":"The reply is 2048 bytes, more than the 1024 allowed; narrow it with fields, limit or the method's own filters.","data":{"bytes":2048,"limit":1024,"counts":{"things":3,"cells":0}}}""",
            ApiJson.WriteFresh(error));
    }

    [Fact]
    public void AnErrorWithoutDataIsWrittenAsBefore()
    {
        Assert.Equal("""{"code":"x","message":"y"}""", ApiJson.WriteFresh(new ErrorView("x", "y")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"reference_id\"")]
    [InlineData("[\"reference_id\"]")]
    [InlineData("7")]
    public void AShapeThatIsNotAnObjectIsIgnored(string shape)
    {
        Assert.Null(ShapeRequest.Lenient(JToken.Parse(shape)));
        Assert.Null(ShapeRequest.Lenient(null));
    }

    [Fact]
    public void TheLenientReadingIgnoresWhatItCannotUse()
    {
        ShapeRequest shape = ShapeRequest.Lenient(JObject.Parse(
            """{"fields":[],"limit":{"a":-1,"b":100001,"c":2.5,"d":"3","e":3.0,"f":1e2},"max_bytes":10,"unknown":true}"""))!;

        Assert.Null(shape.Fields);
        Assert.Equal(new Dictionary<string, int> { ["e"] = 3, ["f"] = 100 }, shape.Limits);
        Assert.Null(shape.MaxBytes);
        Assert.Equal(2048, ShapeRequest.Lenient(JObject.Parse("""{"max_bytes":2048}"""))!.MaxBytes);
        Assert.Empty(ShapeRequest.Lenient(JObject.Parse("""{"limit":[1]}"""))!.Limits);
    }

    [Fact]
    public void TheEnvelopeIsLeftAloneAndMarkedShaped()
    {
        CallReplyView reply = CallReplyView.Of("r7", ShapingChecks.Parse("""{"things":[{"a":1,"b":2}],"count":1}"""), true, 0.5, 0.25, 3);

        string text = ApiJson.WriteShaped(ApiJson.Fresh(), reply, ShapingChecks.Fields(new[] { "a" }), ShapingRoot.Envelope).Json;

        Assert.Equal("""{"type":"reply","id":"r7","ok":true,"shaped":true,"result":{"things":[{"a":1}],"count":1},"elapsed_ms":0.5,"queue_ms":0.25,"frame":3}""", text);
    }

    [Fact]
    public async Task TheSidecarSendsFieldsAsShapeAndKeepsAShapedReply()
    {
        (string forwarded, JsonElement result) = await CallThroughSidecar(
            """{"kind":"structure","fields":["things.position.x"]}""", """{"things":[{"position":{"x":1}}]}""", shaped: true);

        using JsonDocument request = JsonDocument.Parse(forwarded);
        Assert.Equal("""{"kind":"structure"}""", request.RootElement.GetProperty("params").GetRawText());
        Assert.Equal("""{"fields":["things.position.x"]}""", request.RootElement.GetProperty("shape").GetRawText());
        Assert.Equal("""{"things":[{"position":{"x":1}}]}""", result.GetRawText());
    }

    // The mod owns shaping: a reply it did not mark shaped is passed on as it came, never shaped a second time here.
    [Fact]
    public async Task AnUnshapedReplyIsPassedOnAsItCame()
    {
        (_, JsonElement result) = await CallThroughSidecar(
            """{"kind":"structure","fields":["reference_id"]}""", """{"things":[{"reference_id":"1","position":{"x":1}}]}""",
            shaped: false);

        Assert.Equal("""{"things":[{"reference_id":"1","position":{"x":1}}]}""", result.GetRawText());
    }

    [Fact]
    public async Task NoShapeIsSentWithoutFields()
    {
        (string forwarded, _) = await CallThroughSidecar("""{"kind":"structure"}""", """{"things":[]}""", shaped: false);

        using JsonDocument request = JsonDocument.Parse(forwarded);
        Assert.False(request.RootElement.TryGetProperty("shape", out _));
    }

    private static async Task<(string Forwarded, JsonElement Result)> CallThroughSidecar(string arguments, string result, bool shaped)
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok(result, shaped));

        string? line = await Program.HandleMcpMessageAsync(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_things","arguments":{{{arguments}}}}}""",
            new Program.GameTransportSettings(game.Target));
        using JsonDocument reply = JsonDocument.Parse(line!);
        return (Assert.Single(game.CallsTo("find_things")).Message.GetRawText(),
            reply.RootElement.GetProperty("result").GetProperty("structuredContent").Clone());
    }

    private static string MixedCase(string name)
    {
        char[] characters = name.ToCharArray();
        for (int index = 0; index < characters.Length; index += 2)
        {
            characters[index] = char.ToUpperInvariant(characters[index]);
        }

        return new string(characters);
    }
}
