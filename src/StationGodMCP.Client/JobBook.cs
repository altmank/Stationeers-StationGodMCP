using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// The jobs topic's job_ended facts (protocol.md, Job events), for waits by job id. The last Capacity facts are kept,
/// so a wait that starts after its job ended still finds it. A wait answers null when its timeout passes, when the link
/// reconnects (events sent during the break are lost, so the waiter polls the job_id instead), or when the link closes.
/// </summary>
internal sealed class JobBook(int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _ended = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, List<TaskCompletionSource<JsonElement?>>> _waiters = new(StringComparer.Ordinal);
    private bool _closed;

    /// <summary>A job_ended event's job object; ignored when it names no job_id.</summary>
    internal void Ended(JsonElement job)
    {
        if (IdOf(job) is not { } id)
        {
            return;
        }

        List<TaskCompletionSource<JsonElement?>>? woken;
        lock (_gate)
        {
            if (!_ended.ContainsKey(id))
            {
                _order.Enqueue(id);
                if (_order.Count > capacity)
                {
                    _ended.Remove(_order.Dequeue());
                }
            }

            _ended[id] = job;
            _waiters.Remove(id, out woken);
        }

        if (woken != null)
        {
            foreach (TaskCompletionSource<JsonElement?> waiter in woken)
            {
                waiter.TrySetResult(job);
            }
        }
    }

    /// <summary>Answers every wait null now: the link reconnected and may have missed their events.</summary>
    internal void WakeAll() => Wake(close: false);

    /// <summary>Answers every wait null, and every later one for a job not already known.</summary>
    internal void Close() => Wake(close: true);

    /// <summary>The job's facts, or null (timeout, reconnect, closed). Throws when cancelled.</summary>
    internal async Task<JsonElement?> WaitAsync(string jobId, TimeSpan timeout, CancellationToken cancellation)
    {
        TaskCompletionSource<JsonElement?> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_ended.TryGetValue(jobId, out JsonElement known))
            {
                return known;
            }

            if (_closed)
            {
                return null;
            }

            if (!_waiters.TryGetValue(jobId, out List<TaskCompletionSource<JsonElement?>>? waiting))
            {
                waiting = [];
                _waiters[jobId] = waiting;
            }

            waiting.Add(waiter);
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Forget(jobId, waiter);
            return null;
        }
        catch (OperationCanceledException)
        {
            Forget(jobId, waiter);
            throw;
        }
    }

    private void Wake(bool close)
    {
        List<TaskCompletionSource<JsonElement?>> woken = [];
        lock (_gate)
        {
            _closed |= close;
            foreach (List<TaskCompletionSource<JsonElement?>> waiting in _waiters.Values)
            {
                woken.AddRange(waiting);
            }

            _waiters.Clear();
        }

        foreach (TaskCompletionSource<JsonElement?> waiter in woken)
        {
            waiter.TrySetResult(null);
        }
    }

    private void Forget(string jobId, TaskCompletionSource<JsonElement?> waiter)
    {
        lock (_gate)
        {
            if (_waiters.TryGetValue(jobId, out List<TaskCompletionSource<JsonElement?>>? waiting) &&
                waiting.Remove(waiter) && waiting.Count == 0)
            {
                _waiters.Remove(jobId);
            }
        }
    }

    private static string? IdOf(JsonElement job) =>
        Wire.Child(job, "job_id") is { } id
            ? id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText()
            : null;
}
