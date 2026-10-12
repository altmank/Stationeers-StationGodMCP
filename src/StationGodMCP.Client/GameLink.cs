using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// The hub's one game connection (hub.md, Streams; protocol.md, Streams, A call's world, Resuming). Calls are tagged
/// by stream and carry the world id they were made in; admission keeps the welcome's per-stream and per-connection
/// limits, round robin by stream with stream 0 first. Every hello carries this link's resume token, and a broken
/// connection is reopened with it at once, then with backoff from 100 ms doubling to 5 s.
///
/// The resend rule is StationGodClient's: a call never written goes on the next connection; a written call with no
/// reply is sent again, once, only when it is read class with no x-effects, and otherwise answers Unreachable with
/// MaybeRan; nothing is sent again into another world. Safe to use from several threads.
/// </summary>
public sealed class GameLink : IGameLink, IAsyncDisposable
{
    private const int KeptJobs = 256;
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(5);
    private static readonly JsonElement NoParameters = Json("{}");
    private static readonly JsonElement NoResult = Json("null");
    private static readonly GameCall WorldTopic = new("subscribe", Json("""{"topic":"world"}"""));
    private static readonly GameCall JobsTopic = new("subscribe", Json("""{"topic":"jobs"}"""));
    private static readonly GameCall CatalogueCall = new("catalogue", NoParameters);

    private readonly ClientOptions _options;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Admission _admission = new();
    private readonly JobBook _jobs = new(KeptJobs);
    private readonly Dictionary<string, GameCatalogue> _catalogues = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private volatile GameConnection? _connection;
    private volatile GameCatalogue _catalogue;
    private string? _lastWorldId;
    private JsonElement? _world;
    private bool _welcomed;
    private long _nextId;
    private long _lastStream;
    private int _reconnecting;
    private bool _disposed;

    public GameLink(ClientOptions options)
    {
        _options = options;
        _catalogue = options.BuiltInCatalogue;
        _catalogues[_catalogue.Hash] = _catalogue;
        ResumeToken = NewResumeToken();
    }

    /// <summary>"hub-" and 16 lower-case hex digits, sent as resume in every hello of this link.</summary>
    public string ResumeToken { get; }

    /// <summary>The last welcome message; null before the first connection.</summary>
    public JsonElement? Welcome { get; private set; }

    /// <summary>
    /// The world id from the last welcome or world_changed; null while no connection is open or no world runs. Going
    /// unreachable raises nothing: the world has not changed, it is only out of sight.
    /// </summary>
    public string? WorldId
    {
        get
        {
            lock (_gate)
            {
                return _connection is { Alive: true } ? _lastWorldId : null;
            }
        }
    }

    public GameCatalogue Catalogue => _catalogue;

    public event Action<string?>? WorldChanged;

    /// <summary>
    /// Every event message but ping, after the link has handled it (world_changed and job_ended included), raised on
    /// the reader: the subscription events the hub relays.
    /// </summary>
    public event Action<JsonElement>? EventReceived;

    public long NewStream() => Interlocked.Increment(ref _lastStream);

    /// <summary>Connects now if not connected; false when the game could not be reached.</summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellation = default) =>
        await ConnectionAsync(cancellation).ConfigureAwait(false) is Connecting.Connected;

    // ---- calls ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// One call on its stream. It waits its connect timeout plus the deadline (30 s by default) plus x-duration plus
    /// 5 s, then answers Unreachable and cancels the call when the server allows. Throws only when cancelled.
    /// </summary>
    public async Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
    {
        JsonElement parameters = call.Parameters.ValueKind == JsonValueKind.Object ? call.Parameters : NoParameters;
        TimeSpan reply = (call.DeadlineMs is { } given ? TimeSpan.FromMilliseconds(given) : DefaultDeadline) +
                         _catalogue.Duration(call.Method, parameters) + ReplyGrace;
        DateTime expires = DateTime.UtcNow + _options.EffectiveConnectTimeout + reply;
        TimeSpan backoff = FirstBackoff;
        string? world = null;
        bool started = false;
        bool waiting = false;
        int resends = 0;
        while (true)
        {
            if (waiting && DateTime.UtcNow >= expires)
            {
                return new GameReply.Unreachable(
                    $"{call.Method} could not be sent to {_options.Target.Description} again before its time ran out.", maybeRan: false);
            }

            Connecting connecting = await ConnectionAsync(cancellation).ConfigureAwait(false);
            if (connecting is Connecting.Failed failed)
            {
                // A new call answers at once; one waiting to be sent again tries until its time runs out.
                if (!waiting || DateTime.UtcNow + backoff >= expires)
                {
                    return ReplyOf(failed.Outcome);
                }

                await Task.Delay(backoff, cancellation).ConfigureAwait(false);
                backoff = Min(backoff * 2, LongestBackoff);
                continue;
            }

            GameConnection connection = ((Connecting.Connected)connecting).Connection;
            string? current = CurrentWorld();
            if (!started)
            {
                // The call means the world it was made in; it carries that id on every try.
                world = current;
                started = true;
            }
            else if (current != world)
            {
                return WorldMoved(call.Method);
            }

            Attempt attempt = await AttemptAsync(connection, call, parameters, world, expires, cancellation).ConfigureAwait(false);
            bool safe = _catalogue.ResendSafe(call.Method, parameters);
            switch (attempt)
            {
                case Attempt.Done done:
                    return done.Reply;
                case Attempt.NotWritten:
                    waiting = true;
                    continue;
                case Attempt.TimedOut:
                    return new GameReply.Unreachable(
                        $"The game took {call.Method} through {_options.Target.Description} but sent no reply within " +
                        $"{reply.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s; it may still have run: " +
                        "read the state back before repeating a change.",
                        maybeRan: !safe);
                case Attempt.Broken when safe && resends < 1:
                    resends++;
                    waiting = true;
                    continue;
                default:
                    return new GameReply.Unreachable(
                        $"The connection to the game through {_options.Target.Description} broke while {call.Method} was in flight; " +
                        (safe
                            ? "it was sent again once already."
                            : "it may have reached the game, so it was not sent again: read the state back before repeating a change."),
                        maybeRan: !safe);
            }
        }
    }

    // One try on one connection: wait for a slot on the call's stream, write the call, wait for its end.
    private async Task<Attempt> AttemptAsync(GameConnection connection, GameCall call, JsonElement parameters,
        string? world, DateTime expires, CancellationToken cancellation)
    {
        if (!await _admission.EnterAsync(call.Stream, Remaining(expires), cancellation).ConfigureAwait(false))
        {
            return new Attempt.Done(new GameReply.Unreachable(
                $"{call.Method} waited for a free call slot on stream {call.Stream} until its time ran out.", maybeRan: false));
        }

        string id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        PendingCall pending = new(id, null);
        try
        {
            connection.Pending[id] = pending;
            if (!connection.Alive)
            {
                return Attempt.NotWritten.Instance;
            }

            if (!await connection.TrySendAsync(Wire.LinkCall(connection.Features, id, call, parameters, world)).ConfigureAwait(false))
            {
                // A write that failed part way may still have reached the game: only a safe call is sent again.
                return Attempt.Broken.Instance;
            }

            CallEnd end;
            try
            {
                end = await pending.Ended.WaitAsync(Remaining(expires), cancellation).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (connection.Features.Contains("cancel"))
                {
                    await connection.TrySendAsync(Wire.Cancel(id)).ConfigureAwait(false);
                }

                cancellation.ThrowIfCancellationRequested();
                return Attempt.TimedOut.Instance;
            }

            return end is CallEnd.Reply answered
                ? new Attempt.Done(ReplyOf(answered.Message))
                : Attempt.Broken.Instance;
        }
        finally
        {
            connection.Pending.TryRemove(id, out _);
            _admission.Leave(call.Stream);
        }
    }

    // ---- protocol messages and methods ----------------------------------------------------------------------------

    /// <summary>
    /// stream_end, when the mod lists streams. Unreachable, it reconnects first: with the token the mod still holds the
    /// stream. Stream 0 never ends, so nothing is sent for it.
    /// </summary>
    public async Task EndStreamAsync(long stream)
    {
        if (stream > 0 && await ConnectionAsync(CancellationToken.None).ConfigureAwait(false) is Connecting.Connected { Connection: var connection } &&
            connection.Features.Contains("streams"))
        {
            await connection.TrySendAsync(Wire.StreamEnd(stream)).ConfigureAwait(false);
        }
    }

    /// <summary>session_end. Reconnecting first, with the token, reaches the state the mod holds for it.</summary>
    public async Task SessionEndAsync()
    {
        if (await ConnectionAsync(CancellationToken.None).ConfigureAwait(false) is Connecting.Connected { Connection: var connection })
        {
            await connection.TrySendAsync(Wire.SessionEnd()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends what the mod pins for the stream (job snapshots). Sent only when the welcome lists feature release; the mod
    /// does not yet, so today this does nothing.
    /// </summary>
    public async Task ReleaseAsync(long stream)
    {
        if (_connection is { Alive: true } connection && connection.Features.Contains("release"))
        {
            await connection.TrySendAsync(Wire.Release(stream)).ConfigureAwait(false);
        }
    }

    public Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation) =>
        CallAsync(new GameCall("hold_restore", change, stream), cancellation);

    /// <summary>stream_costs for the streams asked, in the order asked; empty when the game did not answer it.</summary>
    public async Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation)
    {
        GameReply reply = await CallAsync(new GameCall("stream_costs", StreamsParameter(streams)), cancellation).ConfigureAwait(false);
        return reply is GameReply.Ok ok ? StreamCostsOf(ok.Result) : [];
    }

    public Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation) =>
        _jobs.WaitAsync(jobId, timeout, cancellation);

    public async ValueTask DisposeAsync()
    {
        GameConnection? connection;
        lock (_gate)
        {
            _disposed = true;
            connection = _connection;
            _connection = null;
        }

        if (connection != null)
        {
            await connection.TrySendAsync(Wire.Bye()).ConfigureAwait(false);
            connection.Close();
        }
    }

    internal static StreamCost[] StreamCostsOf(JsonElement result)
    {
        if (Wire.Child(result, "streams") is not { ValueKind: JsonValueKind.Array } entries)
        {
            return [];
        }

        StreamCost[] costs = new StreamCost[entries.GetArrayLength()];
        int index = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            costs[index++] = new StreamCost(Whole(entry, "stream"), Whole(entry, "calls"), Number(entry, "main_thread_ms"),
                Whole(entry, "over_budget_frames"));
        }

        return costs;
    }

    private static JsonElement StreamsParameter(IReadOnlyList<long> streams)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("streams");
            for (int index = 0; index < streams.Count; index++)
            {
                writer.WriteNumberValue(streams[index]);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    // ---- replies --------------------------------------------------------------------------------------------------

    internal static GameReply ReplyOf(JsonElement message)
    {
        if (Wire.Child(message, "ok") is { ValueKind: JsonValueKind.True })
        {
            return new GameReply.Ok(Wire.Child(message, "result") ?? NoResult, Whole(message, "frame"),
                Number(message, "elapsed_ms"), Wire.Child(message, "budget_hit") is { ValueKind: JsonValueKind.True });
        }

        return Wire.Child(message, "error") is { ValueKind: JsonValueKind.Object } error
            ? RefusedOf(error)
            : new GameReply.Refused("internal_error", "The game answered with neither a result nor an error.", null);
    }

    private static GameReply ReplyOf(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Refused refused => RefusedOf(refused.Error),
        CallOutcome.NoAnswer noAnswer => new GameReply.Unreachable(noAnswer.Message, noAnswer.MaybeRan),
        _ => new GameReply.Unreachable("The game could not be reached.", maybeRan: false)
    };

    private static GameReply.Refused RefusedOf(JsonElement error) =>
        new(Wire.Text(error, "code") ?? "internal_error", Wire.Text(error, "message") ?? "", Wire.Child(error, "data"));

    // The call was made in a world the game has left: refused here as the mod would refuse it, data naming the world now.
    private GameReply WorldMoved(string method)
    {
        JsonElement? world;
        lock (_gate)
        {
            world = _world;
        }

        JsonElement data = Json("{\"world\":" + (world?.GetRawText() ?? "null") + "}");
        return new GameReply.Refused("world_changed",
            $"The game is in another world than when {method} was made; it was not sent, because the reference ids it " +
            "carries may name other things now.", data);
    }

    // ---- connecting -----------------------------------------------------------------------------------------------

    private async Task<Connecting> ConnectionAsync(CancellationToken cancellation)
    {
        GameConnection? current = _connection;
        if (current is { Alive: true })
        {
            return new Connecting.Connected(current);
        }

        await _connectLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            current = _connection;
            if (current is { Alive: true })
            {
                return new Connecting.Connected(current);
            }

            if (_disposed)
            {
                return new Connecting.Failed(new CallOutcome.NoAnswer("The game link is closed.", MaybeRan: false));
            }

            Connecting connecting = await Handshake.OpenAsync(_options, Library.LinkFeatures, ResumeToken, cancellation)
                .ConfigureAwait(false);
            if (connecting is Connecting.Connected connected)
            {
                await AdoptAsync(connected.Connection, cancellation).ConfigureAwait(false);
            }

            return connecting;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // Starts the reader, takes the welcome's limits, world and catalogue, and subscribes to the world and jobs topics
    // unless the mod resumed this token, which keeps its subscriptions.
    private async Task AdoptAsync(GameConnection connection, CancellationToken cancellation)
    {
        _admission.SetLimits(connection.PerStreamLimit, connection.PerConnectionLimit);
        string? world = WorldIdOf(connection.World);
        bool worldChanged;
        lock (_gate)
        {
            _connection = connection;
            Welcome = connection.Welcome;
            worldChanged = _welcomed && world != _lastWorldId;
            _welcomed = true;
            _lastWorldId = world;
            _world = connection.World;
        }

        _ = Task.Run(() => connection.ReadAsync(OnMessage, OnClosed), CancellationToken.None);
        await UseCatalogueAsync(connection, cancellation).ConfigureAwait(false);
        if (worldChanged)
        {
            WorldChanged?.Invoke(world);
        }

        if (!connection.Resumed && connection.Features.Contains("subscriptions"))
        {
            _ = Task.Run(() => CallAsync(WorldTopic, CancellationToken.None), CancellationToken.None);
            _ = Task.Run(() => CallAsync(JobsTopic, CancellationToken.None), CancellationToken.None);
        }
    }

    // The mod's catalogue when its hash differs from the one in use; the built-in one when the fetch fails.
    private async Task UseCatalogueAsync(GameConnection connection, CancellationToken cancellation)
    {
        if (connection.CatalogueHash is not { } hash)
        {
            _catalogue = _options.BuiltInCatalogue;
            return;
        }

        GameCatalogue? known;
        lock (_gate)
        {
            _catalogues.TryGetValue(hash, out known);
        }

        if (known == null)
        {
            DateTime expires = DateTime.UtcNow + DefaultDeadline + ReplyGrace;
            Attempt attempt = await AttemptAsync(connection, CatalogueCall, NoParameters, null, expires, cancellation)
                .ConfigureAwait(false);
            if (attempt is Attempt.Done { Reply: GameReply.Ok { Result.ValueKind: JsonValueKind.Object } fetched })
            {
                known = GameCatalogue.FromServer(fetched.Result, hash);
                lock (_gate)
                {
                    _catalogues[hash] = known;
                }
            }
        }

        _catalogue = known ?? _options.BuiltInCatalogue;
    }

    private void OnClosed(GameConnection connection)
    {
        bool reconnect;
        lock (_gate)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }

            reconnect = !_disposed;
        }

        if (reconnect && Interlocked.Exchange(ref _reconnecting, 1) == 0)
        {
            _ = Task.Run(ReconnectAsync);
        }
    }

    // Reopens a broken connection at once, then with backoff from 100 ms doubling to 5 s, until it is open again.
    private async Task ReconnectAsync()
    {
        TimeSpan backoff = FirstBackoff;
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_disposed || _connection is { Alive: true })
                    {
                        return;
                    }
                }

                if (await ConnectionAsync(CancellationToken.None).ConfigureAwait(false) is Connecting.Connected)
                {
                    return;
                }

                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = Min(backoff * 2, LongestBackoff);
            }
        }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);
        }
    }

    // ---- the reader -----------------------------------------------------------------------------------------------

    private void OnMessage(GameConnection connection, JsonElement message)
    {
        switch (Wire.Text(message, "type"))
        {
            case "reply":
                OnReply(connection, message);
                break;
            case "event":
                OnEvent(message);
                break;
            case "goodbye":
                connection.Goodbye = Wire.Text(message, "reason");
                break;
        }
    }

    private static void OnReply(GameConnection connection, JsonElement message)
    {
        string? id = Wire.Child(message, "id") is { } given
            ? given.ValueKind == JsonValueKind.String ? given.GetString() : given.GetRawText()
            : null;
        if (id != null && connection.Pending.TryGetValue(id, out PendingCall? call))
        {
            call.Reply(connection, message);
        }
    }

    private void OnEvent(JsonElement message)
    {
        switch (Wire.Text(message, "event"))
        {
            case "ping":
                return;
            case "world_changed":
                OnWorldChanged(Wire.Child(message, "world"));
                break;
            case "job_ended" when Wire.Child(message, "job") is { ValueKind: JsonValueKind.Object } job:
                _jobs.Ended(job);
                break;
        }

        EventReceived?.Invoke(message);
    }

    // Raised once per new world id, from this event or from a welcome after a reconnect.
    private void OnWorldChanged(JsonElement? world)
    {
        if (world is not { } changed)
        {
            return;
        }

        string? id = WorldIdOf(changed);
        lock (_gate)
        {
            if (id == _lastWorldId)
            {
                return;
            }

            _lastWorldId = id;
            _world = changed;
        }

        WorldChanged?.Invoke(id);
    }

    private string? CurrentWorld()
    {
        lock (_gate)
        {
            return _lastWorldId;
        }
    }

    // ---- small helpers --------------------------------------------------------------------------------------------

    // An empty id is no world (the menu, a load in progress); the mod refuses an empty world on a call.
    private static string? WorldIdOf(JsonElement? world) =>
        world is { } given && Wire.Text(given, "id") is { Length: > 0 } id ? id : null;

    private static string NewResumeToken()
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        return "hub-" + Convert.ToHexString(random).ToLowerInvariant();
    }

    private static long Whole(JsonElement message, string key) =>
        Wire.Child(message, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out long whole) ? whole : 0;

    private static double Number(JsonElement message, string key) =>
        Wire.Child(message, key) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : 0;

    private static TimeSpan Remaining(DateTime expires)
    {
        TimeSpan left = expires - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>How one try of a call on one connection ended.</summary>
    private abstract class Attempt
    {
        private Attempt()
        {
        }

        internal sealed class Done(GameReply reply) : Attempt
        {
            internal GameReply Reply { get; } = reply;
        }

        /// <summary>The connection was gone before the line was written: safe to send on the next one.</summary>
        internal sealed class NotWritten : Attempt
        {
            internal static readonly NotWritten Instance = new();
        }

        internal sealed class TimedOut : Attempt
        {
            internal static readonly TimedOut Instance = new();
        }

        /// <summary>Written (or partly written) and the connection broke before the reply.</summary>
        internal sealed class Broken : Attempt
        {
            internal static readonly Broken Instance = new();
        }
    }
}
