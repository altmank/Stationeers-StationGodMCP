#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// The game's whole frame time (Time.unscaledDeltaTime in ms) over the last WindowSeconds of real time, for server
/// info's runtime.game_frame_ms. A fixed ring of frame ms and the real time each was taken at: a frame older than the
/// window is left out of a reading, and when more frames arrive than the ring holds the oldest are overwritten. Add
/// writes two array slots, nothing allocated per frame; Read (only when server info asks) copies the frames inside the
/// window, sorts the copy and takes nearest-rank percentiles. One lock, in this class.
/// </summary>
internal sealed class GameFrameWindow
{
    /// <summary>How far back a reading looks, in real seconds.</summary>
    internal const double WindowSeconds = 60.0;

    /// <summary>The ring's size: 60 s at over 1000 fps.</summary>
    internal const int DefaultCapacity = 65536;

    private readonly object _gate = new object();
    private readonly float[] _ms;
    private readonly double[] _at;
    private int _next;
    private int _count;

    internal GameFrameWindow(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ms = new float[capacity];
        _at = new double[capacity];
    }

    internal int Capacity => _ms.Length;

    /// <summary>One frame: its ms and the real time (seconds) it was taken at. Negative or NaN counts as 0.</summary>
    internal void Add(double ms, double nowSeconds)
    {
        float counted = ms > 0.0 ? (float)ms : 0f;
        lock (_gate)
        {
            _ms[_next] = counted;
            _at[_next] = nowSeconds;
            _next = (_next + 1) % _ms.Length;
            _count = Math.Min(_count + 1, _ms.Length);
        }
    }

    /// <summary>The frames taken in the WindowSeconds up to nowSeconds, as ms statistics (nulls with none).</summary>
    internal GameFrameReading Read(double nowSeconds)
    {
        float[] inside;
        lock (_gate)
        {
            double oldest = nowSeconds - WindowSeconds;
            int first = (_next - _count + _ms.Length) % _ms.Length;
            int kept = 0;
            inside = new float[_count];
            for (int index = 0; index < _count; index++)
            {
                int slot = (first + index) % _ms.Length;
                if (_at[slot] >= oldest)
                {
                    inside[kept++] = _ms[slot];
                }
            }

            Array.Resize(ref inside, kept);
        }

        if (inside.Length == 0)
        {
            return new GameFrameReading(0, null, null, null, null, null);
        }

        Array.Sort(inside);
        double sum = 0.0;
        foreach (float value in inside)
        {
            sum += value;
        }

        return new GameFrameReading(inside.Length, sum / inside.Length, Rank(inside, 50), Rank(inside, 95),
            Rank(inside, 99), inside[inside.Length - 1]);
    }

    /// <summary>The value at nearest rank ceil(percent / 100 * n) of a sorted series.</summary>
    private static double Rank(float[] sorted, int percent)
    {
        int rank = Math.Max(1, (int)(((long)percent * sorted.Length + 99) / 100));
        return sorted[rank - 1];
    }
}

/// <summary>GameFrameWindow as read at one moment: frames in the window and their ms mean, p50, p95, p99 and max.</summary>
internal sealed class GameFrameReading
{
    internal GameFrameReading(int frames, double? mean, double? p50, double? p95, double? p99, double? max)
    {
        Frames = frames;
        Mean = mean;
        P50 = p50;
        P95 = p95;
        P99 = p99;
        Max = max;
    }

    internal int Frames { get; }

    internal double? Mean { get; }

    internal double? P50 { get; }

    internal double? P95 { get; }

    internal double? P99 { get; }

    internal double? Max { get; }
}
