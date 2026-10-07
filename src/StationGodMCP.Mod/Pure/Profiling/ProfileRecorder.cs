#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationGodMCP.Pure.Profiling;

/// <summary>The slow-frame threshold's one declaration of limits: the profiling method and the config use it.</summary>
internal static class SlowFrameLimits
{
    internal const double DefaultMs = 8.0;
    internal const double MinimumMs = 1.0;
    internal const double MaximumMs = 1000.0;

    internal static bool Allowed(double ms) => ms >= MinimumMs && ms <= MaximumMs;
}

/// <summary>
/// One profiling session. Per frame (BeginFrame to EndFrame: StationGod's whole Update) it adds up every scope's
/// inclusive and self time on a stack of open scopes, every [Profiled] method's time, the calls the frame ran and the
/// heap's growth; EndFrame moves the frame into a ring of the last WindowFrames frames and into totals since on or
/// reset, keeps the costliest frame with its breakdown, warns about slow frames (rate-limited) and hands rows to the
/// CSV. Timed methods that run between two Updates (game patches) count in the next frame. Every array is allocated
/// here, once; recording allocates nothing except when a frame is the new worst, is warned about, or a tick hold
/// ends. Main thread only (Prof checks the thread), except CallTimes, which locks.
/// </summary>
internal sealed class ProfileRecorder
{
    internal const int WindowFrames = 600;
    internal const int MaxDepth = 32;
    internal const int CallsPerFrame = 32;

    private const long HeapDropped = long.MinValue;
    private const int TopCount = 3;

    private readonly ProfileClock _clock;
    private readonly long _frequency;
    private readonly int _slots;
    private readonly string[] _names;

    private readonly long[] _frameTicks;
    private readonly long[] _frameSelf;
    private readonly int[] _frameRuns;
    private readonly int[] _stackIds = new int[MaxDepth];
    private readonly long[] _stackChildren = new long[MaxDepth];
    private readonly int[] _methodDepth;
    private readonly string?[] _callNames = new string?[CallsPerFrame];
    private readonly double[] _callMs = new double[CallsPerFrame];
    private int _depth;
    private int _calls;
    private int _callsOver;
    private long _heapAtBegin;
    private bool _frameOpen;

    private readonly double[] _ringMs = new double[WindowFrames];
    private readonly double[] _ringUnscoped = new double[WindowFrames];
    private readonly long[] _ringHeap = new long[WindowFrames];
    private readonly double[] _ringSlotMs;
    private long _recorded;

    private readonly long[] _totalTicks;
    private readonly long[] _totalSelf;
    private readonly long[] _totalRuns;
    private long _unscopedTicks;
    private long _resetAt;
    private WorstFrame? _worst;
    private double _worstMs = -1.0;
    private readonly TickHolds _holds = new TickHolds();
    private readonly SlowFrameGate _gate;
    private long _slowFrames;

    private readonly long[] _secondSelf = new long[ProfIds.Count + 1];
    private long _lastFrame;
    private long _secondBegan;
    private int _secondFrames;
    private double _secondMs;
    private double _secondMaxMs;
    private long _secondHeap;
    private int _secondCalls;
    private string? _secondCall;
    private double _secondCallMs;

    internal ProfileRecorder(double slowFrameMs, IReadOnlyList<string> methods, ProfileClock? clock = null,
        SlowFrameGate? gate = null)
    {
        _clock = clock ?? ProfileClock.System;
        _frequency = _clock.Frequency;
        _gate = gate ?? new SlowFrameGate();
        SlowFrameMs = slowFrameMs;
        _slots = ProfIds.Count + methods.Count;
        _names = new string[_slots];
        for (int id = 0; id < ProfIds.Count; id++)
        {
            _names[id] = ProfIds.Name((ProfId)id);
        }

        for (int method = 0; method < methods.Count; method++)
        {
            _names[ProfIds.Count + method] = methods[method];
        }

        _frameTicks = new long[_slots];
        _frameSelf = new long[_slots];
        _frameRuns = new int[_slots];
        _methodDepth = new int[methods.Count];
        _ringSlotMs = new double[WindowFrames * _slots];
        _totalTicks = new long[_slots];
        _totalSelf = new long[_slots];
        _totalRuns = new long[_slots];
        CallTimes = new MethodTimings();
        _resetAt = _clock.Timestamp();
        _secondBegan = _resetAt;
    }

    /// <summary>Per call method since on or reset: queue wait, execute, serialise and reply size. Thread-safe.</summary>
    internal MethodTimings CallTimes { get; private set; }

    internal double SlowFrameMs { get; set; }

    /// <summary>The rolling CSV, when one is being written.</summary>
    internal ProfileCsvWriter? Csv { get; set; }

    internal long FramesRecorded => _recorded;

    internal long Now() => _clock.Timestamp();

    /// <summary>Clears the window, totals, worst frame, call times and recorded holds; open scopes and holds go on.</summary>
    internal void Reset()
    {
        Array.Clear(_ringMs, 0, _ringMs.Length);
        Array.Clear(_ringUnscoped, 0, _ringUnscoped.Length);
        Array.Clear(_ringHeap, 0, _ringHeap.Length);
        Array.Clear(_ringSlotMs, 0, _ringSlotMs.Length);
        Array.Clear(_totalTicks, 0, _slots);
        Array.Clear(_totalSelf, 0, _slots);
        Array.Clear(_totalRuns, 0, _slots);
        _recorded = 0;
        _unscopedTicks = 0;
        _worst = null;
        _worstMs = -1.0;
        _slowFrames = 0;
        _holds.Clear();
        CallTimes = new MethodTimings();
        _resetAt = _clock.Timestamp();
    }

    internal long BeginFrame()
    {
        _frameOpen = true;
        _heapAtBegin = _clock.HeapBytes();
        return _clock.Timestamp();
    }

    /// <summary>A scope opens: its start, or 0 when the stack is full (that scope is not recorded).</summary>
    internal long Enter(int id)
    {
        if (_depth == MaxDepth)
        {
            return 0;
        }

        _stackIds[_depth] = id;
        _stackChildren[_depth] = 0;
        _depth++;
        return _clock.Timestamp();
    }

    internal void Exit(int id, long started)
    {
        long elapsed = _clock.Timestamp() - started;
        if (_depth == 0 || _stackIds[_depth - 1] != id)
        {
            throw new InvalidOperationException($"Scope {_names[id]} ended out of order.");
        }

        _depth--;
        long self = elapsed - _stackChildren[_depth];
        if (_depth > 0)
        {
            _stackChildren[_depth - 1] += elapsed;
        }

        _frameTicks[id] += elapsed;
        _frameSelf[id] += self;
        _frameRuns[id]++;
    }

    internal long MethodEnter(int method)
    {
        if (Environment.CurrentManagedThreadId != Prof.MainThreadId)
        {
            return 0;
        }

        return _methodDepth[method]++ > 0 ? -1 : _clock.Timestamp();
    }

    internal void MethodExit(int method, long state)
    {
        if (_methodDepth[method] > 0)
        {
            _methodDepth[method]--;
        }

        if (state < 0)
        {
            return;
        }

        long elapsed = _clock.Timestamp() - state;
        int slot = ProfIds.Count + method;
        _frameTicks[slot] += elapsed;
        _frameSelf[slot] += elapsed;
        _frameRuns[slot]++;
    }

    internal void Call(string? method, long started)
    {
        double ms = Milliseconds(_clock.Timestamp() - started);
        if (_calls < CallsPerFrame)
        {
            _callNames[_calls] = method ?? "unknown";
            _callMs[_calls] = ms;
            _calls++;
        }
        else
        {
            _callsOver++;
        }
    }

    internal void TickHeld(string job, string? tool, long frame) => _holds.Held(job, tool, _clock.Timestamp(), frame);

    internal void TickReleased(long frame) => _holds.Released(_clock.Timestamp(), frame, _frequency);

    /// <summary>
    /// The frame BeginFrame opened ends. A frame this recorder did not begin (profiling went on during it) is not
    /// recorded; what it timed counts in the next frame.
    /// </summary>
    internal void EndFrame(long started, long frame)
    {
        if (!_frameOpen)
        {
            return;
        }

        _frameOpen = false;
        long now = _clock.Timestamp();
        long ticks = now - started;
        double ms = Milliseconds(ticks);
        long delta = _clock.HeapBytes() - _heapAtBegin;
        int at = (int)(_recorded % WindowFrames);
        int row = at * _slots;
        long scoped = 0;
        for (int slot = 0; slot < _slots; slot++)
        {
            _ringSlotMs[row + slot] = Milliseconds(_frameTicks[slot]);
            _totalTicks[slot] += _frameTicks[slot];
            _totalSelf[slot] += _frameSelf[slot];
            _totalRuns[slot] += _frameRuns[slot];
            if (slot < ProfIds.Count)
            {
                scoped += _frameSelf[slot];
                _secondSelf[slot] += _frameSelf[slot];
            }
        }

        long unscoped = Math.Max(0, ticks - scoped);
        _secondSelf[ProfIds.Count] += unscoped;
        _unscopedTicks += unscoped;
        _ringMs[at] = ms;
        _ringUnscoped[at] = Milliseconds(unscoped);
        _ringHeap[at] = delta >= 0 ? delta : HeapDropped;
        _recorded++;
        int slowestCall = SlowestCall();
        if (ms > _worstMs)
        {
            _worstMs = ms;
            _worst = Worst(frame, ms, delta, unscoped);
        }

        if (ms > SlowFrameMs)
        {
            _slowFrames++;
            Slow(frame, ms, delta, unscoped, slowestCall, now);
        }

        Second(frame, ms, delta, slowestCall, now);
        Array.Clear(_frameTicks, 0, _slots);
        Array.Clear(_frameSelf, 0, _slots);
        Array.Clear(_frameRuns, 0, _slots);
        Array.Clear(_callNames, 0, _calls);
        _calls = 0;
        _callsOver = 0;
    }

    /// <summary>The session's report as it stands; enabled is whether profiling is on.</summary>
    internal ProfileSnapshot Report(bool enabled)
    {
        long now = _clock.Timestamp();
        int window = (int)Math.Min(_recorded, WindowFrames);
        double[] scratch = new double[Math.Max(window, 1)];
        Array.Copy(_ringMs, scratch, window);
        Spread frameMs = Spread.Of(scratch, window);
        int heapKept = 0;
        for (int frame = 0; frame < window; frame++)
        {
            if (_ringHeap[frame] != HeapDropped)
            {
                scratch[heapKept++] = _ringHeap[frame];
            }
        }

        Spread heap = Spread.Of(scratch, heapKept);
        List<SlotStat> slots = new List<SlotStat>();
        for (int slot = 0; slot < _slots; slot++)
        {
            if (_totalRuns[slot] == 0)
            {
                continue;
            }

            int ran = 0;
            for (int frame = 0; frame < window; frame++)
            {
                double ms = _ringSlotMs[frame * _slots + slot];
                if (ms > 0.0)
                {
                    scratch[ran++] = ms;
                }
            }

            slots.Add(new SlotStat(_names[slot], slot < ProfIds.Count ? "scope" : "method",
                Milliseconds(_totalTicks[slot]), Milliseconds(_totalSelf[slot]), _totalRuns[slot], Spread.Of(scratch, ran)));
        }

        if (_recorded > 0)
        {
            Array.Copy(_ringUnscoped, scratch, window);
            slots.Add(new SlotStat(ProfIds.Unscoped, "scope", Milliseconds(_unscopedTicks), Milliseconds(_unscopedTicks),
                _recorded, Spread.Of(scratch, window)));
        }

        slots.Sort(static (a, b) =>
        {
            int bySelf = b.SelfMs.CompareTo(a.SelfMs);
            return bySelf != 0 ? bySelf : string.CompareOrdinal(a.Name, b.Name);
        });
        return new ProfileSnapshot(enabled, Milliseconds(now - _resetAt) / 1000.0, SlowFrameMs, _recorded, window,
            frameMs, heap, window - heapKept, slots, _worst, CallTimes.Called(), _holds.Summary(now, _lastFrame, _frequency),
            _slowFrames, Csv?.State());
    }

    private double Milliseconds(long ticks) => ticks * 1000.0 / _frequency;

    private int SlowestCall()
    {
        int slowest = -1;
        for (int call = 0; call < _calls; call++)
        {
            if (slowest < 0 || _callMs[call] > _callMs[slowest])
            {
                slowest = call;
            }
        }

        return slowest;
    }

    // The frame's self ticks of scope id, or of the unscoped rest for id ProfIds.Count.
    private long SelfOf(int id, long unscoped) => id == ProfIds.Count ? unscoped : _frameSelf[id];

    private string NameOf(int id) => id == ProfIds.Count ? ProfIds.Unscoped : _names[id];

    // The three scopes (unscoped included) with the most self time in values, most first; -1 where fewer ran.
    private void Top(long[] values, long unscoped, bool frame, out int first, out int second, out int third)
    {
        first = second = third = -1;
        for (int id = 0; id <= ProfIds.Count; id++)
        {
            long value = frame ? SelfOf(id, unscoped) : values[id];
            if (value <= 0)
            {
                continue;
            }

            if (first < 0 || value > Value(first))
            {
                third = second;
                second = first;
                first = id;
            }
            else if (second < 0 || value > Value(second))
            {
                third = second;
                second = id;
            }
            else if (third < 0 || value > Value(third))
            {
                third = id;
            }
        }

        long Value(int id) => frame ? SelfOf(id, unscoped) : values[id];
    }

    private NamedSlot FrameSlot(int id, long unscoped) =>
        id < 0 ? default : new NamedSlot(NameOf(id), Milliseconds(SelfOf(id, unscoped)));

    private WorstFrame Worst(long frame, double ms, long delta, long unscoped)
    {
        List<NamedMs> scopes = new List<NamedMs>();
        for (int id = 0; id <= ProfIds.Count; id++)
        {
            long self = SelfOf(id, unscoped);
            if (self > 0)
            {
                scopes.Add(new NamedMs(NameOf(id), Milliseconds(self)));
            }
        }

        for (int slot = ProfIds.Count; slot < _slots; slot++)
        {
            if (_frameRuns[slot] > 0)
            {
                scopes.Add(new NamedMs(_names[slot], Milliseconds(_frameTicks[slot])));
            }
        }

        scopes.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));
        List<NamedMs> calls = new List<NamedMs>(_calls);
        for (int call = 0; call < _calls; call++)
        {
            calls.Add(new NamedMs(_callNames[call]!, _callMs[call]));
        }

        calls.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));

        return new WorstFrame(frame, ms, delta >= 0 ? delta : null, scopes, calls, _callsOver);
    }

    private void Slow(long frame, double ms, long delta, long unscoped, int slowestCall, long now)
    {
        Top(_frameSelf, unscoped, true, out int first, out int second, out int third);
        Csv?.TryEnqueue(new ProfileRow(ProfileRowKind.Frame, _clock.UtcNow.Ticks, frame, 1, ms, ms,
            delta >= 0 ? delta : null, _calls + _callsOver, FrameSlot(first, unscoped), FrameSlot(second, unscoped),
            FrameSlot(third, unscoped), slowestCall < 0 ? default : new NamedSlot(_callNames[slowestCall], _callMs[slowestCall])));
        if (!_gate.Allow(Milliseconds(now) / 1000.0, out int suppressed))
        {
            return;
        }

        StringBuilder message = new StringBuilder(256);
        message.Append("Slow frame ").Append(frame.ToString(CultureInfo.InvariantCulture)).Append(": StationGod took ")
            .Append(Rounded(ms)).Append(" ms on the main thread (slow_frame_ms ").Append(Rounded(SlowFrameMs))
            .Append("); top:");
        AppendTop(message, first, unscoped, true);
        AppendTop(message, second, unscoped, false);
        AppendTop(message, third, unscoped, false);
        if (_calls > 0)
        {
            message.Append("; calls:");
            for (int call = 0; call < _calls && call < TopCount; call++)
            {
                message.Append(call == 0 ? " " : ", ").Append(_callNames[call]).Append(' ').Append(Rounded(_callMs[call]))
                    .Append(" ms");
            }

            int more = _calls + _callsOver - TopCount;
            if (more > 0)
            {
                message.Append(" and ").Append(more.ToString(CultureInfo.InvariantCulture)).Append(" more");
            }
        }

        if (suppressed > 0)
        {
            message.Append("; ").Append(suppressed.ToString(CultureInfo.InvariantCulture))
                .Append(" slow frame(s) not logged since the last warning");
        }

        Prof.WarningSink?.Invoke(message.Append('.').ToString());
    }

    private void AppendTop(StringBuilder message, int id, long unscoped, bool first)
    {
        if (id < 0)
        {
            return;
        }

        message.Append(first ? " " : ", ").Append(NameOf(id)).Append(' ').Append(Rounded(Milliseconds(SelfOf(id, unscoped))))
            .Append(" ms");
    }

    private static string Rounded(double ms) => ms.ToString("0.00", CultureInfo.InvariantCulture);

    // The CSV's once-a-second summary: frames, StationGod's total and longest frame, heap growth, calls, the three
    // scopes with the most self time and the slowest call of the second.
    private void Second(long frame, double ms, long delta, int slowestCall, long now)
    {
        _lastFrame = frame;
        _secondFrames++;
        _secondMs += ms;
        _secondMaxMs = Math.Max(_secondMaxMs, ms);
        _secondHeap += Math.Max(0, delta);
        _secondCalls += _calls + _callsOver;
        if (slowestCall >= 0 && (_secondCall == null || _callMs[slowestCall] > _secondCallMs))
        {
            _secondCall = _callNames[slowestCall];
            _secondCallMs = _callMs[slowestCall];
        }

        if (now - _secondBegan < _frequency)
        {
            return;
        }

        if (Csv != null)
        {
            Top(_secondSelf, 0, false, out int first, out int second, out int third);
            Csv.TryEnqueue(new ProfileRow(ProfileRowKind.Second, _clock.UtcNow.Ticks, frame, _secondFrames, _secondMs,
                _secondMaxMs, _secondHeap, _secondCalls, SecondSlot(first), SecondSlot(second), SecondSlot(third),
                _secondCall == null ? default : new NamedSlot(_secondCall, _secondCallMs)));
        }

        Array.Clear(_secondSelf, 0, _secondSelf.Length);
        _secondBegan = now;
        _secondFrames = 0;
        _secondMs = 0.0;
        _secondMaxMs = 0.0;
        _secondHeap = 0;
        _secondCalls = 0;
        _secondCall = null;
        _secondCallMs = 0.0;
    }

    private NamedSlot SecondSlot(int id) => id < 0 ? default : new NamedSlot(NameOf(id), Milliseconds(_secondSelf[id]));
}
