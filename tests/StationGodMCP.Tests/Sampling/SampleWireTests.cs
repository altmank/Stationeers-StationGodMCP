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
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Sampling;

/// <summary>
/// read {what: "sample"} over a real pipe with the lane scheduler's frames run by the test: its handler (ReadSample)
/// starts a run in the subscription lane on the real clock, and its call waits in the slice lane until the run's last
/// sample is taken, answering the catalogue's reply; an unusable request is refused at once; a failed read fails the
/// call; a closed connection drops its run; its stream's end answers it cancelled; and it waits in the queue for its
/// deadline plus the seconds it runs (x-cost duration).
/// </summary>
public sealed class SampleWireTests
{
    private readonly ToggleReader _logic = new ToggleReader();
    private readonly Scheduling.FakeClock _clock = new Scheduling.FakeClock();
    private long _frame = 1;
    private double _realS;

    [Fact]
    public async Task ACallIsAnsweredWhenItsLastSampleIsTaken()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", Watching(0.3, 0.1)));
        StartRuns(rig, hub, 1, atS: 0.0);
        Frame(rig, 0.0);

        Assert.Null(client.NextOrNull(100));
        foreach (double at in new[] { 0.1, 0.2 })
        {
            Frame(rig, at);
            Assert.Null(client.NextOrNull(50));
        }

        JObject reply = NextAfterFrames(rig, client, 0.3);
        Assert.Equal("reply", (string?)reply["type"]);
        Assert.True((bool)reply["ok"]!, reply.ToString());
        JObject result = (JObject)reply["result"]!;
        Assert.Equal(4, (int)result["sample_count"]!);
        Assert.Equal(4, (int)result["change_count"]!);
        Assert.Equal(new[] { 0.0, 0.1, 0.2, 0.3 }, AtOf(result));
        Assert.Equal(0.3, (double)result["duration_seconds"]!);
        Assert.Equal(0.1, (double)result["interval_seconds"]!);
        Assert.Equal("world", (string?)result["gateway_id"]);
        Assert.NotNull(reply["frame"]);
        Assert.NotNull(reply["elapsed_ms"]);
        Assert.Equal(0, hub.SampleRuns);
    }

    [Fact]
    public async Task TheSamplesAreTheSubscriptionLanes()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", Watching(0.1, 0.1)));
        StartRuns(rig, hub, 1, atS: 0.0);

        _frame++;
        _realS = 0.0;
        Assert.Equal(1, rig.Mod.RunFrame().Samples);
        _frame++;
        _realS = 0.1;
        Assert.Equal(1, rig.Mod.RunFrame().Samples);
        Assert.Equal(2, (int)NextAfterFrames(rig, client, 0.1)["result"]!["sample_count"]!);
    }

    [Fact]
    public async Task AnUnusableRequestIsRefusedAtOnce()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", Watching(30, 0.05)));

        JObject reply = NextAfterFrames(rig, client, 0.0);

        Assert.False((bool)reply["ok"]!);
        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Contains("at most 120", reply["error"]!.ToString(Formatting.None));
        Assert.Equal(0, hub.SampleRuns);
    }

    [Fact]
    public async Task AFailedReadFailsTheCallWithItsError()
    {
        SubscriptionHub hub = Hub();
        _logic.Failure = ("gateway_not_found", "No gateway gw9.");
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", Watching(5, 0.5)));

        JObject reply = NextAfterFrames(rig, client, 0.0);

        Assert.Equal("gateway_not_found", (string?)reply["error"]!["code"]);
        Assert.Equal(0, hub.SampleRuns);
    }

    [Fact]
    public async Task AClosedConnectionDropsItsRun()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", Watching(5, 0.5)));
        StartRuns(rig, hub, 1, atS: 0.0);

        client.Dispose();
        Stopwatch waited = Stopwatch.StartNew();
        while (rig.Mod.ClosedSources.IsEmpty)
        {
            Assert.True(waited.ElapsedMilliseconds < 30000, "the close never reached the mod");
            await Task.Delay(20);
        }

        Frame(rig, 0.5);
        Assert.Equal(0, hub.SampleRuns);
    }

    [Fact]
    public async Task EndingItsStreamAnswersAnUnfinishedRunCancelledAndLeavesOtherStreamsRuns()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(CallOn("1", Watching(5, 0.5), 3));
        client.Send(CallOn("2", Watching(5, 0.5), 0));
        StartRuns(rig, hub, 2, atS: 0.0);

        client.Send("""{"type":"stream_end","stream":3}""");
        JObject reply = NextAfterFrames(rig, client, 0.0);

        Assert.Equal("1", (string?)reply["id"]);
        Assert.False((bool)reply["ok"]!);
        Assert.Equal("cancelled", (string?)reply["error"]!["code"]);
        Assert.Equal(1, hub.SampleRuns);
    }

    [Fact]
    public async Task ACallWaitsInTheQueueForItsDeadlinePlusItsDuration()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        JObject call = JObject.Parse(Call("1", Watching(1, 0.5)));
        call["deadline_ms"] = 100;
        client.Send(call.ToString(Formatting.None));

        // Past its 100 ms deadline, but not past the deadline plus its 1 s of watching: it still starts.
        rig.Mod.WaitSubmitted(1);
        _clock.Advance(400);
        rig.Deadlines.CheckNow();
        StartRuns(rig, hub, 1, atS: 0.0);
        Assert.Null(client.NextOrNull(50));
    }

    [Fact]
    public async Task ACallPastItsDeadlinePlusItsDurationIsNotStarted()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        JObject call = JObject.Parse(Call("1", Watching(1, 0.5)));
        call["deadline_ms"] = 100;
        client.Send(call.ToString(Formatting.None));

        rig.Mod.WaitSubmitted(1);
        _clock.Advance(1200);
        rig.Deadlines.CheckNow();

        Assert.Equal("game_timeout", (string?)client.Next()["error"]!["code"]);
        Frame(rig, 0.0);
        Assert.Equal(0, hub.SampleRuns);
    }

    private SubscriptionHub Hub() => new SubscriptionHub(NoDevices.Instance, _logic, SubscriptionLimits.Default);

    // Strict: the reader thread checks read sample's options against the catalogue, as the mod does.
    private PipeRig Rig(SubscriptionHub hub) =>
        new PipeRig(mainThread: false, subscriptions: hub, settings: new ProtocolSettings(32, strictArguments: true),
            tick: () => new SamplingTick(_frame, 0), realTick: () => new RealTimeTick(_frame, _realS, DateTimeOffset.UnixEpoch),
            frameClock: _clock, deadlineClock: _clock);

    // Watch id 100's On for seconds, at most every interval.
    private static JObject Watching(double seconds, double interval) => new JObject
    {
        ["what"] = "sample", ["id"] = new JArray("100"), ["logic"] = "On", ["seconds"] = seconds, ["interval"] = interval
    };

    private static string CallOn(string id, JObject parameters, long stream) =>
        new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "read", ["params"] = parameters, ["stream"] = stream
        }.ToString(Formatting.None);

    private static string Call(string id, JObject parameters) =>
        new JObject { ["type"] = "call", ["id"] = id, ["method"] = "read", ["params"] = parameters }
            .ToString(Formatting.None);

    private static double[] AtOf(JObject result)
    {
        List<double> at = new List<double>();
        foreach (JToken change in (JArray)result["changes"]!)
        {
            at.Add((double)change["at_s"]!);
        }

        return at.ToArray();
    }

    private void Frame(PipeRig rig, double atS)
    {
        _frame++;
        _realS = atS;
        rig.Mod.RunFrame();
    }

    // Once the calls have reached the queue, the one frame at a fixed real time that starts their runs.
    private void StartRuns(PipeRig rig, SubscriptionHub hub, int calls, double atS)
    {
        rig.Mod.WaitSubmitted(calls);
        Frame(rig, atS);
        Assert.Equal(calls, hub.SampleRuns);
    }

    // Frames at atS until a line comes; the bound only ends a hang.
    private JObject NextAfterFrames(PipeRig rig, V2Client client, double atS)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 30000)
        {
            Frame(rig, atS);
            JObject? message = client.NextOrNull(20);
            if (message != null)
            {
                return message;
            }
        }

        throw new TimeoutException("no reply");
    }

    /// <summary>One On value per target, flipping every sample, so every sample is a change.</summary>
    private sealed class ToggleReader : ILogicSampleReader<BatchItemView>
    {
        private int _samples;

        internal (string Code, string Message)? Failure { get; set; }

        public LogicSampleRead Read(SampleRequest arguments, List<BatchItemView> into)
        {
            if (Failure is { } failure)
            {
                return LogicSampleRead.Failed(failure.Code, failure.Message);
            }

            int sample = _samples++;
            for (int target = 0; target < arguments.Targets.Count; target++)
            {
                into.Add(new LogicReadItemView(target,
                    new LogicRead(new ThingId(100 + target), new LogicTypeView(28, "On"), sample % 2)));
            }

            return LogicSampleRead.Read("world");
        }
    }

    private sealed class NoDevices : IDeviceReader<ReadDevicesView>
    {
        internal static readonly NoDevices Instance = new NoDevices();

        public ReadDevicesView Read(DeviceSubscriptionQuery query) => throw new InvalidOperationException("No devices here.");
    }
}
