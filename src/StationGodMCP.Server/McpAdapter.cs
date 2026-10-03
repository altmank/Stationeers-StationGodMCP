using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The sidecar: MCP on one side, the StationGod client on the other, over one connection for the life of the sidecar,
/// opened at the first call (an agent session usually starts before the game). tools/list comes from the catalogue;
/// when the mod's catalogue differs from the built-in one, the tools switch to it and the agent is told
/// (notifications/tools/list_changed). tools/call takes out the sidecar's own arguments (fields, output_file), sends
/// the call, writes the reply to a file when asked or when it is larger than the inline limit, and wraps the result or
/// error in the one MCP result shape (ToolReplies). Arguments are the mod's to check on protocol version 2; on version 1,
/// or before any connection, the sidecar checks them against the tool's schema first, as it always has.
/// </summary>
internal sealed class McpAdapter : IAsyncDisposable
{
    private const string McpProtocolVersion = "2025-06-18";
    private const string ListChanged = """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""";

    private static readonly JsonElement NullId = JsonSerializer.SerializeToElement<object?>(null);
    private static readonly JsonElement NoArguments = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SidecarOptions _options;
    private readonly StationGodClient _client;
    private readonly Func<string, Task> _notify;
    private ToolSet _tools = ToolSet.BuiltIn;

    internal McpAdapter(SidecarOptions options, Func<string, Task>? notify = null)
    {
        _options = options;
        _notify = notify ?? (_ => Task.CompletedTask);
        _client = new StationGodClient(options.Client);
        _client.CatalogueChanged += OnCatalogueChanged;
    }

    /// <summary>The tools as tools/list answers them now.</summary>
    internal ToolSet Tools => Volatile.Read(ref _tools);

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

    public ValueTask DisposeAsync() => _client.DisposeAsync();

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

            ToolSet tools = Tools;
            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = RequestedProtocolVersion(root),
                    capabilities = new { tools = new { listChanged = true } },
                    serverInfo = new { name = tools.ServerName, version = Program.ServerVersion },
                    instructions = tools.Instructions
                },
                "ping" => new { },
                "tools/list" => new { tools = tools.Tools },
                "tools/call" => await CallToolAsync(root, tools).ConfigureAwait(false),
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

    // Every tool result, the game's and the sidecar's own refusals alike, has one shape (ToolReplies).
    private async Task<object> CallToolAsync(JsonElement root, ToolSet tools)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            throw new McpException(-32602, "tools/call needs params with a string name.");
        }

        string tool = nameElement.GetString()!;
        if (!tools.InputSchemas.TryGetValue(tool, out JsonElement schema))
        {
            throw new McpException(-32602, $"Unknown StationGodMCP tool '{tool}'.");
        }

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.Clone()
            : NoArguments;

        if (tool == SampleLogic.Method && _client.Protocol is null)
        {
            // Where sample_logic runs depends on the game's protocol: learn it before the first one is answered.
            await _client.ConnectAsync().ConfigureAwait(false);
        }

        bool modChecks = _client.Protocol == ProtocolVersion.Version2;
        IReadOnlyList<string> problems = modChecks
            ? ArgumentCheck.ProblemsOf(schema, arguments, SidecarArguments.Names)
            : ArgumentCheck.Problems(schema, arguments);
        if (problems.Count > 0)
        {
            return ToolReplies.Error(ToolFailure.InvalidArgument, string.Join(" ", problems));
        }

        if (!modChecks)
        {
            arguments = ArgumentCheck.Normalised(schema, arguments);
        }

        SidecarArguments call = SidecarArguments.Take(arguments);
        if (call.Output is OutputChoice.Refused refused)
        {
            return ToolReplies.Error(ToolFailure.InvalidArgument, refused.Message);
        }

        CallOutcome outcome;
        IReadOnlyList<string> unparsed = [];
        if (tools.RunInSidecar.Contains(tool) || SampleLogic.RunsHere(tool, _client.Protocol))
        {
            outcome = await SampleLogic.RunAsync(call.Forwarded,
                reads => _client.CallAsync("read_logic_many", reads), CancellationToken.None).ConfigureAwait(false);
            if (outcome is CallOutcome.Answered answered && call.Fields is { ValueKind: JsonValueKind.Array } fields)
            {
                outcome = answered with { Result = FieldSelection.Of(fields).Apply(answered.Result) };
            }
        }
        else
        {
            (JsonElement? shape, unparsed) = call.ShapeFor(_client.Protocol);
            outcome = await _client.CallAsync(tool, call.Forwarded, shape).ConfigureAwait(false);
        }

        return outcome.Match(
            answered => ToolReplies.Of(Delivered(tool, call.Output, SidecarArguments.WithUnmatched(answered.Result, unparsed)), isError: false),
            gameError => ToolReplies.Of(gameError.Error, isError: true),
            noAnswer => ToolReplies.Error(ToolFailure.GameUnavailable, noAnswer.Message));
    }

    // The reply inline, or written to a file when output_file asks or when it is larger than the inline limit.
    private JsonElement Delivered(string tool, OutputChoice output, JsonElement result)
    {
        if (output is OutputChoice.ToFile asked)
        {
            return _options.Output.Write(tool, asked.Target, result);
        }

        if (_options.InlineLimitBytes <= 0 || Encoding.UTF8.GetByteCount(result.GetRawText()) <= _options.InlineLimitBytes)
        {
            return result;
        }

        JsonElement written = _options.Output.Write(tool, new OutputTarget.Auto(), result);
        if (written.TryGetProperty("output_file_error", out _))
        {
            return written;
        }

        JsonObject pointer = JsonObject.Create(written)!;
        pointer["auto_output_file"] = true;
        return JsonSerializer.SerializeToElement(pointer);
    }

    private void OnCatalogueChanged(GameCatalogue catalogue)
    {
        try
        {
            Volatile.Write(ref _tools, ToolSet.From(catalogue.Document));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException or
                                              KeyNotFoundException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"[StationGodMCP] The game's catalogue ({catalogue.Hash}) gives no tool list; keeping the built-in one: {exception.Message}");
            Volatile.Write(ref _tools, ToolSet.BuiltIn);
        }

        _ = _notify(ListChanged);
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
