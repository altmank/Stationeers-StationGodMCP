#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>What one cached sample read is of: a logic value, a slot's logic value, a slot's every value, an atmosphere, reagents.</summary>
internal enum SampleReadPart : byte
{
    Logic,
    SlotLogic,
    SlotAll,
    Atmosphere,
    Reagents,
}

/// <summary>One read of one thing in a sample: the thing, the part, the slot (or port), the logic type (or target kind).</summary>
internal readonly struct SampleReadKey : IEquatable<SampleReadKey>
{
    internal SampleReadKey(long referenceId, SampleReadPart part, int index, int type)
    {
        ReferenceId = referenceId;
        Part = part;
        Index = index;
        Type = type;
    }

    internal long ReferenceId { get; }

    internal SampleReadPart Part { get; }

    internal int Index { get; }

    internal int Type { get; }

    public bool Equals(SampleReadKey other) =>
        ReferenceId == other.ReferenceId && Part == other.Part && Index == other.Index && Type == other.Type;

    public override bool Equals(object? obj) => obj is SampleReadKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = ReferenceId.GetHashCode();
            hash = hash * 397 ^ (int)Part;
            hash = hash * 397 ^ Index;
            return hash * 397 ^ Type;
        }
    }
}

/// <summary>A cached read: a number, or what else the read gave (a view, an error); the reader knows which.</summary>
internal readonly struct SampleRead
{
    internal SampleRead(double number, object? other)
    {
        Number = number;
        Other = other;
    }

    internal double Number { get; }

    internal object? Other { get; }
}

/// <summary>
/// Subscription reads shared within one sample tick (cost design A6): every devices subscription sampled in a frame
/// takes a device's logic type, slot value, atmosphere or reagents from the first read of it in that frame, so
/// subscribers of the same device, on any connection or hub session, cost one read between them. Begin with the frame
/// before a sample: a new frame forgets the last one's reads. What is kept is the read's result as the reader made it
/// (a number, a view, an error), never changed after: two readings may hold the same object. Main thread.
/// </summary>
internal sealed class SampleReads
{
    private readonly Dictionary<SampleReadKey, SampleRead> _reads = new Dictionary<SampleReadKey, SampleRead>();
    private long _frame = long.MinValue;

    /// <summary>Reads made (each kept for the rest of its frame).</summary>
    internal long Made { get; private set; }

    /// <summary>Reads taken from the frame's earlier read instead of reading again.</summary>
    internal long Shared { get; private set; }

    /// <summary>The frame the next reads belong to; another frame than the last forgets every read kept.</summary>
    internal void Begin(long frame)
    {
        if (frame != _frame)
        {
            _reads.Clear();
            _frame = frame;
        }
    }

    /// <summary>The frame's earlier read of key, if any (the caller reads and Keeps it when not).</summary>
    internal bool TryTake(SampleReadKey key, out SampleRead read)
    {
        if (_reads.TryGetValue(key, out read))
        {
            Shared++;
            return true;
        }

        return false;
    }

    /// <summary>A read just made, kept for the rest of the frame.</summary>
    internal void Keep(SampleReadKey key, SampleRead read)
    {
        _reads[key] = read;
        Made++;
    }
}
