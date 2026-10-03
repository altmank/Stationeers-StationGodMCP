using System.Collections.Concurrent;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>The protocol a connection speaks: version 2, or today's version 1 with one call at a time.</summary>
public enum ProtocolVersion
{
    Version1 = 1,
    Version2 = 2
}

/// <summary>
/// One signed-in connection: its protocol, what its welcome said, the calls waiting for a reply on it, and its reader.
/// It knows nothing about resending; the client decides that when the connection reports that it ended.
/// </summary>
internal sealed class GameConnection
{
    private readonly LineChannel _channel;
    private readonly CancellationTokenSource _closing = new();
    private int _closed;

    internal GameConnection(GameTarget target, LineChannel channel, ProtocolVersion version, JsonElement? welcome)
    {
        Target = target;
        _channel = channel;
        Version = version;
        Welcome = welcome;
        HashSet<string> features = new(StringComparer.Ordinal);
        if (welcome is { } message && Wire.Child(message, "features") is { ValueKind: JsonValueKind.Array } listed)
        {
            foreach (JsonElement feature in listed.EnumerateArray())
            {
                if (feature.ValueKind == JsonValueKind.String)
                {
                    features.Add(feature.GetString()!);
                }
            }
        }

        Features = features;
        int inFlight = welcome is { } hello && Wire.Child(hello, "limits") is { } limits &&
                       Wire.Child(limits, "max_in_flight") is { ValueKind: JsonValueKind.Number } limit &&
                       limit.TryGetInt32(out int count) && count > 0
            ? count
            : 16;
        Slots = new SemaphoreSlim(version == ProtocolVersion.Version2 ? inFlight : 1);
        World = welcome is { } greeting && Wire.Child(greeting, "server") is { } server ? Wire.Child(server, "world") : null;
        CatalogueHash = welcome is { } greeted && Wire.Child(greeted, "catalogue") is { } catalogue
            ? Wire.Text(catalogue, "hash")
            : null;
    }

    internal GameTarget Target { get; }

    internal ProtocolVersion Version { get; }

    internal JsonElement? Welcome { get; }

    internal IReadOnlySet<string> Features { get; }

    /// <summary>One slot per call in flight: max_in_flight on version 2, one on version 1.</summary>
    internal SemaphoreSlim Slots { get; }

    /// <summary>welcome.server.world: {id, save, epoch}; null on version 1.</summary>
    internal JsonElement? World { get; }

    internal string? WorldId => World is { } world ? Wire.Text(world, "id") : null;

    internal string? CatalogueHash { get; }

    internal ConcurrentDictionary<string, PendingCall> Pending { get; } = new(StringComparer.Ordinal);

    internal bool Alive => Volatile.Read(ref _closed) == 0;

    /// <summary>The reason of the server's goodbye, when it said one before closing.</summary>
    internal string? Goodbye { get; set; }

    /// <summary>Writes one message; false when the connection is broken, which also closes it.</summary>
    internal async Task<bool> TrySendAsync(string line)
    {
        if (!Alive)
        {
            return false;
        }

        try
        {
            await _channel.WriteLineAsync(line, _closing.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Close();
            return false;
        }
    }

    /// <summary>
    /// Reads until the connection ends, handing every message to onMessage on this task; then closes it, ends every
    /// call still waiting with CallEnd.Broken and calls onClosed once.
    /// </summary>
    internal async Task ReadAsync(Action<GameConnection, JsonElement> onMessage, Action<GameConnection> onClosed)
    {
        try
        {
            while (true)
            {
                byte[]? line = await _channel.ReadLineAsync(_closing.Token).ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                if (Wire.Parse(line) is { } message)
                {
                    onMessage(this, message);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection ended under the read.
        }
        finally
        {
            Close();
            foreach (PendingCall call in Pending.Values)
            {
                call.End(new CallEnd.Broken());
            }

            onClosed(this);
        }
    }

    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        _closing.Cancel();
        _ = _channel.DisposeAsync().AsTask();
    }
}

/// <summary>A call written or about to be written on one connection, and how it ended there.</summary>
internal sealed class PendingCall(string id, Action<GameConnection, JsonElement>? onReply)
{
    private readonly TaskCompletionSource<CallEnd> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string Id { get; } = id;

    internal Task<CallEnd> Ended => _end.Task;

    /// <summary>Runs on the reader before the caller wakes, so a subscription is bound before its first update.</summary>
    internal void Reply(GameConnection connection, JsonElement message)
    {
        onReply?.Invoke(connection, message);
        _end.TrySetResult(new CallEnd.Reply(message));
    }

    internal void End(CallEnd end) => _end.TrySetResult(end);
}

/// <summary>How a written call ended on its connection: a reply, or the connection broke first.</summary>
internal abstract record CallEnd
{
    private CallEnd()
    {
    }

    internal sealed record Reply(JsonElement Message) : CallEnd;

    internal sealed record Broken : CallEnd;
}
