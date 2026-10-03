using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>How subscribing ended: a subscription, a server without subscriptions, or the call's own outcome.</summary>
public abstract record SubscribeOutcome
{
    private SubscribeOutcome()
    {
    }

    public sealed record Subscribed(Subscription Subscription) : SubscribeOutcome;

    /// <summary>Version 1, or 'subscriptions' not in welcome.features: poll read_devices with the same items.</summary>
    public sealed record Unsupported(string Message) : SubscribeOutcome;

    /// <summary>Refused (subscription_limit: poll instead) or no answer.</summary>
    public sealed record Failed(CallOutcome Outcome) : SubscribeOutcome;
}

/// <summary>
/// One device subscription (clients.md, Subscriptions in the library). The request is kept: after a reconnect into the
/// same world the client subscribes again with it and State becomes the new first reading; into another world, or when
/// the mod ends it, it closes and EndReason says why (closed, world_changed, limit, or a refusal on
/// resubscribing).
/// </summary>
public sealed class Subscription : IAsyncDisposable
{
    private readonly StationGodClient _client;
    private readonly object _gate = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Subscription(StationGodClient client, JsonElement request)
    {
        _client = client;
        Request = request;
    }

    /// <summary>The subscribe params, sent again after a reconnect into the same world.</summary>
    public JsonElement Request { get; }

    /// <summary>The server's id on the current connection; null while not subscribed.</summary>
    public string? ServerId { get; private set; }

    /// <summary>The last read_devices result received: the first reading, then each update's.</summary>
    public JsonElement? State { get; private set; }

    public long Seq { get; private set; }

    public long? Frame { get; private set; }

    public bool IsClosed => EndReason != null;

    public string? EndReason { get; private set; }

    /// <summary>Raised on the client's reader after each update, with the new state.</summary>
    public event Action<JsonElement>? Updated;

    /// <summary>Waits for the next update (or a fresh first reading); false on timeout or when the subscription closed.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellation = default)
    {
        Task next;
        lock (_gate)
        {
            if (IsClosed)
            {
                return false;
            }

            next = _next.Task;
        }

        Task finished = await Task.WhenAny(next, Task.Delay(timeout, cancellation)).ConfigureAwait(false);
        return finished == next && !IsClosed;
    }

    public ValueTask DisposeAsync() => new(_client.CloseAsync(this));

    internal void Bind(string serverId, JsonElement reply)
    {
        lock (_gate)
        {
            ServerId = serverId;
            State = Wire.Child(reply, "result");
            Frame = Wire.Child(reply, "frame") is { } frame && frame.TryGetInt64(out long value) ? value : null;
            Seq = 0;
        }

        Signal();
    }

    internal void Update(JsonElement message)
    {
        JsonElement? state;
        lock (_gate)
        {
            State = state = Wire.Child(message, "result");
            Seq = Wire.Child(message, "seq") is { } seq && seq.TryGetInt64(out long value) ? value : Seq + 1;
            Frame = Wire.Child(message, "frame") is { } frame && frame.TryGetInt64(out long at) ? at : Frame;
        }

        Signal();
        if (state is { } reading)
        {
            Updated?.Invoke(reading);
        }
    }

    internal void Detach()
    {
        lock (_gate)
        {
            ServerId = null;
        }
    }

    internal void End(string reason)
    {
        lock (_gate)
        {
            if (IsClosed)
            {
                return;
            }

            EndReason = reason;
            ServerId = null;
        }

        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource woken;
        lock (_gate)
        {
            woken = _next;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        woken.TrySetResult();
    }
}
