#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Profiling;

/// <summary>
/// The game-tick holds of jobs while profiling is on: one entry per stretch from a job asking for the hold to letting
/// the tick go (a job that lets it go and holds it again has several), the last Kept of them, plus count, total and
/// longest. A hold that started before profiling went on is not counted. Main thread only.
/// </summary>
internal sealed class TickHolds
{
    internal const int Kept = 16;

    private readonly TickHold?[] _recent = new TickHold?[Kept];
    private int _next;
    private long _count;
    private double _totalMs;
    private double _maxMs;

    private string? _job;
    private string? _tool;
    private long _startedTicks;
    private long _startedFrame;

    /// <summary>A hold starts; a null tool keeps the tool of the job's earlier hold.</summary>
    internal void Held(string job, string? tool, long ticks, long frame)
    {
        if (tool == null && _tool != null && job == _job)
        {
            tool = _tool;
        }

        _job = job;
        _tool = tool;
        _startedTicks = ticks;
        _startedFrame = frame;
    }

    /// <summary>The tick is let go: the open hold, if any, is recorded.</summary>
    internal void Released(long ticks, long frame, long frequency)
    {
        if (_startedTicks == 0 || _job == null)
        {
            return;
        }

        double ms = (ticks - _startedTicks) * 1000.0 / frequency;
        _recent[_next] = new TickHold(_job, _tool, ms, frame - _startedFrame, false);
        _next = (_next + 1) % Kept;
        _count++;
        _totalMs += ms;
        if (ms > _maxMs)
        {
            _maxMs = ms;
        }

        _startedTicks = 0;
    }

    /// <summary>Forgets the recorded holds (reset); a hold under way stays open and is recorded when it ends.</summary>
    internal void Clear()
    {
        Array.Clear(_recent, 0, Kept);
        _next = 0;
        _count = 0;
        _totalMs = 0.0;
        _maxMs = 0.0;
    }

    internal TickHoldSummary Summary(long ticks, long frame, long frequency)
    {
        List<TickHold> recent = new List<TickHold>(Kept + 1);
        if (_startedTicks != 0 && _job != null)
        {
            recent.Add(new TickHold(_job, _tool, (ticks - _startedTicks) * 1000.0 / frequency, frame - _startedFrame, true));
        }

        for (int back = 1; back <= Kept; back++)
        {
            TickHold? hold = _recent[(_next - back + Kept) % Kept];
            if (hold != null)
            {
                recent.Add(hold);
            }
        }

        return new TickHoldSummary(_count, _totalMs, _maxMs, recent);
    }
}
