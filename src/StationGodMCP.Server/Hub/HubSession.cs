using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// One caller of the hub protocol (docs/architecture/hub.md, The hub protocol) over a pair of streams: the --hub
/// program on stdio, or a run's worker on its pipes. The hub is the server: hello is answered with the mod's last
/// welcome as the link saw it (the caller's client_id, features plus pipeline); a mod method is passed to the mod on
/// the caller's mapped stream and its reply passed back under the caller's id; subscriptions are relayed one for one,
/// their events passed through under the caller's subscription id; verb goes through the VerbPipeline on the session's
/// face; ref, more, wait and hold go to the session's IPipelineExtras; run_analysis and run_end to its IRunChannel.
///
/// Every caller stream, 0 included, maps to a fresh hub stream, never the MCP client's stream 0, so callers never share
/// a stream's order, cursors or held restores. RunAsync reads until the input ends, bye, or a protocol error; the
/// creator then calls CloseAsync, which cancels the calls in flight, ends the relayed subscriptions and ends the hub
/// streams the session made.
/// </summary>
public sealed class HubSession
{
    internal const string GameUnreachable = "game_unreachable";

    private const long LargestStream = 1L << 53;
    private const int LineStartLength = 80;

    private static readonly string[] HelloKeys = ["type", "protocol", "client", "features", "resume"];
    private static readonly string[] CallKeys =
        ["type", "id", "method", "params", "shape", "deadline_ms", "stream", "rename", "world"];
    private static readonly string[] CancelKeys = ["type", "id"];
    private static readonly string[] StreamKeys = ["type", "stream"];
    private static readonly string[] CursorKeys = ["type", "cursor_id"];
    private static readonly string[] TypeOnly = ["type"];
    private static readonly string[] VerbKeys = ["verb", "what", "args", "line"];
    private static readonly string[] AnalysisKeys = ["type", "literals", "while_lines", "writes", "holds", "syntax_error"];
    private static readonly string[] RunEndKeys = ["type", "state", "returned", "printed", "error"];
    private static readonly string[] PipelineMethods = ["verb", "ref", "more", "wait", "hold"];
    private const string NotAllowed = "not_allowed";
    private const string PipelineOnly = "a run's session calls verb, ref, more, wait and hold; mod methods go through verb";
    private static readonly JsonElement NoParameters = HubLines.Element("{}");
    private static readonly JsonElement SupportedVersions = HubLines.Element(HubLines.Supported);
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);
    private static int _sessions;

    private readonly IHubLink _link;
    private readonly VerbPipeline _pipeline;
    private readonly HubSessionSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<long, long> _streams = new();
    private readonly HashSet<long> _endedStreams = new();
    private readonly List<long> _ownStreams = new();
    private readonly Dictionary<string, Relay> _relays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _callerSubscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cursors = new(StringComparer.Ordinal);
    private List<JsonElement>? _early;
    private HubOutput? _output;
    private int _subscribing;
    private int _lastSubscription;
    private bool _welcomed;
    private bool _sessionEnded;
    private bool _analysed;
    private bool _runEnded;
    private bool _closing;

    public HubSession(IHubLink link, VerbPipeline pipeline, HubSessionSettings settings)
    {
        _link = link;
        _pipeline = pipeline;
        _settings = settings;
        if (settings.MainStream is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "A session's main stream is a hub stream from 1.");
        }

        ClientId = settings.ClientId ?? "hub-" + Interlocked.Increment(ref _sessions).ToString(CultureInfo.InvariantCulture);
        if (settings.MainStream is { } given)
        {
            MainStream = given;
        }
        else
        {
            MainStream = link.NewStream();
            _ownStreams.Add(MainStream);
        }

        _streams[0] = MainStream;
    }

    /// <summary>The hub's name for the caller: welcome.client_id.</summary>
    public string ClientId { get; }

    /// <summary>The hub stream the caller's stream 0 maps to.</summary>
    public long MainStream { get; }

    /// <summary>True once hello was answered: the caller started speaking the protocol.</summary>
    public bool Welcomed
    {
        get
        {
            lock (_gate)
            {
                return _welcomed;
            }
        }
    }

    /// <summary>True once the caller sent session_end.</summary>
    public bool SessionEndSent
    {
        get
        {
            lock (_gate)
            {
                return _sessionEnded;
            }
        }
    }

    /// <summary>Serves the caller until its input ends, it says bye, or it breaks the protocol; then call CloseAsync.</summary>
    public async Task<HubSessionEnd> RunAsync(Stream input, Stream output, CancellationToken cancellation)
    {
        _output = new HubOutput(output);
        _link.EventReceived += OnEvent;
        using StreamReader reader = new(input, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                line = null;
            }

            if (line == null)
            {
                return HubSessionEnd.InputEnded;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            HubSessionEnd? end;
            try
            {
                end = await HandleAsync(line, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return HubSessionEnd.InputEnded;
            }
            catch (Exception exception)
            {
                // A fault of the hub's own (a run channel's, a link's) must not end the caller's session.
                Send(HubLines.Error(null, "internal_error", $"The hub failed on a message: {exception.Message}"));
                end = null;
            }

            if (end != null)
            {
                return end.Value;
            }
        }
    }

    /// <summary>
    /// Ends the session: calls still in flight are cancelled (answered cancelled), each relayed subscription is
    /// unsubscribed, the hub streams the session made are ended, and the lines queued are written out.
    /// </summary>
    public async Task CloseAsync()
    {
        List<Task> running = [];
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            foreach (InFlight call in _inFlight.Values)
            {
                call.Cancellation.Cancel();
                running.Add(call.Finished.Task);
            }
        }

        _link.EventReceived -= OnEvent;
        await Task.WhenAll(running).ConfigureAwait(false);

        // A subscribe answered while the calls wound down is registered by now, so it is ended here too.
        List<Relay> relays;
        List<long> streams;
        lock (_gate)
        {
            relays = [.. _relays.Values];
            _relays.Clear();
            _callerSubscriptions.Clear();
            streams = [.. _ownStreams];
            _ownStreams.Clear();
        }

        using (CancellationTokenSource bound = new(CloseWait))
        {
            foreach (Relay relay in relays)
            {
                await UnsubscribeQuietlyAsync(relay, bound.Token).ConfigureAwait(false);
            }
        }

        foreach (long stream in streams)
        {
            await _link.EndStreamAsync(stream).ConfigureAwait(false);
        }

        if (_output != null)
        {
            await _output.CompleteAsync().ConfigureAwait(false);
        }
    }

    // ---- messages -------------------------------------------------------------------------------------------------

    // One line; null to go on reading, else why the session stops.
    private async Task<HubSessionEnd?> HandleAsync(string line, CancellationToken cancellation)
    {
        JsonElement message;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Broken(null, "The line is not JSON.", line);
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return Broken(null, "A message is a JSON object.", line);
        }

        string? type = Text(message, "type");
        if (!_welcomed)
        {
            return type == "hello"
                ? await HelloAsync(message, line, cancellation).ConfigureAwait(false)
                : Broken(IdOf(message), "The first message must be hello.", line);
        }

        switch (type)
        {
            case "call":
                return Call(message, line);
            case "cancel":
                return Cancel(message, line);
            case "stream_end" or "release" or "cursor_close" when !_settings.PassThrough:
                Send(HubLines.Error(null, NotAllowed, PipelineOnly));
                return null;
            case "stream_end":
                return await StreamEndAsync(message, line).ConfigureAwait(false);
            case "release":
                return await ReleaseAsync(message, line).ConfigureAwait(false);
            case "cursor_close":
                return await CursorCloseAsync(message, line).ConfigureAwait(false);
            case "session_end" when _settings.RunChannel != null:
                // A worker ends its run with run_end; the link's session is the hub's alone.
                Send(HubLines.Error(null, NotAllowed, "A run's session ends with run_end, not session_end."));
                return null;
            case "session_end":
                return await SessionEndAsync(message, line).ConfigureAwait(false);
            case "bye":
                return KeyProblem(message, TypeOnly) is { } byeProblem ? Broken(null, byeProblem, line) : HubSessionEnd.Bye;
            case "run_analysis":
                return await RunAnalysisAsync(message, line).ConfigureAwait(false);
            case "run_end":
                return await RunEndAsync(message, line).ConfigureAwait(false);
            case "hello":
                return Broken(null, "hello was answered already; a session says hello once.", line);
            default:
                return Broken(IdOf(message), $"Unknown message type '{type}'.", line);
        }
    }

    private async Task<HubSessionEnd?> HelloAsync(JsonElement message, string line, CancellationToken cancellation)
    {
        if (KeyProblem(message, HelloKeys) is { } problem)
        {
            return Broken(null, problem, line);
        }

        if (!OffersVersion2(message))
        {
            Send(HubLines.Error(null, "unsupported_protocol", "This hub speaks protocol version 2 only.", SupportedVersions));
            return HubSessionEnd.ProtocolError;
        }

        bool reachable;
        try
        {
            reachable = await _link.ConnectAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reachable = false;
        }

        lock (_gate)
        {
            _welcomed = true;
            Send(HubLines.Line(Welcome(reachable)));
        }

        return null;
    }

    // The mod's last welcome as the link saw it, with the caller's client_id, features plus pipeline, and no resume or
    // restores (they are the link's own). Unreachable: game_state Unreachable, from the last welcome if there was one.
    private JsonObject Welcome(bool reachable)
    {
        JsonObject welcome = _link.Welcome is { ValueKind: JsonValueKind.Object } seen
            ? JsonNode.Parse(seen.GetRawText())!.AsObject()
            : new JsonObject { ["type"] = "welcome", ["client"] = "anonymous" };
        welcome["type"] = "welcome";
        welcome["protocol"] = 2;
        welcome["client_id"] = ClientId;
        welcome.Remove("resume");
        welcome.Remove("restores");
        if (!reachable)
        {
            if (welcome["server"] is not JsonObject server)
            {
                server = new JsonObject();
                welcome["server"] = server;
            }

            server["game_state"] = "Unreachable";
        }

        JsonArray features = welcome["features"] as JsonArray ?? new JsonArray();
        welcome["features"] = features;
        bool listed = false;
        foreach (JsonNode? feature in features)
        {
            listed |= feature is JsonValue value && value.TryGetValue(out string? name) && name == "pipeline";
        }

        if (!listed)
        {
            features.Add("pipeline");
        }

        if (_settings.Run != null)
        {
            welcome["run"] = _settings.Run.DeepClone();
        }

        return welcome;
    }

    private HubSessionEnd? Call(JsonElement message, string line)
    {
        if (Text(message, "id") is not { Length: >= 1 and <= 64 } id)
        {
            return Broken(null, "A call needs an id: a string of 1 to 64 characters.", line);
        }

        if (KeyProblem(message, CallKeys, out bool repeated) is { } problem)
        {
            // An unknown key is a protocol_error; a key given twice is invalid_argument (protocol.md, Framing).
            return repeated ? Refuse(id, "invalid_argument", problem) : Broken(id, problem, line);
        }

        if (Text(message, "method") is not { Length: > 0 } method)
        {
            return Refuse(id, "invalid_argument", "A call needs a method: a string.");
        }

        JsonElement parameters = NoParameters;
        if (Child(message, "params") is { } given)
        {
            if (given.ValueKind != JsonValueKind.Object)
            {
                return Refuse(id, "invalid_argument", "params must be an object.");
            }

            parameters = given;
        }

        if (!_settings.PassThrough && Array.IndexOf(PipelineMethods, method) < 0)
        {
            return Refuse(id, NotAllowed, PipelineOnly);
        }

        if (!StreamOf(message, minimum: 0, out long stream))
        {
            return Refuse(id, "invalid_argument", "stream must be a whole number from 0 to 2^53.");
        }

        if (_settings.RunChannel != null)
        {
            // Everything a run does goes on its own stream: its costs, order and held restores are the run's.
            if (stream != 0)
            {
                return Refuse(id, "invalid_argument", "A run's calls go on stream 0 (the run's stream).");
            }

            bool analysed;
            lock (_gate)
            {
                analysed = _analysed;
            }

            if (!analysed)
            {
                return Broken(id, "run_analysis comes before a run's first call.", line);
            }
        }

        int? deadline = null;
        if (Child(message, "deadline_ms") is { } deadlineGiven)
        {
            if (deadlineGiven.ValueKind != JsonValueKind.Number || !deadlineGiven.TryGetInt32(out int ms) ||
                ms is < 100 or > 600000)
            {
                return Refuse(id, "invalid_argument", "deadline_ms must be a whole number from 100 to 600000.");
            }

            deadline = ms;
        }

        if (Child(message, "world") is { } world && world.ValueKind != JsonValueKind.String)
        {
            return Refuse(id, "invalid_argument", "world must be a world id: a string.");
        }

        InFlight call;
        long hubStream;
        lock (_gate)
        {
            if (_closing)
            {
                return null;
            }

            if (_runEnded || _sessionEnded)
            {
                Send(HubLines.Error(id, "session_ended", _runEnded
                    ? "The run has ended; nothing this session sends runs now."
                    : "This session sent session_end; nothing it sends runs now."));
                return null;
            }

            if (_endedStreams.Contains(stream))
            {
                Send(HubLines.Error(id, "stream_ended",
                    $"Stream {stream} was ended with stream_end; streams are never reused.",
                    HubLines.Element("{\"stream\":" + stream.ToString(CultureInfo.InvariantCulture) + "}")));
                return null;
            }

            if (_inFlight.ContainsKey(id))
            {
                Send(HubLines.Error(id, "duplicate_id", $"A call with id '{id}' is still in flight; nothing ran."));
                return null;
            }

            hubStream = HubStream(stream);
            call = new InFlight();
            _inFlight[id] = call;
        }

        // Started here, on the reader, so calls reach the link in the order they came: the mod keeps the order on a
        // stream only as the calls arrive. A call still in flight when run_end comes is late, and still answered.
        _ = RunCallAsync(id, method, parameters, message, deadline, hubStream, call);
        return null;
    }

    private async Task RunCallAsync(string id, string method, JsonElement parameters, JsonElement message, int? deadline,
        long hubStream, InFlight inFlight)
    {
        CancellationToken cancellation = inFlight.Cancellation.Token;
        try
        {
            switch (method)
            {
                case "verb":
                    VerbReply verb = await VerbAsync(parameters, hubStream, cancellation).ConfigureAwait(false);
                    Answer(id, HubLines.Reply(id, verb));
                    break;
                case "ref":
                case "more":
                case "wait":
                case "hold":
                    VerbReply extra = await ExtrasAsync(method, parameters, hubStream, cancellation).ConfigureAwait(false);
                    Answer(id, HubLines.Reply(id, extra));
                    break;
                case "subscribe":
                    await SubscribeAsync(id, parameters, deadline, hubStream, cancellation).ConfigureAwait(false);
                    break;
                case "unsubscribe":
                    await UnsubscribeAsync(id, parameters, cancellation).ConfigureAwait(false);
                    break;
                default:
                    string passed = await PassAsync(id, method, parameters, message, deadline, hubStream, cancellation)
                        .ConfigureAwait(false);
                    Answer(id, passed);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Answer(id, HubLines.Error(id, "cancelled",
                $"{method} was cancelled; if it had reached the game it may have run: read the state back before " +
                "repeating a change.",
                maybeRan: true));
        }
        catch (Exception exception)
        {
            Answer(id, HubLines.Error(id, "internal_error", $"The hub failed on {method}: {exception.Message}"));
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(id, out InFlight? call) && call == inFlight)
                {
                    _inFlight.Remove(id);
                }
            }

            inFlight.Finished.TrySetResult();
        }
    }

    // The id is free again before its reply goes, so a caller may reuse it as soon as it reads the reply.
    private void Answer(string id, string line)
    {
        lock (_gate)
        {
            _inFlight.Remove(id);
            Send(line);
        }
    }

    private async Task<string> PassAsync(string id, string method, JsonElement parameters, JsonElement message,
        int? deadline, long hubStream, CancellationToken cancellation)
    {
        if (Text(message, "world") is { } meant && _link.WorldId is { } current && meant != current)
        {
            return HubLines.Error(id, "world_changed",
                $"The game is in another world than the one this call names; {method} was not sent, because the ids it " +
                "carries may name other things now.",
                HubLines.Element("{\"world\":{\"id\":" + JsonSerializer.Serialize(current) + "}}"));
        }

        GameCall call = new(method, parameters, hubStream)
        {
            Shape = Child(message, "shape") is { ValueKind: JsonValueKind.Object } shape ? shape : null,
            Rename = Child(message, "rename") is { ValueKind: JsonValueKind.Object } rename ? rename : null,
            DeadlineMs = deadline
        };
        GameReply reply = await _link.CallAsync(call, cancellation).ConfigureAwait(false);
        if (reply is GameReply.Ok { Result.ValueKind: JsonValueKind.Object } ok && Text(ok.Result, "cursor_id") is { } cursor)
        {
            lock (_gate)
            {
                _cursors.Add(cursor);
            }
        }

        return HubLines.Reply(id, reply);
    }

    private Task<VerbReply> VerbAsync(JsonElement parameters, long hubStream, CancellationToken cancellation)
    {
        if (KeyProblem(parameters, VerbKeys) is { } problem)
        {
            return Refused("verb takes verb, what, args and line. " + problem);
        }

        if (Text(parameters, "verb") is not { Length: > 0 } verb)
        {
            return Refused("verb needs verb: the verb's name.");
        }

        string? what = null;
        if (Child(parameters, "what") is { } whatGiven)
        {
            if (whatGiven.ValueKind != JsonValueKind.String)
            {
                return Refused("what must be a string.");
            }

            what = whatGiven.GetString();
        }

        JsonObject args = new();
        if (Child(parameters, "args") is { } argsGiven)
        {
            if (argsGiven.ValueKind != JsonValueKind.Object)
            {
                return Refused("args must be an object of the verb's options.");
            }

            args = JsonObject.Create(argsGiven)!;
        }

        if (!LineOf(parameters, out int? line))
        {
            return Refused("line must be a code line: a whole number from 1.");
        }

        VerbCall call = new(verb, what, args, _settings.Face, hubStream, line);
        return _settings.Verbs != null
            ? _settings.Verbs.VerbAsync(call, cancellation)
            : _pipeline.CallAsync(call, cancellation);
    }

    private Task<VerbReply> ExtrasAsync(string method, JsonElement parameters, long hubStream, CancellationToken cancellation)
    {
        if (!LineOf(parameters, out int? line))
        {
            return Refused("line must be a code line: a whole number from 1.");
        }

        PipelineRequest request = new(method, JsonObject.Create(parameters)!, _settings.Face, hubStream, ClientId, line);
        IPipelineExtras extras = _settings.Extras;
        return method switch
        {
            "ref" => extras.RefAsync(request, cancellation),
            "more" => extras.MoreAsync(request, cancellation),
            "wait" => extras.WaitAsync(request, cancellation),
            _ => extras.HoldAsync(request, cancellation)
        };
    }

    // ---- subscriptions --------------------------------------------------------------------------------------------

    // One mod subscription per caller subscription, under an id of the session's own (s1, s2, ...), so a caller can
    // neither see nor end another caller's subscription on the shared connection.
    private async Task SubscribeAsync(string id, JsonElement parameters, int? deadline, long hubStream,
        CancellationToken cancellation)
    {
        lock (_gate)
        {
            _subscribing++;
        }

        try
        {
            GameReply reply = await _link.CallAsync(new GameCall("subscribe", parameters, hubStream) { DeadlineMs = deadline },
                cancellation).ConfigureAwait(false);
            if (reply is not GameReply.Ok { Result.ValueKind: JsonValueKind.Object } ok ||
                Text(ok.Result, "subscription") is not { } modId)
            {
                Answer(id, HubLines.Reply(id, reply));
                return;
            }

            lock (_gate)
            {
                _lastSubscription++;
                string callerId = "s" + _lastSubscription.ToString(CultureInfo.InvariantCulture);
                _relays[callerId] = new Relay(modId, hubStream);
                _callerSubscriptions[modId] = callerId;
                Answer(id, HubLines.Ok(id, HubLines.Element(HubLines.WithSubscription(ok.Result, callerId)), ok));
                FlushEarly(modId, callerId);
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribing--;
                if (_subscribing == 0)
                {
                    _early = null;
                }
            }
        }
    }

    // Events that came for the subscription before its reply was read, sent after the reply. Under the gate.
    private void FlushEarly(string modId, string callerId)
    {
        if (_early == null)
        {
            return;
        }

        foreach (JsonElement early in _early)
        {
            if (Text(early, "subscription") == modId)
            {
                Send(HubLines.WithSubscription(early, callerId));
            }
        }
    }

    private async Task UnsubscribeAsync(string id, JsonElement parameters, CancellationToken cancellation)
    {
        string? callerId = Text(parameters, "subscription");
        Relay? relay = null;
        lock (_gate)
        {
            if (callerId != null)
            {
                _relays.TryGetValue(callerId, out relay);
            }
        }

        if (relay == null)
        {
            Answer(id, HubLines.Error(id, "unknown_subscription", $"This session has no subscription '{callerId}'."));
            return;
        }

        GameReply reply = await _link.CallAsync(
            new GameCall("unsubscribe", HubLines.Parameters("subscription", relay.ModId), relay.Stream), cancellation)
            .ConfigureAwait(false);
        if (reply is GameReply.Ok or GameReply.Refused { Code: "unknown_subscription" })
        {
            lock (_gate)
            {
                Forget(callerId!, relay.ModId);
            }
        }

        Answer(id, reply is GameReply.Ok { Result.ValueKind: JsonValueKind.Object } ok
            ? HubLines.Ok(id, HubLines.Element(HubLines.WithSubscription(ok.Result, callerId!)), ok)
            : HubLines.Reply(id, reply));
    }

    private async Task UnsubscribeQuietlyAsync(Relay relay, CancellationToken cancellation)
    {
        try
        {
            await _link.CallAsync(new GameCall("unsubscribe", HubLines.Parameters("subscription", relay.ModId), relay.Stream),
                cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stream's end below ends it on the mod's side too.
        }
    }

    // Raised on the link's reader for every event: a relayed subscription's goes to the caller under its id, a world
    // change to everyone; the rest (the link's own topics, other sessions' subscriptions) are not this caller's.
    private void OnEvent(JsonElement message)
    {
        string? modId = Text(message, "subscription");
        lock (_gate)
        {
            if (_closing || !_welcomed)
            {
                return;
            }

            if (modId == null)
            {
                if (Text(message, "event") == "world_changed")
                {
                    Send(message.GetRawText());
                }

                return;
            }

            if (_callerSubscriptions.TryGetValue(modId, out string? callerId))
            {
                Send(HubLines.WithSubscription(message, callerId));
                if (Text(message, "event") == "subscription_ended")
                {
                    Forget(callerId, modId);
                }
            }
            else if (_subscribing > 0)
            {
                (_early ??= []).Add(message);
            }
        }
    }

    // Under the gate.
    private void Forget(string callerId, string modId)
    {
        _relays.Remove(callerId);
        _callerSubscriptions.Remove(modId);
    }

    // ---- messages without a reply ---------------------------------------------------------------------------------

    private HubSessionEnd? Cancel(JsonElement message, string line)
    {
        if (KeyProblem(message, CancelKeys) is { } problem)
        {
            return Broken(null, problem, line);
        }

        lock (_gate)
        {
            if (Text(message, "id") is { } id && _inFlight.TryGetValue(id, out InFlight? call))
            {
                call.Cancellation.Cancel();
            }
        }

        return null;
    }

    // Ends the caller's stream n: its mapped hub stream ends on the mod (its calls not started answer cancelled, its
    // subscriptions end silently), and a later call on n is refused stream_ended, as the mod would.
    private async Task<HubSessionEnd?> StreamEndAsync(JsonElement message, string line)
    {
        if (KeyProblem(message, StreamKeys) is { } problem)
        {
            return Broken(null, problem, line);
        }

        if (!StreamOf(message, minimum: 1, out long stream) || Child(message, "stream") == null)
        {
            Send(HubLines.Error(null, "invalid_argument", "stream_end needs a stream from 1 to 2^53; stream 0 never ends."));
            return null;
        }

        long hubStream;
        lock (_gate)
        {
            if (!_endedStreams.Add(stream) || !_streams.TryGetValue(stream, out hubStream))
            {
                return null;
            }

            _ownStreams.Remove(hubStream);
            ForgetStream(hubStream);
        }

        await _link.EndStreamAsync(hubStream).ConfigureAwait(false);
        return null;
    }

    // Under the gate: the mod ends a stream's subscriptions without an event.
    private void ForgetStream(long hubStream)
    {
        List<string>? gone = null;
        foreach (KeyValuePair<string, Relay> relay in _relays)
        {
            if (relay.Value.Stream == hubStream)
            {
                (gone ??= []).Add(relay.Key);
            }
        }

        if (gone == null)
        {
            return;
        }

        foreach (string callerId in gone)
        {
            Forget(callerId, _relays[callerId].ModId);
        }
    }

    private async Task<HubSessionEnd?> ReleaseAsync(JsonElement message, string line)
    {
        if (KeyProblem(message, StreamKeys) is { } problem)
        {
            return Broken(null, problem, line);
        }

        if (!StreamOf(message, minimum: 0, out long stream) || Child(message, "stream") == null)
        {
            Send(HubLines.Error(null, "invalid_argument", "release needs a stream from 0 to 2^53."));
            return null;
        }

        long hubStream;
        lock (_gate)
        {
            // A stream never used has made no jobs, so it holds no pins.
            if (!_streams.TryGetValue(stream, out hubStream))
            {
                return null;
            }
        }

        await _link.ReleaseAsync(hubStream).ConfigureAwait(false);
        return null;
    }

    // Only a cursor this session's replies carried is closed: the connection is shared, so another caller's cursor
    // would close as well.
    private async Task<HubSessionEnd?> CursorCloseAsync(JsonElement message, string line)
    {
        if (KeyProblem(message, CursorKeys) is { } problem)
        {
            return Broken(null, problem, line);
        }

        if (Text(message, "cursor_id") is not { } cursor)
        {
            Send(HubLines.Error(null, "invalid_argument", "cursor_close needs cursor_id: a string."));
            return null;
        }

        bool ours;
        lock (_gate)
        {
            ours = _cursors.Remove(cursor);
        }

        if (ours)
        {
            await _link.CursorCloseAsync(cursor).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<HubSessionEnd?> SessionEndAsync(JsonElement message, string line)
    {
        if (KeyProblem(message, TypeOnly) is { } problem)
        {
            return Broken(null, problem, line);
        }

        lock (_gate)
        {
            _sessionEnded = true;
        }

        if (_settings.EndsLinkSession)
        {
            await _link.SessionEndAsync().ConfigureAwait(false);
        }

        return null;
    }

    private async Task<HubSessionEnd?> RunAnalysisAsync(JsonElement message, string line)
    {
        if (_settings.RunChannel is not { } channel)
        {
            return Broken(null, "run_analysis belongs to a run's session; this session serves no run.", line);
        }

        if (KeyProblem(message, AnalysisKeys) is { } problem)
        {
            return Broken(null, problem, line);
        }

        lock (_gate)
        {
            if (_analysed)
            {
                return Broken(null, "The hub takes only the first run_analysis of a session.", line);
            }

            _analysed = true;
        }

        await channel.AnalysisAsync(message).ConfigureAwait(false);
        return null;
    }

    private async Task<HubSessionEnd?> RunEndAsync(JsonElement message, string line)
    {
        if (_settings.RunChannel is not { } channel)
        {
            return Broken(null, "run_end belongs to a run's session; this session serves no run.", line);
        }

        if (KeyProblem(message, RunEndKeys) is { } problem)
        {
            // The run still hears of it, so it ends failed with the reason rather than as a broken session.
            lock (_gate)
            {
                _runEnded = true;
            }

            await channel.EndedAsync(message).ConfigureAwait(false);
            return Broken(null, problem, line);
        }

        lock (_gate)
        {
            if (_runEnded)
            {
                return Broken(null, "run_end was sent already.", line);
            }

            _runEnded = true;
        }

        await channel.EndedAsync(message).ConfigureAwait(false);
        return null;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    // Under the gate: the hub stream for the caller's stream, a fresh one the first time.
    private long HubStream(long callerStream)
    {
        if (!_streams.TryGetValue(callerStream, out long hubStream))
        {
            hubStream = _link.NewStream();
            _streams[callerStream] = hubStream;
            _ownStreams.Add(hubStream);
        }

        return hubStream;
    }

    private void Send(string line) => _output?.Send(line);

    private HubSessionEnd? Refuse(string id, string code, string message)
    {
        Send(HubLines.Error(id, code, message));
        return null;
    }

    private static Task<VerbReply> Refused(string message) =>
        Task.FromResult<VerbReply>(new VerbReply.Failed(VerbFailure.InvalidArgument, message));

    // protocol_error, then the session stops (protocol.md, Errors).
    private HubSessionEnd Broken(string? id, string message, string line)
    {
        string start = line.Length <= LineStartLength ? line : line[..LineStartLength];
        Send(HubLines.Error(id, "protocol_error", message,
            HubLines.Element("{\"line_start\":" + JsonSerializer.Serialize(start) + "}")));
        return HubSessionEnd.ProtocolError;
    }

    private static string? KeyProblem(JsonElement message, string[] allowed) => KeyProblem(message, allowed, out _);

    // An unknown key, or a key given twice (repeated), or null. Few keys each, so no set is built.
    private static string? KeyProblem(JsonElement message, string[] allowed, out bool repeated)
    {
        repeated = false;
        int seen = 0;
        foreach (JsonProperty property in message.EnumerateObject())
        {
            int index = Array.IndexOf(allowed, property.Name);
            if (index < 0)
            {
                return $"Unknown key '{property.Name}'.";
            }

            if ((seen & (1 << index)) != 0)
            {
                repeated = true;
                return $"Key '{property.Name}' is given twice; name each key once.";
            }

            seen |= 1 << index;
        }

        return null;
    }

    private static bool OffersVersion2(JsonElement hello)
    {
        if (Child(hello, "protocol") is not { ValueKind: JsonValueKind.Array } versions)
        {
            return false;
        }

        foreach (JsonElement version in versions.EnumerateArray())
        {
            if (version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int major) && major == 2)
            {
                return true;
            }
        }

        return false;
    }

    // stream: absent or null is 0 (for a call); else a whole number from minimum to 2^53, a number without a fraction
    // counting.
    private static bool StreamOf(JsonElement message, long minimum, out long stream)
    {
        stream = 0;
        if (Child(message, "stream") is not { } given)
        {
            return true;
        }

        if (given.ValueKind != JsonValueKind.Number || !given.TryGetDouble(out double value) || value != Math.Floor(value) ||
            value < minimum || value > LargestStream)
        {
            return false;
        }

        stream = (long)value;
        return true;
    }

    private static bool LineOf(JsonElement parameters, out int? line)
    {
        line = null;
        if (Child(parameters, "line") is not { } given)
        {
            return true;
        }

        if (given.ValueKind != JsonValueKind.Number || !given.TryGetInt32(out int number) || number < 1)
        {
            return false;
        }

        line = number;
        return true;
    }

    private static string? IdOf(JsonElement message) => Text(message, "id");

    private static string? Text(JsonElement message, string key) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? Child(JsonElement message, string key) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    /// <summary>A call in flight: what cancels it, and its task once started.</summary>
    private sealed class InFlight
    {
        internal CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Set when the call's task has finished, so CloseAsync can wait for it.</summary>
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A relayed subscription: the mod's id and the hub stream it was made on.</summary>
    private sealed class Relay(string modId, long stream)
    {
        internal string ModId { get; } = modId;

        internal long Stream { get; } = stream;
    }
}
