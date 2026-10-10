#nullable enable

using System;

namespace StationGodMCP.Pure.Profiling;

/// <summary>
/// How long jobs held the game tick since the mod loaded, profiling on or off: each stretch from a job asking for the
/// hold to letting the tick go counts once, in the bucket of its length. The buckets end at EdgesMs (100 ms, 1 s,
/// 10 s, 60 s; each edge belongs to the bucket above it), the last one open. A hold asked for while one is open keeps
/// the first start; a release with none open counts nothing. Timestamps and frequency are the profiler's
/// (ProfileClock). Main thread only: plain increments into a fixed array, nothing allocated per hold.
/// </summary>
internal sealed class HoldLengths
{
    /// <summary>The upper edges of every bucket but the last, in ms.</summary>
    internal static readonly double[] EdgesMs = { 100.0, 1_000.0, 10_000.0, 60_000.0 };

    internal static int BucketCount => EdgesMs.Length + 1;

    private readonly long[] _counts = new long[EdgesMs.Length + 1];
    private bool _open;
    private long _started;

    internal void Held(long timestamp)
    {
        if (_open)
        {
            return;
        }

        _open = true;
        _started = timestamp;
    }

    internal void Released(long timestamp, long frequency)
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _counts[BucketOf((timestamp - _started) * 1000.0 / frequency)]++;
    }

    /// <summary>The bucket a hold of this many ms counts in.</summary>
    internal static int BucketOf(double ms)
    {
        int bucket = 0;
        while (bucket < EdgesMs.Length && ms >= EdgesMs[bucket])
        {
            bucket++;
        }

        return bucket;
    }

    /// <summary>A copy of the counts, shortest bucket first (BucketCount of them).</summary>
    internal long[] Counts()
    {
        long[] copy = new long[_counts.Length];
        Array.Copy(_counts, copy, _counts.Length);
        return copy;
    }
}
