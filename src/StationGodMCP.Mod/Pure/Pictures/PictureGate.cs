#nullable enable

using System;

namespace StationGodMCP.Pure.Pictures;

/// <summary>
/// One picture at a time (show picture): a call while another is being taken is refused busy rather than queued, since
/// each holds a render texture and a frame's readback, and a script waiting on its reply simply calls again. The gate
/// is taken when a picture's work starts and given back once, when it ends or fails. A call the scheduler drops (its
/// deadline passed) never ends, so a holder older than StaleAfterSeconds is disposed (its render texture freed) and the
/// gate passes to the new call. Main thread only.
/// </summary>
internal sealed class PictureGate
{
    internal const string Busy = "busy";
    internal const double StaleAfterSeconds = 30;

    private object? _holder;
    private double _takenAt;

    internal static PictureGate Main { get; } = new PictureGate();

    internal bool Taken => _holder != null;

    /// <summary>Takes the gate for holder at nowSeconds; false when another picture holds it and is not stale.</summary>
    internal bool TryTake(object holder, double nowSeconds)
    {
        if (_holder != null)
        {
            if (nowSeconds - _takenAt < StaleAfterSeconds)
            {
                return false;
            }

            object stale = _holder;
            _holder = null;
            (stale as IDisposable)?.Dispose();
        }

        _holder = holder;
        _takenAt = nowSeconds;
        return true;
    }

    /// <summary>Gives the gate back if holder holds it; a second release, or another's, does nothing.</summary>
    internal void Release(object holder)
    {
        if (ReferenceEquals(_holder, holder))
        {
            _holder = null;
        }
    }
}
