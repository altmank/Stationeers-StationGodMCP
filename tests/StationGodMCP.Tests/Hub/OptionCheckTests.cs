#nullable enable

using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// OptionCheck: every problem of a call at once, as one invalid_argument in verb names with the nearest valid names;
/// nothing is sent; the faces' rules (script: x-script drop refused, confirm belongs to the run; program: plus confirm).
/// </summary>
public sealed class OptionCheckTests
{
    [Fact]
    public void EveryProblemComesBackAtOnceAndNothingIsSent()
    {
        FakeGameLink game = new();

        VerbReply.Failed failed = game.Pipeline()
            .Call("find", "thing", """{"prefx":"*Tank*","limit":-1,"order":"far","near":7}""").Failed();

        Assert.Equal(new[]
        {
            "prefx: not an option of find thing",
            "limit: must be at least 1",
            "order: 'far' is not one of nearest, id",
            "near: must be a string or a list"
        }, failed.Problems());
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void AnUnknownOptionNamesTheNearestOptionsAndTheValidOnesNearestFirst()
    {
        VerbReply.Failed failed = new FakeGameLink().Pipeline().Call("find", "thing", """{"nme":"Tank"}""").Failed();

        JsonNode problem = failed.Data!["problems"]![0]!;
        Assert.Equal("name", (string)problem["nearest"]![0]!);
        JsonArray valid = failed.Data["valid"]!.AsArray();
        Assert.Equal("name", (string)valid[0]!);
        Assert.Contains("fields", valid.Select(name => (string)name!));
    }

    [Fact]
    public void ProblemsUseVerbNamesNeverHandlerNames()
    {
        VerbReply.Failed failed = new FakeGameLink().Pipeline()
            .Call("find", "thing", """{"nam":"Tank","radius_m":3,"box":[[0,0,0]]}""").Failed();

        string text = failed.ToJson().ToJsonString();
        Assert.DoesNotContain("name_contains", text);
        Assert.Contains("box: needs at least 2 entries", failed.Problems());
        Assert.Equal("radius", (string)failed.Data!["problems"]![1]!["nearest"]![0]!);
    }

    [Fact]
    public void AnUnknownWhatOrVerbNamesTheNearest()
    {
        VerbPipeline pipeline = new FakeGameLink().Pipeline();

        VerbReply.Failed what = pipeline.Call("find", "thng").Failed();
        VerbReply.Failed verb = pipeline.Call("finde", null).Failed();
        VerbReply.Failed none = pipeline.Call("find", null).Failed();

        Assert.Equal("thing", (string)what.Data!["problems"]![0]!["nearest"]![0]!);
        Assert.Equal("find", (string)verb.Data!["problems"]![0]!["nearest"]![0]!);
        Assert.StartsWith("what: required: one of thing, item", none.Problems()[0]);
    }

    [Fact]
    public void RequiredOptionsAreNamed()
    {
        VerbReply.Failed failed = new FakeGameLink().Pipeline().Call("write", "memory", """{"id":"1"}""").Failed();

        Assert.Equal(new[] { "start: required", "values: required" }, failed.Problems());
    }

    [Fact]
    public void ListItemsAreHeldToTheirEnumAndAnEnumTakesAnyCase()
    {
        VerbPipeline pipeline = new FakeGameLink().Pipeline();

        VerbReply.Failed failed = pipeline.Call("find", "frame", """{"include":["inner","iner"]}""").Failed();
        pipeline.Call("find", "thing", """{"order":" ID "}""").Done();

        Assert.Equal("include[1]: 'iner' is not one of inner", Assert.Single(failed.Problems()));
        Assert.Equal("inner", (string)failed.Data!["problems"]![0]!["nearest"]![0]!);
    }

    [Fact]
    public void NullIsAnOmittedOption()
    {
        new FakeGameLink().Pipeline().Call("find", "thing", """{"name":null}""").Done();
    }

    [Fact]
    public void AWhatlessVerbTakesNoWhat()
    {
        Assert.Equal("what: undo takes no what",
            Assert.Single(new FakeGameLink().Pipeline().Call("undo", "job", """{"id":"place-1"}""").Failed().Problems()));
    }

    [Fact]
    public void TheScriptFaceRefusesTheRunLevelOptionsAndConfirmBelongsToTheRun()
    {
        FakeGameLink game = new();

        VerbReply.Failed write = game.Pipeline().Call("write", "logic", """{"id":"1","logic":"On","value":1,"confirm":true}""",
            Face.Script).Failed();
        VerbReply.Failed build = game.Pipeline().Call("build", "structure",
            """{"prefab":"StructureWallLight","at":[1,2,3],"queue":true,"max_wait":3}""", Face.Script).Failed();
        VerbReply.Failed find = game.Pipeline().Call("find", "thing", """{"offset":8,"omit":["a"]}""", Face.Script).Failed();

        Assert.Equal("confirm: confirm belongs to the run", Assert.Single(write.Problems()));
        Assert.Equal(new[] { "queue: not taken in a script", "max_wait: not taken in a script" }, build.Problems());
        Assert.Equal(new[] { "offset: not taken in a script", "omit: not taken in a script" }, find.Problems());
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void TheProgramFaceTakesTheScriptOptionsPlusConfirm()
    {
        FakeGameLink game = new();

        game.Pipeline().Call("write", "logic", """{"id":"1","logic":"On","value":1,"confirm":true}""", Face.Program).Done();
        VerbReply.Failed offset = game.Pipeline().Call("find", "thing", """{"offset":8}""", Face.Program).Failed();

        Assert.Equal("offset: not taken by a program", Assert.Single(offset.Problems()));
    }

    [Fact]
    public void TheDirectFaceTakesTheToolListsOptions()
    {
        new FakeGameLink().Pipeline().Call("find", "thing", """{"offset":8,"omit":["a"],"whole":true}""").Done();
    }

    [Fact]
    public void AScriptAsksHelpByTheSameNames()
    {
        VerbReply reply = new FakeGameLink().Pipeline().Call("help", null, """{"verb":"read","what":"logic"}""", Face.Script);

        Assert.StartsWith("Logic values of devices", (string)reply.Done()!["text"]!);
    }

    [Fact]
    public void AnOptionAnotherFormTakesIsRefusedNamingThatForm()
    {
        VerbReply.Failed failed = new FakeGameLink().Pipeline()
            .Call("swap", "cable", """{"network":"5","into":"heavy","spare":["1"]}""").Failed();

        Assert.Equal("spare: not taken with into (it goes with tidy)", Assert.Single(failed.Problems()));
    }

    [Fact]
    public void AnOptionNoHandlerTakesYetIsNotAvailable()
    {
        FakeGameLink game = new();

        VerbReply.Failed failed = game.Pipeline().Call("check", "ignition", """{"id":"5"}""").Failed();

        Assert.Equal("not_available", failed.Code);
        Assert.Contains("id", failed.Message);
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void ShapingASmallReplyIsRefused()
    {
        VerbReply.Failed failed = new FakeGameLink().Pipeline().Call("inspect", "player", """{"fields":["x"]}""").Failed();

        Assert.Equal("fields: not taken by inspect player (its reply stays small)", Assert.Single(failed.Problems()));
    }
}
