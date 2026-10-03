#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.DeviceReads;
using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>A reading of one value plus a clock that changes every sample and is never a change.</summary>
internal sealed class FakeReading
{
    internal FakeReading(double value, double clock)
    {
        Value = value;
        Clock = clock;
    }

    internal double Value { get; }

    internal double Clock { get; }
}

internal sealed class FakeComparer : IReadingComparer<FakeReading>
{
    internal static readonly FakeComparer Instance = new FakeComparer();

    public bool SameValues(FakeReading previous, FakeReading next) => previous.Value.Equals(next.Value);
}

/// <summary>Reads Value (the device's state now); counts reads; throws when told to.</summary>
internal sealed class FakeReader : IDeviceReader<FakeReading>
{
    private double _clock;

    internal double Value { get; set; }

    internal int Reads { get; private set; }

    internal Exception? Throw { get; set; }

    public FakeReading Read(DeviceSubscriptionQuery query)
    {
        if (Throw != null)
        {
            throw Throw;
        }

        Reads++;
        return new FakeReading(Value, _clock++);
    }
}

/// <summary>Records what the engine hands the transport, in order.</summary>
internal sealed class RecordingEvents : ISubscriptionEvents<FakeReading>
{
    internal List<UpdateSlot<FakeReading>> Queue { get; } = new List<UpdateSlot<FakeReading>>();

    internal List<(ConnectionId Connection, SubscriptionId Subscription, SubscriptionEnd End)> Ended { get; } =
        new List<(ConnectionId, SubscriptionId, SubscriptionEnd)>();

    internal List<(ConnectionId Connection, SubscriptionId Subscription, WorldId World)> Worlds { get; } =
        new List<(ConnectionId, SubscriptionId, WorldId)>();

    internal List<(ConnectionId Connection, SubscriptionId Subscription, string State)> States { get; } =
        new List<(ConnectionId, SubscriptionId, string)>();

    public void Enqueue(UpdateSlot<FakeReading> slot) => Queue.Add(slot);

    void ISubscriptionEvents<FakeReading>.Ended(ConnectionId connection, SubscriptionId subscription, SubscriptionEnd end) =>
        Ended.Add((connection, subscription, end));

    public void WorldChanged(ConnectionId connection, SubscriptionId subscription, WorldId world) =>
        Worlds.Add((connection, subscription, world));

    public void GameStateChanged(ConnectionId connection, SubscriptionId subscription, string state) =>
        States.Add((connection, subscription, state));

    /// <summary>The writer: takes every queued slot in order, as a client reading its pipe would receive them.</summary>
    internal List<SubscriptionUpdate<FakeReading>> Drain()
    {
        List<SubscriptionUpdate<FakeReading>> written = new List<SubscriptionUpdate<FakeReading>>();
        foreach (UpdateSlot<FakeReading> slot in Queue)
        {
            if (slot.TryTake(out SubscriptionUpdate<FakeReading> update))
            {
                written.Add(update);
            }
        }

        Queue.Clear();
        return written;
    }
}

/// <summary>Allows a fixed number of samples per frame (Unlimited: every one).</summary>
internal sealed class CountBudget : ISamplingBudget
{
    private int _left;

    internal CountBudget(int samples)
    {
        _left = samples;
    }

    internal static CountBudget Unlimited => new CountBudget(int.MaxValue);

    public bool MayTakeAnother() => _left-- > 0;
}

internal sealed class ScriptedWorldIds
{
    private byte _next;

    public WorldId Next() => WorldId.FromBytes(new byte[] { 0, 0, 0, 0, 0, 0, 0, ++_next });
}

internal static class Queries
{
    /// <summary>A devices query of items items, each reading logicPerItem logic values.</summary>
    internal static DeviceSubscriptionQuery Logic(int items = 1, int logicPerItem = 1)
    {
        StringBuilder json = new StringBuilder("[");
        for (int item = 0; item < items; item++)
        {
            json.Append(item == 0 ? "" : ",").Append("{\"reference_id\":\"").Append(1000 + item).Append("\",\"logic\":[");
            for (int logic = 0; logic < logicPerItem; logic++)
            {
                json.Append(logic == 0 ? "" : ",").Append(logic);
            }

            json.Append("]}");
        }

        json.Append(']');
        return Of(json.ToString());
    }

    internal static DeviceSubscriptionQuery Of(string items)
    {
        SubscribeRequest request = SubscribeRequest.Of(new JObject { ["items"] = JToken.Parse(items) });
        return Assert.IsType<SubscribeRequest.Devices>(request).Query;
    }

    internal static SamplingInterval Interval(double seconds)
    {
        Assert.True(SamplingInterval.TryOf(seconds, out SamplingInterval interval));
        return interval;
    }
}
