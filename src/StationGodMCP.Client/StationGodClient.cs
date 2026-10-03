using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// A client of the StationGod protocol over one kept connection (clients.md). It connects at the first call, signs
/// in, keeps several calls in flight, reconnects after a break and resends only what is safe, fetches the mod's
/// catalogue when its hash differs from the built-in one, follows world changes and keeps subscriptions. It holds no
/// rule of the game's: arguments, shaping and permissions are the mod's.
///
/// The resend rule, the dashboard's with the catalogue deciding what is a read:
/// - a call whose line was never written is sent again on the next connection;
/// - a written call with no reply is sent again, once, only if it is read class at its arguments and has no x-effects;
/// - nothing is sent again when the connection comes back to another world (a different welcome.server.world.id);
/// - an error reply is never resent, whatever its code.
/// Safe to use from several threads.
/// </summary>
public sealed class StationGodClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(5);
    private static readonly JsonElement NoParameters = JsonSerializer.SerializeToElement(new Dictionary<string, object>());

    private readonly ClientOptions _options;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, GameCatalogue> _catalogues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(GameConnection, string), Subscription> _bound = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly object _gate = new();
    private GameConnection? _connection;
    private long _nextId;
    private string? _lastWorldId;
    private int _reconnecting;
    private bool _disposed;

    public StationGodClient(ClientOptions options)
    {
        _options = options;
        Catalogue = options.BuiltInCatalogue;
        _catalogues[Catalogue.Hash] = Catalogue;
    }

    /// <summary>The catalogue in use: the built-in one, or the mod's after a welcome announced another hash.</summary>
    public GameCatalogue Catalogue { get; private set; }

    /// <summary>The protocol of the last connection, null before the first.</summary>
    public ProtocolVersion? Protocol { get; private set; }

    /// <summary>The last welcome message (null on version 1).</summary>
    public JsonElement? Welcome { get; private set; }

    /// <summary>The world the game is in, {id, save, epoch}, as the last welcome or world_changed event said.</summary>
    public JsonElement? World { get; private set; }

    /// <summary>The game state from the last welcome or game_state event (Running, Paused, Loading).</summary>
    public string? GameState { get; private set; }

    public GameTarget Target => _options.Target;

    /// <summary>The catalogue in use changed; raised on the connecting call's task.</summary>
    public event Action<GameCatalogue>? CatalogueChanged;

    /// <summary>The game is in another world than before: every reference id the caller holds may name something else.</summary>
    public event Action<JsonElement>? WorldChanged;

    /// <summary>Every event message after the client has handled it (ping left out); raised on the reader.</summary>
    public event Action<JsonElement>? EventReceived;

    /// <summary>
    /// Calls a method and waits for how it ended. shape goes beside params ({fields, limit, max_bytes}); deadlineMs is
    /// sent only when given, the mod then adds the method's x-duration itself. The call waits its connect timeout plus
    /// the deadline (30 s by default) plus x-duration plus 5 seconds, then answers NoAnswer and cancels the call when the
    /// server allows.
    /// </summary>
    public Task<CallOutcome> CallAsync(string method, JsonElement parameters, JsonElement? shape = null,
        int? deadlineMs = null, CancellationToken cancellation = default) =>
        CallCoreAsync(method, parameters, shape, deadlineMs, onReply: null, cancellation);

    /// <summary>
    /// Subscribes to device values (protocol.md, Subscriptions). Refused carries the mod's subscription_limit (poll
    /// read_devices instead); a server without subscriptions answers Unsupported.
    /// </summary>
    public async Task<SubscribeOutcome> SubscribeAsync(JsonElement request, CancellationToken cancellation = default)
    {
        Connecting connecting = await ConnectionAsync(cancellation).ConfigureAwait(false);
        if (connecting is Connecting.Failed failed)
        {
            return new SubscribeOutcome.Failed(failed.Outcome);
        }

        GameConnection connection = ((Connecting.Connected)connecting).Connection;
        if (connection.Version != ProtocolVersion.Version2 || !connection.Features.Contains("subscriptions"))
        {
            return new SubscribeOutcome.Unsupported(
                "This server has no subscriptions (protocol version 1, or 'subscriptions' not in welcome.features); poll read_devices instead.");
        }

        Subscription subscription = new(this, request.Clone());
        CallOutcome outcome = await SubscribeOnceAsync(subscription, cancellation).ConfigureAwait(false);
        if (outcome is not CallOutcome.Answered)
        {
            return new SubscribeOutcome.Failed(outcome);
        }

        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return new SubscribeOutcome.Subscribed(subscription);
    }

    public async ValueTask DisposeAsync()
    {
        GameConnection? connection;
        Subscription[] open;
        lock (_gate)
        {
            _disposed = true;
            connection = _connection;
            _connection = null;
            open = [.. _subscriptions];
        }

        foreach (Subscription subscription in open)
        {
            Drop(subscription, "closed");
        }

        if (connection != null)
        {
            if (connection.Version == ProtocolVersion.Version2)
            {
                await connection.TrySendAsync(Wire.Bye()).ConfigureAwait(false);
            }

            connection.Close();
        }
    }

    // ---- calls ----------------------------------------------------------------------------------------------------

    private async Task<CallOutcome> CallCoreAsync(string method, JsonElement parameters, JsonElement? shape,
        int? deadlineMs, Action<GameConnection, JsonElement>? onReply, CancellationToken cancellation)
    {
        parameters = parameters.ValueKind == JsonValueKind.Object ? parameters : NoParameters;
        TimeSpan reply = (deadlineMs is { } given ? TimeSpan.FromMilliseconds(given) : DefaultDeadline) +
                         Catalogue.Duration(method, parameters) + ReplyGrace;
        DateTime expires = DateTime.UtcNow + _options.EffectiveConnectTimeout + reply;
        TimeSpan backoff = FirstBackoff;
        string? world = null;
        bool waiting = false;
        int resends = 0;
        while (true)
        {
            if (waiting && DateTime.UtcNow >= expires)
            {
                return new CallOutcome.NoAnswer(
                    $"{method} could not be sent to {_options.Target.Description} again before its time ran out.", MaybeRan: false);
            }

            Connecting connecting = await ConnectionAsync(cancellation).ConfigureAwait(false);
            if (connecting is Connecting.Failed failed)
            {
                // A new call answers at once; one waiting to be sent again tries until its time runs out.
                if (!waiting || DateTime.UtcNow + backoff >= expires)
                {
                    return failed.Outcome;
                }

                await Task.Delay(backoff, cancellation).ConfigureAwait(false);
                backoff = Min(backoff * 2, LongestBackoff);
                continue;
            }

            GameConnection connection = ((Connecting.Connected)connecting).Connection;
            bool safe = Catalogue.ResendSafe(method, parameters);
            if (waiting && connection.WorldId != world)
            {
                // Only an unwritten call or a safe one waits to be sent again, so neither can have changed the game.
                return new CallOutcome.NoAnswer(
                    $"The game came back in another world; {method} was not sent again, because the reference ids it " +
                    "carries may name other things now.",
                    MaybeRan: false, WorldChanged: true);
            }

            world = connection.WorldId;
            Attempt attempt = await AttemptAsync(connection, method, parameters, shape, deadlineMs, onReply, expires, cancellation)
                .ConfigureAwait(false);
            switch (attempt)
            {
                case Attempt.Done done:
                    return done.Outcome;
                case Attempt.NotWritten:
                    waiting = true;
                    continue;
                case Attempt.TimedOut:
                    return new CallOutcome.NoAnswer(
                        $"The game took the request through {_options.Target.Description} but sent no reply within " +
                        $"{reply.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s; it may still have run: " +
                        "read the state back before repeating a change.",
                        MaybeRan: !safe);
                case Attempt.Broken when safe && resends < 1:
                    resends++;
                    waiting = true;
                    continue;
                case Attempt.Broken:
                    return new CallOutcome.NoAnswer(
                        $"The connection to the game through {_options.Target.Description} broke while {method} was in flight; " +
                        (safe
                            ? "it was sent again once already."
                            : "it may have reached the game, so it was not sent again: read the state back before repeating a change."),
                        MaybeRan: !safe);
            }
        }
    }

    // One try on one connection: wait for a slot, write the call, wait for its end.
    private async Task<Attempt> AttemptAsync(GameConnection connection, string method, JsonElement parameters,
        JsonElement? shape, int? deadlineMs, Action<GameConnection, JsonElement>? onReply, DateTime expires,
        CancellationToken cancellation)
    {
        if (!await connection.Slots.WaitAsync(Remaining(expires), cancellation).ConfigureAwait(false))
        {
            return new Attempt.Done(new CallOutcome.NoAnswer(
                $"{method} waited for a free call slot on {_options.Target.Description} until its time ran out.", MaybeRan: false));
        }

        string id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        PendingCall call = new(id, onReply);
        try
        {
            connection.Pending[id] = call;
            if (!connection.Alive)
            {
                return new Attempt.NotWritten();
            }

            string line = Wire.Call(connection.Version, connection.Features, id, method, parameters, shape, deadlineMs);
            if (!await connection.TrySendAsync(line).ConfigureAwait(false))
            {
                // A write that failed part way may still have reached the game: only a safe call is sent again.
                return new Attempt.Broken();
            }

            Task<CallEnd> ended = call.Ended;
            Task finished = await Task.WhenAny(ended, Task.Delay(Remaining(expires), cancellation)).ConfigureAwait(false);
            if (finished != ended)
            {
                cancellation.ThrowIfCancellationRequested();
                if (connection.Version == ProtocolVersion.Version2 && connection.Features.Contains("cancel"))
                {
                    await connection.TrySendAsync(Wire.Cancel(id)).ConfigureAwait(false);
                }

                return new Attempt.TimedOut();
            }

            return await ended.ConfigureAwait(false) switch
            {
                CallEnd.Reply reply => new Attempt.Done(Outcome(reply.Message)),
                _ => new Attempt.Broken()
            };
        }
        finally
        {
            connection.Pending.TryRemove(id, out _);
            connection.Slots.Release();
        }
    }

    private static CallOutcome Outcome(JsonElement reply)
    {
        bool ok = reply.TryGetProperty("ok", out JsonElement mark) && mark.ValueKind == JsonValueKind.True;
        if (ok)
        {
            bool shaped = reply.TryGetProperty("shaped", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            return new CallOutcome.Answered(Wire.Child(reply, "result") ?? JsonSerializer.SerializeToElement<object?>(null), shaped);
        }

        return Wire.Child(reply, "error") is { ValueKind: JsonValueKind.Object } error
            ? new CallOutcome.Refused(error)
            : CallOutcome.Refused.Of("internal_error", "The game answered with neither a result nor an error.");
    }

    private static TimeSpan Remaining(DateTime expires)
    {
        TimeSpan left = expires - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    // ---- connecting -----------------------------------------------------------------------------------------------

    /// <summary>Connects now if not connected; the protocol the game speaks, or null when it could not be reached.</summary>
    public async Task<ProtocolVersion?> ConnectAsync(CancellationToken cancellation = default) =>
        await ConnectionAsync(cancellation).ConfigureAwait(false) is Connecting.Connected connected
            ? connected.Connection.Version
            : null;

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
                return new Connecting.Failed(new CallOutcome.NoAnswer("The client is closed.", MaybeRan: false));
            }

            Connecting connecting = await Handshake.OpenAsync(_options, cancellation).ConfigureAwait(false);
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

    // Starts the reader, takes the catalogue, and decides what the new connection means for the world.
    private async Task AdoptAsync(GameConnection connection, CancellationToken cancellation)
    {
        _ = Task.Run(() => connection.ReadAsync(OnMessage, OnClosed), CancellationToken.None);
        bool worldChanged;
        Subscription[] open;
        lock (_gate)
        {
            _connection = connection;
            Protocol = connection.Version;
            Welcome = connection.Welcome;
            worldChanged = connection.WorldId != null && _lastWorldId != null && connection.WorldId != _lastWorldId;
            if (connection.WorldId != null)
            {
                _lastWorldId = connection.WorldId;
                World = connection.World;
            }

            open = _subscriptions.Where(subscription => !subscription.IsClosed).ToArray();
        }

        if (connection.Welcome is { } welcome && Wire.Child(welcome, "server") is { } server &&
            Wire.Text(server, "game_state") is { } state)
        {
            GameState = state;
        }

        await UseCatalogueAsync(connection, cancellation).ConfigureAwait(false);

        if (worldChanged)
        {
            foreach (Subscription subscription in open)
            {
                Drop(subscription, "world_changed");
            }

            WorldChanged?.Invoke(connection.World!.Value);
        }
        else
        {
            foreach (Subscription subscription in open)
            {
                _ = ResubscribeAsync(subscription);
            }
        }

        if (connection.Version == ProtocolVersion.Version2 && connection.Features.Contains("subscriptions"))
        {
            _ = Task.Run(() => CallCoreAsync("subscribe", JsonSerializer.SerializeToElement(new { topic = "world" }), null,
                null, null, CancellationToken.None), CancellationToken.None);
        }
    }

    // The mod's catalogue when its hash differs from the one in use; the built-in one on version 1 or when the fetch fails.
    private async Task UseCatalogueAsync(GameConnection connection, CancellationToken cancellation)
    {
        GameCatalogue chosen = _options.BuiltInCatalogue;
        if (connection.CatalogueHash is { } hash)
        {
            if (_catalogues.TryGetValue(hash, out GameCatalogue? known))
            {
                chosen = known;
            }
            else if (await FetchCatalogueAsync(connection, cancellation).ConfigureAwait(false) is { } fetched)
            {
                chosen = GameCatalogue.FromServer(fetched, hash);
                _catalogues[hash] = chosen;
            }
        }

        if (!ReferenceEquals(chosen, Catalogue))
        {
            Catalogue = chosen;
            CatalogueChanged?.Invoke(chosen);
        }
    }

    // The catalogue method on this connection only, during adoption, while the connect lock is held.
    private async Task<JsonElement?> FetchCatalogueAsync(GameConnection connection, CancellationToken cancellation)
    {
        DateTime expires = DateTime.UtcNow + DefaultDeadline + ReplyGrace;
        Attempt attempt = await AttemptAsync(connection, "catalogue", NoParameters, null, null, null, expires, cancellation)
            .ConfigureAwait(false);
        return attempt is Attempt.Done { Outcome: CallOutcome.Answered answered } &&
               answered.Result.ValueKind == JsonValueKind.Object
            ? answered.Result
            : null;
    }

    // ---- the reader -----------------------------------------------------------------------------------------------

    private void OnMessage(GameConnection connection, JsonElement message)
    {
        string? type = Wire.Text(message, "type");
        if (connection.Version == ProtocolVersion.Version1)
        {
            if (type == null)
            {
                OnReply(connection, message);
            }

            return;
        }

        switch (type)
        {
            case "reply":
                OnReply(connection, message);
                break;
            case "event":
                OnEvent(connection, message);
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
        PendingCall? call = null;
        if (id != null)
        {
            connection.Pending.TryGetValue(id, out call);
        }
        else if (connection.Version == ProtocolVersion.Version1 && connection.Pending.Count == 1)
        {
            // A version-1 error that could not read the request's id belongs to the one call in flight.
            call = connection.Pending.Values.FirstOrDefault();
        }

        call?.Reply(connection, message);
    }

    private void OnEvent(GameConnection connection, JsonElement message)
    {
        switch (Wire.Text(message, "event"))
        {
            case "ping":
                return;
            case "update" when Wire.Text(message, "subscription") is { } id &&
                               _bound.TryGetValue((connection, id), out Subscription? subscription):
                subscription.Update(message);
                break;
            case "subscription_ended" when Wire.Text(message, "subscription") is { } id &&
                                           _bound.TryRemove((connection, id), out Subscription? ended):
                Drop(ended, Wire.Text(message, "reason") ?? "ended");
                break;
            case "world_changed":
                OnWorldChanged(Wire.Child(message, "world"));
                break;
            case "game_state":
                GameState = Wire.Text(message, "game_state") ?? GameState;
                break;
        }

        EventReceived?.Invoke(message);
    }

    // Fires once per new world id, from this event or from a welcome after a reconnect.
    private void OnWorldChanged(JsonElement? world)
    {
        if (world is not { } changed || Wire.Text(changed, "id") is not { } id)
        {
            return;
        }

        Subscription[] open;
        lock (_gate)
        {
            if (id == _lastWorldId)
            {
                return;
            }

            _lastWorldId = id;
            World = changed;
            open = _subscriptions.Where(subscription => !subscription.IsClosed).ToArray();
        }

        foreach (Subscription subscription in open)
        {
            Drop(subscription, "world_changed");
        }

        WorldChanged?.Invoke(changed);
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

            reconnect = !_disposed && _subscriptions.Any(subscription => !subscription.IsClosed);
        }

        foreach ((GameConnection owner, string id) in _bound.Keys.Where(key => ReferenceEquals(key.Item1, connection)).ToArray())
        {
            if (_bound.TryRemove((owner, id), out Subscription? subscription))
            {
                subscription.Detach();
            }
        }

        if (reconnect && Interlocked.Exchange(ref _reconnecting, 1) == 0)
        {
            _ = Task.Run(ReconnectAsync);
        }
    }

    // While subscriptions are open, reopens a broken connection at once, then with backoff from 100 ms doubling to 5 s.
    private async Task ReconnectAsync()
    {
        TimeSpan backoff = FirstBackoff;
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_disposed || !_subscriptions.Any(subscription => !subscription.IsClosed) || _connection is { Alive: true })
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

    // ---- subscriptions --------------------------------------------------------------------------------------------

    // The subscribe reply is bound to its subscription on the reader, before the caller wakes, so an update sent
    // straight after the reply is not lost.
    private Task<CallOutcome> SubscribeOnceAsync(Subscription subscription, CancellationToken cancellation) =>
        CallCoreAsync("subscribe", subscription.Request, null, null, (connection, reply) =>
        {
            if (reply.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True &&
                Wire.Child(reply, "result") is { } result && Wire.Text(result, "subscription") is { } id)
            {
                subscription.Bind(id, result);
                _bound[(connection, id)] = subscription;
            }
        }, cancellation);

    private async Task ResubscribeAsync(Subscription subscription)
    {
        CallOutcome outcome = await SubscribeOnceAsync(subscription, CancellationToken.None).ConfigureAwait(false);
        if (outcome is not CallOutcome.Answered)
        {
            Drop(subscription, "refused on resubscribe: " + outcome.Match(
                answered => "answered",
                refused => refused.Code,
                noAnswer => noAnswer.Message));
        }
    }

    internal async Task CloseAsync(Subscription subscription)
    {
        string? id = subscription.ServerId;
        Drop(subscription, "closed");
        if (id != null && _connection is { Alive: true })
        {
            await CallAsync("unsubscribe", JsonSerializer.SerializeToElement(new { subscription = id })).ConfigureAwait(false);
        }
    }

    private void Drop(Subscription subscription, string reason)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }

        foreach ((GameConnection, string) key in _bound.Where(pair => ReferenceEquals(pair.Value, subscription)).Select(pair => pair.Key).ToArray())
        {
            _bound.TryRemove(key, out _);
        }

        subscription.End(reason);
    }

    /// <summary>How one try of a call on one connection ended.</summary>
    private abstract record Attempt
    {
        private Attempt()
        {
        }

        internal sealed record Done(CallOutcome Outcome) : Attempt;

        internal sealed record NotWritten : Attempt;

        internal sealed record TimedOut : Attempt;

        internal sealed record Broken : Attempt;
    }
}
