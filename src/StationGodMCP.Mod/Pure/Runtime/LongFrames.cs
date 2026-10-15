#nullable enable

using System;
using System.Globalization;
using System.Text;

namespace StationGodMCP.Pure;

/// <summary>
/// What identifies a long frame (review D, R-D-28), always on and cheap: each frame StationGod notes the time of its
/// Update's parts (Section) and the calls the scheduler ran in it (Call: method and ms; the first CallCapacity, the
/// rest counted), in fixed arrays. At the next frame's start the game's whole frame time is known (Unity's
/// unscaledDeltaTime); Check then gives a log line when that frame ran at least WholeFrameMs and TypicalTimes the
/// typical frame (a running average: a dedicated server's ordinary frame is about 50 ms), or StationGod's own Update at
/// least OwnMs: the frame, its whole ms, StationGod's ms by part, its calls and the GC collections since the frame
/// before, so a reader can tell StationGod's frame from the game's or a collection's. At most one line per
/// MinimumGapS. Main thread only; nothing is allocated unless a line is written.
/// </summary>
internal sealed class LongFrames
{
    internal const double WholeFrameMs = 50.0;
    internal const double TypicalTimes = 2.5;

    // The running average's weight of a new frame.
    private const double Weight = 0.02;
    internal const double OwnMs = 16.0;
    internal const double MinimumGapS = 10.0;
    internal const int CallCapacity = 16;

    private readonly string[] _sections;
    private readonly double[] _sectionMs;
    private readonly string[] _calls = new string[CallCapacity];
    private readonly double[] _callMs = new double[CallCapacity];
    private int _callCount;
    private int _callsOver;
    private long _frame = -1;
    private double _updateMs;
    private int _gcBefore;
    private int _gcAfter;
    private double _lastLoggedS = double.NegativeInfinity;
    private double _typicalMs = double.NaN;

    /// <summary>The mod's notes, which the scheduler tells each call it runs; null (nothing noted) in tests.</summary>
    internal static LongFrames? Main { get; set; }

    /// <summary>The parts of an Update, in order (their names in the log line).</summary>
    internal LongFrames(params string[] sections)
    {
        _sections = sections;
        _sectionMs = new double[sections.Length];
    }

    /// <summary>A new frame's notes begin: the GC collections now.</summary>
    internal void Begin(long frame, int gcCollections)
    {
        _frame = frame;
        _gcBefore = gcCollections;
        _callCount = 0;
        _callsOver = 0;
        _updateMs = 0.0;
        Array.Clear(_sectionMs, 0, _sectionMs.Length);
    }

    /// <summary>The Update part at index took ms.</summary>
    internal void Section(int index, double ms) => _sectionMs[index] += ms;

    /// <summary>The scheduler ran a call of method for ms.</summary>
    internal void Call(string method, double ms)
    {
        if (_callCount < CallCapacity)
        {
            _calls[_callCount] = method;
            _callMs[_callCount++] = ms;
            return;
        }

        _callsOver++;
    }

    /// <summary>The frame's Update ended: its whole ms, and the GC collections now.</summary>
    internal void End(double updateMs, int gcCollections)
    {
        _updateMs = updateMs;
        _gcAfter = gcCollections;
    }

    /// <summary>
    /// The noted frame's log line when the game's whole frame (wholeMs, read at the next frame's start) or StationGod's
    /// Update in it was long, at most one per MinimumGapS of nowS; null otherwise.
    /// </summary>
    internal string? Check(double wholeMs, int gcNow, double nowS)
    {
        bool longWhole = wholeMs >= WholeFrameMs && !double.IsNaN(_typicalMs) && wholeMs >= TypicalTimes * _typicalMs;
        // A long frame moves the average only as far as twice it, so one stall does not hide the next.
        _typicalMs = double.IsNaN(_typicalMs)
            ? wholeMs
            : _typicalMs + Weight * (Math.Min(wholeMs, 2.0 * _typicalMs) - _typicalMs);
        if (_frame < 0 || (!longWhole && _updateMs < OwnMs) || nowS - _lastLoggedS < MinimumGapS)
        {
            return null;
        }

        _lastLoggedS = nowS;
        StringBuilder line = new StringBuilder("Long frame ").Append(_frame).Append(": ")
            .Append(Ms(wholeMs)).Append(" ms in all, StationGod ").Append(Ms(_updateMs)).Append(" ms (");
        bool first = true;
        for (int index = 0; index < _sections.Length; index++)
        {
            if (_sectionMs[index] < 0.05)
            {
                continue;
            }

            line.Append(first ? string.Empty : ", ").Append(_sections[index]).Append(' ').Append(Ms(_sectionMs[index]));
            first = false;
        }

        line.Append(first ? "nothing over 0.05 ms" : string.Empty).Append("); calls: ");
        if (_callCount == 0)
        {
            line.Append("none");
        }

        for (int index = 0; index < _callCount; index++)
        {
            line.Append(index == 0 ? string.Empty : ", ").Append(_calls[index]).Append(' ').Append(Ms(_callMs[index]));
        }

        if (_callsOver > 0)
        {
            line.Append(" and ").Append(_callsOver).Append(" more");
        }

        int gc = gcNow - _gcBefore;
        line.Append("; GC collections in or after it: ").Append(gc);
        if (gc > 0 && _gcAfter == _gcBefore)
        {
            line.Append(" (after StationGod's Update)");
        }

        return line.Append('.').ToString();
    }

    private static string Ms(double ms) => ms.ToString("F1", CultureInfo.InvariantCulture);
}
