#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>
/// Subscriptions and read sample across a resume (protocol.md, Resuming), on a real pipe with the frames run by the
/// test: a held token's subscriptions take no samples, and on resume the next frame sends each one's reading whole to
/// the new connection, changed or not; past the window they are gone. A sampler run ends with its call's connection, unanswered.
/// </summary>
public sealed class ResumeSubscriptionTests
{
    private const string Token = "hub-session-0123456789abcdef";
    private const int WindowMs = 60000;

    private readonly ValueReader _reader = new ValueReader();
    private readonly ToggleLogic _logic = new ToggleLogic();
    private readonly FakeClock _clock = new FakeClock();
    private long _frame = 1;
    private double _gameTimeS;

    [Fact]
    public async Task AHeldSubscriptionTakesNoSamplesAndOnResumeSendsItsReadingWholeToTheNewConnection()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        string subscription;
        using (V2Client first = await Resume(rig))
        {
            subscription = (string)Call(rig, first, "1", "subscribe", DeviceParams(), stream: 2)["result"]!["subscription"]!;
        }

        WaitHeldAndClosed(rig);
        Frame(rig, atS: 0.5);
        int reads = _reader.Reads;
        Frame(rig, atS: 2.0);
        Frame(rig, atS: 3.0);
        Assert.Equal(reads, _reader.Reads);

        using V2Client second = await Resume(rig);
        Frame(rig, atS: 3.5);
        JObject update = second.Next();

        Assert.True((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal("update", (string?)update["event"]);
        Assert.Equal(subscription, (string?)update["subscription"]);
        Assert.Equal(1, (long)update["seq"]!);
        Assert.Equal(1.0, (double)update["result"]!["results"]![0]!["logic"]!["On"]!);

        _reader.Value = 0;
        Frame(rig, atS: 4.5);
        Assert.Equal(2, (long)second.Next()["seq"]!);
        JObject ended = Call(rig, second, "2", "unsubscribe", new JObject { ["subscription"] = subscription });
        Assert.True((bool)ended["ok"]!, ended.ToString());
    }

    [Fact]
    public async Task AfterTheWindowTheSubscriptionsAreGone()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        string subscription;
        using (V2Client first = await Resume(rig))
        {
            subscription = (string)Call(rig, first, "1", "subscribe", DeviceParams(), stream: 2)["result"]!["subscription"]!;
        }

        WaitHeldAndClosed(rig);
        Assert.True(rig.Mod.HeldOwners.TryPeek(out string? owner));
        Frame(rig, atS: 0.5);
        Assert.Equal(1, hub.CountOf(owner!));
        _clock.Advance(WindowMs);
        Frame(rig, atS: 1.0);
        Assert.Equal(0, hub.CountOf(owner!));

        using V2Client second = await Resume(rig);
        JObject gone = Call(rig, second, "2", "unsubscribe", new JObject { ["subscription"] = subscription });

        Assert.False((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal("unknown_subscription", (string?)gone["error"]!["code"]);
    }

    [Fact]
    public async Task AReadSampleRunEndsWithItsConnectionAndIsNotAnsweredOnResume()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub, strict: false);
        using (V2Client first = await Resume(rig))
        {
            first.Send(new JObject
            {
                ["type"] = "call", ["id"] = "run", ["method"] = "read", ["stream"] = 3,
                ["params"] = new JObject
                {
                    ["what"] = "sample",
                    ["id"] = new JArray("100"),
                    ["logic"] = "On",
                    ["seconds"] = 1,
                    ["interval"] = 0.1
                }
            }.ToString(Formatting.None));
            WaitUntil(() =>
            {
                Frame(rig, atS: 0.0);
                return hub.SampleRuns == 1;
            });
        }

        WaitHeldAndClosed(rig);
        Frame(rig, atS: 0.1);
        Assert.Equal(0, hub.SampleRuns);

        using V2Client second = await Resume(rig);
        for (int frame = 0; frame < 12; frame++)
        {
            Frame(rig, atS: 0.2 + frame * 0.1);
        }

        Assert.True((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Null(second.NextOrNull(200));
    }

    [Fact]
    public void ARequeuedSlotUsesTheWaitingUpdatesSeqAgainAndItsOldQueueEntryWritesNothing()
    {
        UpdateSlot<string> slot = new UpdateSlot<string>(new ConnectionId("r1"), new SubscriptionId(1), 0);
        Assert.True(slot.Offer("a", 1, 1.0, 0));
        int old = slot.Epoch;

        slot.Requeue();
        Assert.True(slot.Offer("b", 2, 2.0, 0));

        Assert.False(slot.TryTake(old, out _));
        Assert.True(slot.TryTake(slot.Epoch, out SubscriptionUpdate<string> update));
        Assert.Equal(1, update.Seq);
        Assert.Equal("b", update.Reading);
    }

    private SubscriptionHub Hub() => new SubscriptionHub(_reader, _logic, SubscriptionLimits.Default);

    // strict false: read {what: "sample"} with the sampler's own arguments (PipeRig.IsSampleRun).
    private PipeRig Rig(SubscriptionHub hub, bool strict = true) =>
        new PipeRig(mainThread: false, subscriptions: hub, settings: new ProtocolSettings(32, strictArguments: strict),
            tick: () => new SamplingTick(_frame, _gameTimeS),
            realTick: () => new RealTimeTick(_frame, _gameTimeS, DateTimeOffset.UnixEpoch),
            resumeWindowMs: WindowMs, resumeClock: _clock);

    private static JObject DeviceParams() => new JObject
    {
        ["reads"] = new JArray(new JObject { ["id"] = "5", ["logic"] = new JArray("On") }),
        ["interval_s"] = 1
    };

    private static async Task<V2Client> Resume(PipeRig rig)
    {
        V2Client client = await V2Client.Open(rig);
        client.Send(new JObject
        {
            ["type"] = "hello", ["protocol"] = new JArray(2), ["client"] = new JObject { ["name"] = "hub", ["version"] = "1" },
            ["resume"] = Token
        }.ToString(Formatting.None));
        client.Welcome = client.Next();
        Assert.Equal("welcome", (string?)client.Welcome["type"]);
        return client;
    }

    private void Frame(PipeRig rig, double atS)
    {
        _frame++;
        _gameTimeS = atS;
        rig.Mod.RunFrame();
    }

    // Sends the call, then runs frames until its reply comes.
    private JObject Call(PipeRig rig, V2Client client, string id, string method, JObject parameters, long stream = 0)
    {
        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = method, ["params"] = parameters, ["stream"] = stream
        }.ToString(Formatting.None));
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

    // The host holds the token (Holds.Held, then the hub's Held) before it passes the close on (the hub's Closed, then
    // ClosedSources), all on the connection's thread: once ClosedSources has it, the next frame takes in both.
    private static void WaitHeldAndClosed(PipeRig rig) =>
        WaitUntil(() => rig.Holds.Held == 1 && rig.Mod.ClosedSources.Count == 1);

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

    /// <summary>One device whose On value the test sets.</summary>
    private sealed class ValueReader : IDeviceReader<ReadDevicesView>
    {
        internal double Value { get; set; } = 1;

        internal int Reads { get; private set; }

        public ReadDevicesView Read(DeviceSubscriptionQuery query)
        {
            Reads++;
            BatchBuilder batch = new BatchBuilder(1);
            batch.Succeeded(new DeviceReadItemView(0, new ThingId(5), new DeviceReadParts(
                new LogicReadings(new Dictionary<string, double> { ["On"] = Value }, new Dictionary<string, ErrorView>()),
                null, null, null, null)));
            return new ReadDevicesView("world", null, batch.Build());
        }
    }

    /// <summary>One On value per target, flipping every sample.</summary>
    private sealed class ToggleLogic : ILogicSampleReader<BatchItemView>
    {
        private int _samples;

        public LogicSampleRead Read(SampleRequest arguments, List<BatchItemView> into)
        {
            int sample = _samples++;
            for (int target = 0; target < arguments.Targets.Count; target++)
            {
                into.Add(new LogicReadItemView(target,
                    new LogicRead(new ThingId(100 + target), new LogicTypeView(28, "On"), sample % 2)));
            }

            return LogicSampleRead.Read("world");
        }
    }
}
