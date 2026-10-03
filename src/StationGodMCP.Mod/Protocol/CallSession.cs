#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Protocol;

/// <summary>
/// Version 2 on one connection: hello and welcome, then calls by id, several in flight, answered in any order and
/// matched by id; cancel; events. Lines are parsed here, on the connection's reader thread; only the methods themselves
/// run on the main thread.
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
    private bool _welcomed;
    private string? _label;

    internal CallSession(Connection connection, ProtocolHost host)
    {
        _connection = connection;
        _host = host;
    }

    internal override int Protocol => 2;

    internal override int? MaximumLineBytes => MaxRequestBytes;

    internal override bool Pings => _welcomed;

    internal override int WriteTimeoutMilliseconds => _host.Settings.SlowClientMilliseconds;

    internal override string Client => AnonymousClient;

    internal override string? Label => _label;

    internal override string Level => "cheat";

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
            case ClientMessage.Hello hello when !_welcomed:
                Greet(hello);
                break;
            case ClientMessage.Hello _:
                Refuse(ClientMessage.Refused.Protocol("hello was already answered.", line));
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
        if (!hello.Protocols.Contains(2))
        {
            _connection.Send(Wire.Refusal(null, "unsupported_protocol", "This server speaks protocol 2 only.",
                new Dictionary<string, int[]> { ["supported"] = new[] { 2 } }));
            _connection.EndGracefully();
            return;
        }

        if (hello.ProvesKey || _connection.Transport != "pipe")
        {
            _connection.Send(Wire.Refusal(null, "unauthorized", "This server has no keys yet; connect without auth on the pipe."));
            _connection.EndGracefully();
            return;
        }

        _label = hello.ClientName;
        CatalogueFile? file = _host.Catalogue;
        WelcomeView welcome = new WelcomeView(_connection.ClientId, Client, Level, Array.Empty<string>(),
            new CheatView(false, null, true), new WelcomeServerView(ServerFacts.Current, _connection.Transport),
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
        bool isWrite = method != null && method.ClassAt(call.Params) != MethodClass.Read;
        CallRequest request = new CallRequest(call.Id, call.Method, call.Params, ShapeRequest.Lenient(call.Shape));
        ProtocolCall queued = new ProtocolCall(request, isWrite, call.DeadlineMs, _connection, Answered);
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

        if (call != null && call.State.TryDrop())
        {
            call.Deliver(call.CancelledReply(), null);
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
