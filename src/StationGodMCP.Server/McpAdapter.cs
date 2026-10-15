using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The sidecar: MCP on one side, the verb pipeline on the other, over one game connection for the life of the sidecar,
/// opened at the first call (an agent session usually starts before the game). tools/list answers the generated verb
/// tools (generated/verbs/tools.json) and initialize their instructions; tools/call sends the verb call through the
/// VerbPipeline on the direct face (stream 0), writes the reply to a file when output_file asks or when it is larger
/// than the inline limit, and wraps the result or error in the one MCP result shape.
/// </summary>
internal sealed class McpAdapter : IAsyncDisposable
{
    private const string McpProtocolVersion = "2025-06-18";

    /// <summary>The MCP server's name, as initialize's serverInfo gives it.</summary>
    internal const string ServerName = "StationGodMCP";

    private static readonly JsonElement NullId = JsonSerializer.SerializeToElement<object?>(null);
    // Plain JSON escaping: the default encoder writes every quote inside a string as a six-character \u escape, and
    // apostrophes and pluses too, which makes the tool list and each reply's text copy larger for nothing.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // The text copy of a tool result, written as the adapter writes structuredContent (plain escaping), so the two
    // read the same.
    private static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SidecarOptions _options;
    private readonly IAsyncDisposable? _owned;
    private readonly CallRecorder? _recorder;
    private readonly VerbPipeline _pipeline;
    private readonly SeenSet _seen = new();
    private readonly RunHost? _runs;
    private readonly IGameLink _link;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    internal McpAdapter(SidecarOptions options)
        : this(options, new GameLink(options.Client), owned: true)
    {
    }

    /// <summary>
    /// A sidecar over a game link of the caller's (tests use a fake one); owned links are closed with it. Runs are served
    /// when the link is a hub link (run_script needs a stream per run and a worker session over it).
    /// </summary>
    internal McpAdapter(SidecarOptions options, IGameLink link, bool owned = false, IShownText? shown = null,
        RunHostSettings? runs = null)
    {
        _options = options;
        _link = link;
        _owned = owned ? link as IAsyncDisposable : null;
        _recorder = options.RecordPath is { } path ? new CallRecorder(path) : null;
        RunHostSettings hubSettings = runs ?? new RunHostSettings
        {
            ConfirmedCeiling = TimeSpan.FromSeconds(options.ConfirmedCeilingSeconds),
            Python = options.Python
        };
        ScriptStore scripts = ScriptStore.For(options, hubSettings.SgRoot, hubSettings.Journal);
        _pipeline = new VerbPipeline(link, recorder: _recorder, shown: shown == null ? _seen : new ShownToBoth(_seen, shown),
            scripts: scripts);
        if (link is IHubLink hub)
        {
            _runs = new RunHost(hub, _pipeline, _seen, hubSettings);
        }
    }

    internal VerbPipeline Pipeline => _pipeline;

    /// <summary>The hub's seen set: every reply text sent feeds it.</summary>
    internal SeenSet Seen => _seen;

    internal RunHost? Runs => _runs;

    /// <summary>Writes a notification line to the MCP client (notifications/progress); set by whoever owns the output.</summary>
    internal Func<string, Task>? Notify { get; set; }

    /// <summary>One MCP message line: the response line, or null for a notification.</summary>
    internal async Task<string?> HandleAsync(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            return RpcError(RequestIds.Recover(line), -32700, $"Parse error: the line is not JSON ({exception.Message})");
        }

        using (document)
        {
            return await HandleRequestAsync(document.RootElement).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// stdin ended (hub.md, Session end): the runs' session-end order, or session_end alone when no runs are served.
    /// Replies still in flight answer session_ended from here on.
    /// </summary>
    internal Task EndSessionAsync(Task? direct = null) =>
        _runs != null ? _runs.EndSessionAsync(direct) : _link.SessionEndAsync();

    public async ValueTask DisposeAsync()
    {
        if (_runs != null)
        {
            await _runs.DisposeAsync().ConfigureAwait(false);
        }

        _recorder?.Dispose();
        if (_owned != null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<string?> HandleRequestAsync(JsonElement root)
    {
        object? requestId = null;
        try
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out JsonElement idElement))
            {
                if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                {
                    throw new McpException(-32600, "Invalid request: id must be a string, a number or null.");
                }

                requestId = idElement.Clone();
            }

            // A key given twice would let the last one win silently (another method, another tool).
            string? repeated = ArgumentCheck.RepeatedKey(root) ??
                               (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("params", out JsonElement given)
                                   ? ArgumentCheck.RepeatedKey(given)
                                   : null);
            if (repeated != null)
            {
                throw new McpException(-32600, $"Invalid request: key '{repeated}' is given twice.");
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out JsonElement methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                throw new McpException(-32600, "Invalid request: a JSON-RPC request is an object with a string method.");
            }

            string method = methodElement.GetString()!;
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                if (method == "notifications/cancelled")
                {
                    Cancelled(root);
                }

                return null;
            }

            VerbCatalogue catalogue = _pipeline.Catalogue;
            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = RequestedProtocolVersion(root),
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = ServerName, version = Program.ServerVersion },
                    instructions = catalogue.Instructions
                },
                "ping" => new { },
                "tools/list" => new { tools = catalogue.Tools },
                "tools/call" => await CallToolAsync(root, catalogue, requestId).ConfigureAwait(false),
                _ => throw new McpException(-32601, $"Unknown MCP method '{method}'.")
            };

            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = requestId, result }, JsonOptions);
        }
        catch (McpException exception)
        {
            return RpcError(requestId, exception.Code, exception.Message + await NoticeTextAsync(root).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            // The details (handler names among them) go to the log; the agent gets no internals.
            Console.Error.WriteLine($"[StationGodMCP] {exception}");
            return RpcError(requestId, -32603,
                "Internal error in the MCP server; its log has the details." + await NoticeTextAsync(root).ConfigureAwait(false));
        }
    }

    // A tool call answered with a JSON-RPC error still carries the runs' notices (after its message).
    private async Task<string> NoticeTextAsync(JsonElement root)
    {
        if (_runs == null || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out JsonElement method) ||
            method.ValueKind != JsonValueKind.String || method.GetString() != "tools/call")
        {
            return string.Empty;
        }

        try
        {
            List<string> notices = await _runs.NoticesAsync("", null, null).ConfigureAwait(false);
            return notices.Count == 0 ? string.Empty : "\n" + string.Join("\n", notices);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    // Every tool result, the game's and the pipeline's own refusals alike, has one shape: content carries the reply's
    // JSON text (with the runs' notices after it), structuredContent the same object, isError whether it is an error.
    private async Task<object> CallToolAsync(JsonElement root, VerbCatalogue catalogue, object? requestId)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            throw new McpException(-32602, "tools/call needs params with a string name.");
        }

        string verb = nameElement.GetString()!;
        JsonElement given = parameters.TryGetProperty("arguments", out JsonElement raw) ? raw : default;
        JsonObject recorded = given.ValueKind == JsonValueKind.Object ? JsonObject.Create(given.Clone())! : new JsonObject();
        string? whatGiven = JsonNodes.Text(recorded["what"]);
        recorded.Remove("what");
        _pipeline.RecordInput(Face.Direct, 0, verb, whatGiven, recorded);
        if (catalogue.Verb(verb) == null)
        {
            List<string> nearest = Nearest.Of(verb, catalogue.VerbNames);
            throw new McpException(-32602, $"Unknown StationGodMCP tool '{verb}'" +
                                           (nearest.Count > 0 ? $"; nearest: {string.Join(", ", nearest)}." : "."));
        }

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement value) &&
                                value.ValueKind != JsonValueKind.Null
            ? value
            : default;
        if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        {
            return await ErrorAsync(new VerbReply.Failed(VerbFailure.InvalidArgument, "The arguments must be a JSON object."),
                verb, null).ConfigureAwait(false);
        }

        if (arguments.ValueKind == JsonValueKind.Object && ArgumentCheck.Unsendable(arguments) is { Count: > 0 } unsendable)
        {
            return await ErrorAsync(new VerbReply.Failed(VerbFailure.InvalidArgument, string.Join(" ", unsendable)), verb, null)
                .ConfigureAwait(false);
        }

        JsonObject options = arguments.ValueKind == JsonValueKind.Object
            ? JsonObject.Create(arguments.Clone())!
            : new JsonObject();
        string? what = null;
        if (options["what"] is { } whatNode)
        {
            what = JsonNodes.Text(whatNode);
            if (what == null)
            {
                return await ErrorAsync(OptionProblem.Refusal(verb, [new OptionProblem("what", "must be a string")]), verb, null)
                    .ConfigureAwait(false);
            }

            options.Remove("what");
        }

        OutputChoice output = OutputChoice.Of(options["output_file"] is { } file ? JsonNodes.ToElement(file) : null);
        if (output is OutputChoice.Refused refusedFile)
        {
            return await ErrorAsync(new VerbReply.Failed(VerbFailure.InvalidArgument,
                refusedFile.Message.Replace("Argument 'output_file'", "output_file", StringComparison.Ordinal) + " " +
                _options.Output.WhereFilesGo(Environment.MachineName)), verb, what).ConfigureAwait(false);
        }

        if (_runs != null && await RunsAsync(verb, what, options, parameters, requestId).ConfigureAwait(false) is { } runs)
        {
            return runs;
        }

        VerbReply reply = await _pipeline.CallAsync(new VerbCall(verb, what, options, Face.Direct) { Recorded = true },
            CancellationToken.None).ConfigureAwait(false);
        return reply switch
        {
            VerbReply.Done done => await SentAsync(Delivered(verb + (what == null ? string.Empty : "_" + what), output,
                JsonNodes.ToElement(done.Reply)), isError: false, verb, what).ConfigureAwait(false),
            VerbReply.Failed failed => await ErrorAsync(failed, verb, what).ConfigureAwait(false),
            _ => throw new InvalidOperationException("A verb reply is done or failed.")
        };
    }

    // run_script, and inspect job and server stop for runs: the run host's. Null when the call is not a run's.
    private async Task<object?> RunsAsync(string verb, string? what, JsonObject options, JsonElement parameters,
        object? requestId)
    {
        VerbReply? reply;
        string? about = JsonNodes.Text(options["id"])?.Trim();
        if (verb == "run_script")
        {
            string? key = requestId is JsonElement id ? id.GetRawText() : null;
            using CancellationTokenSource cancel = new();
            if (key != null)
            {
                _pending[key] = cancel;
            }

            try
            {
                reply = await _runs!.RunScriptAsync(options, new ReplyHooks
                {
                    Progress = ProgressOf(parameters),
                    Cancelled = cancel.Token
                }).ConfigureAwait(false);
            }
            finally
            {
                if (key != null)
                {
                    _pending.TryRemove(key, out _);
                }
            }

            about = reply is VerbReply.Done { Reply: JsonObject done } && JsonNodes.Text(done["text"]) is { } text
                ? text.Split(' ', 2)[0]
                : null;
        }
        else if (verb == "inspect" && what == "job")
        {
            reply = await _runs!.InspectJobAsync(options).ConfigureAwait(false);
        }
        else if (verb == "server" && what == "stop")
        {
            reply = await _runs!.StopAsync(options).ConfigureAwait(false);
        }
        else if (verb == "undo")
        {
            reply = await _runs!.UndoAsync(options).ConfigureAwait(false);
        }
        else
        {
            return null;
        }

        switch (reply)
        {
            case null:
                return null;
            case VerbReply.Failed failed:
                return await ErrorAsync(failed, verb, what).ConfigureAwait(false);
            case VerbReply.Done { Reply: JsonObject { Count: 1 } body } when JsonNodes.Text(body["text"]) is { } runText:
                // A run's reply is text, sent as it reads; its seen ids were added as it was written.
                return await SentAsync(JsonNodes.ToElement(body), isError: false, verb, what, about, runText)
                    .ConfigureAwait(false);
            default:
                return await SentAsync(JsonNodes.ToElement(((VerbReply.Done)reply).Reply), isError: false, verb, what, about)
                    .ConfigureAwait(false);
        }
    }

    // notifications/progress every ProgressEvery while a run_script waits, when the request carried a progressToken.
    private Func<double, Task>? ProgressOf(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("_meta", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("progressToken", out JsonElement token) ||
            token.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return null;
        }

        JsonElement kept = token.Clone();
        return async seconds =>
        {
            if (Notify != null)
            {
                await Notify(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    method = "notifications/progress",
                    @params = new { progressToken = kept, progress = seconds, message = $"the run is {seconds} s in" }
                }, JsonOptions)).ConfigureAwait(false);
            }
        };
    }

    // A client cancelling a pending run_script stops that run.
    private void Cancelled(JsonElement root)
    {
        if (root.TryGetProperty("params", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("requestId", out JsonElement id) && _pending.TryGetValue(id.GetRawText(), out
                CancellationTokenSource? cancel))
        {
            cancel.Cancel();
        }
    }

    private Task<object> ErrorAsync(VerbReply.Failed failed, string verb, string? what) =>
        SentAsync(JsonNodes.ToElement(failed.ToJson()), isError: true, verb, what);

    // The cap's seen-set hook hears exactly the JSON text the client gets; the runs' notices ride after it.
    private async Task<object> SentAsync(JsonElement body, bool isError, string verb, string? what, string? about = null,
        string? plainText = null)
    {
        string text = plainText ?? JsonSerializer.Serialize(body, TextOptions);
        if (plainText == null)
        {
            _pipeline.TextShown(text);
        }

        if (_runs != null && await _runs.NoticesAsync(verb, what, about).ConfigureAwait(false) is { Count: > 0 } notices)
        {
            text += "\n" + string.Join("\n", notices);
        }

        return new
        {
            content = new[] { new { type = "text", text } },
            structuredContent = body,
            isError
        };
    }

    // The reply inline, or written to a file when output_file asks or when it is larger than the inline limit.
    private JsonElement Delivered(string name, OutputChoice output, JsonElement result)
    {
        if (output is OutputChoice.ToFile asked)
        {
            return _options.Output.Write(name, asked.Target, result);
        }

        if (_options.InlineLimitBytes <= 0 || Encoding.UTF8.GetByteCount(result.GetRawText()) <= _options.InlineLimitBytes)
        {
            return result;
        }

        JsonElement written = _options.Output.Write(name, new OutputTarget.Auto(), result);
        if (written.TryGetProperty("output_file_error", out _))
        {
            return written;
        }

        JsonObject pointer = JsonObject.Create(written)!;
        pointer["auto_output_file"] = true;
        return JsonSerializer.SerializeToElement(pointer);
    }

    private static string RequestedProtocolVersion(JsonElement root) =>
        root.TryGetProperty("params", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("protocolVersion", out JsonElement requested) &&
        requested.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(requested.GetString())
            ? requested.GetString()!
            : McpProtocolVersion;

    // An error reply always carries id, null when the request's id could not be read (JSON-RPC 2.0).
    private static string RpcError(object? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id ?? NullId, error = new { code, message } }, JsonOptions);

    /// <summary>The shown text goes to the hub's seen set and to the hook the caller gave.</summary>
    private sealed class ShownToBoth(IShownText first, IShownText second) : IShownText
    {
        public void Shown(string text)
        {
            first.Shown(text);
            second.Shown(text);
        }
    }

    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
