#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Protocol;

/// <summary>
/// The protocol on one connection: hello and welcome, then calls by id, several in flight, answered in any order and
/// matched by id; cancel; stream_end; cursor_close; session_end; events. The first message must be hello; anything else is a protocol error that
/// closes the connection. Lines are parsed and checked here, on the connection's reader thread; only the methods
/// themselves run on the main thread.
/// <para>
/// Streams (protocol.md, Streams): each call carries a stream, 0 unless it says otherwise. MaxInFlight calls may be in
/// flight on one stream and MaxInFlightPerConnection on the connection, its streams together; ids stay unique per
/// connection. stream_end answers the stream's unstarted calls cancelled here, marks the stream ended (a later call on
/// it is refused stream_ended), and posts the end for the main thread, which drops the stream's waiting calls,
/// subscriptions, sample_logic runs and per-stream stores at the next frame. A slow client goes the same way for every
/// stream before it is closed.
/// </para>
/// <para>
/// Resuming (protocol.md, Resuming): a hello with resume claims the token from ResumeHolds, so the connection's state,
/// its ended streams included, is the token's (Connection.Owner) and outlives it. Such a connection's slow-client close
/// drops its unstarted calls and leaves its streams to the token: held for the window, then ended. A bye ends the
/// token's state when the connection closes.
/// </para>
/// </summary>
internal sealed class CallSession : Session
{
    /// <summary>Calls in flight on one stream (welcome.limits max_in_flight and max_in_flight_per_stream).</summary>
    internal const int MaxInFlight = SchedulerSettings.DefaultMaxInFlight;

    /// <summary>Calls in flight on the connection, every stream together (max_in_flight_per_connection).</summary>
    internal const int MaxInFlightPerConnection = SchedulerSettings.DefaultMaxInFlightPerConnection;

    internal const string StreamEndedCode = "stream_ended";
    internal const int MaxRequestBytes = 4194304;
    internal const string CatalogueMethod = "catalogue";
    internal const string AnonymousClient = "anonymous";

    /// <summary>What welcome lists in features: what this server does beyond the basics.</summary>
    internal static readonly string[] Features =
        { "shape", "shape.paths", "shape.omit", "cancel", "streams", "cursors", "rename" };

    /// <summary>The features with subscriptions, when the server has them.</summary>
    internal static readonly string[] FeaturesWithSubscriptions =
        { "shape", "shape.paths", "shape.omit", "subscriptions", "cancel", "streams", "cursors", "rename" };

    private static readonly string[] FeaturesWithResume = WithResume(Features);

    private static readonly string[] FeaturesWithSubscriptionsAndResume = WithResume(FeaturesWithSubscriptions);

    private readonly Connection _connection;
    private readonly ProtocolHost _host;
    private readonly object _sync = new object();
    private readonly Dictionary<string, ProtocolCall> _inFlight = new Dictionary<string, ProtocolCall>(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _inFlightByStream = new Dictionary<long, int>();
    private EndedStreams _ended = new EndedStreams();
    private ClientMessage.Hello? _hello;
    private bool _welcomed;

    internal CallSession(Connection connection, ProtocolHost host)
    {
        _connection = connection;
        _host = host;
    }

    internal override int? MaximumLineBytes => MaxRequestBytes;

    internal override bool Pings => _welcomed;

    internal override int WriteTimeoutMilliseconds => _host.Settings.SlowClientMilliseconds;

    internal override string Client => AnonymousClient;

    internal override string? Label => _hello?.ClientName;

    internal override int InFlight
    {
        get
        {
            lock (_sync)
            {
                return _inFlight.Count;
            }
        }
    }

    internal override void OnLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        ClientMessage message = ClientMessage.Parse(line);
        // Before hello only a hello may come; a refusal that closes the connection keeps its own reason.
        if (_hello == null && !(message is ClientMessage.Hello) &&
            !(message is ClientMessage.Refused refusal && refusal.Closes))
        {
            Refuse(ClientMessage.Refused.Protocol("The first message must be hello.", line));
            return;
        }

        switch (message)
        {
            case ClientMessage.Refused refused:
                Refuse(refused);
                break;
            case ClientMessage.Hello hello when _hello == null:
                Greet(hello);
                break;
            case ClientMessage.Hello _:
                Refuse(ClientMessage.Refused.Protocol("hello was already answered.", line));
                break;
            case ClientMessage.Call call:
                Accept(call);
                break;
            case ClientMessage.Cancel cancel:
                Cancel(cancel.Id);
                break;
            case ClientMessage.EndOfStream ended:
                EndStream(ended.Stream);
                break;
            case ClientMessage.CursorClose closed:
                _host.CursorClosed(_connection.Owner, closed.CursorId);
                break;
            case ClientMessage.SessionEnd _:
                EndSession();
                break;
            case ClientMessage.Bye _:
                _connection.Goodbye();
                _host.Holds.Goodbye(_connection);
                _connection.EndGracefully();
                break;
        }
    }

    internal override void OnOverflow()
    {
        _connection.Send(Wire.Refusal(null, "request_too_large",
            $"A message longer than {MaxRequestBytes} bytes; the connection is closed.",
            new Dictionary<string, int> { ["limit"] = MaxRequestBytes }));
        _connection.EndGracefully();
    }

    internal override void ShutDown(string reason)
    {
        foreach (ProtocolCall call in Waiting())
        {
            if (call.State.TryDrop())
            {
                call.Deliver(call.ShuttingDownReply(), null);
            }
        }

        _connection.Send(Wire.Line(new GoodbyeView(reason)));
        _connection.EndGracefully();
    }

    /// <summary>
    /// One socket and one writer serve every stream, so a write that timed out cannot be pinned on one stream: every
    /// stream of the connection ends as with stream_end (its unstarted calls dropped, unanswered since the client takes
    /// nothing, and its subscriptions, sample_logic runs and per-stream stores ended), then the connection closes. A
    /// connection holding a resume token only drops its unstarted calls: its streams are the token's, held on close.
    /// </summary>
    internal override void OnSlowClient()
    {
        if (_connection.IsResumable)
        {
            foreach (ProtocolCall call in Waiting())
            {
                call.State.TryDrop();
            }

            return;
        }

        // The client never said bye and takes nothing: an abrupt end, so its held restores apply before its streams end.
        if (!_connection.SaidBye)
        {
            _host.SessionEnded(_connection.Owner);
        }

        EndStreams(StreamEnd.AllOf(_connection.Owner), false);
    }

    internal override void OnWorldChanged(WorldFacts world)
    {
        if (_welcomed)
        {
            _connection.Send(Wire.Line(new WorldChangedView(world)));
        }
    }

    private List<ProtocolCall> Waiting()
    {
        lock (_sync)
        {
            return new List<ProtocolCall>(_inFlight.Values);
        }
    }

    private void Greet(ClientMessage.Hello hello)
    {
        _hello = hello;
        if (!hello.Protocols.Contains(2))
        {
            _connection.Send(Wire.Refusal(null, "unsupported_protocol", "This server speaks protocol 2 only.",
                new Dictionary<string, int[]> { ["supported"] = new[] { 2 } }));
            _connection.EndGracefully();
            return;
        }

        WelcomeResumeView? resume = null;
        RestoreReport? restores = null;
        if (hello.Resume != null)
        {
            bool resumed = false;
            if (_host.Holds.Enabled)
            {
                _ended = _host.Holds.Claim(hello.Resume, _connection, out resumed);
                restores = _host.Restores?.TakeReport(hello.Resume);
            }

            resume = new WelcomeResumeView(hello.Resume, resumed, _host.Holds.WindowSeconds);
        }

        CatalogueFile? file = _host.Catalogue;
        WelcomeView welcome = new WelcomeView(_connection.ClientId, Client,
            new WelcomeServerView(ServerFacts.Current, _connection.Transport),
            new WelcomeCatalogueView(file?.Hash ?? string.Empty, file?.Catalogue.MethodCount ?? 0,
                file?.Catalogue.ProtocolMethodCount ?? 0),
            new WelcomeLimitsView(MaxInFlight, MaxInFlightPerConnection, MaxRequestBytes, _host.Settings.MaxReplyBytes,
                _host.Subscriptions?.Limits),
            FeaturesFor(_host.Subscriptions != null, _host.Holds.Enabled), resume, restores);
        _connection.Send(Wire.Line(welcome));
        _welcomed = true;
    }

    private void Refuse(ClientMessage.Refused refused)
    {
        _connection.Send(Wire.Refusal(refused.Id, refused.Code, refused.Message, refused.Data));
        if (refused.Closes)
        {
            _connection.Send(Wire.Line(new GoodbyeView(refused.Code)));
            _connection.EndGracefully();
        }
    }

    private static string[] FeaturesFor(bool subscriptions, bool resume) =>
        subscriptions
            ? resume ? FeaturesWithSubscriptionsAndResume : FeaturesWithSubscriptions
            : resume ? FeaturesWithResume : Features;

    private static string[] WithResume(string[] features)
    {
        string[] with = new string[features.Length + 1];
        features.CopyTo(with, 0);
        with[features.Length] = ResumeHolds.Feature;
        return with;
    }

    private void Accept(ClientMessage.Call call)
    {
        bool ended;
        lock (_ended)
        {
            ended = _ended.Contains(call.Stream);
        }

        if (ended)
        {
            _connection.Send(Wire.Refusal(call.Id, StreamEndedCode,
                $"Stream {call.Stream} has ended on this connection; it is never used again. Nothing ran.",
                new Dictionary<string, long> { ["stream"] = call.Stream }));
            return;
        }

        if (call.Method == CatalogueMethod)
        {
            AnswerCatalogue(call.Id);
            return;
        }

        CatalogueMethod? method = null;
        _host.Catalogue?.Catalogue.TryGet(call.Method, out method);
        ShapeRequest? shape;
        bool namesChecked = false;
        if (_host.Settings.StrictArguments)
        {
            bool passed;
            using (Vocabulary.Enter(call.Rename))
            {
                passed = Checked(call, method, out shape);
            }

            if (!passed)
            {
                return;
            }

            namesChecked = method != null && method.RefusesUnknownArguments;
        }
        else
        {
            shape = ShapeRequest.Lenient(call.Shape);
        }

        if (method != null)
        {
            shape = ShapeRequest.WithDefaultLimits(shape, method.DefaultLimits);
        }

        CallRequest request = new CallRequest(call.Id, call.Method, call.Params, shape, namesChecked, call.Rename,
            call.World);
        CallProfile profile = method != null
            ? new CallProfile(method.Name, method.ClassAt(call.Params), method.CostAt(call.Params))
            : CallProfiles.Of(null, call.Method, call.Params);
        // A method that runs for a while (x-duration: sample_logic) gets that long added to its deadline.
        int deadlineMs = call.DeadlineMs + (method?.DurationMs(call.Params) ?? 0);
        ProtocolCall queued = new ProtocolCall(request, profile, deadlineMs, _connection,
            new CallContext(_connection.Owner, call.Stream), _hello?.ClientName, Answered);
        lock (_sync)
        {
            _inFlightByStream.TryGetValue(call.Stream, out int onStream);
            if (onStream >= MaxInFlight)
            {
                _connection.Send(Wire.Refusal(call.Id, InFlightLimits.Code, InFlightLimits.StreamMessage(MaxInFlight),
                    InFlightLimits.Data(MaxInFlight, InFlightLimits.StreamScope)));
                return;
            }

            if (_inFlight.Count >= MaxInFlightPerConnection)
            {
                _connection.Send(Wire.Refusal(call.Id, InFlightLimits.Code,
                    InFlightLimits.ConnectionMessage(MaxInFlightPerConnection),
                    InFlightLimits.Data(MaxInFlightPerConnection, InFlightLimits.ConnectionScope)));
                return;
            }

            if (_inFlight.ContainsKey(call.Id))
            {
                _connection.Send(Wire.Refusal(call.Id, "duplicate_id", $"A call with id '{call.Id}' is already in flight."));
                return;
            }

            _inFlight.Add(call.Id, queued);
            _inFlightByStream[call.Stream] = onStream + 1;
        }

        _host.Submit(queued);
    }

    // The full check, before anything is queued: the shape (invalid_shape), then the params against the method's
    // schema (invalid_argument with every problem and its path). A method the catalogue does not have is left to the
    // main thread, which answers method_not_found.
    private bool Checked(ClientMessage.Call call, CatalogueMethod? method, out ShapeRequest? shape)
    {
        List<string> shapeProblems = new List<string>();
        shape = ShapeRequest.Strict(call.Shape, method?.ReplyLists, shapeProblems);
        if (shapeProblems.Count > 0)
        {
            List<Dictionary<string, string>> listed = new List<Dictionary<string, string>>(shapeProblems.Count);
            foreach (string problem in shapeProblems)
            {
                listed.Add(new Dictionary<string, string> { ["path"] = "shape", ["problem"] = problem });
            }

            _connection.Send(Wire.Refusal(call.Id, "invalid_shape", string.Join(" ", shapeProblems),
                new Dictionary<string, object> { ["problems"] = listed }));
            return false;
        }

        if (method == null)
        {
            return true;
        }

        List<SchemaProblem> problems = method.Check(call.Params);
        if (problems.Count == 0)
        {
            return true;
        }

        List<Dictionary<string, string>> found = new List<Dictionary<string, string>>(problems.Count);
        List<string> sentences = new List<string>(problems.Count);
        foreach (SchemaProblem problem in problems)
        {
            found.Add(new Dictionary<string, string>
            {
                ["path"] = Vocabulary.Path(problem.Path), ["problem"] = problem.Problem
            });
            sentences.Add(problem.Problem);
        }

        _connection.Send(Wire.Refusal(call.Id, "invalid_argument", string.Join(" ", sentences),
            new Dictionary<string, object> { ["problems"] = found }));
        return false;
    }

    // The protocol method catalogue: the embedded file itself, answered here without the main thread.
    private void AnswerCatalogue(string id)
    {
        CatalogueFile? file = _host.Catalogue;
        if (file == null)
        {
            _connection.Send(Wire.Refusal(id, "internal_error", "The method catalogue did not load."));
            return;
        }

        _connection.Send(Wire.Line(new CatalogueReplyView(id, new JRaw(file.CompactText))));
    }

    private void Cancel(string id)
    {
        ProtocolCall? call;
        lock (_sync)
        {
            _inFlight.TryGetValue(id, out call);
        }

        if (call != null && call.Drop(call.CancelledReply()))
        {
            _host.Withdraw(call);
        }
    }

    // stream_end: a stream already ended ends again without a word, and one never used ends like any other; its
    // unstarted calls are answered cancelled at once, and the rest waits for the main thread. Building jobs (queued or
    // running), job snapshots and undo data are not the stream's: its end leaves them as they are.
    private void EndStream(long stream)
    {
        bool ending;
        lock (_ended)
        {
            ending = _ended.End(stream);
        }

        if (ending)
        {
            EndStreams(StreamEnd.Of(_connection.Owner, stream), true);
        }
    }

    // session_end: the connection's unstarted calls are answered cancelled and never run, so none of them writes after
    // the held restores; then the owner's restores apply at the next frame. Its streams stay open.
    private void EndSession()
    {
        foreach (ProtocolCall call in Waiting())
        {
            if (call.Drop(call.CancelledReply()))
            {
                _host.Withdraw(call);
            }
        }

        _host.SessionEnded(_connection.Owner);
    }

    // The one path for ended streams (stream_end, a slow client): their unstarted calls are dropped here, answered
    // cancelled when answer is set, and the end is posted for the main thread's next frame.
    private void EndStreams(StreamEnd end, bool answer)
    {
        foreach (ProtocolCall call in Waiting())
        {
            if (!end.Covers(call.Context))
            {
                continue;
            }

            if (answer)
            {
                call.Drop(call.CancelledReply());
            }
            else
            {
                call.State.TryDrop();
            }
        }

        _host.StreamEnded(_connection, end);
    }

    private void Answered(ProtocolCall call, string reply, string? method)
    {
        lock (_sync)
        {
            if (_inFlight.Remove(call.Request.Id))
            {
                long stream = call.Context.Stream;
                int left = _inFlightByStream[stream] - 1;
                if (left == 0)
                {
                    _inFlightByStream.Remove(stream);
                }
                else
                {
                    _inFlightByStream[stream] = left;
                }
            }
        }

        _connection.Send(reply, method);
        _connection.Served();
    }
}

/// <summary>The reply to the protocol method catalogue: the catalogue file as result.</summary>
internal sealed class CatalogueReplyView
{
    internal CatalogueReplyView(string id, JRaw catalogue)
    {
        Id = id;
        Result = catalogue;
    }

    public string Type => "reply";

    public string Id { get; }

    public bool Ok => true;

    public bool Shaped => false;

    public JRaw Result { get; }
}

/// <summary>
/// The methods of the protocol itself rather than of a tool; the catalogue lists them in protocol_methods. catalogue
/// is answered on the connection's reader thread, the others on the main thread outside ApiHost.
/// </summary>
internal static class ProtocolMethods
{
    internal static readonly string[] Names =
    {
        CallSession.CatalogueMethod, SubscriptionHub.SubscribeMethod, SubscriptionHub.UnsubscribeMethod,
        JobDropCalls.DropJobMethod, StreamCostCalls.StreamCostsMethod, HoldRestoreCalls.HoldRestoreMethod,
        ControllerCalls.InspectControllersMethod
    };

    /// <summary>
    /// A method the main thread answers outside ApiHost: subscribe, unsubscribe, sample_logic, drop_job, stream_costs,
    /// hold_restore and inspect_controllers.
    /// </summary>
    internal static bool RunsOnMainThread(string? method) =>
        method != null && (SubscriptionHub.Handles(method) || method == SubscriptionHub.SampleLogicMethod ||
                           JobDropCalls.Handles(method) || StreamCostCalls.Handles(method) ||
                           HoldRestoreCalls.Handles(method) || ControllerCalls.Handles(method));
}
