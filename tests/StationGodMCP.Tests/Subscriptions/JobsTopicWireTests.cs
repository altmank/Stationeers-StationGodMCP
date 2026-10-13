#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// The jobs topic on a real pipe: subscribe {topic: "jobs"} answers no reading, and every jobs subscription of every
/// client gets one job_ended event per ended job (finished, failed or dropped), with the job's facts and its own seq;
/// an unsubscribed or ended-stream subscription gets nothing, and a new world leaves the topic in place.
/// </summary>
public sealed class JobsTopicWireTests
{
    private long _frame = 1;

    [Fact]
    public async Task AFinishedJobReachesItsSubscriberAsOneJobEndedEvent()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = Call(rig, client, "1", "subscribe", new JObject { ["topic"] = "jobs" });
        Assert.True((bool)reply["ok"]!, reply.ToString());
        string subscription = (string)reply["result"]!["subscription"]!;
        Assert.Equal(0, (int)reply["result"]!["values"]!);
        Assert.Null(reply["result"]!["result"]);

        hub.JobEnded(FinishedRun(), 120);

        JObject ended = client.Next();
        Assert.Equal(new JObject
        {
            ["type"] = "event", ["event"] = "job_ended", ["subscription"] = subscription, ["seq"] = 1,
            ["frame"] = 120,
            ["job"] = new JObject
            {
                ["job_id"] = "run-7", ["tool"] = "place_pipes", ["status"] = "applied",
                ["created_ids"] = new JArray("101", "102"), ["removed"] = new JArray("55"),
                ["changed"] = new JArray(new JObject
                {
                    ["old_reference_id"] = "60", ["new_reference_id"] = "102",
                    ["target_prefab_name"] = "StructurePipeTJunction"
                }),
                ["placed"] = new JArray(new JObject
                {
                    ["reference_id"] = "101", ["prefab_name"] = "StructurePipeStraight"
                })
            }
        }, ended);
        Assert.Null(client.NextOrNull(200));
    }

    [Fact]
    public async Task FailedAndDroppedJobsAreEventsTooWithTheirErrorAndTheNextSeq()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        Call(rig, client, "1", "subscribe", new JObject { ["topic"] = "jobs" });

        hub.JobEnded(new RunJobView("run-8", "place_cables", "stopped", null,
            new RunJobResultView(null, new RunLogView(), null, new ErrorView("internal_error", "The step threw."))), 130);
        hub.JobEnded(new JobDroppedView("place-9", "place_structure", JobDrops.Dropped()), 131);

        JObject failed = client.Next();
        Assert.Equal(1, (long)failed["seq"]!);
        Assert.Equal("stopped", (string?)failed["job"]!["status"]);
        Assert.Equal("internal_error", (string?)failed["job"]!["error"]!["code"]);
        JObject dropped = client.Next();
        Assert.Equal(2, (long)dropped["seq"]!);
        Assert.Equal(new JObject
        {
            ["job_id"] = "place-9", ["tool"] = "place_structure", ["status"] = "refused",
            ["error"] = new JObject
            {
                ["code"] = "dropped", ["message"] = "Dropped from the queue before it started; nothing was changed."
            }
        }, dropped["job"]);
    }

    [Fact]
    public async Task EverySubscriberOfEveryClientGetsEveryJobAndEndedOnesGetNothing()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client first = await V2Client.Connect(rig);
        using V2Client second = await V2Client.Connect(rig);
        string firstJobs = (string)Call(rig, first, "1", "subscribe", new JObject { ["topic"] = "jobs" })["result"]!["subscription"]!;
        string secondJobs = (string)Call(rig, second, "1", "subscribe", new JObject { ["topic"] = "jobs" })["result"]!["subscription"]!;
        string unsubscribed = (string)Call(rig, second, "2", "subscribe", new JObject { ["topic"] = "jobs" })["result"]!["subscription"]!;
        Call(rig, second, "3", "subscribe", new JObject { ["topic"] = "jobs" }, stream: 5);
        Assert.True((bool)Call(rig, second, "4", "unsubscribe", new JObject { ["subscription"] = unsubscribed })["ok"]!);
        second.Send("""{"type":"stream_end","stream":5}""");
        Stopwatch waited = Stopwatch.StartNew();
        while (hub.CountOf(ClientId(second)) != 1 && waited.ElapsedMilliseconds < 5000)
        {
            Frame(rig);
            await Task.Delay(10);
        }

        hub.JobEnded(FinishedRun(), 140);

        Assert.Equal(firstJobs, (string?)first.Next()["subscription"]);
        Assert.Equal(secondJobs, (string?)second.Next()["subscription"]);
        Assert.Null(first.NextOrNull(200));
        Assert.Null(second.NextOrNull(200));
    }

    [Fact]
    public async Task ANewWorldLeavesTheJobsTopicInPlace()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        Call(rig, client, "1", "subscribe", new JObject { ["topic"] = "jobs" });

        hub.WorldChanged("3b9e1a40c2d84f6e");
        hub.JobEnded(FinishedRun(), 150);

        Assert.Equal("job_ended", (string?)client.Next()["event"]);
        Assert.Equal(1, hub.CountOf(ClientId(client)));
    }

    [Fact]
    public async Task TheJobsTopicTakesTopicAloneAndCountsAgainstTheLimit()
    {
        using PipeRig rig = Rig(new SubscriptionHub(NoDevices.Instance, NoLogic.Instance, new SubscriptionLimits(1, 8192, 1.5)));
        using V2Client client = await V2Client.Connect(rig);

        JObject bad = Call(rig, client, "1", "subscribe", new JObject { ["topic"] = "jobs", ["job_ids"] = new JArray("run-1") });
        Assert.Equal("invalid_argument", (string?)bad["error"]!["code"]);
        Assert.True((bool)Call(rig, client, "2", "subscribe", new JObject { ["topic"] = "world" })["ok"]!);
        JObject over = Call(rig, client, "3", "subscribe", new JObject { ["topic"] = "jobs" });
        Assert.Equal("subscription_limit", (string?)over["error"]!["code"]);
        Assert.Equal("max_subscriptions", (string?)over["error"]!["data"]!["name"]);
    }

    [Fact]
    public void WithNoSubscriberAnEndedJobIsNotDescribed()
    {
        SubscriptionHub hub = Hub();
        Probe probe = new Probe();

        hub.JobEnded(probe, 1);

        Assert.False(probe.Read);
    }

    // A place_pipes run that built 101 (new) and 102 (a tee in place of 60), and removed 55.
    private static RunJobView FinishedRun()
    {
        RunLogView log = new RunLogView();
        log.Removed.Add(new ThingId(55));
        log.Changed.Add(new UpgradeSwappedView(new ThingId(60), new ThingId(102), "StructurePipeStraight",
            "StructurePipeTJunction"));
        log.PlacedPieces.Add(new ThingView(new ThingId(101), "StructurePipeStraight", "Pipe"));
        log.AddCreated("run", new ThingId(101));
        log.AddCreated("run", new ThingId(102));
        return new RunJobView("run-7", "place_pipes", "applied", null, new RunJobResultView(null, log, null, null));
    }

    private static SubscriptionHub Hub() =>
        new SubscriptionHub(NoDevices.Instance, NoLogic.Instance, SubscriptionLimits.Default);

    private PipeRig Rig(SubscriptionHub hub) =>
        new PipeRig(mainThread: false, subscriptions: hub, tick: () => new SamplingTick(_frame, 0.0));

    private void Frame(PipeRig rig)
    {
        _frame++;
        rig.Mod.RunFrame();
    }

    private static string ClientId(V2Client client) => (string)client.Welcome["client_id"]!;

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
            Frame(rig);
            JObject? message = client.NextOrNull(20);
            if (message != null && (string?)message["type"] == "reply" && (string?)message["id"] == id)
            {
                return message;
            }
        }

        throw new TimeoutException($"no reply to {method}");
    }

    /// <summary>A view that notes whether it was serialised.</summary>
    private sealed class Probe
    {
        internal bool Read { get; private set; }

        public string JobId
        {
            get
            {
                Read = true;
                return "run-1";
            }
        }
    }

    private sealed class NoDevices : IDeviceReader<ReadDevicesView>
    {
        internal static readonly NoDevices Instance = new NoDevices();

        public ReadDevicesView Read(DeviceSubscriptionQuery query) =>
            throw new InvalidOperationException("No device reads here.");
    }

    private sealed class NoLogic : ILogicSampleReader<BatchItemView>
    {
        internal static readonly NoLogic Instance = new NoLogic();

        public LogicSampleRead Read(SampleRequest arguments, List<BatchItemView> into) =>
            throw new InvalidOperationException("No read sample here.");
    }
}
