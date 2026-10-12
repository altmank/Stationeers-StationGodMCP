using System.Text.Json;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// IGameLink over StationGodClient: stream 0 only, the rename envelope sent where the mod lists the feature. Streams,
/// holds, stream costs and job waits are not served (NotSupportedException).
/// </summary>
internal sealed class ClientGameLink : IGameLink, IAsyncDisposable
{
    private readonly StationGodClient _client;

    internal ClientGameLink(StationGodClient client)
    {
        _client = client;
        _client.WorldChanged += OnWorldChanged;
    }

    public string? WorldId => _client.World is { ValueKind: JsonValueKind.Object } world &&
                              world.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
        ? id.GetString()
        : null;

    public event Action<string?>? WorldChanged;

    public long NewStream() => throw new NotSupportedException("Streams come with the hub's GameLink.");

    public async Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
    {
        if (call.Stream != 0)
        {
            throw new NotSupportedException("ClientGameLink serves stream 0 only.");
        }

        CallOutcome outcome = await _client.CallAsync(call.Method, call.Parameters, call.Shape, call.DeadlineMs,
            cancellation, call.Rename).ConfigureAwait(false);
        return outcome.Match<GameReply>(
            answered => new GameReply.Ok(answered.Result, 0, 0, false),
            refused => new GameReply.Refused(refused.Code, Message(refused.Error), Data(refused.Error)),
            noAnswer => new GameReply.Unreachable(noAnswer.Message, noAnswer.MaybeRan));
    }

    public Task EndStreamAsync(long stream) => throw new NotSupportedException("Streams come with the hub's GameLink.");

    public Task SessionEndAsync() => Task.CompletedTask;

    public Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation) =>
        throw new NotSupportedException("Held restores come with the hub's GameLink.");

    public Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation) =>
        throw new NotSupportedException("Stream costs come with the hub's GameLink.");

    public Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation) =>
        throw new NotSupportedException("Job waits come with the hub's GameLink.");

    public ValueTask DisposeAsync()
    {
        _client.WorldChanged -= OnWorldChanged;
        return _client.DisposeAsync();
    }

    private void OnWorldChanged(JsonElement world) => WorldChanged?.Invoke(WorldId);

    private static string Message(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message) &&
        message.ValueKind == JsonValueKind.String
            ? message.GetString()!
            : string.Empty;

    private static JsonElement? Data(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out JsonElement data) &&
        data.ValueKind != JsonValueKind.Null
            ? data.Clone()
            : null;
}
