#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Sampling;

/// <summary>
/// The sampler (SubscriptionHub's sample runs) over a real pipe with the lane scheduler's frames run by the test,
/// reached as read {what: "sample"} with the sampler's own arguments on a rig that does not check options on the reader
/// thread (PipeRig.IsSampleRun): the call starts a run whose samples are taken in the subscription lane on the real
/// clock, and is answered when its last sample is taken; invalid arguments are answered at once; a closed connection
/// drops its run.
/// </summary>
public sealed class SampleLogicWireTests
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
        client.Send(Call("1", new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
            ["duration_seconds"] = 0.3,
            ["interval_seconds"] = 0.1
        }));
        StartRuns(rig, hub, 1, atS: 0.0);
        Frame(rig, 0.0);

        Assert.Null(client.NextOrNull(100));
        foreach (double at in new[] { 0.1, 0.2 })
        {
            Frame(rig, at);
            Assert.Null(client.NextOrNull(50));
        }

        Frame(rig, 0.3);
        JObject reply = client.Next();
        Assert.Equal("reply", (string?)reply["type"]);
        Assert.True((bool)reply["ok"]!, reply.ToString());
        JObject result = (JObject)reply["result"]!;
        Assert.Equal(4, (int)result["sample_count"]!);
        Assert.Equal(4, (int)result["change_count"]!);
        Assert.Equal(new[] { 0.0, 0.1, 0.2, 0.3 }, ElapsedOf(result));
        Assert.Equal(0.3, (double)result["duration_seconds"]!);
        Assert.Equal(0.1, (double)result["interval_seconds"]!);
        Assert.NotNull(reply["frame"]);
        Assert.NotNull(reply["elapsed_ms"]);
        Assert.Equal(0, hub.SampleLogicRuns);
    }

    [Fact]
    public async Task TheSamplesAreTheSubscriptionLanes()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
            ["duration_seconds"] = 0.1,
            ["interval_seconds"] = 0.1
        }));
        StartRuns(rig, hub, 1, atS: 0.0);

        _frame++;
        _realS = 0.0;
        Assert.Equal(1, rig.Mod.RunFrame().Samples);
        _frame++;
        _realS = 0.1;
        Assert.Equal(1, rig.Mod.RunFrame().Samples);
        Assert.Equal(2, (int)client.Next()["result"]!["sample_count"]!);
    }

    [Fact]
    public async Task InvalidArgumentsAreAnsweredAtOnce()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
            ["duration_seconds"] = 30,
            ["interval_seconds"] = 0.05
        }));

        JObject reply = NextAfterFrames(rig, client);

        Assert.False((bool)reply["ok"]!);
        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Contains("at most 120", (string?)reply["error"]!["message"]);
        Assert.Equal(0, hub.SampleLogicRuns);
    }

    [Fact]
    public async Task AFailedReadFailsTheCallWithItsError()
    {
        SubscriptionHub hub = Hub();
        _logic.Failure = ("gateway_not_found", "No gateway gw9.");
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" })
        }));

        JObject reply = NextAfterFrames(rig, client);

        Assert.Equal("gateway_not_found", (string?)reply["error"]!["code"]);
        Assert.Equal(0, hub.SampleLogicRuns);
    }

    [Fact]
    public async Task AClosedConnectionDropsItsRun()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        V2Client client = await V2Client.Connect(rig);
        client.Send(Call("1", new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
            ["duration_seconds"] = 5
        }));
        StartRuns(rig, hub, 1, atS: 0.0);

        client.Dispose();
        Stopwatch waited = Stopwatch.StartNew();
        while (rig.Mod.ClosedSources.IsEmpty)
        {
            Assert.True(waited.ElapsedMilliseconds < 30000, "the close never reached the mod");
            await Task.Delay(20);
        }

        Frame(rig, 0.5);
        Assert.Equal(0, hub.SampleLogicRuns);
    }

    [Fact]
    public async Task EndingItsStreamAnswersAnUnfinishedRunCancelledAndLeavesOtherStreamsRuns()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        JObject parameters = new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
            ["duration_seconds"] = 5
        };
        client.Send(CallOn("1", parameters, 3));
        client.Send(CallOn("2", parameters, 0));
        StartRuns(rig, hub, 2, atS: 0.0);

        client.Send("""{"type":"stream_end","stream":3}""");
        JObject reply = NextAfterFrames(rig, client);

        Assert.Equal("1", (string?)reply["id"]);
        Assert.False((bool)reply["ok"]!);
        Assert.Equal("cancelled", (string?)reply["error"]!["code"]);
        Assert.Equal(1, hub.SampleLogicRuns);
    }

    [Fact]
    public void TheDurationIsTheArgumentAtMostItsMaximumElseTheMaximum()
    {
        Assert.True(TestCatalogue.File.Value.Catalogue.TryGet("sample_logic", out CatalogueMethod? method));

        Assert.Equal(5000, method!.DurationMs(JObject.Parse("""{"duration_seconds": 5}""")));
        Assert.Equal(30000, method.DurationMs(JObject.Parse("""{"duration_seconds": 300}""")));
        Assert.Equal(30000, method.DurationMs(new JObject()));
        Assert.True(TestCatalogue.File.Value.Catalogue.TryGet("read_logic", out CatalogueMethod? read));
        Assert.Equal(0, read!.DurationMs(new JObject()));
    }

    private SubscriptionHub Hub() => new SubscriptionHub(NoDevices.Instance, _logic, SubscriptionLimits.Default);

    // Not strict: read {what: "sample"} goes with the sampler's own arguments (PipeRig.IsSampleRun).
    private PipeRig Rig(SubscriptionHub hub) =>
        new PipeRig(mainThread: false, subscriptions: hub, settings: new ProtocolSettings(32, strictArguments: false),
            tick: () => new SamplingTick(_frame, 0), realTick: () => new RealTimeTick(_frame, _realS, DateTimeOffset.UnixEpoch),
            frameClock: _clock, deadlineClock: _clock);

    private static string CallOn(string id, JObject parameters, long stream) =>
        new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "read", ["params"] = Sampled(parameters), ["stream"] = stream
        }.ToString(Formatting.None);

    private static JObject Sampled(JObject parameters)
    {
        JObject sampled = new JObject { ["what"] = "sample" };
        sampled.Merge(parameters);
        return sampled;
    }

    private static string Call(string id, JObject parameters) =>
        new JObject { ["type"] = "call", ["id"] = id, ["method"] = "read", ["params"] = Sampled(parameters) }
            .ToString(Formatting.None);

    private static double[] ElapsedOf(JObject result)
    {
        List<double> elapsed = new List<double>();
        foreach (JToken change in (JArray)result["changes"]!)
        {
            elapsed.Add((double)change["elapsed_seconds"]!);
        }

        return elapsed.ToArray();
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
        Assert.Equal(calls, hub.SampleLogicRuns);
    }

    // Frames until a line comes; the bound only ends a hang.
    private JObject NextAfterFrames(PipeRig rig, V2Client client)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 30000)
        {
            Frame(rig, _realS);
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

        public LogicSampleRead Read(SampleLogicArguments arguments, List<BatchItemView> into)
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
