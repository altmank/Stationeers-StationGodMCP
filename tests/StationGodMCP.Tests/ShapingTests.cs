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
