#nullable enable

using System;
using System.Diagnostics;

namespace StationGodMCP.Pure.Profiling;

/// <summary>
/// The runtime profiler's switch and the entry points the mod calls. Enabled is set on the main thread (the profiling
/// method, or [Performance] Profiling at load); every entry point reads it first and returns at once when it is
/// false, so profiling off costs one field read per scope and allocates nothing. While on, frames, scopes, calls, tick
/// holds and timed methods are recorded on the main thread only (MainThreadId): one reached on another thread records
/// nothing. Call times and reply sizes go to a locked table (MethodTimings) and may come from any thread. A failure
/// inside the profiler is caught here, logged once through WarningSink, and turns profiling off.
/// </summary>
internal static class Prof
{
    private static volatile bool _enabled;
    private static volatile ProfileRecorder? _recorder;

    /// <summary>Whether profiling records; read by every scope.</summary>
    internal static bool Enabled => _enabled;

    /// <summary>The session being recorded, or the last one after off (its report stays readable); null before the first.</summary>
    internal static ProfileRecorder? Recorder => _recorder;

    /// <summary>The managed id of the thread that runs StationGod's Update.</summary>
    internal static int MainThreadId { get; set; }

    /// <summary>Where slow-frame warnings and the profiler's own failure go (the mod's LogWarning).</summary>
    internal static Action<string>? WarningSink { get; set; }

    /// <summary>The internal failure that turned profiling off, until the next session starts.</summary>
    internal static string? Failure { get; private set; }

    internal static void Start(ProfileRecorder recorder)
    {
        Failure = null;
        _recorder = recorder;
        _enabled = true;
    }

    internal static void Stop() => _enabled = false;

    /// <summary>Drops the last session's report while profiling is off (reset after off).</summary>
    internal static void Forget()
    {
        if (!_enabled)
        {
            _recorder?.Csv?.Stop();
            _recorder = null;
            Failure = null;
        }
    }

    /// <summary>The profiler's own boundary: logged once, profiling off; the caller goes on as if it were never on.</summary>
    internal static void Fail(string where, Exception exception)
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        Failure = $"{where}: {exception.Message}";
        try
        {
            WarningSink?.Invoke($"Profiling stopped after an internal failure in {where}: {exception}");
        }
        catch (Exception)
        {
            // The log sink itself failing: profiling is already off, and nothing may reach the game loop.
        }
    }

    /// <summary>A timed scope; with profiling off, an empty struct that records nothing.</summary>
    internal static ProfScope Scope(ProfId id) => _enabled ? Enter(_recorder, (int)id) : default;

    /// <summary>The start of StationGod's Update; 0 when off (EndFrame then records nothing).</summary>
    internal static long BeginFrame()
    {
        if (!_enabled)
        {
            return 0;
        }

        try
        {
            return _recorder!.BeginFrame();
        }
        catch (Exception exception)
        {
            Fail("BeginFrame", exception);
            return 0;
        }
    }

    /// <summary>The end of StationGod's Update, given BeginFrame's value and Unity's frame number.</summary>
    internal static void EndFrame(long started, long frame)
    {
        if (started == 0 || !_enabled)
        {
            return;
        }

        try
        {
            _recorder!.EndFrame(started, frame);
        }
        catch (Exception exception)
        {
            Fail("EndFrame", exception);
        }
    }

    /// <summary>A timestamp to time a call with, or 0 when off.</summary>
    internal static long CallStarted() => _enabled ? _recorder!.Now() : 0;

    /// <summary>One call run on the main thread this frame, for the frame's call list.</summary>
    internal static void CallEnded(string? method, long started)
    {
        if (started == 0 || !_enabled)
        {
            return;
        }

        try
        {
            _recorder!.Call(method, started);
        }
        catch (Exception exception)
        {
            Fail("CallEnded", exception);
        }
    }

    /// <summary>A call's queue wait, execute and serialise times, counted per method since on or reset.</summary>
    internal static void CallTimes(string method, bool ok, double handlerMs, double serializeMs, double queueWaitMs)
    {
        if (_enabled)
        {
            _recorder?.CallTimes.Record(method, ok, handlerMs, serializeMs, queueWaitMs);
        }
    }

    /// <summary>A reply's size, from the thread that writes it.</summary>
    internal static void ReplyBytes(string method, long bytes)
    {
        if (_enabled)
        {
            _recorder?.CallTimes.RecordReply(method, bytes);
        }
    }

    /// <summary>A job asked the game to hold its tick; tool is null when it holds it again.</summary>
    internal static void TickHeld(string job, string? tool, long frame)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            _recorder!.TickHeld(job, tool, frame);
        }
        catch (Exception exception)
        {
            Fail("TickHeld", exception);
        }
    }

    /// <summary>The job let the tick go.</summary>
    internal static void TickReleased(long frame)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            _recorder!.TickReleased(frame);
        }
        catch (Exception exception)
        {
            Fail("TickReleased", exception);
        }
    }

    /// <summary>
    /// A [Profiled] method starts (the timing prefix): its timestamp, -1 when it runs inside itself (only the outermost
    /// run is timed), or 0 when nothing is recorded (off, or not the main thread).
    /// </summary>
    internal static long MethodEnter(int method)
    {
        if (!_enabled)
        {
            return 0;
        }

        try
        {
            return _recorder!.MethodEnter(method);
        }
        catch (Exception exception)
        {
            Fail("MethodEnter", exception);
            return 0;
        }
    }

    /// <summary>A [Profiled] method ended, by return or by exception (the timing finalizer), given MethodEnter's value.</summary>
    internal static void MethodExit(int method, long state)
    {
        ProfileRecorder? recorder = _recorder;
        if (state == 0 || recorder == null)
        {
            return;
        }

        try
        {
            recorder.MethodExit(method, state);
        }
        catch (Exception exception)
        {
            Fail("MethodExit", exception);
        }
    }

    private static ProfScope Enter(ProfileRecorder? recorder, int id)
    {
        if (recorder == null || Environment.CurrentManagedThreadId != MainThreadId)
        {
            return default;
        }

        try
        {
            long started = recorder.Enter(id);
            return started == 0 ? default : new ProfScope(recorder, id, started);
        }
        catch (Exception exception)
        {
            Fail("Scope", exception);
            return default;
        }
    }

    internal static void Exit(ProfileRecorder recorder, int id, long started)
    {
        try
        {
            recorder.Exit(id, started);
        }
        catch (Exception exception)
        {
            Fail("Scope", exception);
        }
    }
}

/// <summary>
/// One timed scope: `using (Prof.Scope(ProfId.RunFrame)) { ... }`. A readonly struct, so a scope allocates nothing;
/// default (profiling off) records nothing on Dispose. A scope that started while on always ends on the recorder it
/// started on, so its stack stays balanced if profiling is switched off inside it.
/// </summary>
internal readonly struct ProfScope : IDisposable
{
    private readonly ProfileRecorder? _recorder;
    private readonly int _id;
    private readonly long _started;

    internal ProfScope(ProfileRecorder recorder, int id, long started)
    {
        _recorder = recorder;
        _id = id;
        _started = started;
    }

    public void Dispose()
    {
        if (_recorder != null)
        {
            Prof.Exit(_recorder, _id, _started);
        }
    }
}

/// <summary>
/// A method the profiler times while it is on: an API handler or one of StationGod's own game patch methods. The mod
/// finds them once at load and wraps each with a timing prefix and finalizer only while profiling is on.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class ProfiledAttribute : Attribute
{
}

/// <summary>Where the recorder reads time and the heap; the system's in the game, a scripted one in tests.</summary>
internal class ProfileClock
{
    internal static readonly ProfileClock System = new ProfileClock();

    internal virtual long Frequency => Stopwatch.Frequency;

    internal virtual long Timestamp() => Stopwatch.GetTimestamp();

    /// <summary>The managed heap's size as the collector reports it (no collection forced).</summary>
    internal virtual long HeapBytes() => GC.GetTotalMemory(false);

    internal virtual DateTime UtcNow => DateTime.UtcNow;
}
