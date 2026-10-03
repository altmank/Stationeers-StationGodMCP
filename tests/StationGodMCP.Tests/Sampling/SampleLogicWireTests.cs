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
/// sample_logic in the mod, over a real pipe with the lane scheduler's frames run by the test: the call starts a run
/// whose samples are taken in the subscription lane on the real clock, and is answered when its last sample is taken,
/// on version 2 and on version 1; invalid arguments are answered at once; a closed connection drops its run; and the
/// call's deadline and every wait for it grow by its duration.
/// </summary>
public sealed class SampleLogicWireTests
{
    private readonly ToggleReader _logic = new ToggleReader();
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
        WaitUntil(() => hub.SampleLogicRuns == 1, rig, atS: 0.0);
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
        WaitUntil(() => hub.SampleLogicRuns == 1, rig, atS: 0.0);

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
        using PipeRig rig = Rig(hub, strict: false);
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
    public async Task VersionOneGetsTodaysEnvelope()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using NamedPipeClientStream pipe = await rig.ConnectAsync();
        byte[] line = Encoding.UTF8.GetBytes(new JObject
        {
            ["id"] = "v1", ["method"] = "sample_logic",
            ["params"] = new JObject
            {
                ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
                ["duration_seconds"] = 0.1, ["interval_seconds"] = 0.1
            }
        }.ToString(Formatting.None) + "\n");
        await pipe.WriteAsync(line, 0, line.Length);
        WaitUntil(() => hub.SampleLogicRuns == 1, rig, atS: 0.0);
        Frame(rig, 0.0);
        Frame(rig, 0.1);

        byte[] buffer = new byte[65536];
        Task<int> read = pipe.ReadAsync(buffer, 0, buffer.Length);
        Assert.True(read.Wait(5000));
        JObject reply = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, read.Result).Trim());

        Assert.Equal("v1", (string?)reply["id"]);
        Assert.True((bool)reply["ok"]!);
        Assert.Null(reply["type"]);
        Assert.Equal(2, (int)reply["result"]!["sample_count"]!);
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
        WaitUntil(() => hub.SampleLogicRuns == 1, rig, atS: 0.0);

        client.Dispose();
        Stopwatch waited = Stopwatch.StartNew();
        while (rig.Mod.ClosedSources.IsEmpty && waited.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(20);
        }

        Frame(rig, 0.5);
        Assert.Equal(0, hub.SampleLogicRuns);
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

        CallProfiles.OfLine(TestCatalogue.File.Value,
            """{"id":"1","method":"sample_logic","params":{"targets":[],"duration_seconds":12.5}}""", out int lineMs);
        Assert.Equal(12500, lineMs);
    }

    [Fact]
    public async Task ACallWaitsInTheQueueForItsDeadlinePlusItsDuration()
    {
        SubscriptionHub hub = Hub();
        using PipeRig rig = Rig(hub);
        using V2Client client = await V2Client.Connect(rig);
        JObject call = JObject.Parse(Call("1", new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
            ["duration_seconds"] = 1,
            ["interval_seconds"] = 0.5
        }));
        call["deadline_ms"] = 100;
        client.Send(call.ToString(Formatting.None));

        // Past its 100 ms deadline, but not past the deadline plus its 1 s duration: it still starts.
        await Task.Delay(400);
        WaitUntil(() => hub.SampleLogicRuns == 1, rig, atS: 0.0);
        Assert.Null(client.NextOrNull(50));
    }

    private SubscriptionHub Hub() => new SubscriptionHub(NoDevices.Instance, _logic, SubscriptionLimits.Default);

    private PipeRig Rig(SubscriptionHub hub, bool strict = true) =>
        new PipeRig(mainThread: false, subscriptions: hub, settings: new ProtocolSettings(32, strictArguments: strict),
            tick: () => new SamplingTick(_frame, 0), realTick: () => new RealTimeTick(_frame, _realS, DateTimeOffset.UnixEpoch));

    private static string Call(string id, JObject parameters) =>
        new JObject { ["type"] = "call", ["id"] = id, ["method"] = "sample_logic", ["params"] = parameters }
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

    // Runs frames at a fixed real time until the condition holds (the call has reached the main thread).
    private void WaitUntil(Func<bool> condition, PipeRig rig, double atS)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException("the call did not start");
            }

            Frame(rig, atS);
            System.Threading.Thread.Sleep(10);
        }
    }

    private JObject NextAfterFrames(PipeRig rig, V2Client client)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
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
