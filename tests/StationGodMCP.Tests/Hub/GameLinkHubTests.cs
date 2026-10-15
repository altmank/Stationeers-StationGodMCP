#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>What the hub's sessions need of GameLink: the request bound, stream ends that never wait, reply keys kept.</summary>
public sealed class GameLinkHubTests
{
    [Fact]
    public async Task ACallOverMaxRequestBytesIsRefusedUnsentAndTheConnectionStays()
    {
        await using FakeGame game = HubGame();
        game.MaxRequestBytes = 1024;
        await using GameLink link = new(new ClientOptions(game.Target));
        await link.ConnectAsync();

        GameReply big = await link.CallAsync(new GameCall("find",
            Json("""{"reference_ids":[""" + "\"" + new string('5', 2000) + "\"]}")), default);
        GameReply small = await link.CallAsync(new GameCall("find", Json("""{"what":"thing","name":"5"}""")), default);

        GameReply.Refused refused = Assert.IsType<GameReply.Refused>(big);
        Assert.Equal("request_too_large", refused.Code);
        Assert.Equal(1024, refused.Data!.Value.GetProperty("limit").GetInt32());
        Assert.IsType<GameReply.Ok>(small);
        Assert.Single(game.CallsTo("find"));
        Assert.Equal(1, game.Connections);
        Assert.Equal(1024, link.MaxRequestBytes);
    }

    [Fact]
    public async Task EndStreamIfConnectedNeverConnectsAndSendsOnAnOpenConnection()
    {
        await using GameLink down = new(new ClientOptions(new GameTarget.Pipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N"))));
        Stopwatch waited = Stopwatch.StartNew();
        await down.EndStreamIfConnectedAsync(3);
        Assert.True(waited.Elapsed < TimeSpan.FromMilliseconds(200), $"It waited {waited.Elapsed}.");

        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        await link.ConnectAsync();
        await link.EndStreamIfConnectedAsync(4);
        await WaitUntil(() => game.Received.Any(message => Type(message) == "stream_end"));

        JsonElement ended = game.Received.First(message => Type(message) == "stream_end");
        Assert.Equal(4, ended.GetProperty("stream").GetInt64());
    }

    [Fact]
    public async Task ARepliesShapedAndQueueMsAndAnErrorsMaybeRanAreKept()
    {
        await using FakeGame game = HubGame();
        game.Answer = call => Task.FromResult<string?>(call.Method == "find"
            ? call.Ok("{}", shaped: true)
            : "{\"type\":\"reply\",\"id\":" + call.Id.GetRawText() +
              ",\"ok\":false,\"error\":{\"code\":\"internal_error\",\"message\":\"Partly ran.\",\"maybe_ran\":true}}");
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply.Ok ok = Assert.IsType<GameReply.Ok>(await link.CallAsync(new GameCall("find", Json("""{"what":"thing"}""")), default));
        GameReply.Refused refused = Assert.IsType<GameReply.Refused>(
            await link.CallAsync(new GameCall("read.clock", Json("{}")), default));

        Assert.True(ok.Shaped);
        Assert.Equal(0.2, ok.QueueMs);
        Assert.True(refused.MaybeRan);
    }

    private static FakeGame HubGame()
    {
        FakeGame game = FakeGame.OnPipe();
        game.Features = ["shape", "subscriptions", "cancel", "streams", "resume"];
        return game;
    }

    private static string? Type(JsonElement message) =>
        message.TryGetProperty("type", out JsonElement type) ? type.GetString() : null;

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
