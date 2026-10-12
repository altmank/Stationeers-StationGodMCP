using System.Collections.Concurrent;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// One signed-in connection: what its welcome said, the calls waiting for a reply on it, and its reader.
/// It knows nothing about resending; the client decides that when the connection reports that it ended.
/// </summary>
internal sealed class GameConnection
{
    private readonly LineChannel _channel;
    private readonly CancellationTokenSource _closing = new();
    private int _closed;

    internal GameConnection(GameTarget target, LineChannel channel, JsonElement welcome)
    {
        Target = target;
        _channel = channel;
        Welcome = welcome;
        HashSet<string> features = new(StringComparer.Ordinal);
        if (Wire.Child(welcome, "features") is { ValueKind: JsonValueKind.Array } listed)
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
        JsonElement? limits = Wire.Child(welcome, "limits");
        int inFlight = Limit(limits, "max_in_flight") ?? 16;
        Slots = new SemaphoreSlim(inFlight);
        PerStreamLimit = Limit(limits, "max_in_flight_per_stream") ?? inFlight;
        int perConnection = Limit(limits, "max_in_flight_per_connection") ?? 256;
        // Without streams every call is on the mod's stream 0, so its one-stream limit is the whole limit.
        PerConnectionLimit = features.Contains("streams") ? perConnection : Math.Min(perConnection, inFlight);
        Resumed = Wire.Child(welcome, "resume") is { } resume &&
                  Wire.Child(resume, "resumed") is { ValueKind: JsonValueKind.True };
        World = Wire.Child(welcome, "server") is { } server ? Wire.Child(server, "world") : null;
        CatalogueHash = Wire.Child(welcome, "catalogue") is { } catalogue ? Wire.Text(catalogue, "hash") : null;
    }

    internal GameTarget Target { get; }

    internal JsonElement Welcome { get; }

    internal IReadOnlySet<string> Features { get; }

    /// <summary>One slot per call in flight: welcome's max_in_flight.</summary>
    internal SemaphoreSlim Slots { get; }

    /// <summary>limits.max_in_flight_per_stream, else max_in_flight, else 16.</summary>
    internal int PerStreamLimit { get; }

    /// <summary>limits.max_in_flight_per_connection (256 when absent); max_in_flight when the mod has no streams.</summary>
    internal int PerConnectionLimit { get; }

    /// <summary>welcome.resume.resumed: the mod picked up the state it held for the resume token.</summary>
    internal bool Resumed { get; }

    /// <summary>welcome.server.world: {id, save, epoch}; null when the welcome has none.</summary>
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

    /// <summary>Writes one message and says whether it can have reached the server; anything but Written closes it.</summary>
    internal async Task<Sending> SendAsync(string line)
    {
        if (!Alive)
        {
            return Sending.NotWritten.Instance;
        }

        Sending sent = await _channel.TryWriteLineAsync(line, _closing.Token).ConfigureAwait(false);
        if (sent is not Sending.Written)
        {
            Close();
        }

        return sent;
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

    private static int? Limit(JsonElement? limits, string key) =>
        limits is { } given && Wire.Child(given, key) is { ValueKind: JsonValueKind.Number } limit &&
        limit.TryGetInt32(out int count) && count > 0
            ? count
            : null;

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

/// <summary>How writing one line ended: written whole, never started, or failed part way (it may have left).</summary>
internal abstract class Sending
{
    private Sending()
    {
    }

    internal sealed class Written : Sending
    {
        internal static readonly Written Instance = new();
    }

    internal sealed class NotWritten : Sending
    {
        internal static readonly NotWritten Instance = new();
    }

    internal sealed class Broken : Sending
    {
        internal static readonly Broken Instance = new();
    }
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
