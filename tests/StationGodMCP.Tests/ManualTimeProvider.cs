#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StationGodMCP.Tests;

/// <summary>
/// A clock a test moves by hand: time passes only in Advance, and a timer (so a Task.Delay(span, this)) fires only when
/// Advance passes its due time. Minimal on purpose: one-shot timers, Change and Dispose, which is all Task.Delay needs.
/// One lock guards the time and the timers; callbacks run outside it, in due order, on the thread that calls Advance.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => new(new DateTime(GetTimestamp(), DateTimeKind.Utc));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
        {
            throw new NotSupportedException("ManualTimeProvider makes one-shot timers only.");
        }

        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing each timer that falls due, earliest first, with the clock at its due time.</summary>
    internal void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next == null)
                {
                    _ticks = target;
                    return;
                }

                _timers.Remove(next);
                next.Due = long.MaxValue;
                _ticks = Math.Max(_ticks, next.DueAtArm);
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        // Due and DueAtArm are read and written under the owner's lock.
        internal long Due { get; set; } = long.MaxValue;

        internal long DueAtArm { get; private set; }

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = long.MaxValue;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                DueAtArm = owner._ticks + dueTime.Ticks;
                Due = DueAtArm;
                owner._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = long.MaxValue;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
