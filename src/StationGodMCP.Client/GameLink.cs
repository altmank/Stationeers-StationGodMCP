using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// The hub's one game connection (hub.md, Streams; protocol.md, Streams, A call's world, Resuming). Calls are tagged
/// by stream and carry the world id the link had seen when they were made; admission keeps the welcome's per-stream
/// and per-connection limits, round robin by stream with stream 0 first. Every hello carries this link's resume token,
/// and a broken connection is reopened with it at once, then with backoff from 100 ms doubling to 5 s.
///
/// The resend rule is StationGodClient's: a call never written goes on the next connection; a written call with no
/// reply is sent again, once, only when it is read class with no x-effects, and otherwise answers Unreachable with
/// MaybeRan; nothing is sent into a world other than the one the call was made in. After SessionEndAsync or
/// DisposeAsync nothing more is written. Safe to use from several threads.
/// </summary>
public sealed class GameLink : IHubLink, IAsyncDisposable
{
    private const int KeptJobs = 256;
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(5);
    private static readonly JsonElement NoParameters = Json("{}");
    private static readonly JsonElement NoResult = Json("null");
    private static readonly GameCall WorldTopic = new("subscribe", Json("""{"topic":"world"}"""));
    private static readonly GameCall JobsTopic = new("subscribe", Json("""{"topic":"jobs"}"""));
    private static readonly GameCall CatalogueCall = new("catalogue", NoParameters);

    private readonly ClientOptions _options;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Admission _admission = new();
    private readonly JobBook _jobs = new(KeptJobs);
    private readonly Dictionary<string, GameCatalogue> _catalogues = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private volatile GameConnection? _connection;
    private volatile GameCatalogue _catalogue;
    private volatile bool _disposed;
    private volatile bool _ended;
    private string? _lastWorldId;
    private JsonElement? _world;
    private bool _welcomed;
    private long _nextId;
    private long _lastStream;
    private int _reconnecting;

    public GameLink(ClientOptions options)
        : this(options, NewResumeToken())
    {
    }

    /// <summary>
    /// A link that says hello with a resume token of the caller's: a hub adopting an earlier hub's journal reads that
    /// token's held-restore outcomes with it (docs/architecture/hub.md, The journal).
    /// </summary>
    public GameLink(ClientOptions options, string resumeToken)
    {
        _options = options;
        _catalogue = options.BuiltInCatalogue;
        _catalogues[_catalogue.Hash] = _catalogue;
        ResumeToken = resumeToken;
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

    /// <summary>The world as the last welcome or world_changed gave it ({id, save, epoch}); null before any.</summary>
    public JsonElement? World
    {
        get
        {
            lock (_gate)
            {
                return _world;
            }
        }
    }

    /// <summary>limits.max_request_bytes of the open connection, else the protocol's default.</summary>
    public int MaxRequestBytes => _connection?.MaxRequestBytes ?? GameConnection.DefaultMaxRequestBytes;

    public event Action<string?>? WorldChanged;

    /// <summary>
    /// Every event message but ping, after the link has handled it (world_changed and job_ended included), raised on
    /// the reader: the subscription events the hub relays. A handler that throws is ignored.
    /// </summary>
    public event Action<JsonElement>? EventReceived;

    /// <summary>The time added to each call's wait for its reply, after its deadline and x-duration.</summary>
    internal TimeSpan ReplyGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long SessionEndAsync may spend opening a connection when none is open.</summary>
    // The hub's cleanup window (0.3 s) plus this stays inside the ~0.4 s the client gives a hub after EOF.
    internal TimeSpan SessionEndWait { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>True while a connection is being opened under the connect lock.</summary>
    internal bool IsConnecting => _connectLock.CurrentCount == 0;

    private bool Closed => _disposed || _ended;

    public long NewStream() => Interlocked.Increment(ref _lastStream);

    /// <summary>Connects now if not connected; false when the game could not be reached.</summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellation = default) =>
        await ConnectionAsync(cancellation).ConfigureAwait(false) is Connecting.Connected;

    // ---- calls ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// One call on its stream, in the world the link had seen when it was made. It must be written within its connect
    /// timeout plus its reply time (the deadline, 30 s by default, plus x-duration plus the grace), else it answers
    /// Unreachable unwritten; once written it waits its reply time from the write, then answers Unreachable and cancels
    /// the call when the server allows. Throws only when cancelled.
    /// </summary>
    public async Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
    {
        JsonElement parameters = call.Parameters.ValueKind == JsonValueKind.Object ? call.Parameters : NoParameters;
        TimeSpan reply = (call.DeadlineMs is { } given ? TimeSpan.FromMilliseconds(given) : DefaultDeadline) +
                         _catalogue.Duration(call.Method, parameters) + ReplyGrace;
        DateTime sendBy = DateTime.UtcNow + _options.EffectiveConnectTimeout + reply;
        bool worldKnown;
        string? world;
        lock (_gate)
        {
            // A link that has seen a welcome tags the call with that world even while disconnected, so a call made for
            // one save is never written into the next.
            worldKnown = _welcomed;
            world = _lastWorldId;
        }

        if (call.World is { } pinned)
        {
            // The caller's own world (a run's): a call for a world the link has left is not sent at all.
            if (worldKnown && world != pinned)
            {
                return new GameReply.Refused("world_changed",
                    $"The game is in another world than the one {call.Method} was made for; it was not sent, because the " +
                    "ids it carries may name other things now.", null);
            }

            world = pinned;
            worldKnown = true;
        }

        TimeSpan backoff = FirstBackoff;
        bool waiting = false;
        int resends = 0;
        while (true)
        {
            if (Closed)
            {
                return ClosedReply(call.Method);
            }

            if (waiting && DateTime.UtcNow >= sendBy)
            {
                return new GameReply.Unreachable(
                    $"{call.Method} could not be sent to {_options.Target.Description} again before its time ran out.", maybeRan: false);
            }

            Connecting connecting = await ConnectionAsync(cancellation).ConfigureAwait(false);
            if (connecting is Connecting.Failed failed)
            {
                // A new call answers at once; one waiting to be sent again tries until its time runs out.
                if (Closed || !waiting || DateTime.UtcNow + backoff >= sendBy)
                {
                    return Closed ? ClosedReply(call.Method) : ReplyOf(failed.Outcome);
                }

                await Task.Delay(backoff, cancellation).ConfigureAwait(false);
                backoff = Min(backoff * 2, LongestBackoff);
                continue;
            }

            GameConnection connection = ((Connecting.Connected)connecting).Connection;
            string? current = CurrentWorld();
            if (!worldKnown)
            {
                world = current;
                worldKnown = true;
            }

            if (current != world)
            {
                return WorldMoved(call.Method);
            }

            if (world == null && ChangesTheWorld(call.Method, parameters))
            {
                return NoWorld(call.Method);
            }

            Attempt attempt = await AttemptAsync(connection, call, parameters, world, sendBy, reply, cancellation)
                .ConfigureAwait(false);
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

    // One try on one connection: wait for a slot on the call's stream (until sendBy), write the call, wait its reply
    // time from the write.
    private async Task<Attempt> AttemptAsync(GameConnection connection, GameCall call, JsonElement parameters,
        string? world, DateTime sendBy, TimeSpan reply, CancellationToken cancellation)
    {
        if (!await _admission.EnterAsync(call.Stream, Remaining(sendBy), cancellation).ConfigureAwait(false))
        {
            return new Attempt.Done(Closed
                ? ClosedReply(call.Method)
                : new GameReply.Unreachable(
                    $"{call.Method} waited for a free call slot on stream {call.Stream} until its time ran out; it was not sent.",
                    maybeRan: false));
        }

        string id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        string line = Wire.LinkCall(connection.Features, id, call, parameters, world);
        int bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes > connection.MaxRequestBytes)
        {
            // The mod closes the whole connection on a longer message, which would break every caller's calls.
            _admission.Leave(call.Stream);
            return new Attempt.Done(TooLarge(call.Method, bytes, connection.MaxRequestBytes));
        }

        Action<GameConnection, JsonElement>? onReply = call.OnReply is { } given
            ? (_, message) => given(ReplyOf(message))
            : null;
        PendingCall pending = new(id, onReply);
        bool holdsSlot = true;
        connection.Pending[id] = pending;
        try
        {
            if (Closed)
            {
                return new Attempt.Done(ClosedReply(call.Method));
            }

            if (DateTime.UtcNow >= sendBy)
            {
                return new Attempt.Done(new GameReply.Unreachable(
                    $"{call.Method} ran out of time before it could be written; it was not sent.", maybeRan: false));
            }

            Sending sent = await connection.SendAsync(line).ConfigureAwait(false);
            if (sent is not Sending.Written)
            {
                // A write that failed part way may still have reached the game: only a safe call is sent again.
                return sent is Sending.NotWritten ? Attempt.NotWritten.Instance : Attempt.Broken.Instance;
            }

            CallEnd end;
            try
            {
                end = await pending.Ended.WaitAsync(reply, cancellation).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (connection.Features.Contains("cancel"))
                {
                    await connection.TrySendAsync(Wire.Cancel(id)).ConfigureAwait(false);
                }

                // The mod may still run it: the slot stays taken until its late reply or the connection's close.
                holdsSlot = false;
                _ = HoldSlotUntilEndAsync(connection, pending, call.Stream);
                cancellation.ThrowIfCancellationRequested();
                return Attempt.TimedOut.Instance;
            }

            return end is CallEnd.Reply answered
                ? new Attempt.Done(ReplyOf(answered.Message))
                : Attempt.Broken.Instance;
        }
        finally
        {
            if (holdsSlot)
            {
                connection.Pending.TryRemove(id, out _);
                _admission.Leave(call.Stream);
            }
        }
    }

    private async Task HoldSlotUntilEndAsync(GameConnection connection, PendingCall pending, long stream)
    {
        try
        {
            await pending.Ended.ConfigureAwait(false);
        }
        finally
        {
            connection.Pending.TryRemove(pending.Id, out _);
            _admission.Leave(stream);
        }
    }

    private static GameReply TooLarge(string method, int bytes, int limit) => new GameReply.Refused("request_too_large",
        $"{method} would be a {bytes}-byte message, over the game's limit of {limit} bytes (welcome " +
        "limits.max_request_bytes); it was not sent. Send less per call.",
        Json("{\"limit\":" + limit.ToString(CultureInfo.InvariantCulture) + ",\"bytes\":" +
             bytes.ToString(CultureInfo.InvariantCulture) + "}"));

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

    /// <summary>
    /// stream_end on the open connection only, never reconnecting: a hub closing a session must not wait for a game
    /// that is down. A stream left so is the token's, and ends when the resume window passes (or with session_end).
    /// </summary>
    public async Task EndStreamIfConnectedAsync(long stream)
    {
        if (stream > 0 && !Closed && _connection is { Alive: true } connection && connection.Features.Contains("streams"))
        {
            await connection.TrySendAsync(Wire.StreamEnd(stream)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// session_end: from now on nothing more is written (calls not yet written answer Unreachable unrun). Sent on the
    /// open connection at once; with none open, one connection is tried for at most SessionEndWait, beside any
    /// reconnect in progress, never behind it. When that fails too, the mod applies the held restores when the token's
    /// resume window passes.
    /// </summary>
    public async Task SessionEndAsync()
    {
        _ended = true;
        _admission.Close();
        if (_connection is { Alive: true } live &&
            await live.SendAsync(Wire.SessionEnd()).ConfigureAwait(false) is Sending.Written)
        {
            return;
        }

        await SessionEndOnNewConnectionAsync().ConfigureAwait(false);
    }

    private async Task SessionEndOnNewConnectionAsync()
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        bound.CancelAfter(SessionEndWait);
        GameConnection connection;
        try
        {
            if (await Handshake.OpenAsync(_options, Library.LinkFeatures, ResumeToken, bound.Token).ConfigureAwait(false)
                is not Connecting.Connected connected)
            {
                return;
            }

            connection = connected.Connection;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await connection.TrySendAsync(Wire.SessionEnd()).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_disposed)
            {
                // Kept so DisposeAsync says bye on it; it reads, but no reconnect follows its close.
                _connection = connection;
                _ = Task.Run(() => connection.ReadAsync(OnMessage, OnClosed), CancellationToken.None);
                return;
            }
        }

        connection.Close();
    }

    /// <summary>
    /// Ends what the mod pins for the stream (its jobs' snapshots; protocol.md, Job pins). Sent only when the welcome
    /// lists feature release; a mod without it pins nothing, so there is nothing to end.
    /// </summary>
    public async Task ReleaseAsync(long stream)
    {
        if (_connection is { Alive: true } connection && connection.Features.Contains("release"))
        {
            await connection.TrySendAsync(Wire.Release(stream)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Frees a walk's cursor (protocol.md, Walks), on the open connection only: a cursor does not outlive the connection
    /// that made it unless the token resumes, and then it is freed when its stream ends.
    /// </summary>
    public async Task CursorCloseAsync(string cursorId)
    {
        if (!Closed && _connection is { Alive: true } connection && connection.Features.Contains("cursors"))
        {
            await connection.TrySendAsync(Wire.CursorClose(cursorId)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// hold_restore on the stream. Refused not_supported, unsent, when the mod has no streams: it would act on stream
    /// 0, which every run shares (a clear_all would clear them all).
    /// </summary>
    public async Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation)
    {
        if (await ConnectionAsync(cancellation).ConfigureAwait(false) is Connecting.Connected { Connection: var connection } &&
            !connection.Features.Contains("streams"))
        {
            return new GameReply.Refused("not_supported",
                "This mod has no streams ('streams' not in welcome.features), so hold_restore would act on the stream " +
                "every run shares; it was not sent.", null);
        }

        return await CallAsync(new GameCall("hold_restore", change, stream), cancellation).ConfigureAwait(false);
    }

    /// <summary>hold_restore for a run in its own world: refused world_changed, unsent, once the link has left it.</summary>
    public async Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, string? world,
        CancellationToken cancellation)
    {
        if (await ConnectionAsync(cancellation).ConfigureAwait(false) is Connecting.Connected { Connection: var connection } &&
            !connection.Features.Contains("streams"))
        {
            return new GameReply.Refused("not_supported",
                "This mod has no streams ('streams' not in welcome.features), so hold_restore would act on the stream " +
                "every run shares; it was not sent.", null);
        }

        return await CallAsync(new GameCall("hold_restore", change, stream) { World = world }, cancellation).ConfigureAwait(false);
    }

    /// <summary>stream_costs for the streams asked, in the order asked; empty when the game did not answer it.</summary>
    public async Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation)
    {
        GameReply reply = await CallAsync(new GameCall("stream_costs", StreamsParameter(streams)), cancellation).ConfigureAwait(false);
        return reply is GameReply.Ok ok ? StreamCostsOf(ok.Result) : [];
    }

    /// <summary>
    /// The job's job_ended facts, or null: the timeout passed, the link reconnected (events of the break are lost, so
    /// poll the job's job_id), or the link closed.
    /// </summary>
    public Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation) =>
        _jobs.WaitAsync(jobId, timeout, cancellation);

    /// <summary>
    /// Closes the link. bye is sent only after SessionEndAsync: bye clears every held restore unwritten, so a link closed
    /// without ending its session closes without it, and the mod applies the restores when the resume window passes.
    /// </summary>
    public ValueTask DisposeAsync() => CloseAsync(_ended);

    /// <summary>
    /// Closes the link, with bye or without it whatever SessionEndAsync did: adoption closes a resumed token's link
    /// without bye after session_end (so the restores are applied, not cleared), and says bye on its last link.
    /// </summary>
    public async ValueTask CloseAsync(bool bye)
    {
        GameConnection? connection;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            _connection = null;
        }

        _lifetime.Cancel();
        _admission.Close();
        _jobs.Close();
        if (connection != null)
        {
            if (bye)
            {
                await connection.TrySendAsync(Wire.Bye()).ConfigureAwait(false);
            }

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
                Whole(entry, "over_budget_frames"), Whole(entry, "updates"), Number(entry, "update_ms"));
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
                Number(message, "elapsed_ms"), Wire.Child(message, "budget_hit") is { ValueKind: JsonValueKind.True })
            {
                Shaped = Wire.Child(message, "shaped") is { ValueKind: JsonValueKind.True },
                QueueMs = Wire.Child(message, "queue_ms") is { ValueKind: JsonValueKind.Number } queue ? queue.GetDouble() : null
            };
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
        new(Wire.Text(error, "code") ?? "internal_error", Wire.Text(error, "message") ?? "", Wire.Child(error, "data"))
        {
            MaybeRan = Wire.Child(error, "maybe_ran") is { ValueKind: JsonValueKind.True }
        };

    private GameReply ClosedReply(string method) => new GameReply.Unreachable(
        $"{method} was not sent: the game link {(_disposed ? "is closed" : "has ended its session")}.", maybeRan: false);

    // The call was made in a world the game has left: refused here as the mod would refuse it, data naming the world now.
    private GameReply WorldMoved(string method) => WorldRefusal(
        $"The game is in another world than when {method} was made; it was not sent, because the reference ids it " +
        "carries may name other things now.");

    // No world runs (the menu, a load): a write or cheat would act in whatever world loads next.
    private GameReply NoWorld(string method) => WorldRefusal(
        $"No world is loaded, so {method} was not sent: it would act in whatever world loads next.");

    private GameReply WorldRefusal(string message)
    {
        JsonElement? world;
        lock (_gate)
        {
            world = _world;
        }

        return new GameReply.Refused("world_changed", message, Json("{\"world\":" + (world?.GetRawText() ?? "null") + "}"));
    }

    private bool ChangesTheWorld(string method, JsonElement parameters) =>
        _catalogue.EffectiveClass(method, parameters) is "write" or "cheat";

    // ---- connecting -----------------------------------------------------------------------------------------------

    // Opening and adopting run under the connect lock; what follows a new connection (WorldChanged, the topics, the
    // catalogue) runs after it, on the link's lifetime, so a caller's cancellation cannot cut it short.
    private async Task<Connecting> ConnectionAsync(CancellationToken cancellation)
    {
        if (Closed)
        {
            return ClosedConnecting();
        }

        GameConnection? current = _connection;
        if (current is { Alive: true })
        {
            return new Connecting.Connected(current);
        }

        Adoption? adopted = null;
        Connecting connecting;
        await _connectLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            current = _connection;
            if (current is { Alive: true })
            {
                return new Connecting.Connected(current);
            }

            if (Closed)
            {
                return ClosedConnecting();
            }

            connecting = await Handshake.OpenAsync(_options, Library.LinkFeatures, ResumeToken, cancellation)
                .ConfigureAwait(false);
            if (connecting is Connecting.Connected connected)
            {
                adopted = Adopt(connected.Connection);
                if (adopted == null)
                {
                    return ClosedConnecting();
                }
            }
        }
        finally
        {
            _connectLock.Release();
        }

        if (adopted != null)
        {
            AfterAdopting(adopted);
        }

        return connecting;
    }

    private Connecting ClosedConnecting() => new Connecting.Failed(new CallOutcome.NoAnswer(
        _disposed ? "The game link is closed." : "The game link has ended its session.", MaybeRan: false));

    // Publishes the connection and its world, unless the link closed while it was opening: then it is closed unused.
    private Adoption? Adopt(GameConnection connection)
    {
        string? world = WorldIdOf(connection.World);
        Adoption adoption;
        lock (_gate)
        {
            if (Closed)
            {
                connection.Close();
                return null;
            }

            adoption = new Adoption(connection, world, worldChanged: _welcomed && world != _lastWorldId, reconnected: _welcomed);
            _connection = connection;
            Welcome = connection.Welcome;
            _welcomed = true;
            _lastWorldId = world;
            _world = connection.World;
        }

        _admission.SetLimits(connection.PerStreamLimit, connection.PerConnectionLimit);
        _ = Task.Run(() => connection.ReadAsync(OnMessage, OnClosed), CancellationToken.None);
        return adoption;
    }

    // WorldChanged, waking job waits after a break, the world and jobs topics unless the mod resumed this token (it
    // keeps its subscriptions), then the catalogue: the topics never wait behind a catalogue fetch.
    private void AfterAdopting(Adoption adoption)
    {
        GameConnection connection = adoption.Connection;
        if (adoption.WorldChanged)
        {
            RaiseWorldChanged(adoption.World);
        }

        if (adoption.Reconnected)
        {
            _jobs.WakeAll();
        }

        if (!connection.Resumed && connection.Features.Contains("subscriptions"))
        {
            _ = Task.Run(() => CallAsync(WorldTopic, CancellationToken.None), CancellationToken.None);
            _ = Task.Run(() => CallAsync(JobsTopic, CancellationToken.None), CancellationToken.None);
        }

        _ = Task.Run(() => UseCatalogueAsync(connection), CancellationToken.None);
    }

    // The mod's catalogue when its hash differs from the one in use; the built-in one when the fetch fails.
    private async Task UseCatalogueAsync(GameConnection connection)
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
            try
            {
                TimeSpan reply = DefaultDeadline + ReplyGrace;
                Attempt attempt = await AttemptAsync(connection, CatalogueCall, NoParameters, null, DateTime.UtcNow + reply,
                    reply, _lifetime.Token).ConfigureAwait(false);
                if (attempt is Attempt.Done { Reply: GameReply.Ok { Result.ValueKind: JsonValueKind.Object } fetched })
                {
                    known = GameCatalogue.FromServer(fetched.Result, hash);
                    lock (_gate)
                    {
                        _catalogues[hash] = known;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The link closed during the fetch.
            }
        }

        _catalogue = known ?? _options.BuiltInCatalogue;
    }

    private void OnClosed(GameConnection connection)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }
        }

        if (NeedsReconnect())
        {
            StartReconnecting();
        }
    }

    private bool NeedsReconnect() => !Closed && _connection is not { Alive: true };

    private void StartReconnecting()
    {
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0)
        {
            _ = Task.Run(ReconnectAsync);
        }
    }

    // Reopens a broken connection at once, then with backoff from 100 ms doubling to 5 s, until it is open again. A
    // break between the last check and the reset of the flag found the flag still set, so it is checked again after.
    private async Task ReconnectAsync()
    {
        TimeSpan backoff = FirstBackoff;
        try
        {
            while (NeedsReconnect())
            {
                if (await ConnectionAsync(_lifetime.Token).ConfigureAwait(false) is Connecting.Connected)
                {
                    continue;
                }

                await Task.Delay(backoff, _lifetime.Token).ConfigureAwait(false);
                backoff = Min(backoff * 2, LongestBackoff);
            }
        }
        catch (OperationCanceledException)
        {
            // The link closed.
        }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);
            if (NeedsReconnect())
            {
                StartReconnecting();
            }
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

        if (EventReceived is { } handlers)
        {
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                Guarded(() => ((Action<JsonElement>)handler)(message));
            }
        }
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

        RaiseWorldChanged(id);
    }

    private void RaiseWorldChanged(string? world)
    {
        if (WorldChanged is { } handlers)
        {
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                Guarded(() => ((Action<string?>)handler)(world));
            }
        }
    }

    // A handler's fault is its own: it must not end the reader (and with it the connection) or the adoption.
    private static void Guarded(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception)
        {
            // Ignored on purpose; see above.
        }
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

    private static TimeSpan Remaining(DateTime until)
    {
        TimeSpan left = until - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A connection just published, and what it changed.</summary>
    private sealed class Adoption(GameConnection connection, string? world, bool worldChanged, bool reconnected)
    {
        internal GameConnection Connection { get; } = connection;

        internal string? World { get; } = world;

        internal bool WorldChanged { get; } = worldChanged;

        /// <summary>Not the link's first welcome: events may have been missed during the break.</summary>
        internal bool Reconnected { get; } = reconnected;
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

        /// <summary>Nothing of the line left: safe to send on the next connection.</summary>
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
