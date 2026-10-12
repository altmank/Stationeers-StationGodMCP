#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// StationGod's request milliseconds per busy frame since the mod loaded, in fixed buckets, for mod_info's frame_ms
/// percentiles. The buckets end at EdgesMs, each edge belonging to the bucket below it (a 4.0 ms frame counts under
/// 4), the last one open. The edges are half a millisecond to a millisecond apart up to the 4 ms default budget and
/// its 2 ms job-held cap, coarser above, with 8, 16 and 33 ms (half a 60 Hz frame, a 60 Hz and a 30 Hz frame) among
/// them, so over_8ms is exact. pN is the upper edge of the bucket that holds the N-th percentile frame (nearest
/// rank); in the open bucket it is the longest frame seen. Plain increments into a fixed array, nothing allocated per
/// frame; the owner (DispatchStats) takes the lock.
/// </summary>
internal sealed class FrameHistogram
{
    /// <summary>The upper edges of every bucket but the last, in ms; each edge is in the bucket it ends.</summary>
    internal static readonly double[] EdgesMs =
        { 0.1, 0.25, 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0, 12.0, 16.0, 25.0, 33.0, 50.0, 100.0, 250.0 };

    /// <summary>over_8ms counts the frames above this, which are exactly the buckets past its edge.</summary>
    internal const double OverMs = 8.0;

    internal static int BucketCount => EdgesMs.Length + 1;

    private static readonly int FirstOver = Array.IndexOf(EdgesMs, OverMs) + 1;

    private readonly long[] _counts = new long[EdgesMs.Length + 1];
    private long _frames;
    private double _maxMs;

    /// <summary>One frame's request time. Negative or not a number counts as 0.</summary>
    internal void Add(double ms)
    {
        double counted = ms > 0.0 ? ms : 0.0;
        _counts[BucketOf(counted)]++;
        _frames++;
        _maxMs = Math.Max(_maxMs, counted);
    }

    /// <summary>The bucket a frame of this many ms counts in.</summary>
    internal static int BucketOf(double ms)
    {
        int bucket = 0;
        while (bucket < EdgesMs.Length && ms > EdgesMs[bucket])
        {
            bucket++;
        }

        return bucket;
    }

    internal FrameHistogramReading Read()
    {
        long over = 0;
        for (int bucket = FirstOver; bucket < _counts.Length; bucket++)
        {
            over += _counts[bucket];
        }

        return new FrameHistogramReading(_frames, Percentile(50), Percentile(90), Percentile(99), over);
    }

    /// <summary>
    /// The upper edge of the bucket holding the frame at nearest rank ceil(percent / 100 * frames), or the longest
    /// frame when that is the open bucket; null with no frames.
    /// </summary>
    internal double? Percentile(int percent)
    {
        if (_frames == 0)
        {
            return null;
        }

        long rank = Math.Max(1, (percent * _frames + 99) / 100);
        long seen = 0;
        for (int bucket = 0; bucket < EdgesMs.Length; bucket++)
        {
            seen += _counts[bucket];
            if (seen >= rank)
            {
                return EdgesMs[bucket];
            }
        }

        return _maxMs;
    }
}

/// <summary>FrameHistogram as read at one moment: busy frames, p50, p90 and p99 in ms (null with none), over_8ms.</summary>
internal sealed class FrameHistogramReading
{
    internal FrameHistogramReading(long frames, double? p50, double? p90, double? p99, long over8Ms)
    {
        Frames = frames;
        P50 = p50;
        P90 = p90;
        P99 = p99;
        Over8Ms = over8Ms;
    }

    internal long Frames { get; }

    internal double? P50 { get; }

    internal double? P90 { get; }

    internal double? P99 { get; }

    internal long Over8Ms { get; }
}
