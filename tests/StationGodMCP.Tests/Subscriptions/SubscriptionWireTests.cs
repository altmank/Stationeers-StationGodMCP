#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>
/// Subscriptions over version 2 on a real pipe, with the lane scheduler's frames run by the test: subscribe answers
/// the first reading, updates come only on a change and carry a later frame, unsubscribe and unknown ids, the world
/// topic's events, a new world ending devices subscriptions, limits, and a closed connection dropping its
/// subscriptions.
/// </summary>
public sealed class SubscriptionWireTests
{
    private readonly ValueReader _reader = new ValueReader();
    private long _frame = 1;
    private double _gameTimeS;

    [Fact]
    public async Task WelcomeListsSubscriptionsAndTheirLimits()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        Assert.Contains("subscriptions", client.Welcome["features"]!.Values<string>());
        JObject limits = (JObject)client.Welcome["limits"]!;
        Assert.Equal(64, (int)limits["max_subscriptions"]!);
        Assert.Equal(8192, (int)limits["max_subscription_values"]!);
        Assert.Equal(1024, (int)limits["max_values_per_subscription"]!);
        Assert.Equal(0.5, (double)limits["min_subscription_interval_s"]!);
    }

    [Fact]
    public async Task ASubscriptionSendsItsFirstReadingThenUpdatesOnlyOnAChange()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = Call(rig, client, "1", "subscribe", DeviceParams());
        Assert.True((bool)reply["ok"]!, reply.ToString());
        JObject result = (JObject)reply["result"]!;
        string subscription = (string)result["subscription"]!;
        Assert.Matches("^s[0-9]+$", subscription);
        Assert.Equal(1.0, (double)result["interval_s"]!);
        Assert.Equal(1, (int)result["values"]!);
        Assert.Equal(1.0, (double)result["result"]!["results"]![0]!["logic"]!["On"]!);
        long subscribedFrame = (long)reply["frame"]!;

        Frame(rig, atS: 1.0);
        Assert.Null(client.NextOrNull(200));

        _reader.Value = 0;
        Frame(rig, atS: 2.0);
        JObject update = client.Next();
        Assert.Equal("event", (string?)update["type"]);
        Assert.Equal("update", (string?)update["event"]);
        Assert.Equal(subscription, (string?)update["subscription"]);
        Assert.Equal(1, (long)update["seq"]!);
        Assert.True((long)update["frame"]! > subscribedFrame);
        Assert.Equal(2.0, (double)update["game_time_s"]!);
        Assert.Equal(0.0, (double)update["result"]!["results"]![0]!["logic"]!["On"]!);

        _reader.Value = 1;
        Frame(rig, atS: 3.0);
        Assert.Equal(2, (long)client.Next()["seq"]!);
    }

    [Fact]
    public async Task APausedGameSamplesNothing()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        Call(rig, client, "1", "subscribe", DeviceParams());
        int reads = _reader.Reads;

        _reader.Value = 0;
        Frame(rig, atS: 0.0);
        Frame(rig, atS: 0.0);

        Assert.Equal(reads, _reader.Reads);
        Assert.Null(client.NextOrNull(200));
    }

    [Fact]
    public async Task UnsubscribeEndsASubscriptionAndAnUnknownIdIsRefused()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        string subscription = (string)Call(rig, client, "1", "subscribe", DeviceParams())["result"]!["subscription"]!;

        JObject ended = Call(rig, client, "2", "unsubscribe", new JObject { ["subscription"] = subscription });
        Assert.True((bool)ended["ok"]!);
        Assert.Equal(subscription, (string?)ended["result"]!["subscription"]);
        Assert.True((bool)ended["result"]!["ended"]!);
        Assert.Equal(0, hub.CountOf(ClientId(client)));

        _reader.Value = 0;
        Frame(rig, atS: 5.0);
        Assert.Null(client.NextOrNull(200));

        JObject again = Call(rig, client, "3", "unsubscribe", new JObject { ["subscription"] = subscription });
        Assert.False((bool)again["ok"]!);
        Assert.Equal("unknown_subscription", (string?)again["error"]!["code"]);
    }

    [Fact]
    public async Task TheWorldTopicHearsWorldsAndGameStatesAndANewWorldEndsDeviceSubscriptions()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        string devices = (string)Call(rig, client, "1", "subscribe", DeviceParams())["result"]!["subscription"]!;
        JObject worldReply = Call(rig, client, "2", "subscribe", new JObject { ["topic"] = "world" });
        string world = (string)worldReply["result"]!["subscription"]!;
        Assert.Null(worldReply["result"]!["result"]);
        Assert.Equal(0, (int)worldReply["result"]!["values"]!);

        rig.Mod.BeforeFrame += () => hub.ObserveGameState("Paused");
        Frame(rig, atS: 0.5);
        JObject state = client.Next();
        Assert.Equal(new JObject
        {
            ["type"] = "event", ["event"] = "game_state", ["subscription"] = world, ["state"] = "Paused"
        }, state);

        rig.Mod.BeforeFrame += () => hub.WorldChanged("3b9e1a40c2d84f6e");
        Frame(rig, atS: 1.0);
        List<JObject> events = new List<JObject> { client.Next(), client.Next() };
        JObject endedEvent = events.Single(message => (string?)message["event"] == "subscription_ended");
        Assert.Equal(devices, (string?)endedEvent["subscription"]);
        Assert.Equal("world_changed", (string?)endedEvent["reason"]);
        JObject worldEvent = events.Single(message => (string?)message["event"] == "world_changed");
        Assert.Equal(world, (string?)worldEvent["subscription"]);
        Assert.Equal("3b9e1a40c2d84f6e", (string?)worldEvent["world"]!["id"]);
        Assert.Equal(1, hub.CountOf(ClientId(client)));
    }

    [Fact]
    public async Task ASubscriptionOverALimitIsRefusedWithItsData()
    {
        using PipeRig rig = Rig(new SubscriptionHub(_reader, NoLogic.Instance, new SubscriptionLimits(1, 8192, 1.5)));
        using V2Client client = await V2Client.Connect(rig);
        Assert.True((bool)Call(rig, client, "1", "subscribe", DeviceParams())["ok"]!);

        JObject refused = Call(rig, client, "2", "subscribe", DeviceParams());

        Assert.False((bool)refused["ok"]!);
        Assert.Equal("subscription_limit", (string?)refused["error"]!["code"]);
        JObject data = (JObject)refused["error"]!["data"]!;
        Assert.Equal("max_subscriptions", (string?)data["name"]);
        Assert.Equal(1.0, (double)data["limit"]!);
        Assert.Equal(2.0, (double)data["requested"]!);
        Assert.NotNull(data["projected_ms_per_s"]);
    }

    [Fact]
    public async Task SubscriptionsOffRefusesEverySubscribe()
    {
        using PipeRig rig = Rig(new SubscriptionHub(_reader, NoLogic.Instance, SubscriptionLimits.From(0, 4)));
        using V2Client client = await V2Client.Connect(rig);

        JObject refused = Call(rig, client, "1", "subscribe", DeviceParams());

        Assert.Equal("subscription_limit", (string?)refused["error"]!["code"]);
        Assert.Equal("subscription_budget_ms", (string?)refused["error"]!["data"]!["name"]);
    }

    [Fact]
    public async Task ABadSubscribeIsAnInvalidArgumentAndAFailingReadIsTheReadsError()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject bad = Call(rig, client, "1", "subscribe", new JObject { ["topic"] = "world", ["interval_s"] = 2 });
        Assert.Equal("invalid_argument", (string?)bad["error"]!["code"]);

        _reader.Refusal = new ApiException("gateway_not_found", "No gateway 9.");
        JObject failed = Call(rig, client, "2", "subscribe", DeviceParams());
        Assert.Equal("gateway_not_found", (string?)failed["error"]!["code"]);
        Assert.NotNull(failed["frame"]);
    }

    [Fact]
    public async Task AClosedConnectionsSubscriptionsEndWithoutAnEvent()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        V2Client client = await V2Client.Connect(rig);
        Call(rig, client, "1", "subscribe", DeviceParams());
        string clientId = ClientId(client);
        Assert.Equal(1, hub.CountOf(clientId));

        client.Dispose();
        Stopwatch waited = Stopwatch.StartNew();
        while (rig.Mod.ClosedSources.IsEmpty && waited.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(20);
        }

        Frame(rig, atS: 1.0);
        Assert.Equal(0, hub.CountOf(clientId));
    }

    [Fact]
    public async Task UpdatesWaitInTheOutboundQueueAsTheNewestReadingWithoutASeqGap()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        Call(rig, client, "1", "subscribe", DeviceParams());
        Connection connection = rig.Host.Open.Single();
        UpdateHold hold = new UpdateHold();
        connection.Send(hold);

        _reader.Value = 0;
        Frame(rig, atS: 1.0);
        _reader.Value = 7;
        Frame(rig, atS: 2.0);
        Assert.Null(client.NextOrNull(200));
        hold.Release();

        JObject update = client.Next();
        Assert.Equal(1, (long)update["seq"]!);
        Assert.Equal(7.0, (double)update["result"]!["results"]![0]!["logic"]!["On"]!);
        Assert.Null(client.NextOrNull(200));
    }

    private static JObject DeviceParams() => new JObject
    {
        ["items"] = new JArray(new JObject { ["reference_id"] = "5", ["logic"] = new JArray("On") }),
        ["interval_s"] = 1
    };

    private SubscriptionHub Hub() => new SubscriptionHub(_reader, NoLogic.Instance, SubscriptionLimits.Default);

    private PipeRig Rig(SubscriptionHub? hub = null) =>
        new PipeRig(mainThread: false, subscriptions: hub ?? Hub(), tick: () => new SamplingTick(_frame, _gameTimeS));

    private void Frame(PipeRig rig, double atS)
    {
        _frame++;
        _gameTimeS = atS;
        rig.Mod.RunFrame();
    }

    private static string ClientId(V2Client client) => (string)client.Welcome["client_id"]!;

    // Sends the call, then runs frames until its reply comes.
    private JObject Call(PipeRig rig, V2Client client, string id, string method, JObject parameters)
    {
        client.Send(new JObject { ["type"] = "call", ["id"] = id, ["method"] = method, ["params"] = parameters }
            .ToString(Formatting.None));
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            _frame++;
            rig.Mod.RunFrame();
            JObject? message = client.NextOrNull(20);
            if (message != null && (string?)message["type"] == "reply" && (string?)message["id"] == id)
            {
                return message;
            }
        }

        throw new TimeoutException($"no reply to {method}");
    }

    /// <summary>No sample_logic in these tests.</summary>
    private sealed class NoLogic : ILogicSampleReader<BatchItemView>
    {
        internal static readonly NoLogic Instance = new NoLogic();

        public LogicSampleRead Read(StationGodMCP.Pure.Sampling.SampleLogicArguments arguments, List<BatchItemView> into) =>
            throw new InvalidOperationException("No sample_logic here.");
    }

    /// <summary>One device whose On value the test sets.</summary>
    private sealed class ValueReader : IDeviceReader<ReadDevicesView>
    {
        internal double Value { get; set; } = 1;

        internal int Reads { get; private set; }

        internal ApiException? Refusal { get; set; }

        public ReadDevicesView Read(DeviceSubscriptionQuery query)
        {
            if (Refusal != null)
            {
                throw Refusal;
            }

            Reads++;
            BatchBuilder batch = new BatchBuilder(1);
            batch.Succeeded(new DeviceReadItemView(0, new ThingId(5), new DeviceReadParts(
                new LogicReadings(new Dictionary<string, double> { ["On"] = Value }, new Dictionary<string, ErrorView>()),
                null, null, null, null)));
            return new ReadDevicesView("world", null, batch.Build());
        }
    }

    /// <summary>A line that holds the writer until the test lets it go, as a client that stopped reading would.</summary>
    private sealed class UpdateHold : IOutboundLine
    {
        private readonly System.Threading.ManualResetEventSlim _released = new System.Threading.ManualResetEventSlim(false);

        internal void Release() => _released.Set();

        public bool TryTake(out string line)
        {
            _released.Wait(10000);
            line = string.Empty;
            return false;
        }
    }
}
