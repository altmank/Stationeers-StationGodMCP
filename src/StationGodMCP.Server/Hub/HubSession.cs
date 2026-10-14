using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// One caller of the hub protocol (docs/architecture/hub.md, The hub protocol) over a pair of streams: the --hub
/// program on stdio, or a run's worker on its pipes. The hub is the server: hello is answered with the mod's last
/// welcome as the link saw it (the caller's client_id, features plus pipeline); a read-only mod method is passed to the
/// mod on the caller's mapped stream and its reply passed back under the caller's id (writes go through verb, where the
/// write rule applies); subscriptions are relayed one for one (HubSession.Relay.cs); verb goes through the
/// VerbPipeline on the session's face; ref, more, wait and hold go to the session's IPipelineExtras; run_analysis and
/// run_end to its IRunChannel.
///
/// Every caller stream, 0 included, maps to a fresh hub stream, never the MCP client's stream 0, so callers never share
/// a stream's order, cursors or held restores. RunAsync reads (lines bounded by max_request_bytes) until the input
/// ends, bye, a protocol error, or the caller stops reading; the creator then calls CloseAsync (HubSession.Close.cs).
/// </summary>
public sealed partial class HubSession
{
    internal const string GameUnreachable = "game_unreachable";

    /// <summary>Open caller streams per session; past it a call on a new stream is refused too_many_streams.</summary>
    internal const int MaxStreams = 64;

    private const long LargestStream = 1L << 53;
    private const int LineStartLength = 80;
    private const int DefaultPerStreamLimit = 16;

    private static readonly string[] HelloKeys = ["type", "protocol", "client", "features", "resume"];
    private static readonly string[] CallKeys =
        ["type", "id", "method", "params", "shape", "deadline_ms", "stream", "world"];
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
    private static int _sessions;

    private readonly IHubLink _link;
    private readonly VerbPipeline _pipeline;
    private readonly HubSessionSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<long, long> _streams = new();
    private readonly HashSet<long> _endedStreams = new();
    private readonly HashSet<long> _ownStreams = new();
    private readonly HashSet<long> _carried = new();
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<HubSessionEnd> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HubOutput? _output;
    private int _openStreams;
    private int _inFlightLimit = DefaultPerStreamLimit * MaxStreams;
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
        _openStreams = 1;
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

    /// <summary>How long the oldest queued line may wait for the caller to read before the session ends (the mod's 35 s).</summary>
    internal TimeSpan SlowClientAfter { get; init; } = TimeSpan.FromSeconds(35);

    /// <summary>
    /// Serves the caller until its input ends, it says bye, it breaks the protocol, it sends a line over
    /// max_request_bytes, or its output is lost (it stopped reading, or writing failed); then call CloseAsync.
    /// </summary>
    public async Task<HubSessionEnd> RunAsync(Stream input, Stream output, CancellationToken cancellation)
    {
        _output = new HubOutput(output, SlowClientAfter, end => _stopped.TrySetResult(end));
        _link.EventReceived += OnEvent;
        _link.WorldChanged += OnWorldChanged;
        HubLineReader reader = new(input, _link.MaxRequestBytes);
        while (true)
        {
            reader.Limit = _link.MaxRequestBytes;
            Task<LineRead> reading = reader.ReadAsync(cancellation);
            if (await Task.WhenAny(reading, _stopped.Task).ConfigureAwait(false) != reading)
            {
                // The output is lost: the read still pending is left to end with the input.
                return await _stopped.Task.ConfigureAwait(false);
            }

            LineRead read;
            try
            {
                read = await reading.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return HubSessionEnd.InputEnded;
            }

            switch (read)
            {
                case LineRead.End:
                    return HubSessionEnd.InputEnded;
                case LineRead.TooLong:
                    Send(HubLines.Error(null, "request_too_large",
                        $"A message was longer than {reader.Limit} bytes (welcome limits.max_request_bytes); the session ends.",
                        HubLines.Element("{\"limit\":" + reader.Limit.ToString(CultureInfo.InvariantCulture) + "}")));
                    return HubSessionEnd.RequestTooLarge;
            }

            string line = ((LineRead.Text)read).Line;
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
                return StreamEnd(message, line);
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
            _inFlightLimit = PerStreamLimit(_link.Welcome) * MaxStreams;
            Send(HubLines.Line(Welcome(reachable)));
        }

        return null;
    }

    // The mod's last welcome as the link saw it, with the caller's client_id, features plus pipeline, and no resume or
    // restores (they are the link's own; the hub keeps no state for a caller's resume token, so the feature goes too).
    // Unreachable: game_state Unreachable, from the last welcome if there was one.
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

        JsonArray features = new();
        if (welcome["features"] is JsonArray listed)
        {
            foreach (JsonNode? feature in listed)
            {
                if (feature is JsonValue value && value.TryGetValue(out string? name) && name is not ("resume" or "pipeline"))
                {
                    features.Add(name);
                }
            }
        }

        features.Add("pipeline");
        welcome["features"] = features;
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

            if (Admission(id, stream) is { } refusal)
            {
                Send(refusal);
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

    // Under the gate: the session's bounds, so one caller cannot take every stream or call slot of the shared
    // connection. Null when the call may go.
    private string? Admission(string id, long stream)
    {
        if (_inFlight.Count >= _inFlightLimit)
        {
            return HubLines.Error(id, "too_many_in_flight",
                $"This session has {_inFlight.Count} calls in flight, its limit; nothing ran. Wait for a reply first.",
                HubLines.Element("{\"limit\":" + _inFlightLimit.ToString(CultureInfo.InvariantCulture) + ",\"scope\":\"session\"}"));
        }

        if (!_streams.ContainsKey(stream) && _openStreams >= MaxStreams)
        {
            return HubLines.Error(id, "too_many_streams",
                $"This session has {MaxStreams} streams open, its limit; nothing ran. End one with stream_end first.",
                HubLines.Element("{\"limit\":" + MaxStreams.ToString(CultureInfo.InvariantCulture) + "}"));
        }

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

    // A call is answered once: the first answer frees its id (so the caller may reuse it as soon as it reads the
    // reply), a later one finds the id gone and is dropped. A closing session answers nothing: its caller is gone.
    private void Answer(string id, string line)
    {
        lock (_gate)
        {
            if (_inFlight.Remove(id) && !_closing)
            {
                Send(line);
            }
        }
    }

    // A program's pass-through takes only read-class methods with no x-effects (hub.md, Faces): every write goes
    // through verb, where the write rule and the recording apply.
    private async Task<string> PassAsync(string id, string method, JsonElement parameters, JsonElement message,
        int? deadline, long hubStream, CancellationToken cancellation)
    {
        if (PassProblem(id, method, parameters, message) is { } refused)
        {
            return refused;
        }

        GameCall call = new(method, parameters, hubStream)
        {
            Shape = Child(message, "shape"),
            DeadlineMs = deadline
        };
        GameReply reply = await _link.CallAsync(call, cancellation).ConfigureAwait(false);
        KeepCursor(parameters, reply, hubStream);
        return HubLines.Reply(id, reply);
    }

    // A program's pass-through takes only read-class methods (hub.md, Faces): every write goes through verb, where
    // the write rule and the recording apply. Read from the catalogue the mod announced, so what passes is what that
    // mod answers ({catalogue} carries its own protocol methods).
    private string? PassProblem(string id, string method, JsonElement parameters, JsonElement message)
    {
        GameCatalogue catalogue = _link.Catalogue;
        if (!catalogue.Has(method))
        {
            return HubLines.Error(id, "method_not_found", $"No mod method '{method}'.");
        }

        if (!catalogue.ReadsOnly(method, parameters))
        {
            return HubLines.Error(id, "not_allowed",
                $"{method} changes the game; writes go through verb so the write rule applies. Send it as a verb call.");
        }

        if (Child(message, "shape") is { ValueKind: not JsonValueKind.Object })
        {
            return HubLines.Error(id, "invalid_argument", "shape must be an object.");
        }

        if (Text(message, "world") is { } meant)
        {
            if (meant.Length is < 1 or > 64)
            {
                return HubLines.Error(id, "invalid_argument", "world must be a world id: a string of 1 to 64 characters.");
            }

            if (_link.WorldId is { } current && meant != current)
            {
                return HubLines.Error(id, "world_changed",
                    $"The game is in another world than the one this call names; {method} was not sent, because the " +
                    "ids it carries may name other things now.",
                    HubLines.Element("{\"world\":{\"id\":" + JsonSerializer.Serialize(current) + "}}"));
            }
        }

        return null;
    }

    // The walk cursors this session's replies carried, so cursor_close passes only its own; one is forgotten when its
    // walk ends (a page without cursor_id), when it expired, or with its stream.
    private void KeepCursor(JsonElement parameters, GameReply reply, long hubStream)
    {
        string? paged = Text(parameters, "cursor_id");
        string? next = reply is GameReply.Ok { Result.ValueKind: JsonValueKind.Object } ok ? Text(ok.Result, "cursor_id") : null;
        if (paged == null && next == null)
        {
            return;
        }

        lock (_gate)
        {
            if (paged != null && paged != next && reply is GameReply.Ok or GameReply.Refused { Code: "cursor_expired" })
            {
                _cursors.Remove(paged);
            }

            if (next != null)
            {
                _cursors[next] = hubStream;
            }
        }
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
    // subscriptions and cursors end silently), and a later call on n is refused stream_ended, as the mod would.
    private HubSessionEnd? StreamEnd(JsonElement message, string line)
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

            _openStreams--;
            _ownStreams.Remove(hubStream);
            ForgetStream(hubStream);
        }

        // Not awaited: with the game down the link reconnects first, and the caller's next message must not wait.
        _ = _link.EndStreamAsync(hubStream);
        return null;
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

    // Under the gate: the hub stream for the caller's stream, a fresh one the first time; marked as having carried a
    // call, so the close ends it.
    private long HubStream(long callerStream)
    {
        if (!_streams.TryGetValue(callerStream, out long hubStream))
        {
            hubStream = _link.NewStream();
            _streams[callerStream] = hubStream;
            _ownStreams.Add(hubStream);
            _openStreams++;
        }

        _carried.Add(hubStream);
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

    private static int PerStreamLimit(JsonElement? welcome)
    {
        if (Child(welcome ?? default, "limits") is { } limits)
        {
            foreach (string name in (string[])["max_in_flight_per_stream", "max_in_flight"])
            {
                if (Child(limits, name) is { ValueKind: JsonValueKind.Number } limit && limit.TryGetInt32(out int value) &&
                    value > 0)
                {
                    return value;
                }
            }
        }

        return DefaultPerStreamLimit;
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
}
