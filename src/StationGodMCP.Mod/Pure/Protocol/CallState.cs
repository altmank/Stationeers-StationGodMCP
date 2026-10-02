#nullable enable

using System.Threading;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// A call's life, changed atomically so it is answered exactly once: queued, then either running (the main thread
/// took it) and later answered with its result, or answered at once without running (its deadline passed, it was
/// cancelled, or the server is stopping). Whoever wins the change from queued decides which.
/// </summary>
internal sealed class CallState
{
    private const int Queued = 0;
    private const int Running = 1;
    private const int Answered = 2;

    private int _state;

    internal bool IsQueued => Volatile.Read(ref _state) == Queued;

    internal bool IsAnswered => Volatile.Read(ref _state) == Answered;

    /// <summary>The main thread takes the call: true only if nothing answered it first.</summary>
    internal bool TryStart() => Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;

    /// <summary>The call ran and is answered with its result: true only for the run that started it.</summary>
    internal bool TryFinish() => Interlocked.CompareExchange(ref _state, Answered, Running) == Running;

    /// <summary>Answered without running (timeout, cancel, shutdown): true only while it is still queued.</summary>
    internal bool TryDrop() => Interlocked.CompareExchange(ref _state, Answered, Queued) == Queued;
}
