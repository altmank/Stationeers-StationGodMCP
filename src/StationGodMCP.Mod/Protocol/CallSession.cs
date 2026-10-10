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
/// matched by id; cancel; events. The first message must be hello; anything else is a protocol error that closes the
/// connection. Lines are parsed and checked here, on the connection's reader thread; only the methods themselves run
/// on the main thread.
/// </summary>
internal sealed class CallSession : Session
{
    internal const int MaxInFlight = 16;
    internal const int MaxRequestBytes = 4194304;
    internal const string CatalogueMethod = "catalogue";
    internal const string AnonymousClient = "anonymous";

    /// <summary>What welcome lists in features: what this server does beyond the basics.</summary>
    internal static readonly string[] Features = { "shape", "shape.paths", "shape.omit", "cancel" };

    /// <summary>The features with subscriptions, when the server has them.</summary>
    internal static readonly string[] FeaturesWithSubscriptions = { "shape", "shape.paths", "shape.omit", "subscriptions", "cancel" };

    private readonly Connection _connection;
    private readonly ProtocolHost _host;
    private readonly object _sync = new object();
    private readonly Dictionary<string, ProtocolCall> _inFlight = new Dictionary<string, ProtocolCall>(StringComparer.Ordinal);
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
            case ClientMessage.Bye _:
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

        CatalogueFile? file = _host.Catalogue;
        WelcomeView welcome = new WelcomeView(_connection.ClientId, Client,
            new WelcomeServerView(ServerFacts.Current, _connection.Transport),
            new WelcomeCatalogueView(file?.Hash ?? string.Empty, file?.Catalogue.MethodCount ?? 0,
                file?.Catalogue.ProtocolMethodCount ?? 0),
            new WelcomeLimitsView(MaxInFlight, MaxRequestBytes, _host.Settings.MaxReplyBytes, _host.Subscriptions?.Limits),
            _host.Subscriptions != null ? FeaturesWithSubscriptions : Features);
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
        bool namesChecked = false;
        if (_host.Settings.StrictArguments)
        {
            if (!Checked(call, method, out shape))
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

        CallRequest request = new CallRequest(call.Id, call.Method, call.Params, shape, namesChecked);
        CallProfile profile = method != null
            ? new CallProfile(method.Name, method.ClassAt(call.Params), method.CostAt(call.Params))
            : CallProfiles.Of(null, call.Method, call.Params);
        // A method that runs for a while (x-duration: sample_logic) gets that long added to its deadline.
        int deadlineMs = call.DeadlineMs + (method?.DurationMs(call.Params) ?? 0);
        ProtocolCall queued = new ProtocolCall(request, profile, deadlineMs, _connection, Answered);
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
    internal static readonly string[] Names =
        { CallSession.CatalogueMethod, SubscriptionHub.SubscribeMethod, SubscriptionHub.UnsubscribeMethod };
}
