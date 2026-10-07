#nullable enable

namespace StationGodMCP.Pure.Profiling;

/// <summary>
/// The slow-frame warning's rate limit: at most one warning per interval; the slow frames passed over meanwhile are
/// counted and named in the next warning.
/// </summary>
internal sealed class SlowFrameGate
{
    internal const double DefaultIntervalS = 10.0;

    private readonly double _intervalS;
    private double _lastS = double.NegativeInfinity;
    private int _suppressed;

    internal SlowFrameGate(double intervalS = DefaultIntervalS)
    {
        _intervalS = intervalS;
    }

    /// <summary>Slow frames not warned about since the last warning.</summary>
    internal int Suppressed => _suppressed;

    /// <summary>
    /// A slow frame at nowS: true when it may be warned about (suppressed is then how many were passed over before
    /// it), false when it is counted instead.
    /// </summary>
    internal bool Allow(double nowS, out int suppressed)
    {
        if (nowS - _lastS < _intervalS)
        {
            _suppressed++;
            suppressed = 0;
            return false;
        }

        suppressed = _suppressed;
        _suppressed = 0;
        _lastS = nowS;
        return true;
    }
}
