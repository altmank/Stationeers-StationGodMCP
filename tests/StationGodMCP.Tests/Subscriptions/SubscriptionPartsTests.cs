#nullable enable

using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>The update slot, subscribe's params, the ids and the world identity.</summary>
public sealed class SubscriptionPartsTests
{
    private static readonly ConnectionId A = new ConnectionId("c1");

    // ---- the update slot ----

    [Fact]
    public void AnOfferIntoAnEmptySlotIsANewEventWithTheNextSeq()
    {
        UpdateSlot<FakeReading> slot = new UpdateSlot<FakeReading>(A, new SubscriptionId(3));

        Assert.True(slot.Offer(new FakeReading(1, 0), 10, 1.0, 0.0));
        Assert.True(slot.TryTake(out SubscriptionUpdate<FakeReading> first));
        Assert.True(slot.Offer(new FakeReading(2, 0), 11, 2.0, 0.0));
        Assert.True(slot.TryTake(out SubscriptionUpdate<FakeReading> second));

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal("s3", second.Subscription.ToString());
    }

    [Fact]
    public void AnOfferWhileOneWaitsReplacesItsContentsAndKeepsItsSeq()
    {
        UpdateSlot<FakeReading> slot = new UpdateSlot<FakeReading>(A, new SubscriptionId(1));
        slot.Offer(new FakeReading(1, 0), 10, 1.0, 5.0);

        Assert.False(slot.Offer(new FakeReading(2, 0), 12, 3.0, 0.0));
        Assert.True(slot.TryTake(out SubscriptionUpdate<FakeReading> update));

        Assert.Equal(1, update.Seq);
        Assert.Equal(2, update.Reading.Value);
        Assert.Equal(12, update.Frame);
        Assert.Equal(3.0, update.GameTimeS);
        Assert.Equal(0.0, update.LateMs);
        Assert.False(slot.TryTake(out _));
    }

    [Fact]
    public void AClosedSlotTakesNoOfferAndWritesNothing()
    {
        UpdateSlot<FakeReading> slot = new UpdateSlot<FakeReading>(A, new SubscriptionId(1));
        slot.Offer(new FakeReading(1, 0), 10, 1.0, 0.0);

        slot.Close();

        Assert.False(slot.TryTake(out _));
        Assert.False(slot.Offer(new FakeReading(2, 0), 11, 2.0, 0.0));
    }

    [Fact]
    public void OffersAndTakesOnTwoThreadsNeverLoseOrRepeatASeq()
    {
        UpdateSlot<FakeReading> slot = new UpdateSlot<FakeReading>(A, new SubscriptionId(1));
        List<long> written = new List<long>();
        bool done = false;
        Task writer = Task.Run(() =>
        {
            while (true)
            {
                bool finished = Volatile.Read(ref done);
                if (slot.TryTake(out SubscriptionUpdate<FakeReading> update))
                {
                    written.Add(update.Seq);
                }
                else if (finished)
                {
                    return;
                }
            }
        });
        int enqueued = 0;
        for (int index = 0; index < 20000; index++)
        {
            enqueued += slot.Offer(new FakeReading(index, 0), index, index, 0.0) ? 1 : 0;
        }

        Volatile.Write(ref done, true);
        writer.Wait();

        Assert.Equal(enqueued, slot.LastSeq);
        Assert.Equal(enqueued, written.Count);
        for (int index = 0; index < written.Count; index++)
        {
            Assert.Equal(index + 1, written[index]);
        }
    }

    // ---- subscribe's params ----

    private static SubscribeRequest Parse(string json) => SubscribeRequest.Of(JObject.Parse(json));

    [Fact]
    public void DevicesIsTheDefaultTopicAndOneSecondTheDefaultInterval()
    {
        SubscribeRequest.Devices devices = Assert.IsType<SubscribeRequest.Devices>(
            Parse("""{"items": [{"reference_id": "5", "logic": ["On"]}]}"""));

        Assert.Equal(1.0, devices.Interval.Seconds);
        Assert.Null(devices.Query.GatewayId);
        Assert.Single(devices.Query.Request.Items);
    }

    [Fact]
    public void TheQueryKeepsReadDevicesOwnArguments()
    {
        SubscribeRequest.Devices devices = Assert.IsType<SubscribeRequest.Devices>(Parse(
            """{"topic": "devices", "gateway_id": "gw1", "interval_s": 2, "include": ["clock"], "items": [{"reference_id": "5", "logic": ["On"]}]}"""));

        JObject arguments = devices.Query.ReadArguments;
        Assert.Equal("gw1", (string?)arguments["gateway_id"]);
        Assert.Equal("clock", (string?)arguments["include"]![0]);
        Assert.Equal("5", (string?)arguments["items"]![0]!["reference_id"]);
        Assert.Null(arguments["interval_s"]);
        Assert.True(devices.Query.Request.Clock);
        Assert.Equal(2.0, devices.Interval.Seconds);
    }

    [Fact]
    public void TheWorldTopicTakesNothingElse()
    {
        Assert.IsType<SubscribeRequest.World>(Parse("""{"topic": "world"}"""));
        Assert.IsType<SubscribeRequest.Invalid>(Parse("""{"topic": "world", "interval_s": 1}"""));
    }

    [Theory]
    [InlineData("""{"items": [{"reference_id": "5", "logic": ["On"]}], "interval_s": 0.4}""")]
    [InlineData("""{"items": [{"reference_id": "5", "logic": ["On"]}], "interval_s": 3601}""")]
    [InlineData("""{"items": [{"reference_id": "5", "logic": ["On"]}], "interval_s": "1"}""")]
    [InlineData("""{"items": [{"reference_id": "5", "logic": ["On"]}], "gateway_id": 4}""")]
    [InlineData("""{"items": [{"reference_id": "5", "logic": ["On"]}], "fields": ["x"]}""")]
    [InlineData("""{"topic": "rooms"}""")]
    [InlineData("""{"items": [{"reference_id": "5"}]}""")]
    [InlineData("""{}""")]
    public void BadParamsAreInvalidArguments(string json)
    {
        Assert.IsType<SubscribeRequest.Invalid>(Parse(json));
    }

    [Fact]
    public void The129thItemIsASubscriptionLimitNotAnInvalidArgument()
    {
        StringBuilder items = new StringBuilder("[");
        for (int index = 0; index < 129; index++)
        {
            items.Append(index == 0 ? "" : ",").Append("{\"reference_id\":\"").Append(index + 1).Append("\",\"logic\":[\"On\"]}");
        }

        SubscriptionRefusal refusal = Assert.IsType<SubscribeRequest.OverLimit>(
            SubscribeRequest.Of(new JObject { ["items"] = JArray.Parse(items.Append(']').ToString()) })).Refusal;

        Assert.Equal(SubscriptionLimitKind.ItemsPerSubscription, refusal.Kind);
        Assert.Equal(129, refusal.Requested);
    }

    [Fact]
    public void The1025thValueInOneSubscriptionIsASubscriptionLimit()
    {
        // 16 items of 64 logic values = 1,024; one more value passes the bound of one read_devices call.
        StringBuilder items = new StringBuilder("[");
        for (int item = 0; item < 17; item++)
        {
            int count = item < 16 ? 64 : 1;
            items.Append(item == 0 ? "" : ",").Append("{\"reference_id\":\"").Append(item + 1).Append("\",\"logic\":[");
            for (int logic = 0; logic < count; logic++)
            {
                items.Append(logic == 0 ? "" : ",").Append(logic);
            }

            items.Append("]}");
        }

        SubscriptionRefusal refusal = Assert.IsType<SubscribeRequest.OverLimit>(
            SubscribeRequest.Of(new JObject { ["items"] = JArray.Parse(items.Append(']').ToString()) })).Refusal;

        Assert.Equal(SubscriptionLimitKind.ValuesPerSubscription, refusal.Kind);
        Assert.Equal("max_values_per_subscription", refusal.Name);
        Assert.Equal(1024, refusal.Maximum);
        Assert.Equal(1025, refusal.Requested);
    }

    [Fact]
    public void ExactlyOneCallsWorthIsAdmittedByTheParser()
    {
        Assert.Equal(1024, SubscriptionPlan.Of(Queries.Logic(items: 16, logicPerItem: 64).Request).Values);
    }

    [Fact]
    public void ASlotWithoutLogicCountsThirtyTwoValues()
    {
        SubscriptionPlan plan = SubscriptionPlan.Of(Queries.Of(
            """[{"reference_id": "5", "slots": [{"index": 0}], "atmosphere": {}, "reagents": true}]""").Request);

        Assert.Equal(32, plan.Values);
        Assert.Equal(32 * 0.005 + 0.08 + 0.05, plan.CostMs, 9);
    }

    [Fact]
    public void UnsubscribeTakesASubscriptionId()
    {
        Assert.True(UnsubscribeRequest.TryOf(JObject.Parse("""{"subscription": "s12"}"""), out SubscriptionId id, out _));
        Assert.Equal(12, id.Number);
        Assert.False(UnsubscribeRequest.TryOf(JObject.Parse("""{"subscription": "12"}"""), out _, out _));
        Assert.False(UnsubscribeRequest.TryOf(JObject.Parse("""{"subscription": "s0"}"""), out _, out _));
        Assert.False(UnsubscribeRequest.TryOf(JObject.Parse("""{"subscription": "s1", "x": 1}"""), out _, out _));
    }

    // ---- world identity ----

    [Fact]
    public void AWorldGetsANewIdEachTimeItFinishesLoading()
    {
        WorldIdentity identity = new WorldIdentity(new ScriptedWorldIds());
        Assert.False(identity.TryGetCurrent(out _));

        Assert.False(identity.Observe(false));
        Assert.True(identity.Observe(true));
        identity.TryGetCurrent(out WorldId first);
        Assert.False(identity.Observe(true));
        Assert.False(identity.Observe(false));
        identity.TryGetCurrent(out WorldId stillFirst);
        Assert.True(identity.Observe(true));
        identity.TryGetCurrent(out WorldId second);

        Assert.Equal(first, stillFirst);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RandomWorldIdsAreSixteenHexDigitsAndDiffer()
    {
        RandomWorldIds source = new RandomWorldIds();
        WorldId a = source.Next();
        WorldId b = source.Next();

        Assert.Matches("^[0-9a-f]{16}$", a.Value);
        Assert.NotEqual(a, b);
    }
}
