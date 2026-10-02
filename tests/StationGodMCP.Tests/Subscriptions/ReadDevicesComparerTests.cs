#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>
/// The comparison of two read_devices readings, value by value: the clock never counts, every value does, an item that
/// starts failing does, errors compare by code, and the subscription and update wire shapes.
/// </summary>
public sealed class ReadDevicesComparerTests
{
    private sealed class Device
    {
        internal double On = 1;
        internal double Pressure = 101.3;
        internal double Gas = 4.5;
        internal double Iron = 20;
        internal string? LogicError;
        internal string? SlotError;
        internal string? PartError;
        internal bool Missing;
    }

    private static ReadDevicesView Reading(Device device, float clock = 10f)
    {
        BatchBuilder batch = new BatchBuilder(2);
        if (device.Missing)
        {
            batch.Failed(new DeviceReadFailedView(0, new ThingId(5), new ApiException("thing_not_found", "No thing 5.")));
        }
        else
        {
            Dictionary<string, ErrorView> logicErrors = new Dictionary<string, ErrorView>();
            if (device.LogicError != null)
            {
                logicErrors["Bogus"] = new ErrorView(device.LogicError, "Message " + clock);
            }

            Dictionary<string, ErrorView> partErrors = new Dictionary<string, ErrorView>();
            if (device.PartError != null)
            {
                partErrors["reagents"] = new ErrorView(device.PartError, "Message");
            }

            SlotReadView slot = device.SlotError != null
                ? SlotReadView.Failed(0, new ApiException(device.SlotError, "No slot."))
                : SlotReadView.Read(0, new LogicReadings(new Dictionary<string, double> { ["Quantity"] = 3 },
                    new Dictionary<string, ErrorView>()));
            batch.Succeeded(new DeviceReadItemView(0, new ThingId(5), new DeviceReadParts(
                new LogicReadings(new Dictionary<string, double> { ["On"] = device.On, ["Pressure"] = device.Pressure },
                    logicErrors),
                new List<SlotReadView> { slot },
                new AtmosphereReadView("pipe_network", new ThingId(70), new ThingId(71),
                    new AtmosphereState(100, device.Pressure, 293.15, 12, 0),
                    new List<CompactGasView> { new CompactGasView("Oxygen", false, device.Gas, null) }),
                device.PartError != null ? null : new ReagentsReadView(device.Iron,
                    new List<ReagentView> { new ReagentView("Iron", "Iron", device.Iron, "g") }),
                partErrors)));
        }

        batch.Succeeded(new DeviceReadItemView(1, new ThingId(6), new DeviceReadParts(
            new LogicReadings(new Dictionary<string, double> { ["Setting"] = double.NaN },
                new Dictionary<string, ErrorView>()), null, null, null, null)));
        return new ReadDevicesView("world", new GameClockView(clock, false, clock / 100f, 3), batch.Build());
    }

    private static bool Same(Device before, Device after) =>
        ReadDevicesComparer.Instance.SameValues(Reading(before, 10f), Reading(after, 11f));

    [Fact]
    public void TheClockAloneIsNoChange()
    {
        Assert.True(Same(new Device(), new Device()));
    }

    [Fact]
    public void NaNEqualsNaN()
    {
        Device device = new Device { On = double.NaN };

        Assert.True(Same(device, device));
    }

    [Fact]
    public void ALogicValueChangeIsAChange()
    {
        Assert.False(Same(new Device(), new Device { On = 0 }));
    }

    [Fact]
    public void AnAtmosphereFigureChangeIsAChange()
    {
        Assert.False(Same(new Device(), new Device { Gas = 4.6 }));
    }

    [Fact]
    public void AReagentChangeIsAChange()
    {
        Assert.False(Same(new Device(), new Device { Iron = 21 }));
    }

    [Fact]
    public void AnItemThatStartsFailingIsAChange()
    {
        Assert.False(Same(new Device(), new Device { Missing = true }));
        Assert.True(Same(new Device { Missing = true }, new Device { Missing = true }));
    }

    [Fact]
    public void ASlotThatStartsFailingIsAChange()
    {
        Assert.False(Same(new Device(), new Device { SlotError = "slot_not_found" }));
    }

    [Fact]
    public void APartThatStartsFailingIsAChange()
    {
        Assert.False(Same(new Device(), new Device { PartError = "device_out_of_scope" }));
    }

    [Fact]
    public void ErrorsCompareByCodeNotMessage()
    {
        // Reading puts the clock in the logic error's message: same code, other message, no change.
        Assert.True(Same(new Device { LogicError = "logic_not_readable" }, new Device { LogicError = "logic_not_readable" }));
        Assert.False(Same(new Device { LogicError = "logic_not_readable" }, new Device { LogicError = "logic_unknown" }));
        Assert.False(Same(new Device(), new Device { LogicError = "logic_not_readable" }));
    }

    [Fact]
    public void AnotherGatewayIsAChange()
    {
        ReadDevicesView world = Reading(new Device());
        ReadDevicesView other = new ReadDevicesView("gw1", null,
            new BatchResultView(world.Results, world.SuccessCount));

        Assert.False(ReadDevicesComparer.Instance.SameValues(world, other));
    }

    // ---- wire ----

    [Fact]
    public void TheUpdateEventCarriesTheWholeReading()
    {
        UpdateSlot<ReadDevicesView> slot = new UpdateSlot<ReadDevicesView>(new ConnectionId("c1"), new SubscriptionId(3));
        slot.Offer(Reading(new Device()), 81312, 84211.5, 0.004);
        Assert.True(slot.TryTake(out SubscriptionUpdate<ReadDevicesView> update));

        JObject json = JObject.Parse(ApiJson.WriteFresh(UpdateEventView.Of(update)));

        Assert.Equal(new[] { "event", "subscription", "seq", "frame", "game_time_s", "late_ms", "result" },
            Keys(json));
        Assert.Equal("update", (string?)json["event"]);
        Assert.Equal("s3", (string?)json["subscription"]);
        Assert.Equal(1, (long)json["seq"]!);
        Assert.Equal(0.0, (double)json["late_ms"]!);
        Assert.Equal("world", (string?)json["result"]!["gateway_id"]);
        Assert.Equal("5", (string?)json["result"]!["results"]![0]!["reference_id"]);
        Assert.NotNull(json["result"]!["clock"]);
    }

    [Fact]
    public void TheSubscribeReplyNamesTheSubscriptionAndHoldsTheFirstReading()
    {
        SubscribeOutcome<ReadDevicesView>.Subscribed subscribed = new SubscribeOutcome<ReadDevicesView>.Subscribed(
            new SubscriptionId(3), SamplingInterval.Default, 412, 81250, Reading(new Device()));

        JObject json = JObject.Parse(ApiJson.WriteFresh(SubscribeReplyView.Of(subscribed)));

        Assert.Equal(new[] { "subscription", "interval_s", "values", "frame", "result" }, Keys(json));
        Assert.Equal(1.0, (double)json["interval_s"]!);
        Assert.Equal(412, (int)json["values"]!);
    }

    [Fact]
    public void TheOtherSubscriptionMessagesHaveTheirDocumentedKeys()
    {
        SubscriptionId id = new SubscriptionId(3);

        Assert.Equal("""{"subscription":"s3","ended":true}""", ApiJson.WriteFresh(new UnsubscribeReplyView(id)));
        Assert.Equal("""{"event":"subscription_ended","subscription":"s3","reason":"world_changed"}""",
            ApiJson.WriteFresh(new SubscriptionEndedEventView(id, SubscriptionEnd.WorldChanged)));
        Assert.Equal("""{"event":"game_state","subscription":"s3","state":"Paused"}""",
            ApiJson.WriteFresh(new GameStateEventView(id, "Paused")));
        Assert.Equal("""{"event":"world_changed","subscription":"s3","world":{"id":"0000000000000001"}}""",
            ApiJson.WriteFresh(new WorldChangedEventView(id, new ScriptedWorldIds().Next())));
    }

    [Fact]
    public void TheLimitErrorDataGivesTheLimitAndTheProjection()
    {
        SubscriptionRefusal refusal = new SubscriptionRefusal(SubscriptionLimitKind.ProjectedLoad, 22.5, 30.72,
            30.72);

        Assert.Equal("""{"name":"max_projected_ms_per_s","limit":22.5,"requested":30.72,"projected_ms_per_s":30.72}""",
            ApiJson.WriteFresh(refusal.Data));
        Assert.Contains("read_devices", refusal.Message);
    }

    private static string[] Keys(JObject json)
    {
        List<string> keys = new List<string>();
        foreach (JProperty property in json.Properties())
        {
            keys.Add(property.Name);
        }

        return keys.ToArray();
    }
}
