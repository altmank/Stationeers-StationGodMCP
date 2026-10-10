#nullable enable

using System.Threading;

namespace StationGodMCP.Pure.Profiling;

/// <summary>
/// Events counted since the mod loaded, and their rate per minute over the last full window: WindowSlots slots of
/// SlotSeconds each (6 x 10 s, one minute), the slot under way left out, so the rate lags by at most one slot and reads
/// low until a full window has passed since load. Any thread: Note is lock-free (Interlocked) and allocates nothing.
/// Each slot is one long holding its slot number (high bits) and its count (low CountBits bits), so a slot moving on
/// to a new number and the count it starts with land together. Timestamps and frequency are the profiler's
/// (ProfileClock).
/// </summary>
internal sealed class EventRate
{
    internal const int WindowSlots = 6;
    internal const double SlotSeconds = 10.0;

    private const int CountBits = 24;
    private const long CountMask = (1L << CountBits) - 1;

    private readonly long _slotTicks;
    private readonly long[] _slots = new long[WindowSlots + 1];
    private long _total;

    internal EventRate(long frequency)
    {
        _slotTicks = (long)(frequency * SlotSeconds);
    }

    internal long Total => Interlocked.Read(ref _total);

    internal void Note(long timestamp)
    {
        Interlocked.Increment(ref _total);
        long number = timestamp / _slotTicks;
        ref long slot = ref _slots[(int)(number % _slots.Length)];
        while (true)
        {
            long seen = Interlocked.Read(ref slot);
            long next = (seen >> CountBits) != number ? (number << CountBits) | 1
                : (seen & CountMask) == CountMask ? seen
                : seen + 1;
            if (Interlocked.CompareExchange(ref slot, next, seen) == seen)
            {
                return;
            }
        }
    }

    /// <summary>Events per minute over the WindowSlots full slots before the one under way at now.</summary>
    internal double PerMinute(long now)
    {
        long current = now / _slotTicks;
        long counted = 0;
        for (int index = 0; index < _slots.Length; index++)
        {
            long slot = Interlocked.Read(ref _slots[index]);
            long number = slot >> CountBits;
            if (number < current && number >= current - WindowSlots)
            {
                counted += slot & CountMask;
            }
        }

        return counted * 60.0 / (WindowSlots * SlotSeconds);
    }
}
