#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using Xunit;

namespace StationGodMCP.Tests.Sidecar;

/// <summary>
/// The C# client against an in-process fake game: hello and welcome, the TCP secret, calls in flight, the resend rule, the catalogue, world changes and subscriptions.
/// </summary>
public sealed class ClientTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    // The Python library's generated table carries the hash of the same file, computed by another language.
    [Fact]
    public void TheBuiltInHashIsTheOneThePythonLibraryComputed()
    {
        string generated = File.ReadAllText(Path.Combine(Root, "clients", "python", "stationgod", "_methods.py"));
        string python = Regex.Match(generated, "CATALOGUE_HASH = '([^']+)'").Groups[1].Value;

        Assert.Matches("^sha256:[0-9a-f]{64}$", GameCatalogue.BuiltIn.Hash);
        Assert.Equal(python, GameCatalogue.BuiltIn.Hash);
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, "catalogue.json")))).ToLowerInvariant(),
            GameCatalogue.BuiltIn.Hash);
    }

    [Theory]
    [InlineData("read_logic", """{"reference_id":"1","logic_type":"On"}""", true)]
    [InlineData("write_logic", """{"reference_id":"1","logic_type":"On","value":1}""", false)]
    [InlineData("highlight", """{"reference_ids":["1"]}""", false)]
    [InlineData("show_preview", "{}", false)]
    [InlineData("place_cables", "{}", true)]
    [InlineData("place_cables", """{"dry_run":true}""", true)]
    [InlineData("place_cables", """{"dry_run":false,"confirm":true}""", false)]
    [InlineData("subscribe", """{"topic":"world"}""", false)]
    [InlineData("no_such_method", "{}", false)]
    public void OnlyReadsWithoutEffectsAreSafeToSendAgain(string method, string parameters, bool safe)
    {
        Assert.Equal(safe, GameCatalogue.BuiltIn.ResendSafe(method, Json(parameters)));
    }

    [Fact]
    public void ADurationArgumentLengthensTheWait()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), GameCatalogue.BuiltIn.Duration("sample_logic", Json("""{"duration_seconds":12}""")));
        Assert.Equal(TimeSpan.FromSeconds(30), GameCatalogue.BuiltIn.Duration("sample_logic", Json("{}")));
        Assert.Equal(TimeSpan.Zero, GameCatalogue.BuiltIn.Duration("game_clock", Json("{}")));
    }

    [Fact]
    public async Task AHelloNamesTheLibrary()
    {
        await using FakeGame game = FakeGame.OnPipe();
        await using StationGodClient client = new(new ClientOptions(game.Target) { ReadEnvironment = _ => "c2VjcmV0" });

        Assert.IsType<CallOutcome.Answered>(await client.CallAsync("game_clock", Json("{}")));

        JsonElement hello = game.Received.First();
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        Assert.Equal("[2]", hello.GetProperty("protocol").GetRawText());
        Assert.Equal("stationgod-cs", hello.GetProperty("client").GetProperty("name").GetString());
        Assert.StartsWith("stationgod-cs/", hello.GetProperty("client").GetProperty("library").GetString());
        Assert.Contains("shape", hello.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        Assert.False(hello.TryGetProperty("auth", out _));
    }

    [Fact]
    public async Task AGivenNameIsSentInHello()
    {
        await using FakeGame game = FakeGame.OnPipe();
        await using StationGodClient client = new(new ClientOptions(game.Target) { ClientName = "agents", ReadEnvironment = _ => null });

        Assert.IsType<CallOutcome.Answered>(await client.CallAsync("game_clock", Json("{}")));

        JsonElement hello = game.Received.First();
        Assert.Equal("agents", hello.GetProperty("client").GetProperty("name").GetString());
        Assert.False(hello.TryGetProperty("auth", out _));
    }

    [Fact]
    public async Task OverTcpWithoutTheSecretTheCallIsRefusedWithTheReason()
    {
        await using FakeGame game = FakeGame.OnTcp();
        await using StationGodClient client = new(new ClientOptions(game.Target) { ReadEnvironment = _ => null });

        CallOutcome.Refused refused = Assert.IsType<CallOutcome.Refused>(await client.CallAsync("mod_info", Json("{}")));

        Assert.Equal("unauthorized", refused.Code);
        Assert.Contains("STATIONGODMCP_SECRET", refused.Error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task VersionTwoOverTcpSignsInWithTheSharedSecretFirst()
    {
        await using FakeGame game = FakeGame.OnTcp();
        await using StationGodClient client = new(new ClientOptions(game.Target)
        {
            ReadEnvironment = name => name == ClientOptions.DefaultSecretVariable ? FakeGame.TestSecret : null
        });

        Assert.IsType<CallOutcome.Answered>(await client.CallAsync("mod_info", Json("{}")));

        Assert.Equal(1, game.Connections);
        Assert.Equal(FakeGame.TestSecret, game.Received.First().GetProperty("secret").GetString());
    }

    [Fact]
    public async Task FiveCallsAreInFlightOnOneConnection()
    {
        await using FakeGame game = FakeGame.OnPipe();
        TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            if (game.CallsTo("read_logic").Count() == 5)
            {
                allArrived.TrySetResult();
            }

            await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return call.Ok($$"""{"value":{{call.Params.GetProperty("n").GetInt32()}}}""");
        };
        await using StationGodClient client = new(new ClientOptions(game.Target));

        CallOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(n => client.CallAsync("read_logic", Json($$"""{"n":{{n}}}"""))));

        Assert.Equal(Enumerable.Range(0, 5), outcomes.Select(outcome =>
            Assert.IsType<CallOutcome.Answered>(outcome).Result.GetProperty("value").GetInt32()));
        Assert.Equal(1, game.Connections);
    }

    [Fact]
    public async Task AWrittenReadIsSentAgainOnceAfterABreak()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call =>
        {
            if (call.Method == "read_logic" && game.CallsTo("read_logic").Count() == 1)
            {
                game.DropAll();
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(call.Ok("""{"value":7}"""));
        };
        await using StationGodClient client = new(new ClientOptions(game.Target));

        CallOutcome outcome = await client.CallAsync("read_logic", Json("""{"reference_id":"1","logic_type":"On"}"""));

        Assert.Equal(7, Assert.IsType<CallOutcome.Answered>(outcome).Result.GetProperty("value").GetInt32());
        Assert.Equal(2, game.CallsTo("read_logic").Count());
        Assert.Equal(2, game.Connections);
    }

    [Fact]
    public async Task AReadBrokenTwiceIsNotSentAThirdTime()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call =>
        {
            if (call.Method != "read_logic")
            {
                return Task.FromResult<string?>(call.Ok("{}"));
            }

            game.DropAll();
            return Task.FromResult<string?>(null);
        };
        await using StationGodClient client = new(new ClientOptions(game.Target));

        CallOutcome.NoAnswer noAnswer = Assert.IsType<CallOutcome.NoAnswer>(
            await client.CallAsync("read_logic", Json("""{"reference_id":"1","logic_type":"On"}""")));

        Assert.False(noAnswer.MaybeRan);
        Assert.Equal(2, game.CallsTo("read_logic").Count());
    }

    [Theory]
    [InlineData("write_logic", """{"reference_id":"1","logic_type":"On","value":1}""")]
    [InlineData("highlight", """{"reference_ids":["1"]}""")]
    [InlineData("place_cables", """{"dry_run":false,"confirm":true}""")]
    public async Task AWrittenCallThatMayChangeTheGameIsNotSentAgain(string method, string parameters)
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call =>
        {
            if (call.Method != method)
            {
                return Task.FromResult<string?>(call.Ok("{}"));
            }

            game.DropAll();
            return Task.FromResult<string?>(null);
        };
        await using StationGodClient client = new(new ClientOptions(game.Target));

        CallOutcome.NoAnswer noAnswer = Assert.IsType<CallOutcome.NoAnswer>(await client.CallAsync(method, Json(parameters)));

        Assert.True(noAnswer.MaybeRan);
        Assert.Single(game.CallsTo(method));
    }

    [Fact]
    public async Task NothingIsSentAgainIntoAnotherWorld()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call =>
        {
            if (call.Method != "read_logic")
            {
                return Task.FromResult<string?>(call.Ok("{}"));
            }

            game.WorldId = "world-2";
            game.DropAll();
            return Task.FromResult<string?>(null);
        };
        await using StationGodClient client = new(new ClientOptions(game.Target));
        List<string> worlds = [];
        client.WorldChanged += world => worlds.Add(world.GetProperty("id").GetString()!);

        CallOutcome.NoAnswer noAnswer = Assert.IsType<CallOutcome.NoAnswer>(
            await client.CallAsync("read_logic", Json("""{"reference_id":"1","logic_type":"On"}""")));

        Assert.True(noAnswer.WorldChanged);
        Assert.Single(game.CallsTo("read_logic"));
        Assert.Equal(["world-2"], worlds);
    }

    [Fact]
    public async Task AnErrorReplyIsNeverSentAgain()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Error("game_timeout", "Not started in time."));
        await using StationGodClient client = new(new ClientOptions(game.Target));

        CallOutcome outcome = await client.CallAsync("game_clock", Json("{}"));

        Assert.Equal("game_timeout", Assert.IsType<CallOutcome.Refused>(outcome).Code);
        Assert.Single(game.CallsTo("game_clock"));
    }

    [Fact]
    public async Task ACallWithNoReplyInTimeIsCancelledAndSaysItMayHaveRun()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Method == "write_logic" ? null : call.Ok("{}"));
        await using StationGodClient client = new(new ClientOptions(game.Target) { ConnectTimeout = TimeSpan.FromMilliseconds(200) });

        CallOutcome.NoAnswer noAnswer = Assert.IsType<CallOutcome.NoAnswer>(
            await client.CallAsync("write_logic", Json("""{"reference_id":"1","logic_type":"On","value":1}"""), deadlineMs: 100));

        Assert.True(noAnswer.MaybeRan);
        Assert.StartsWith("The game took the request through pipe ", noAnswer.Message);
        FakeCall call = Assert.Single(game.CallsTo("write_logic"));
        Assert.Equal(100, call.Message.GetProperty("deadline_ms").GetInt32());
        await Eventually(() => game.Received.Any(message =>
            message.TryGetProperty("type", out JsonElement type) && type.ValueEquals("cancel") &&
            message.GetProperty("id").GetRawText() == call.Id.GetRawText()));
    }

    [Fact]
    public async Task NoDeadlineIsSentUnlessGivenAndNoShapeUnlessTheServerListsIt()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Features = ["cancel"];
        await using StationGodClient client = new(new ClientOptions(game.Target));

        await client.CallAsync("find_things", Json("{}"), Json("""{"fields":["reference_id"]}"""));

        JsonElement call = Assert.Single(game.CallsTo("find_things")).Message;
        Assert.False(call.TryGetProperty("deadline_ms", out _));
        Assert.False(call.TryGetProperty("shape", out _));
    }

    [Fact]
    public async Task NoGameAnswersAtOnceWithTheReason()
    {
        await using StationGodClient client = new(new ClientOptions(new GameTarget.Pipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N"))));

        CallOutcome.NoAnswer noAnswer = Assert.IsType<CallOutcome.NoAnswer>(await client.CallAsync("game_clock", Json("{}")));

        Assert.False(noAnswer.MaybeRan);
        Assert.StartsWith("No StationGodMCP pipe ", noAnswer.Message);
    }

    [Fact]
    public async Task TheModsCatalogueIsFetchedOncePerHash()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.CatalogueHash = "sha256:" + new string('a', 64);
        game.CatalogueJson = CatalogueWithout("weather");
        await using StationGodClient client = new(new ClientOptions(game.Target));
        List<string> changes = [];
        client.CatalogueChanged += catalogue => changes.Add(catalogue.Hash);

        await client.CallAsync("game_clock", Json("{}"));
        game.DropAll();
        await Eventually(() => game.OpenConnections == 0);
        await client.CallAsync("game_clock", Json("{}"));

        Assert.Equal([game.CatalogueHash], changes);
        Assert.Equal(game.CatalogueHash, client.Catalogue.Hash);
        Assert.Null(client.Catalogue.Method("weather"));
        Assert.Single(game.CallsTo("catalogue"));
    }

    [Fact]
    public async Task ACatalogueThatCannotBeFetchedLeavesTheBuiltInOne()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.CatalogueHash = "sha256:" + new string('b', 64);
        game.CatalogueJson = "[]";
        await using StationGodClient client = new(new ClientOptions(game.Target));

        Assert.IsType<CallOutcome.Answered>(await client.CallAsync("game_clock", Json("{}")));

        Assert.Same(GameCatalogue.BuiltIn, client.Catalogue);
    }

    [Fact]
    public async Task AWorldChangeIsToldOncePerNewWorld()
    {
        await using FakeGame game = FakeGame.OnPipe();
        await using StationGodClient client = new(new ClientOptions(game.Target));
        List<string> worlds = [];
        client.WorldChanged += world => worlds.Add(world.GetProperty("id").GetString()!);
        await client.CallAsync("game_clock", Json("{}"));

        await game.PushAsync("""{"type":"event","event":"world_changed","world":{"id":"world-2","save":"x","epoch":1}}""");
        await game.PushAsync("""{"type":"event","event":"world_changed","world":{"id":"world-2","save":"x","epoch":1}}""");
        await game.PushAsync("""{"type":"event","event":"game_state","game_state":"Paused"}""");

        await Eventually(() => client.GameState == "Paused");
        Assert.Equal(["world-2"], worlds);
        Assert.Equal("world-2", client.World!.Value.GetProperty("id").GetString());
    }

    [Fact]
    public async Task TheClientSubscribesToTheWorldTopic()
    {
        await using FakeGame game = FakeGame.OnPipe();
        await using StationGodClient client = new(new ClientOptions(game.Target));

        await client.CallAsync("game_clock", Json("{}"));

        await Eventually(() => game.CallsTo("subscribe").Any(call => call.Params.GetRawText() == """{"topic":"world"}"""));
    }

    [Fact]
    public async Task ASubscriptionFollowsUpdatesAndComesBackAfterAReconnectIntoTheSameWorld()
    {
        await using FakeGame game = FakeGame.OnPipe();
        int subscriptions = 0;
        game.Answer = call => Task.FromResult<string?>(call.Method == "subscribe" && call.Params.TryGetProperty("items", out _)
            ? call.Ok("{\"subscription\":\"s" + Interlocked.Increment(ref subscriptions) +
                      "\",\"interval_s\":1,\"values\":1,\"frame\":5,\"result\":{\"results\":[{\"n\":0}]}}")
            : call.Ok("{}"));
        await using StationGodClient client = new(new ClientOptions(game.Target));

        SubscribeOutcome outcome = await client.SubscribeAsync(Json("""{"items":[{"reference_id":"1","logic":["On"]}],"interval_s":1}"""));

        Subscription subscription = Assert.IsType<SubscribeOutcome.Subscribed>(outcome).Subscription;
        Assert.Equal("s1", subscription.ServerId);
        Assert.Equal("""{"results":[{"n":0}]}""", subscription.State!.Value.GetRawText());
        Task<bool> next = subscription.WaitAsync(TimeSpan.FromSeconds(5));
        await game.PushAsync("""{"type":"event","event":"update","subscription":"s1","seq":1,"frame":9,"result":{"results":[{"n":1}]}}""");
        Assert.True(await next);
        Assert.Equal(1, subscription.Seq);
        Assert.Equal("""{"results":[{"n":1}]}""", subscription.State!.Value.GetRawText());

        game.DropAll();
        await Eventually(() => subscription.ServerId == "s2");
        Assert.False(subscription.IsClosed);
        Assert.Equal("""{"results":[{"n":0}]}""", subscription.State!.Value.GetRawText());

        game.WorldId = "world-2";
        game.DropAll();
        await Eventually(() => subscription.IsClosed);
        Assert.Equal("world_changed", subscription.EndReason);
    }

    [Fact]
    public async Task ARefusedSubscriptionCarriesTheModsCode()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Method == "subscribe" && call.Params.TryGetProperty("items", out _)
            ? call.Error("subscription_limit", "Too many values.")
            : call.Ok("{}"));
        await using StationGodClient client = new(new ClientOptions(game.Target));

        SubscribeOutcome outcome = await client.SubscribeAsync(Json("""{"items":[]}"""));

        Assert.Equal("subscription_limit",
            Assert.IsType<CallOutcome.Refused>(Assert.IsType<SubscribeOutcome.Failed>(outcome).Outcome).Code);
    }

    [Fact]
    public async Task AServerWithoutTheFeatureHasNoSubscriptions()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Features = ["shape", "cancel"];
        await using StationGodClient client = new(new ClientOptions(game.Target));

        Assert.IsType<SubscribeOutcome.Unsupported>(await client.SubscribeAsync(Json("""{"items":[]}""")));
    }

    [Fact]
    public async Task ClosingSaysByeOnVersionTwo()
    {
        await using FakeGame game = FakeGame.OnPipe();
        StationGodClient client = new(new ClientOptions(game.Target));
        await client.CallAsync("game_clock", Json("{}"));

        await client.DisposeAsync();

        await Eventually(() => game.Received.Any(message =>
            message.TryGetProperty("type", out JsonElement type) && type.ValueEquals("bye")));
    }

    internal static string CatalogueWithout(params string[] methods)
    {
        JsonObject catalogue = JsonNode.Parse(GameCatalogue.BuiltIn.Document.GetRawText())!.AsObject();
        JsonArray kept = new(catalogue["methods"]!.AsArray()
            .Where(method => !methods.Contains((string)method!["name"]!))
            .Select(method => method!.DeepClone())
            .ToArray());
        catalogue["methods"] = kept;
        return catalogue.ToJsonString();
    }

    internal static async Task Eventually(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
