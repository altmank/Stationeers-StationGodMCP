#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A call's world (protocol.md, A call's world): a call that names the world id from welcome or world_changed runs only
/// in that world. The id is compared on the main thread as the call starts, so a call queued across a world load with
/// the old id is refused world_changed, unrun, with the current world; a call without one runs as before; a malformed
/// one is refused at the reader.
/// </summary>
public sealed class CallWorldTests
{
    private static readonly WorldFacts WorldA = new WorldFacts("aaaaaaaaaaaaaaaa", "Vulcan", 2);
    private static readonly WorldFacts WorldB = new WorldFacts("bbbbbbbbbbbbbbbb", "Vulcan", 3);

    private volatile WorldFacts _world = WorldA;

    [Fact]
    public async Task ACallQueuedAcrossAWorldLoadWithTheOldIdIsRefusedUnrun()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(CallLine("1", WorldA.Id));
        WaitUntil(() => Queued(rig));
        _world = WorldB;

        rig.Mod.RunPending();
        JObject refused = client.Next();

        Assert.Equal("1", (string?)refused["id"]);
        Assert.False((bool)refused["ok"]!);
        Assert.Equal("world_changed", (string?)refused["error"]!["code"]);
        Assert.Equal(new JObject { ["id"] = WorldB.Id, ["save"] = "Vulcan", ["epoch"] = 3 },
            refused["error"]!["data"]!["world"]);
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task ACallWithTheCurrentIdRuns()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(CallLine("1", WorldA.Id));

        JObject reply = RunUntilReply(rig, client);

        Assert.True((bool)reply["ok"]!, reply.ToString());
        Assert.Equal(1, rig.Mod.Ran);
    }

    [Fact]
    public async Task ACallWithoutAWorldRunsWhateverTheWorld()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(CallLine("1", null));
        _world = WorldB;

        JObject reply = RunUntilReply(rig, client);

        Assert.True((bool)reply["ok"]!, reply.ToString());
        Assert.Equal(1, rig.Mod.Ran);
    }

    [Fact]
    public async Task WhileNoWorldRunsACallNamingOneIsRefused()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        _world = WorldFacts.None;
        client.Send(CallLine("1", WorldA.Id));

        JObject refused = RunUntilReply(rig, client);

        Assert.Equal("world_changed", (string?)refused["error"]!["code"]);
        Assert.Equal(string.Empty, (string?)refused["error"]!["data"]!["world"]!["id"]);
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("{\"id\":\"aaaaaaaaaaaaaaaa\"}")]
    [InlineData("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public async Task AMalformedWorldIsRefusedAtTheReaderAndNothingIsQueued(string world)
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send($$"""{"type":"call","id":"x","method":"read","params":{"what":"clock"},"world":{{world}}}""");

        JObject refused = client.Next();
        rig.Mod.RunPending();

        Assert.Equal("x", (string?)refused["id"]);
        Assert.Equal("invalid_argument", (string?)refused["error"]!["code"]);
        Assert.Contains("world", (string?)refused["error"]!["message"]);
        Assert.Equal(0, rig.Mod.Ran);
        Assert.Null(client.NextOrNull(50));
    }

    private PipeRig Rig() => new PipeRig(mainThread: false, world: () => _world);

    private static string CallLine(string id, string? world)
    {
        JObject call = new JObject { ["type"] = "call", ["id"] = id, ["method"] = "read", ["params"] = TestVerbs.Clock() };
        if (world != null)
        {
            call["world"] = world;
        }

        return call.ToString(Formatting.None);
    }

    private static bool Queued(PipeRig rig)
    {
        foreach (Connection connection in rig.Host.Open)
        {
            if (connection.Session?.InFlight > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static JObject RunUntilReply(PipeRig rig, V2Client client)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            rig.Mod.RunPending();
            if (client.NextOrNull(20) is JObject reply)
            {
                return reply;
            }
        }

        throw new TimeoutException("no reply");
    }

    private static void WaitUntil(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException("the condition never held");
            }

            Thread.Sleep(10);
        }
    }
}
