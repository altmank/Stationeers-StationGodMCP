#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Access;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Protocol;

/// <summary>
/// Version 2 on one connection: hello, a challenge and the key's proof when the client signs in with a key, and
/// welcome; then calls by id, several in flight, answered in any order and matched by id; cancel; events. Lines are
/// parsed, checked and judged against the connection's level here, on the connection's reader thread; only the methods
/// themselves run on the main thread.
/// </summary>
internal sealed class CallSession : Session
{
    internal const int MaxInFlight = 16;
    internal const int MaxRequestBytes = 4194304;
    internal const string CatalogueMethod = "catalogue";
    internal const string AnonymousClient = "anonymous";

    /// <summary>What welcome lists in features: what this server does beyond the basics.</summary>
    internal static readonly string[] Features = { "shape", "shape.paths", "cancel" };

    private readonly Connection _connection;
    private readonly ProtocolHost _host;
    private readonly object _sync = new object();
    private readonly Dictionary<string, ProtocolCall> _inFlight = new Dictionary<string, ProtocolCall>(StringComparer.Ordinal);
    private ClientMessage.Hello? _hello;
    private string? _nonce;
    private bool _welcomed;
    private string _client = AnonymousClient;
    private ConnectionAccess _access = new ConnectionAccess(AccessLevel.None, null, false);
    private DateTime? _reportedArmedUntil;

    internal CallSession(Connection connection, ProtocolHost host)
    {
        _connection = connection;
        _host = host;
    }

    internal override int Protocol => 2;

    internal override int? MaximumLineBytes => MaxRequestBytes;

    internal override bool Pings => _welcomed;

    internal override int WriteTimeoutMilliseconds => _host.Settings.SlowClientMilliseconds;

    /// <summary>The whole sign-in, from connecting to welcome, has the first-line time; after it reads wait as long as they like.</summary>
    internal override long? SignInBy => _welcomed ? null : _connection.ConnectedAt +
        (long)(_host.Settings.FirstLineTimeoutMilliseconds * (double)System.Diagnostics.Stopwatch.Frequency / 1000.0);

    internal override string Client => _client;

    internal override string? Label => _hello?.ClientName;

    internal override string Level => AccessLevels.Name(_access.Level);

    /// <summary>The key this connection signed in with; null for none.</summary>
    internal ClientKey? Key { get; private set; }

    internal ConnectionAccess Access => _access;

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
            case ClientMessage.Auth auth when _nonce != null && !_welcomed:
                SignIn(auth);
                break;
            case ClientMessage.Auth _:
                Refuse(ClientMessage.Refused.Protocol("auth comes only after a challenge.", line));
                break;
            case ClientMessage.Call _ when !_welcomed:
                Refuse(ClientMessage.Refused.Protocol("A call before welcome.", line));
                break;
            case ClientMessage.Call call:
                Accept(call);
                break;
            case ClientMessage.Cancel cancel:
                Cancel(cancel.Id);
                break;
            case ClientMessage.Bye _:
                _connection.EndGracefully();
                break;
        }
    }

    internal override void OnReadTimeout()
    {
        _connection.Send(Wire.Refusal(null, "protocol_error",
            $"The sign-in did not finish within {_host.Settings.FirstLineTimeoutMilliseconds / 1000} seconds of connecting."));
        _connection.Send(Wire.Line(new GoodbyeView("protocol_error")));
        _connection.EndGracefully();
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

    internal override void OnSlowClient()
    {
        foreach (ProtocolCall call in Waiting())
        {
            call.State.TryDrop();
        }
    }

    internal override void OnWorldChanged(WorldFacts world)
    {
        if (_welcomed)
        {
            _connection.Send(Wire.Line(new WorldChangedView(world)));
        }
    }

    /// <summary>The key was removed or now gives less: its unstarted calls are dropped, goodbye revoked, closed.</summary>
    internal void Revoke()
    {
        foreach (ProtocolCall call in Waiting())
        {
            call.State.TryDrop();
        }

        ProtocolLog.Info($"Connection {_connection.ClientId} ({_client}) revoked: its key changed.");
        _connection.Send(Wire.Line(new GoodbyeView("revoked")));
        _connection.EndGracefully();
    }

    /// <summary>Sends cheat_armed or cheat_disarmed when the owner's approval for this connection began, changed or ended.</summary>
    internal void RefreshCheat()
    {
        if (!_welcomed || _access.Level < AccessLevel.Cheat || _access.StandingCheat)
        {
            return;
        }

        DateTime? until = _host.Access.Arming.ArmedUntil(_connection.ClientId, Key?.Name);
        lock (_sync)
        {
            if (until == _reportedArmedUntil)
            {
                return;
            }

            _reportedArmedUntil = until;
        }

        _connection.Send(until.HasValue
            ? Wire.Line(new CheatArmedView(AccessControl.Utc(until.Value)))
            : Wire.Line(new EventView("cheat_disarmed")));
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

        if (hello.ProvesKey)
        {
            _nonce = KeyProof.NewNonce();
            _connection.Send(Wire.Line(new ChallengeView(_nonce)));
            return;
        }

        ConnectionAccess anonymous = _host.Access.Settings.AnonymousPipe;
        if (_connection.Transport != "pipe" || anonymous.Level == AccessLevel.None)
        {
            Unauthorized(_connection.Transport != "pipe"
                ? "A connection over TCP must sign in with a key (hello auth \"key\")."
                : "This server does not accept connections without a key.");
            return;
        }

        Welcome(AnonymousClient, anonymous, null);
    }

    private void SignIn(ClientMessage.Auth auth)
    {
        ClientKey? key = null;
        bool good = auth.Client == _hello!.ClientName &&
                    _host.Access.Clients.Clients.TryGetValue(auth.Client, out key) &&
                    key.Transports.Contains(_connection.Transport) &&
                    KeyProof.Matches(key.Key, _nonce!, auth.Client, _connection.Transport, auth.Proof);
        if (!good)
        {
            Unauthorized("The key's proof is wrong, the client is unknown, or the key is not allowed on this transport.");
            return;
        }

        Welcome(key!.Name, key.Access, key);
    }

    private void Unauthorized(string message)
    {
        _connection.Send(Wire.Refusal(null, "unauthorized", message));
        _connection.EndGracefully();
    }

    private void Welcome(string client, ConnectionAccess access, ClientKey? key)
    {
        _client = client;
        _access = access;
        Key = key;
        DateTime? until = access.Level == AccessLevel.Cheat && !access.StandingCheat
            ? _host.Access.Arming.ArmedUntil(_connection.ClientId, key?.Name)
            : null;
        _reportedArmedUntil = until;
        CatalogueFile? file = _host.Catalogue;
        List<string> grants = new List<string>(access.Grants);
        grants.Sort(StringComparer.Ordinal);
        WelcomeView welcome = new WelcomeView(_connection.ClientId, client, Level, grants,
            new CheatView(until.HasValue, until.HasValue ? AccessControl.Utc(until.Value) : null, access.StandingCheat),
            new WelcomeServerView(ServerFacts.Current, _connection.Transport),
            new WelcomeCatalogueView(file?.Hash ?? string.Empty, file?.Catalogue.MethodCount ?? 0,
                file?.Catalogue.ProtocolMethodCount ?? 0),
            new WelcomeLimitsView(MaxInFlight, MaxRequestBytes, _host.Settings.MaxReplyBytes), Features);
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

    private void Accept(ClientMessage.Call call)
    {
        if (call.Method == CatalogueMethod)
        {
            AnswerCatalogue(call.Id);
            return;
        }

        CatalogueMethod? method = null;
        _host.Catalogue?.Catalogue.TryGet(call.Method, out method);
        ShapeRequest? shape;
        if (_host.Settings.StrictArguments)
        {
            if (!Checked(call, method, out shape))
            {
                return;
            }
        }
        else
        {
            shape = ShapeRequest.Lenient(call.Shape);
        }

        MethodClass effective = method?.ClassAt(call.Params) ?? MethodClass.Read;
        if (method != null && !Permitted(call, effective))
        {
            return;
        }

        CallRequest request = new CallRequest(call.Id, call.Method, call.Params, shape);
        CallProfile profile = method != null
            ? new CallProfile(method.Name, effective, method.CostAt(call.Params))
            : CallProfiles.Of(null, call.Method, call.Params);
        ProtocolCall queued = new ProtocolCall(request, profile, call.DeadlineMs, _connection, Answered);
        lock (_sync)
        {
            if (_inFlight.Count >= MaxInFlight)
            {
                _connection.Send(Wire.Refusal(call.Id, "too_many_in_flight",
                    $"At most {MaxInFlight} calls may be in flight on a connection.",
                    new Dictionary<string, int> { ["limit"] = MaxInFlight }));
                return;
            }

            if (_inFlight.ContainsKey(call.Id))
            {
                _connection.Send(Wire.Refusal(call.Id, "duplicate_id", $"A call with id '{call.Id}' is already in flight."));
                return;
            }

            _inFlight.Add(call.Id, queued);
        }

        _host.Submit(queued);
    }

    // The connection's level against the call's effective class: permission_denied or cheat_not_armed, nothing run.
    private bool Permitted(ClientMessage.Call call, MethodClass effective)
    {
        bool armed = _host.Access.Arming.ArmedUntil(_connection.ClientId, Key?.Name).HasValue;
        switch (Permission.Decide(_access, call.Method, effective, armed))
        {
            case Permission.Denied denied:
                string required = AccessLevels.Name(denied.Required);
                _connection.Send(Wire.Refusal(call.Id, "permission_denied",
                    $"{call.Method} needs {required} level at these arguments; this connection is {Level}.",
                    new Dictionary<string, string> { ["required"] = required, ["level"] = Level, ["method"] = call.Method }));
                return false;
            case Permission.NotArmed _:
                _connection.Send(Wire.Refusal(call.Id, "cheat_not_armed",
                    $"{call.Method} is a cheat tool and the owner has not approved cheats for {_connection.ClientId} " +
                    $"({_client}). In the game console: stationgod allow {_connection.ClientId}",
                    new Dictionary<string, string> { ["client_id"] = _connection.ClientId, ["client"] = _client }));
                return false;
            default:
                return true;
        }
    }

    // Version 2's full check, before anything is queued: the shape (invalid_shape), then the params against the
    // method's schema (invalid_argument with every problem and its path). A method the catalogue does not have is left
    // to the main thread, which answers method_not_found as on version 1.
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

        if (method == null || method.RunsInSidecar)
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
            found.Add(new Dictionary<string, string> { ["path"] = problem.Path, ["problem"] = problem.Problem });
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

    private void Answered(ProtocolCall call, string reply, string? method)
    {
        lock (_sync)
        {
            _inFlight.Remove(call.Request.Id);
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

/// <summary>The methods the protocol layer answers itself, not the main thread; the catalogue lists them in protocol_methods.</summary>
internal static class ProtocolMethods
{
    internal static readonly string[] Names = { CallSession.CatalogueMethod };
}
