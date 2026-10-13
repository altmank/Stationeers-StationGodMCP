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
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Held restores on the wire (protocol.md, Held restores): hold_restore registers a stream's restore writes, which the
/// mod applies at an abrupt end (a dropped socket whose resume window passes, session_end, a tokenless close without
/// bye) and clears unapplied at a normal one (bye, stream_end). Each applies only in its world and only while the
/// device holds the stream's last written value; welcome reports the results on the next resume. The devices are fakes
/// behind IRestoreDevices, and write {what: "logic"} is a fake that writes to them and tells the store, as LogicOps.Write does.
/// </summary>
public sealed class HeldRestoreWireTests
{
    private const string Token = "hub-session-0123456789abcdef";
    private const int WindowMs = 60000;
    private const long Pump = 101;
    private const long Valve = 202;
    private const long Vent = 303;
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
        _devices.Set(Vent, On, 1);
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
        Assert.Equal(0, _restores.Held);
        Assert.Null(client.NextOrNull(100));
        Assert.False(client.WaitClosed(100));
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
        Assert.Equal(0, _restores.Applied + _restores.Skipped + _restores.Dropped);
    }

    // ---- nothing runs after session_end ----

    [Fact]
    public async Task ALoopWritingTheHeldTargetAfterSessionEndIsRefusedAndTheRestoreIsTheLastWrite()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        Write(client, "w1", Pump, "On", 1, stream: 3);
        Hold(client, "h1", 3, Set(Pump, "On", 0));

        client.Send("""{"type":"session_end"}""");
        JObject late = Write(client, "w2", Pump, "On", 1, stream: 3);
        WaitUntil(() => _restores.Applied == 1);
        Settle(rig);

        Assert.False((bool)late["ok"]!);
        Assert.Equal("session_ended", (string?)late["error"]!["code"]);
        Assert.Contains("Nothing ran", (string?)late["error"]!["message"]);
        Assert.Equal(0, _devices.Get(Pump, On));
        Assert.Equal(2, rig.Mod.Ran);
    }

    [Fact]
    public async Task EveryCallAfterSessionEndIsRefusedOnEveryStreamForTheConnectionsLife()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        client.Send("""{"type":"session_end"}""");

        JObject write = Write(client, "w1", Pump, "On", 1, stream: 0);
        JObject hold = Hold(client, "h1", 9, Set(Pump, "On", 0));
        client.Send("""{"type":"session_end"}""");
        JObject again = Write(client, "w2", Valve, "On", 1, stream: 4);

        Assert.Equal("session_ended", (string?)write["error"]!["code"]);
        Assert.Equal("session_ended", (string?)hold["error"]!["code"]);
        Assert.Equal("session_ended", (string?)again["error"]!["code"]);
        Assert.Equal(0, rig.Mod.Ran);
        Assert.Equal(0, _restores.Held);
        Assert.Equal(0, _devices.Get(Valve, On));
    }

    [Fact]
    public async Task AnotherOwnerIsUnaffectedBySessionEnd()
    {
        using PipeRig rig = Rig();
        using V2Client ending = await V2Client.Connect(rig, "hub");
        using V2Client other = await V2Client.Connect(rig, "hub");
        Write(ending, "w1", Pump, "On", 1, stream: 3);
        Hold(ending, "h1", 3, Set(Pump, "On", 0));
        Write(other, "o1", Valve, "On", 1, stream: 3);
        Hold(other, "o2", 3, Set(Valve, "On", 0));

        ending.Send("""{"type":"session_end"}""");
        WaitUntil(() => _restores.Applied == 1);

        Assert.Equal(0, _devices.Get(Pump, On));
        Assert.True((bool)Write(other, "o3", Pump, "On", 1, stream: 3)["ok"]!);
        Assert.Equal(1, _devices.Get(Pump, On));
        Assert.Equal(1, _devices.Get(Valve, On));
        Assert.Equal(1, _restores.Held);
    }

    [Fact]
    public async Task ATokenPresentedAfterSessionEndStartsFreshAndItsWelcomeReportsTheRestores()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Write(client, "w1", Pump, "On", 1, stream: 3);
            Hold(client, "h1", 3, Set(Pump, "On", 0));
            client.Send("""{"type":"stream_end","stream":5}""");
            client.Send("""{"type":"session_end"}""");
            WaitUntil(() => _restores.Applied == 1);
        }

        using V2Client second = await Resume(rig);

        Assert.False((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Single((JArray)second.Welcome["restores"]!["applied"]!);
        Assert.True((bool)Write(second, "w2", Pump, "On", 1, stream: 5)["ok"]!);
        Assert.Equal(1, _devices.Get(Pump, On));
        WaitUntil(() => rig.Holds.Held == 0);
    }

    [Fact]
    public async Task ATokenTakenOverAfterSessionEndStartsFreshAndTheOldConnectionIsToldWhy()
    {
        using PipeRig rig = Rig();
        using V2Client first = await Resume(rig);
        first.Send("""{"type":"session_end"}""");
        WaitUntil(() => rig.Mod.SessionEnds.Count == 1);

        using V2Client second = await Resume(rig);
        JObject goodbye = first.Next();

        Assert.False((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal("resumed_elsewhere", (string?)goodbye["reason"]);
        Assert.True((bool)Write(second, "w1", Pump, "On", 1, stream: 3)["ok"]!);
    }

    // ---- the write rules on an opening restore ----

    [Fact]
    public async Task AHeldVentClosedWhileOxidiserEntersItsNetworkIsNotReopenedAtSessionEnd()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Hold(client, "h1", 3, Set(Vent, "On", 1));
            Write(client, "w1", Vent, "On", 0, stream: 3);
            _devices.Fill(new Dictionary<string, double> { ["Nitrogen"] = 40.0, ["Oxygen"] = 12.5 });

            client.Send("""{"type":"session_end"}""");
            WaitUntil(() => _restores.Skipped == 1);
        }

        Assert.Equal(0, _devices.Get(Vent, On));
        Assert.Equal(0, _restores.Applied);
        Assert.Equal(1, _devices.Lints);
        using V2Client second = await Resume(rig);
        JObject skipped = (JObject)Assert.Single((JArray)second.Welcome["restores"]!["skipped"]!);
        Assert.Equal("lint", (string?)skipped["reason"]);
        Assert.Equal(0.0, (double)skipped["current"]!);
        Assert.Equal(1.0, (double)skipped["value"]!);
        JToken rule = Assert.Single((JArray)skipped["rules"]!);
        Assert.Equal("no_oxidiser_vented_outdoors", (string?)rule["rule"]);
        Assert.Equal(Id(Vent), (string?)rule["reference_id"]);
        Assert.Contains("vents outdoors", (string?)rule["message"]);
    }

    [Fact]
    public async Task AHeldVentOnACleanNetworkIsReopenedAtSessionEnd()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        Hold(client, "h1", 3, Set(Vent, "On", 1));
        Write(client, "w1", Vent, "On", 0, stream: 3);

        client.Send("""{"type":"session_end"}""");
        WaitUntil(() => _restores.Applied == 1);

        Assert.Equal(1, _devices.Get(Vent, On));
        Assert.Equal(1, _devices.Lints);
        Assert.Equal(0, _restores.Skipped);
    }

    [Fact]
    public async Task AClosingRestoreRunsNoLint()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig, "hub");
        _devices.Set(Vent, On, 0);
        _devices.Fill(new Dictionary<string, double> { ["Oxygen"] = 12.5 });
        Hold(client, "h1", 3, Set(Vent, "On", 0));
        Write(client, "w1", Vent, "On", 1, stream: 3);

        client.Send("""{"type":"session_end"}""");
        WaitUntil(() => _restores.Applied == 1);

        Assert.Equal(0, _devices.Get(Vent, On));
        Assert.Equal(0, _devices.Lints);
        Assert.Equal(0, _devices.RuleReads);
    }

    [Fact]
    public async Task AnOpeningRestoreWhoseLintCannotRunIsSkipped()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Hold(client, "h1", 3, Set(Vent, "On", 1));
            Write(client, "w1", Vent, "On", 0, stream: 3);
            _devices.LintFails = true;

            client.Send("""{"type":"session_end"}""");
            WaitUntil(() => _restores.Skipped == 1);
        }

        Assert.Equal(0, _devices.Get(Vent, On));
        using V2Client second = await Resume(rig);
        JObject skipped = (JObject)Assert.Single((JArray)second.Welcome["restores"]!["skipped"]!);
        Assert.Equal("lint_unavailable", (string?)skipped["reason"]);
        Assert.Equal("unavailable", (string?)skipped["rules"]![0]!["rule"]);
    }

    [Fact]
    public async Task AWarningOnAnOpeningRestoreDoesNotSkipItAndIsListedOnTheAppliedEntry()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Hold(client, "h1", 3, Set(Vent, "On", 1));
            Write(client, "w1", Vent, "On", 0, stream: 3);
            _devices.Indoors();
            _devices.Fill(new Dictionary<string, double> { ["Methane"] = 10.0, ["Oxygen"] = 20.0 }, 650.0);

            client.Send("""{"type":"session_end"}""");
            WaitUntil(() => _restores.Applied == 1);
        }

        Assert.Equal(1, _devices.Get(Vent, On));
        using V2Client second = await Resume(rig);
        JObject applied = (JObject)Assert.Single((JArray)second.Welcome["restores"]!["applied"]!);
        Assert.Equal(new JArray("lint_fuel_oxidiser_mix_temperature"), applied["warnings"]);
        Assert.Null(applied["rules"]);
    }

    [Fact]
    public async Task AnAppliedRestoreWithoutWarningsLeavesThemOut()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await Resume(rig))
        {
            Hold(client, "h1", 3, Set(Vent, "On", 1));
            Write(client, "w1", Vent, "On", 0, stream: 3);
            client.Send("""{"type":"session_end"}""");
            WaitUntil(() => _restores.Applied == 1);
        }

        using V2Client second = await Resume(rig);
        JObject applied = (JObject)Assert.Single((JArray)second.Welcome["restores"]!["applied"]!);
        Assert.Null(applied["warnings"]);
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

        long allocated = ThreadAllocations.Of(() =>
        {
            for (int write = 0; write < 1000; write++)
            {
                _restores.Wrote(stream, 1000, On, write);
                _restores.Wrote(new CallContext("c1", 4), 1000, On, write);
            }
        });

        Assert.Equal(0, allocated);
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
            "write" => new CallOutcome(FakeWrite(call), call.Request.Method),
            _ => null
        };
        return rig;
    }

    // write {what: "logic"} as a logic write does it: the device's value set, then the stream's last written value told.
    private string FakeWrite(ProtocolCall call)
    {
        JObject parameters = call.Request.Params!;
        long id = long.Parse((string)parameters["id"]!, CultureInfo.InvariantCulture);
        ushort type = _devices.LogicType(parameters["logic"]).Id;
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
            ["type"] = "call", ["id"] = id, ["method"] = "write", ["stream"] = stream,
            ["params"] = new JObject
            {
                ["what"] = "logic", ["id"] = Id(device), ["logic"] = logicType, ["value"] = value, ["confirm"] = true
            }
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

    /// <summary>
    /// Devices with logic values by (reference id, logic type); On is 28 and Setting 12, as in the game. The vent (303)
    /// is an outdoor active vent on pipe network 7, judged by the write rules as OpeningWrites judges a game device;
    /// Lints counts the rules' runs, RuleReads the rule set asked for.
    /// </summary>
    private sealed class FakeDevices : IRestoreDevices
    {
        private readonly object _sync = new object();
        private readonly Dictionary<(long, ushort), double> _values = new Dictionary<(long, ushort), double>();
        private readonly LintRuleSet _rules = LintTestKit.World();
        private readonly Dictionary<string, LintValue> _network = new Dictionary<string, LintValue>(StringComparer.Ordinal)
        {
            ["id"] = LintValue.Of(7), ["kind"] = LintValue.Of("pipe"), ["temperature"] = LintValue.Of(290.0),
            ["gases"] = Gases(new Dictionary<string, double> { ["Nitrogen"] = 40.0 })
        };

        private bool _outdoors = true;

        internal int Lints { get; private set; }

        internal int RuleReads { get; private set; }

        /// <summary>The rules throw when they run, as a rule file or game read that fails does.</summary>
        internal bool LintFails { get; set; }

        internal void Fill(Dictionary<string, double> gases, double temperature = 290.0)
        {
            lock (_sync)
            {
                _network["gases"] = Gases(gases);
                _network["temperature"] = LintValue.Of(temperature);
            }
        }

        internal void Indoors()
        {
            lock (_sync)
            {
                _outdoors = false;
            }
        }

        public OpeningVerdict Judge(long referenceId, ushort type, double previous, double value)
        {
            OpeningDevice kind = referenceId == Vent ? OpeningDevice.Vent
                : referenceId == Pump ? OpeningDevice.Pump
                : referenceId == Valve ? OpeningDevice.Valve
                : OpeningDevice.None;
            OpeningLogic logic = type == On ? OpeningLogic.On : type == Setting ? OpeningLogic.Setting : OpeningLogic.None;
            lock (_sync)
            {
                try
                {
                    return OpeningLint.Judge(kind, logic, previous, value, 2, () =>
                        {
                            RuleReads++;
                            return LintFails ? throw new InvalidOperationException("the rule file broke") : _rules;
                        },
                        () =>
                        {
                            Lints++;
                            return new OpeningWorld(new TestLintWorld(), Thing(referenceId, kind), kind);
                        });
                }
                catch (InvalidOperationException error)
                {
                    return OpeningVerdict.Unavailable(referenceId, error);
                }
            }
        }

        // The vent on network 7; a pump or valve on no network, so its rules find nothing.
        private LintRecord Thing(long referenceId, OpeningDevice kind)
        {
            LintRecord network = new LintRecord(LintModel.Network, "network:7", _network);
            Dictionary<string, LintValue> fields = new Dictionary<string, LintValue>(StringComparer.Ordinal)
            {
                ["reference_id"] = LintValue.Of(referenceId), ["kind"] = LintValue.Of("device"),
                ["outdoors"] = LintValue.Of(_outdoors),
                ["networks"] = LintValue.Objects(kind == OpeningDevice.Vent
                    ? new List<ILintObject> { network }
                    : new List<ILintObject>())
            };
            if (kind == OpeningDevice.Vent)
            {
                fields["prefab"] = LintValue.Of("StructureActiveVent");
                fields["display_name"] = LintValue.Of("Active Vent");
                fields["runtime_types"] = LintValue.Strings(new List<string> { "ActiveVent", "Device" });
            }

            LintRecord thing = new LintRecord(LintModel.Thing, "thing:" + referenceId, fields);
            if (kind == OpeningDevice.Vent)
            {
                _network["devices"] = LintValue.Objects(new List<ILintObject> { thing });
            }

            return thing;
        }

        private static LintValue Gases(Dictionary<string, double> gases)
        {
            Dictionary<string, LintValue> map = new Dictionary<string, LintValue>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> gas in gases)
            {
                map[gas.Key] = LintValue.Of(gas.Value);
            }

            return LintValue.Of(map);
        }

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
