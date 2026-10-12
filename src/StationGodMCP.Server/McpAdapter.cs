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
/// than the inline limit, and wraps the result or error in the one MCP result shape (ToolReplies).
/// </summary>
internal sealed class McpAdapter : IAsyncDisposable
{
    private const string McpProtocolVersion = "2025-06-18";

    private static readonly JsonElement NullId = JsonSerializer.SerializeToElement<object?>(null);
    // Plain JSON escaping: the default encoder writes every quote inside a string as a six-character \u escape, and
    // apostrophes and pluses too, which makes the tool list and each reply's text copy larger for nothing.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SidecarOptions _options;
    private readonly IAsyncDisposable? _owned;
    private readonly CallRecorder? _recorder;
    private readonly VerbPipeline _pipeline;

    internal McpAdapter(SidecarOptions options)
        : this(options, new ClientGameLink(new StationGodClient(options.Client)), owned: true)
    {
    }

    /// <summary>A sidecar over a game link of the caller's (tests use a fake one); owned links are closed with it.</summary>
    internal McpAdapter(SidecarOptions options, IGameLink link, bool owned = false)
    {
        _options = options;
        _owned = owned ? link as IAsyncDisposable : null;
        _recorder = options.RecordPath is { } path ? new CallRecorder(path) : null;
        _pipeline = new VerbPipeline(link, recorder: _recorder);
    }

    internal VerbPipeline Pipeline => _pipeline;

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

    public async ValueTask DisposeAsync()
    {
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
                return null;
            }

            VerbCatalogue catalogue = _pipeline.Catalogue;
            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = RequestedProtocolVersion(root),
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = ToolCatalogue.ServerName, version = Program.ServerVersion },
                    instructions = catalogue.Instructions
                },
                "ping" => new { },
                "tools/list" => new { tools = catalogue.Tools },
                "tools/call" => await CallToolAsync(root, catalogue).ConfigureAwait(false),
                _ => throw new McpException(-32601, $"Unknown MCP method '{method}'.")
            };

            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = requestId, result }, JsonOptions);
        }
        catch (McpException exception)
        {
            return RpcError(requestId, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[StationGodMCP] {exception}");
            return RpcError(requestId, -32603, exception.Message);
        }
    }

    // Every tool result, the game's and the pipeline's own refusals alike, has one shape (ToolReplies).
    private async Task<object> CallToolAsync(JsonElement root, VerbCatalogue catalogue)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            throw new McpException(-32602, "tools/call needs params with a string name.");
        }

        string verb = nameElement.GetString()!;
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
            return Error(new VerbReply.Failed(VerbFailure.InvalidArgument, "The arguments must be a JSON object."));
        }

        if (arguments.ValueKind == JsonValueKind.Object && ArgumentCheck.Unsendable(arguments) is { Count: > 0 } unsendable)
        {
            return Error(new VerbReply.Failed(VerbFailure.InvalidArgument, string.Join(" ", unsendable)));
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
                return Error(OptionProblem.Refusal(verb, [new OptionProblem("what", "must be a string")]));
            }

            options.Remove("what");
        }

        OutputChoice output = OutputChoice.Of(options["output_file"] is { } file ? JsonNodes.ToElement(file) : null);
        if (output is OutputChoice.Refused refusedFile)
        {
            return Error(new VerbReply.Failed(VerbFailure.InvalidArgument,
                refusedFile.Message.Replace("Argument 'output_file'", "output_file", StringComparison.Ordinal) + " " +
                _options.Output.WhereFilesGo(Environment.MachineName)));
        }

        VerbReply reply = await _pipeline.CallAsync(new VerbCall(verb, what, options, Face.Direct), CancellationToken.None)
            .ConfigureAwait(false);
        return reply switch
        {
            VerbReply.Done done => ToolReplies.Of(Delivered(verb + (what == null ? string.Empty : "_" + what), output,
                JsonNodes.ToElement(done.Reply)), isError: false),
            VerbReply.Failed failed => Error(failed),
            _ => throw new InvalidOperationException("A verb reply is done or failed.")
        };
    }

    private static object Error(VerbReply.Failed failed) =>
        ToolReplies.Of(JsonNodes.ToElement(failed.ToJson()), isError: true);

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

    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
