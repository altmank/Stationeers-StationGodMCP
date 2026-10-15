#nullable enable

using System;
using System.Collections.Generic;
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
/// Step 6 (cost design A6): the find topic on a real pipe. The first subscriber's reply waits for the query's first
/// evaluation and carries the whole list; a later evaluation sends each subscriber a find_update with only what changed,
/// and none when nothing did; a second connection joins the query without evaluating it again.
/// </summary>
public sealed class FindTopicWireTests
{
    private readonly WireFinds _finds = new WireFinds();
    private long _frame = 1;
    private double _gameTimeS;

    [Fact]
    public async Task AFindSubscriptionAnswersTheListThenOnlyWhatChanged()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        _finds.Reply = """{"frames": [{"reference_id": "1", "color": "Red"}], "total_frames": 1, "count": 1}""";

        JObject reply = Call(rig, client, "1", FrameParams());
        Assert.True((bool)reply["ok"]!, reply.ToString());
        JObject result = (JObject)reply["result"]!;
        string subscription = (string)result["subscription"]!;
        Assert.Equal("find", (string?)result["topic"]);
        Assert.Equal("frame", (string?)result["what"]);
        Assert.Equal(10.0, (double)result["interval_s"]!);
        Assert.Equal("reference_id", (string?)result["keyed"]!["frames"]);
        Assert.Equal("1", (string?)result["result"]!["frames"]![0]!["reference_id"]);
        Assert.Null(result["result"]!["count"]);

        Frame(rig, atS: 10.0);
        Assert.Null(client.NextOrNull(300));

        _finds.Reply = """{"frames": [{"reference_id": "1", "color": "Blue"}, {"reference_id": "2", "color": "Red"}], "total_frames": 2}""";
        Frame(rig, atS: 20.0);
        JObject update = client.Next();
        Assert.Equal("find_update", (string?)update["event"]);
        Assert.Equal(subscription, (string?)update["subscription"]);
        Assert.Equal(1, (long)update["seq"]!);
        Assert.Equal("2", (string?)update["lists"]!["frames"]!["added"]![0]!["reference_id"]);
        Assert.Equal("Blue", (string?)update["lists"]!["frames"]!["changed"]![0]!["color"]);
        Assert.Empty((JArray)update["lists"]!["frames"]!["removed"]!);
        Assert.Equal(2, (int)update["rest"]!["total_frames"]!);
    }

    [Fact]
    public async Task ASecondConnectionJoinsTheQueryWithoutEvaluatingItAgain()
    {
        using PipeRig rig = Rig();
        using V2Client first = await V2Client.Connect(rig);
        using V2Client second = await V2Client.Connect(rig);

        Assert.True((bool)Call(rig, first, "1", FrameParams())["ok"]!);
        JObject joined = Call(rig, second, "1", FrameParams());

        Assert.True((bool)joined["ok"]!, joined.ToString());
        Assert.Equal(1, _finds.Started);
    }

    [Fact]
    public async Task PagingAndShapingAreRefusedWithTheReason()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        JObject parameters = FrameParams();
        parameters["limit"] = 5;

        JObject reply = Call(rig, client, "1", parameters);

        Assert.False((bool)reply["ok"]!);
        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Contains("whole list", (string?)reply["error"]!["message"]);
    }

    private static JObject FrameParams() => new JObject { ["topic"] = "find", ["what"] = "frame" };

    private PipeRig Rig() =>
        new PipeRig(mainThread: false,
            subscriptions: new SubscriptionHub(NoDevices.Instance, NoLogic.Instance, SubscriptionLimits.Default,
                finds: _finds, news: NoNews.Instance),
            tick: () => new SamplingTick(_frame, _gameTimeS));

    private void Frame(PipeRig rig, double atS)
    {
        _frame++;
        _gameTimeS = atS;
        rig.Mod.RunFrame();
    }

    // Sends the call and runs frames until its reply arrives (a find subscribe may answer a frame later).
    private JObject Call(PipeRig rig, V2Client client, string id, JObject parameters)
    {
        int submitted = rig.Mod.Submitted;
        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "subscribe", ["params"] = parameters, ["stream"] = 0
        }.ToString(Formatting.None));
        rig.Mod.WaitSubmitted(submitted + 1);
        for (int frame = 0; frame < 10; frame++)
        {
            _frame++;
            rig.Mod.RunFrame();
            while (client.NextOrNull(100) is JObject message)
            {
                if ((string?)message["type"] == "reply" && (string?)message["id"] == id)
                {
                    return message;
                }
            }
        }

        throw new TimeoutException("No reply to the subscribe.");
    }

    /// <summary>Every evaluation answers Reply in one step.</summary>
    private sealed class WireFinds : IFindEvaluator
    {
        internal string Reply { get; set; } = """{"frames": []}""";

        internal int Started { get; private set; }

        public IFindEvaluation Start(FindQuery query)
        {
            Started++;
            return new OneStep(Reply);
        }

        private sealed class OneStep : IFindEvaluation
        {
            private readonly string _reply;

            internal OneStep(string reply) => _reply = reply;

            public FindResult? Result { get; private set; }

            public FindFailure? Failure => null;

            public bool Step(double untilMs)
            {
                Result = new FindResult(new object[] { JObject.Parse(_reply) }, page => (JObject)page, 0.0, 1, 0.1, 0);
                return true;
            }

            public void Cancel()
            {
            }
        }
    }

    private sealed class NoNews : IWorldNews
    {
        internal static readonly NoNews Instance = new NoNews();

        public long Version(FindChannels channels) => 0;
    }

    private sealed class NoDevices : IDeviceReader<ReadDevicesView>
    {
        internal static readonly NoDevices Instance = new NoDevices();

        public ReadDevicesView Read(DeviceSubscriptionQuery query) =>
            throw new InvalidOperationException("No devices here.");
    }

    private sealed class NoLogic : ILogicSampleReader<BatchItemView>
    {
        internal static readonly NoLogic Instance = new NoLogic();

        public LogicSampleRead Read(SampleRequest arguments, List<BatchItemView> into) =>
            throw new InvalidOperationException("No read sample here.");
    }
}
