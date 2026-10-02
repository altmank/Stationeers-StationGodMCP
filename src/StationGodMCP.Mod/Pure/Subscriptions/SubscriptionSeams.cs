#nullable enable

using System;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>One frame of the game: the mod's frame counter and the game clock (Unity Time.time; stops while paused).</summary>
internal readonly struct SamplingTick
{
    internal SamplingTick(long frame, double gameTimeS)
    {
        Frame = frame;
        GameTimeS = gameTimeS;
    }

    internal long Frame { get; }

    internal double GameTimeS { get; }
}

/// <summary>Reads a devices subscription's items, all in the calling frame, as read_devices reads them.</summary>
internal interface IDeviceReader<out TReading> where TReading : class
{
    TReading Read(DeviceSubscriptionQuery query);
}

/// <summary>
/// Whether two readings carry the same values: numbers as numbers, strings by ordinal equality, errors by code, and
/// never the clock, which is not a change.
/// </summary>
internal interface IReadingComparer<in TReading>
{
    bool SameValues(TReading previous, TReading next);
}

/// <summary>
/// The subscription lane's say over one frame, asked before each sample. Its owner answers yes to the frame's first
/// due sample whatever it costs, so sampling always moves, and stops the lane at its share of the frame.
/// </summary>
internal interface ISamplingBudget
{
    bool MayTakeAnother();
}

/// <summary>
/// The lane budget the scheduler hands the samplers each frame: the first sample always, then more while the
/// milliseconds spent sampling this frame (from the injected clock) stay under the share.
/// </summary>
internal sealed class LaneSamplingBudget : ISamplingBudget
{
    private readonly double _shareMs;
    private readonly Func<double> _spentMs;
    private int _taken;

    internal LaneSamplingBudget(double shareMs, Func<double> spentMs)
    {
        _shareMs = shareMs;
        _spentMs = spentMs;
    }

    public bool MayTakeAnother()
    {
        bool may = _taken == 0 || _spentMs() < _shareMs;
        if (may)
        {
            _taken++;
        }

        return may;
    }
}

/// <summary>Why the mod ended a subscription (subscription_ended's reason).</summary>
internal abstract class SubscriptionEnd
{
    private SubscriptionEnd(string reason)
    {
        Reason = reason;
    }

    internal string Reason { get; }

    /// <summary>A new world loaded: reference ids may name other things now.</summary>
    internal static readonly SubscriptionEnd WorldChanged = new Named("world_changed");

    /// <summary>The connection lost the level the subscription needs.</summary>
    internal static readonly SubscriptionEnd Revoked = new Named("revoked");

    /// <summary>The owner lowered a limit the subscription now passes.</summary>
    internal static readonly SubscriptionEnd Limit = new Named("limit");

    private sealed class Named : SubscriptionEnd
    {
        internal Named(string reason) : base(reason)
        {
        }
    }

    /// <summary>The read threw: a bug or a game change, not a value; the failure is for the log.</summary>
    internal sealed class ReadFailed : SubscriptionEnd
    {
        internal ReadFailed(Exception failure) : base("read_failed")
        {
            Failure = failure;
        }

        internal Exception Failure { get; }
    }
}

/// <summary>What the engine hands the transport. Main thread; implementations only queue.</summary>
internal interface ISubscriptionEvents<TReading> where TReading : class
{
    /// <summary>A new update waits in the slot: put the slot at the back of the connection's outbound queue.</summary>
    void Enqueue(UpdateSlot<TReading> slot);

    /// <summary>The mod ended the subscription: send subscription_ended.</summary>
    void Ended(ConnectionId connection, SubscriptionId subscription, SubscriptionEnd end);

    /// <summary>For a world-topic subscription: a world finished loading.</summary>
    void WorldChanged(ConnectionId connection, SubscriptionId subscription, WorldId world);

    /// <summary>For a world-topic subscription: the game state changed (Running, Paused, Loading, ...).</summary>
    void GameStateChanged(ConnectionId connection, SubscriptionId subscription, string state);
}
