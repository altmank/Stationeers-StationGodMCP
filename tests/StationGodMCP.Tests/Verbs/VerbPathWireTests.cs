#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The verb path over a real pipe, frames run by hand: a handler whose answer is not ready (VerbOutcome.Pending) goes
/// on in the slice lane and answers once, through the same writer (shape, cap, elapsed_ms its slices); it holds its
/// stream, so a write sent after it on that stream runs only once it has answered while another stream goes on; its
/// slices count in its stream's cost; a call is scheduled by its what's x-cost (class, per-list items, keyed
/// find.thing); and every answer is timed under its what's key.
/// </summary>
public sealed class VerbPathWireTests
{
    private readonly FakeClock _clock = new FakeClock();
    private readonly ConcurrentQueue<string> _timed = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<CallProfile> _profiles = new ConcurrentQueue<CallProfile>();
    private readonly ConcurrentQueue<string> _ran = new ConcurrentQueue<string>();

    [Fact]
    public async Task APendingVerbAnswersOnceItsWorkIsReadyAndIsTimedUnderItsWhat()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        client.Send(Call("1", "find", new JObject { ["what"] = "thing", ["steps"] = 3 }, 3));
        rig.Mod.WaitSubmitted(1);
        rig.Mod.RunFrame();
        Assert.Null(client.NextOrNull(50));
        JObject reply = NextAfterFrames(rig, client);

        Assert.True((bool)reply["ok"]!, reply.ToString());
        Assert.Equal(3, (int)reply["result"]!["steps"]!);
        Assert.NotNull(reply["elapsed_ms"]);
        Assert.Equal(new[] { "find.thing ok" }, _timed.ToArray());
    }

    [Fact]
    public async Task APendingVerbsFailureIsItsRefusal()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        client.Send(Call("1", "find", new JObject { ["what"] = "thing", ["steps"] = 2, ["fail"] = true }, 3));
        rig.Mod.WaitSubmitted(1);
        JObject reply = NextAfterFrames(rig, client);

        Assert.False((bool)reply["ok"]!);
        Assert.Equal("cursor_expired", (string?)reply["error"]!["code"]);
        Assert.Equal("k7", (string?)reply["error"]!["data"]!["cursor_id"]);
        Assert.Equal(new[] { "find.thing failed" }, _timed.ToArray());
    }

    [Fact]
    public async Task APendingVerbHoldsItsStreamWhileAnotherStreamGoesOn()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        client.Send(Call("find", "find", new JObject { ["what"] = "thing", ["steps"] = 3 }, 3));
        client.Send(Call("write", "write", new JObject { ["what"] = "label", ["id"] = "7", ["label"] = "x" }, 3));
        client.Send(Call("other", "read", new JObject { ["what"] = "clock" }, 5));
        rig.Mod.WaitSubmitted(3);
        rig.Mod.RunFrame();
        Assert.Equal("other", (string?)client.Next()["id"]);
        Assert.Null(client.NextOrNull(50));

        Assert.Equal("find", (string?)NextAfterFrames(rig, client)["id"]);
        Assert.Equal("write", (string?)NextAfterFrames(rig, client)["id"]);
        Assert.Equal("write.label", _ran.ToArray()[2]);
    }

    [Fact]
    public async Task APendingVerbsSlicesCountInItsStreamsCost()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        Connection connection = rig.WaitConnected();

        client.Send(Call("1", "find", new JObject { ["what"] = "thing", ["steps"] = 3 }, 3));
        rig.Mod.WaitSubmitted(1);
        NextAfterFrames(rig, client);
        StreamCost cost = rig.Mod.Costs.Of(new CallContext(connection.Owner, 3));
        Assert.Equal(1, cost.Calls);
        Assert.Equal(3.0, cost.MainThreadMs, 6);
    }

    [Fact]
    public async Task ACallIsScheduledByItsWhatsCost()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        client.Send(Call("1", "find", new JObject { ["what"] = "thing" }, 3));
        client.Send(Call("2", "read", new JObject { ["what"] = "logic", ["id"] = new JArray("1", "2", "3"), ["logic"] = "On" }, 3));
        client.Send(Call("3", "write", new JObject { ["what"] = "label", ["id"] = "7", ["label"] = "x" }, 3));
        rig.Mod.WaitSubmitted(3);
        for (int frame = 0; frame < 3; frame++)
        {
            rig.Mod.RunFrame();
        }

        Dictionary<string, CallProfile> profiles = _profiles.ToDictionary(profile => profile.Method);
        Assert.Equal(new[] { "find.thing", "read.logic", "write.label" }, profiles.Keys.OrderBy(key => key));
        Assert.Equal(CostClass.World, profiles["find.thing"].Cost);
        Assert.Equal(Lane.Heavy, new CostPredictor().LaneFor(profiles["find.thing"], 1.0));
        Assert.Equal(CostClass.Bounded, profiles["read.logic"].Cost);
        Assert.Equal(3, profiles["read.logic"].Items);
        Assert.False(profiles["read.logic"].IsOrdered);
        Assert.True(profiles["write.label"].IsOrdered);
    }

    [Fact]
    public void TheTableKnowsItsWhatsByKey()
    {
        VerbDispatcher table = Table();

        Assert.Equal(new[] { "find.thing", "read.clock", "read.logic", "write.label" }, table.Keys);
        Assert.True(table.AnswersKey("find.thing"));
        Assert.False(table.AnswersKey("find.item"));
        Assert.False(table.AnswersKey("find"));
        Assert.False(table.AnswersKey(null));
    }

    // Frames until a line comes; the bound only ends a hang.
    private static JObject NextAfterFrames(PipeRig rig, V2Client client)
    {
        for (int frame = 0; frame < 20; frame++)
        {
            rig.Mod.RunFrame();
            if (client.NextOrNull(20) is { } line)
            {
                return line;
            }
        }

        return client.Next();
    }

    private PipeRig Rig()
    {
        VerbDispatcher table = Table();
        PipeRig rig = new(mainThread: false, settings: new ProtocolSettings(32, strictArguments: false), verbs: table,
            frameClock: _clock);
        VerbRunner runner = new(table, ProtocolSettings.DefaultMaxReplyBytes, static () => 1,
            (what, ok, _, _, _) => _timed.Enqueue(what + (ok ? " ok" : " failed")));
        rig.Mod.CallHook = (call, queueMs) =>
        {
            if (!VerbCalls.Handles(call.Request.Method))
            {
                return null;
            }

            _profiles.Enqueue(call.Profile);
            _ran.Enqueue(call.Profile.Method);
            return VerbCalls.Run(runner, call, queueMs);
        };
        return rig;
    }

    // find thing goes on for "steps" slices of 1 ms each (and fails as a gone cursor with "fail"); the rest echo.
    private VerbDispatcher Table() => TestVerbs.Only(
        VerbHandler.For(FindThingOptions.Spec, reader => reader, reader =>
        {
            int steps = (int?)reader.Integer("limit") ?? 0;
            return steps == 0
                ? new VerbOutcome.Answered(new JObject { ["steps"] = 0 })
                : new VerbOutcome.Pending(new Stepped(_clock, steps, reader.Flag("labelled") == true));
        }),
        TestVerbs.Echo(ReadClockOptions.Spec),
        TestVerbs.Echo(ReadLogicOptions.Spec),
        TestVerbs.Echo(WriteLabelOptions.Spec));

    // A test call's params: steps and fail ride on find thing's own options (limit, labelled), as the rig does not
    // check options on the reader thread and the handler reads them by those names.
    private static string Call(string id, string method, JObject parameters, long stream)
    {
        if (parameters["steps"] is { } steps)
        {
            parameters.Remove("steps");
            parameters["limit"] = steps;
        }

        if (parameters["fail"] is { } fail)
        {
            parameters.Remove("fail");
            parameters["labelled"] = fail;
        }

        return new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = method, ["params"] = parameters, ["stream"] = stream
        }.ToString(Formatting.None);
    }

    private sealed class Stepped : IPendingResult
    {
        private readonly FakeClock _clock;
        private readonly int _steps;
        private readonly bool _fail;
        private int _taken;

        internal Stepped(FakeClock clock, int steps, bool fail)
        {
            _clock = clock;
            _steps = steps;
            _fail = fail;
        }

        public bool Step(double untilMs)
        {
            _clock.Advance(1.0);
            _taken++;
            if (_fail && _taken == _steps)
            {
                throw WalkRequest.Expired("k7", 3);
            }

            return _taken >= _steps;
        }

        public object Result() => new JObject { ["steps"] = _taken };
    }
}
