#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Held restores on the wire (protocol.md, Held restores): hold_restore registers a stream's restore writes, which the
/// mod applies at an abrupt end (a dropped socket whose resume window passes, session_end, a tokenless close without
/// bye) and clears unapplied at a normal one (bye, stream_end). Each applies only in its world and only while the
/// device holds the stream's last written value; welcome reports the results on the next resume. The devices are fakes
/// behind IRestoreDevices, and write_logic is a fake that writes to them and tells the store, as LogicOps.Write does.
/// </summary>
public sealed class HeldRestoreWireTests
{
    private const string Token = "hub-session-0123456789abcdef";
    private const int WindowMs = 60000;
    private const long Pump = 101;
    private const long Valve = 202;
    private const ushort On = 28;
    private const ushort Setting = 12;

    private readonly FakeClock _clock = new FakeClock();
    private readonly FakeDevices _devices = new FakeDevices();
    private readonly HeldRestores _restores;
    private volatile string _world = "world-a";

    public HeldRestoreWireTests()
    {
        _restores = new HeldRestores(_devices, () => _world);
        _devices.Set(Pump, On, 0);
        _devices.Set(Pump, Setting, 0);
        _devices.Set(Valve, On, 0);
    }

    [Fact]
    public async Task AClientKilledMidHoldHasItsRestoreAppliedWhenTheWindowPasses()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Assert.True((bool)Write(client, "w1", Pump, "On", 1, stream: 3)["ok"]!);
            JObject held = Hold(client, "h1", 3, Set(Pump, "On", 0));
            Assert.Equal(new JArray(Entry(Pump, On, "On", 0)), held["result"]!["restores"]);
        }

        WaitHeld(rig);
        _clock.Advance(WindowMs - 1);
        Settle(rig);
        Assert.Equal(1, _devices.Get(Pump, On));

        _clock.Advance(1);
        WaitUntil(() => _devices.Get(Pump, On) == 0);
        Assert.Equal(1, _restores.Applied);
        Assert.Equal(0, _restores.Held);
    }

    [Fact]
    public async Task TheNextWelcomeOnTheTokenReportsWhatWasAppliedOnce()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0));
        }

        WaitHeld(rig);
        _clock.Advance(WindowMs);
        WaitUntil(() => _restores.Applied == 1);
        using (V2Client second = await Resume(rig))
        {
            JObject report = (JObject)second.Welcome["restores"]!;
            JObject applied = Entry(Pump, On, "On", 0);
            applied.AddFirst(new JProperty("stream", 3));
            Assert.Equal(new JObject
            {
                ["applied"] = new JArray(applied), ["skipped"] = new JArray(), ["dropped"] = 0
            }, report);
            second.Send("""{"type":"bye"}""");
            Assert.True(second.WaitClosed(5000));
        }

        using V2Client third = await Resume(rig);
        Assert.Null(third.Welcome["restores"]);
    }

    [Fact]
    public async Task AByeClearsTheRestoresAndNothingApplies()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0));
            client.Send("""{"type":"bye"}""");
            Assert.True(client.WaitClosed(5000));
        }

        WaitUntil(() => _restores.Held == 0);
        _clock.Advance(WindowMs);
        Settle(rig);

        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.Equal(0, _restores.Applied + _restores.Skipped + _restores.Dropped);
    }

    [Fact]
    public async Task AByeWithoutATokenClearsTheRestoresAndNothingApplies()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await V2Client.Connect(rig, "hub"))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0));
            client.Send("""{"type":"bye"}""");
            Assert.True(client.WaitClosed(5000));
        }

        WaitUntil(() => _restores.Held == 0);
        Settle(rig);

        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.Equal(0, _restores.Applied);
    }

    [Fact]
    public async Task StreamEndClearsTheStreamsRestoresAndNothingAppliesAfterAKill()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0));
            client.Send("""{"type":"stream_end","stream":3}""");
            WaitUntil(() => _restores.Held == 0);
        }

        WaitHeld(rig);
        _clock.Advance(WindowMs);
        Settle(rig);

        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.Equal(0, _restores.Applied);
    }

    [Fact]
    public async Task ADeviceAnotherWriterChangedIsSkippedAndReported()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Write(client, "w1", Pump, "Setting", 50, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "Setting", 20));
        }

        _devices.Set(Pump, Setting, 70);
        WaitHeld(rig);
        _clock.Advance(WindowMs);
        WaitUntil(() => _restores.Skipped == 1);

        Assert.Equal(70, _devices.Get(Pump, Setting));
        using V2Client second = await Resume(rig);
        JObject skipped = (JObject)Assert.Single((JArray)second.Welcome["restores"]!["skipped"]!);
        Assert.Equal("changed", (string?)skipped["reason"]);
        Assert.Equal(70.0, (double)skipped["current"]!);
        Assert.Equal(20.0, (double)skipped["value"]!);
        Assert.Empty((JArray)second.Welcome["restores"]!["applied"]!);
    }

    [Fact]
    public async Task AWriteByTheStreamAfterRegisteringIsTheValueTheCheckExpects()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Hold(client, "h1", 3, Set(Pump, "On", 0));
            Write(client, "w1", Pump, "On", 1, stream: 3);
        }

        WaitHeld(rig);
        _clock.Advance(WindowMs);
        WaitUntil(() => _devices.Get(Pump, On) == 0);
        Assert.Equal(1, _restores.Applied);
    }

    [Fact]
    public async Task AWriteByAnotherStreamOfTheSameClientCountsAsAnotherWriter()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Hold(client, "h1", 3, Set(Pump, "On", 0));
            Write(client, "w1", Pump, "On", 1, stream: 4);
        }

        WaitHeld(rig);
        _clock.Advance(WindowMs);
        WaitUntil(() => _restores.Skipped == 1);
        Assert.Equal(1, _devices.Get(Pump, On));
    }

    [Fact]
    public async Task AWorldReloadDropsTheRestoresAndTheNextWelcomeCountsThem()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0), Set(Valve, "On", 1));
            rig.Mod.OnMainThread(() =>
            {
                _world = "world-b";
                _stores.WorldChanged();
                rig.Holds.WorldChanged();
            });
        }

        WaitHeld(rig);
        _clock.Advance(WindowMs);
        Settle(rig);

        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.Equal(2, _restores.Dropped);
        Assert.Equal(0, _restores.Applied);
        using V2Client second = await Resume(rig);
        Assert.Equal(2, (int)second.Welcome["restores"]!["dropped"]!);
    }

    [Fact]
    public async Task ARestoreFromAnotherWorldIsDroppedWhenItWouldApply()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        Write(client, "w1", Pump, "On", 1, stream: 3);
        Hold(client, "h1", 3, Set(Pump, "On", 0));
        _world = "world-b";

        client.Send("""{"type":"session_end"}""");
        WaitUntil(() => _restores.Dropped == 1);

        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.Equal(0, _restores.Applied);
    }

    [Fact]
    public async Task SessionEndAppliesAtOnceAndTheConnectionStaysOpen()
    {
        using PipeRig rig = Rig();
        using V2Client client = await Resume(rig);
        Write(client, "w1", Pump, "On", 1, stream: 3);
        Write(client, "w2", Valve, "On", 1, stream: 5);
        Hold(client, "h1", 3, Set(Pump, "On", 0));
        Hold(client, "h2", 5, Set(Valve, "On", 0));

        client.Send("""{"type":"session_end"}""");
        WaitUntil(() => _restores.Applied == 2);

        Assert.Equal(0, _devices.Get(Pump, On));
        Assert.Equal(0, _devices.Get(Valve, On));
        Assert.True((bool)Write(client, "after", Pump, "On", 1, stream: 3)["ok"]!);
        Assert.Equal(0, _restores.Held);
    }

    [Fact]
    public async Task SessionEndCancelsTheUnstartedCallsSoNoneWritesAfterTheRestore()
    {
        using PipeRig rig = Rig(mainThread: false);
        using V2Client client = await V2Client.Connect(rig, "hub");
        client.Send(WriteLine("w1", Pump, "On", 1, 3));
        client.Send(HoldLine("h1", 3, Set(Pump, "On", 0)));
        WaitUntil(() =>
        {
            rig.Mod.RunPending();
            return _restores.Held == 1;
        });
        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.True((bool)NextReply(client, "w1")["ok"]!);
        Assert.True((bool)NextReply(client, "h1")["ok"]!);

        client.Send(WriteLine("late", Pump, "On", 1, 3));
        client.Send("""{"type":"session_end"}""");
        JObject cancelled = NextReply(client, "late");
        WaitUntil(() =>
        {
            rig.Mod.RunPending();
            return _restores.Applied == 1;
        });
        rig.Mod.RunPending();

        Assert.Equal("cancelled", (string?)cancelled["error"]!["code"]);
        Assert.Equal(0, _devices.Get(Pump, On));
        Assert.Equal(2, rig.Mod.Ran);
    }

    [Fact]
    public async Task AConnectionWithoutATokenThatClosesWithoutByeHasItsRestoresApplied()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await V2Client.Connect(rig, "hub"))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0));
        }

        WaitUntil(() => _devices.Get(Pump, On) == 0);
        Assert.Equal(1, _restores.Applied);
    }

    [Fact]
    public async Task ATokenResumedWithinTheWindowKeepsItsRestores()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0));
        }

        WaitHeld(rig);
        _clock.Advance(WindowMs - 1);
        using V2Client second = await Resume(rig);
        _clock.Advance(1);
        Settle(rig);

        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.Equal(1, _restores.Held);
        Assert.Equal(new JArray(Entry(Pump, On, "On", 0)), Hold(second, "h2", 3)["result"]!["restores"]);
    }

    [Fact]
    public async Task SettingTheSameDeviceAndLogicTypeAgainReplacesItsEntry()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        Hold(client, "h1", 3, Set(Pump, "On", 0), Set(Valve, "On", 1));
        JObject replaced = Hold(client, "h2", 3, Set(Pump, 28, 1));

        Assert.Equal(new JArray(Entry(Pump, On, "On", 1), Entry(Valve, On, "On", 1)), replaced["result"]!["restores"]);
        Assert.Equal(2, _restores.Held);
    }

    [Fact]
    public async Task ClearRemovesOneEntryAndClearAllEveryOneBeforeTheSets()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        Hold(client, "h1", 3, Set(Pump, "On", 0), Set(Valve, "On", 1), Set(Pump, "Setting", 5));

        JObject cleared = Call(client, "h2", 3, new JObject
        {
            ["clear"] = new JArray(new JObject { ["reference_id"] = Id(Valve), ["logic_type"] = "On" },
                new JObject { ["reference_id"] = "999", ["logic_type"] = "On" })
        });
        JObject reset = Call(client, "h3", 3, new JObject { ["clear_all"] = true, ["set"] = new JArray(Set(Valve, "On", 0)) });
        JObject emptied = Call(client, "h4", 3, new JObject { ["clear_all"] = true });

        Assert.Equal(new JArray(Entry(Pump, On, "On", 0), Entry(Pump, Setting, "Setting", 5)),
            cleared["result"]!["restores"]);
        Assert.Equal(new JArray(Entry(Valve, On, "On", 0)), reset["result"]!["restores"]);
        Assert.Equal(new JArray(), emptied["result"]!["restores"]);
        Assert.Equal(0, _restores.Held);
    }

    [Fact]
    public async Task AnEntryNamingNoHoldableDeviceRefusesTheWholeChange()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        Hold(client, "h1", 3, Set(Pump, "On", 0));

        JObject refused = Hold(client, "h2", 3, Set(Valve, "On", 0), Set(404, "On", 0));

        Assert.Equal("device_not_found", (string?)refused["error"]!["code"]);
        Assert.Equal(new JArray(Entry(Pump, On, "On", 0)), Hold(client, "h3", 3)["result"]!["restores"]);
    }

    [Fact]
    public async Task TheRestoresAreTheirStreamsAndAnotherStreamListsNone()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        Hold(client, "h1", 3, Set(Pump, "On", 0));

        Assert.Equal(new JArray(), Hold(client, "h2", 4)["result"]!["restores"]);
    }

    [Fact]
    public async Task SessionEndTakesNoKeysButType()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        client.Send("""{"type":"session_end","stream":3}""");

        JObject refused = client.Next();
        Assert.Equal("protocol_error", (string?)refused["error"]!["code"]);
        Assert.Contains("'stream'", (string?)refused["error"]!["message"]);
    }

    [Fact]
    public async Task SessionEndWithNothingHeldChangesNothingAndHasNoReply()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        client.Send("""{"type":"session_end"}""");
        WaitUntil(() => rig.Mod.SessionEnds.Count == 1);
        Settle(rig);

        Assert.Null(client.NextOrNull(100));
        Assert.True((bool)Write(client, "w1", Pump, "On", 1, stream: 3)["ok"]!);
        Assert.Equal(0, _restores.Applied + _restores.Skipped + _restores.Dropped);
    }

    // ---- the store alone: bounds ----

    [Fact]
    public void AStreamHoldsAtMost256AndAChangePastItIsRefusedWhole()
    {
        CallContext stream = new CallContext("c1", 3);
        _restores.Change(stream, null, false, NoClears(), Sets(0, 200));

        ApiException refused = Assert.Throws<ApiException>(() =>
            _restores.Change(stream, null, false, NoClears(), Sets(200, 57)));

        Assert.Equal("too_many_restores", refused.Code);
        Assert.Equal(new Dictionary<string, object> { ["limit"] = 256, ["scope"] = "stream" }, refused.Details);
        Assert.Equal(200, _restores.Held);
        Assert.Equal(256, _restores.Change(stream, null, false, NoClears(), Sets(200, 56)).Count);
        Assert.Equal(256, _restores.Change(stream, null, false, NoClears(), Sets(0, 256)).Count);
    }

    [Fact]
    public void AnOwnerHoldsAtMost4096AcrossItsStreams()
    {
        for (int stream = 1; stream <= 16; stream++)
        {
            _restores.Change(new CallContext("c1", stream), null, false, NoClears(), Sets(0, 256));
        }

        ApiException refused = Assert.Throws<ApiException>(() =>
            _restores.Change(new CallContext("c1", 17), null, false, NoClears(), Sets(0, 1)));
        _restores.Change(new CallContext("c2", 1), null, false, NoClears(), Sets(0, 1));

        Assert.Equal("too_many_restores", refused.Code);
        Assert.Equal(new Dictionary<string, object> { ["limit"] = 4096, ["scope"] = "owner" }, refused.Details);
        Assert.Equal(4097, _restores.Held);
    }

    [Fact]
    public void AWriteCostsOneFieldReadWhileNothingIsHeldAndAllocatesNothingWhenHeld()
    {
        CallContext stream = new CallContext("c1", 3);
        _restores.Wrote(stream, Pump, On, 1);
        _restores.Change(stream, null, false, NoClears(), Sets(0, 1));
        _restores.Wrote(stream, 1000, On, 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int write = 0; write < 1000; write++)
        {
            _restores.Wrote(stream, 1000, On, write);
            _restores.Wrote(new CallContext("c1", 4), 1000, On, write);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- the rig ----

    private StreamStores _stores = new StreamStores();

    private PipeRig Rig(bool mainThread = true)
    {
        _stores = new StreamStores();
        _stores.Register(_restores);
        PipeRig rig = new PipeRig(mainThread: mainThread, stores: _stores, resumeWindowMs: WindowMs,
            resumeClock: _clock, restores: _restores);
        HoldRestoreCalls holds = new HoldRestoreCalls(_restores);
        rig.Mod.CallHook = (call, queueMs) => call.Request.Method switch
        {
            HoldRestoreCalls.HoldRestoreMethod => new CallOutcome(holds.Run(call, queueMs, 1), call.Request.Method),
            "write_logic" => new CallOutcome(FakeWrite(call), call.Request.Method),
            _ => null
        };
        return rig;
    }

    // write_logic as LogicOps.Write does it: the device's value set, then the stream's last written value told.
    private string FakeWrite(ProtocolCall call)
    {
        JObject parameters = call.Request.Params!;
        long id = long.Parse((string)parameters["reference_id"]!, CultureInfo.InvariantCulture);
        ushort type = _devices.LogicType(parameters["logic_type"]).Id;
        double value = (double)parameters["value"]!;
        _devices.Set(id, type, value);
        _restores.Wrote(CallContext.Current, id, type, _devices.Get(id, type));
        return new JObject
        {
            ["type"] = "reply", ["id"] = call.Request.Id, ["ok"] = true, ["result"] = new JObject { ["current_value"] = value }
        }.ToString(Formatting.None);
    }

    private static JObject Set(long id, JToken logicType, double value) =>
        new JObject { ["reference_id"] = Id(id), ["logic_type"] = logicType, ["value"] = value };

    private static JObject Entry(long id, ushort type, string name, double value) =>
        new JObject
        {
            ["reference_id"] = Id(id), ["logic_type"] = new JObject { ["id"] = type, ["name"] = name }, ["value"] = value
        };

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static List<(ThingId Id, JToken? LogicType)> NoClears() => new List<(ThingId, JToken?)>();

    private static List<(ThingId Id, JToken? LogicType, double Value)> Sets(int first, int count)
    {
        List<(ThingId, JToken?, double)> sets = new List<(ThingId, JToken?, double)>(count);
        for (int index = first; index < first + count; index++)
        {
            sets.Add((new ThingId(1000 + index), new JValue("On"), 0));
        }

        return sets;
    }

    private static string WriteLine(string id, long device, string logicType, double value, long stream) =>
        new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "write_logic", ["stream"] = stream,
            ["params"] = new JObject { ["reference_id"] = Id(device), ["logic_type"] = logicType, ["value"] = value }
        }.ToString(Formatting.None);

    private static string HoldLine(string id, long stream, params JObject[] sets) =>
        new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "hold_restore", ["stream"] = stream,
            ["params"] = sets.Length == 0 ? new JObject() : new JObject { ["set"] = new JArray(sets) }
        }.ToString(Formatting.None);

    private static JObject Write(V2Client client, string id, long device, string logicType, double value, long stream)
    {
        client.Send(WriteLine(id, device, logicType, value, stream));
        return NextReply(client, id);
    }

    private static JObject Hold(V2Client client, string id, long stream, params JObject[] sets)
    {
        client.Send(HoldLine(id, stream, sets));
        return NextReply(client, id);
    }

    private static JObject Call(V2Client client, string id, long stream, JObject parameters)
    {
        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "hold_restore", ["stream"] = stream, ["params"] = parameters
        }.ToString(Formatting.None));
        return NextReply(client, id);
    }

    private static JObject NextReply(V2Client client, string id)
    {
        while (true)
        {
            JObject message = client.Next();
            if ((string?)message["type"] == "reply" && (string?)message["id"] == id)
            {
                return message;
            }
        }
    }

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

    private static void WaitHeld(PipeRig rig) => WaitUntil(() => rig.Holds.Held == 1);

    // Two frames on the fake main thread, so whatever was posted before has been taken in.
    private static void Settle(PipeRig rig)
    {
        rig.Mod.RunFrame();
        rig.Mod.RunFrame();
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

    /// <summary>Devices with logic values by (reference id, logic type); On is 28 and Setting 12, as in the game.</summary>
    private sealed class FakeDevices : IRestoreDevices
    {
        private readonly object _sync = new object();
        private readonly Dictionary<(long, ushort), double> _values = new Dictionary<(long, ushort), double>();

        internal void Set(long id, ushort type, double value)
        {
            lock (_sync)
            {
                _values[(id, type)] = value;
            }
        }

        internal double Get(long id, ushort type)
        {
            lock (_sync)
            {
                return _values[(id, type)];
            }
        }

        public LogicTypeView LogicType(JToken? token) => token?.Type == JTokenType.Integer
            ? new LogicTypeView((ushort)(int)token, (int)token == On ? "On" : (int)token == Setting ? "Setting" : null)
            : (string?)token switch
            {
                "On" => new LogicTypeView(On, "On"),
                "Setting" => new LogicTypeView(Setting, "Setting"),
                _ => throw ApiErrors.Refused("invalid_logic_type", $"'{token}' is not a known LogicType name.")
            };

        public void RequireHoldable(long referenceId, LogicTypeView type)
        {
            if (referenceId == 404)
            {
                throw ApiErrors.Refused("device_not_found", $"Device {referenceId} is not visible in the world.");
            }
        }

        public bool TryRead(long referenceId, ushort type, out double value, out string problem)
        {
            problem = string.Empty;
            lock (_sync)
            {
                return _values.TryGetValue((referenceId, type), out value) || Absent(out value, out problem);
            }
        }

        public bool TryWrite(long referenceId, ushort type, double value, out string problem)
        {
            problem = string.Empty;
            Set(referenceId, type, value);
            return true;
        }

        // A device the fake has never been told of reads 0, so the store's bounds tests need no setup.
        private static bool Absent(out double value, out string problem)
        {
            value = 0;
            problem = string.Empty;
            return true;
        }
    }
}
