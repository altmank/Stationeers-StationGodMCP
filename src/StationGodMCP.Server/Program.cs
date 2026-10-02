using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StationGodMCP.Server;

internal static class Program
{
    // Reported in the initialize response. build.ps1 checks it matches StationGodMCP.Server.csproj and the mod.
    private const string ServerVersion = "1.10.0";
    private const string ProtocolVersion = "2025-06-18";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(35);
    private static readonly JsonElement NullId = JsonSerializer.SerializeToElement<object?>(null);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task Main(string[] args)
    {
        GameTransportSettings transport = ReadTransportSettings(args);
        OutputFolder replyFiles = OutputFolder.From(
            ReadArgument(args, OutputFolder.CommandLineOption), Environment.GetEnvironmentVariable(OutputFolder.EnvironmentVariable));
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        using StreamReader reader = new(input, new UTF8Encoding(false), true, 4096, leaveOpen: true);
        using StreamWriter writer = new(output, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? response = await HandleMcpMessageAsync(line, transport, replyFiles);
            if (response != null)
            {
                await writer.WriteLineAsync(response);
            }
        }
    }

    // Every tool's input schema as tools/list publishes it, by tool name: what ArgumentCheck holds arguments to.
    internal static IReadOnlyDictionary<string, JsonElement> InputSchemas => ToolCatalogue.InputSchemas;

    internal static async Task<string?> HandleMcpMessageAsync(string line, GameTransportSettings transport,
        OutputFolder? output = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            return SerializeRpcError(RequestIds.Recover(line), -32700, $"Parse error: the line is not JSON ({exception.Message})");
        }

        using (document)
        {
            return await HandleRequestAsync(document.RootElement, transport, output ?? OutputFolder.Default);
        }
    }

    private static async Task<string?> HandleRequestAsync(JsonElement root, GameTransportSettings transport,
        OutputFolder output)
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

            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = ReadRequestedProtocolVersion(root),
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = ToolCatalogue.ServerName, version = ServerVersion },
                    instructions = ToolCatalogue.Instructions
                },
                "ping" => new { },
                "tools/list" => new { tools = ToolCatalogue.Tools },
                "tools/call" => await CallToolAsync(root, transport, output),
                _ => throw new McpException(-32601, $"Unknown MCP method '{method}'.")
            };

            return SerializeRpcResult(requestId, result);
        }
        catch (McpException exception)
        {
            return SerializeRpcError(requestId, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[StationGodMCP] {exception}");
            return SerializeRpcError(requestId, -32603, exception.Message);
        }
    }

    // Every tool result, the game's and the sidecar's own refusals alike, has one shape (ToolReplies).
    private static async Task<object> CallToolAsync(JsonElement root, GameTransportSettings transport,
        OutputFolder output)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            throw new McpException(-32602, "tools/call needs params with a string name.");
        }

        string toolName = nameElement.GetString()!;
        if (!ToolCatalogue.Names.Contains(toolName))
        {
            throw new McpException(-32602, $"Unknown StationGodMCP tool '{toolName}'.");
        }

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement value) &&
                                value.ValueKind != JsonValueKind.Null
            ? value.Clone()
            : JsonSerializer.SerializeToElement(new { });

        IReadOnlyList<string> problems = ArgumentCheck.Problems(InputSchemas[toolName], arguments);
        if (problems.Count > 0)
        {
            return ToolReplies.Failure(ToolFailure.Argument(string.Join(" ", problems)));
        }

        arguments = ArgumentCheck.Normalised(InputSchemas[toolName], arguments);

        try
        {
            (ReplyShaping shaping, JsonElement forwarded) = ReplyShaping.Take(arguments);
            GameResponse response = toolName == "sample_logic"
                ? await SampleLogicAsync(transport, forwarded)
                : await SendToGameAsync(transport, toolName, forwarded, shaping.ModShape);
            return response.Ok
                ? ToolReplies.Of(shaping.Apply(toolName, response.Result, output, response.Shaped), isError: false)
                : ToolReplies.Of(response.Error, isError: true);
        }
        catch (ToolFailure failure)
        {
            return ToolReplies.Failure(failure);
        }
        catch (Exception exception)
        {
            return ToolReplies.Failure(ToolFailure.Unavailable(
                $"Could not reach the Stationeers mod through {transport.Description}: {exception.Message}"));
        }
    }

    // shape goes beside params; a mod older than shaping ignores it and answers unshaped (no "shaped" mark).
    private static async Task<GameResponse> SendToGameAsync(GameTransportSettings transport, string method, JsonElement arguments,
        JsonElement? shape = null)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string request = JsonSerializer.Serialize(new
        {
            id = requestId,
            method,
            @params = arguments,
            shape
        }, JsonOptions);

        string responseLine = transport.IsRemote
            ? await SendThroughTcpAsync(transport, request)
            : await SendThroughPipeAsync(transport.PipeName, request);

        return ParseGameResponse(responseLine);
    }

    private static async Task<string> SendThroughPipeAsync(string pipeName, string request)
    {
        using CancellationTokenSource connectTimeout = new(ConnectTimeout);
        await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw ToolFailure.Unavailable(
                $"No StationGodMCP pipe '{pipeName}' answered within {ConnectTimeout.TotalSeconds:0} s: the game is not running, is not hosting a " +
                "loaded save, does not have the StationGodMCP mod loaded, or uses another pipe name (the mod's [Pipe] Name or " +
                "STATIONGODMCP_PIPE_NAME; the sidecar's --pipe), or another client held the pipe that long.");
        }

        using CancellationTokenSource timeout = new(ReplyTimeout);
        using StreamReader reader = new(pipe, new UTF8Encoding(false), true, 4096, leaveOpen: true);
        using StreamWriter writer = CreateWriter(pipe);
        await writer.WriteLineAsync(request.AsMemory(), timeout.Token);
        string? responseLine = await ReadReplyAsync(reader, timeout.Token, $"pipe '{pipeName}'");
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new IOException("The game closed the pipe without returning a response.");
        }

        return responseLine;
    }

    private static async Task<string> SendThroughTcpAsync(GameTransportSettings transport, string request)
    {
        using TcpClient client = new();
        client.NoDelay = true;
        using CancellationTokenSource connectTimeout = new(ConnectTimeout);
        try
        {
            await client.ConnectAsync(transport.Host!, transport.Port, connectTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw ToolFailure.Unavailable(
                $"No StationGodMCP bridge at {transport.Host}:{transport.Port} answered within {ConnectTimeout.TotalSeconds:0} s.");
        }

        using CancellationTokenSource timeout = new(ReplyTimeout);
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, new UTF8Encoding(false), true, 4096, leaveOpen: true);
        using StreamWriter writer = CreateWriter(stream);

        string auth = JsonSerializer.Serialize(new { type = "auth", secret = transport.Secret }, JsonOptions);
        await writer.WriteLineAsync(auth.AsMemory(), timeout.Token);
        string? authResponseLine = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(authResponseLine))
        {
            throw new IOException("The remote StationGodMCP bridge closed the connection during authentication.");
        }

        using (JsonDocument authDocument = JsonDocument.Parse(authResponseLine))
        {
            if (!authDocument.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
            {
                throw new UnauthorizedAccessException("The remote StationGodMCP bridge rejected the shared secret.");
            }
        }

        await writer.WriteLineAsync(request.AsMemory(), timeout.Token);
        string? responseLine = await ReadReplyAsync(reader, timeout.Token, transport.Description);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new IOException("The remote StationGodMCP bridge closed the connection without returning a response.");
        }

        return responseLine;
    }

    // The request has gone out: a timeout here does not mean the game ignored it.
    private static async Task<string?> ReadReplyAsync(StreamReader reader, CancellationToken timeout, string where)
    {
        try
        {
            return await reader.ReadLineAsync(timeout);
        }
        catch (OperationCanceledException)
        {
            throw ToolFailure.Unavailable(
                $"The game took the request through {where} but sent no reply within {ReplyTimeout.TotalSeconds:0} s; " +
                "it may still have run: read the state back before repeating a change.");
        }
    }

    private static StreamWriter CreateWriter(Stream stream)
    {
        return new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    private static GameResponse ParseGameResponse(string responseLine)
    {
        using JsonDocument responseDocument = JsonDocument.Parse(responseLine);
        JsonElement response = responseDocument.RootElement;
        bool ok = response.GetProperty("ok").GetBoolean();
        bool shaped = response.TryGetProperty("shaped", out JsonElement mark) && mark.ValueKind == JsonValueKind.True;
        return new GameResponse(
            ok,
            ok ? response.GetProperty("result").Clone() : default,
            ok ? default : response.GetProperty("error").Clone(),
            shaped);
    }

    private static async Task<GameResponse> SampleLogicAsync(GameTransportSettings transport, JsonElement arguments)
    {
        // gateway_id may be omitted: the mod then reaches the whole world.
        string? gatewayId = null;
        if (arguments.TryGetProperty("gateway_id", out JsonElement gatewayElement) &&
            gatewayElement.ValueKind != JsonValueKind.Null)
        {
            if (gatewayElement.ValueKind != JsonValueKind.String)
            {
                throw ToolFailure.Argument("Argument 'gateway_id' must be a string.");
            }

            gatewayId = string.IsNullOrWhiteSpace(gatewayElement.GetString()) ? null : gatewayElement.GetString();
        }

        if (!arguments.TryGetProperty("targets", out JsonElement targetsElement) ||
            targetsElement.ValueKind != JsonValueKind.Array)
        {
            throw ToolFailure.Argument("Argument 'targets' must be an array of 1 to 32 entries.");
        }

        JsonElement[] targets = targetsElement.EnumerateArray().Select(target => target.Clone()).ToArray();
        if (targets.Length == 0 || targets.Length > 32)
        {
            throw ToolFailure.Argument("Argument 'targets' must be an array of 1 to 32 entries.");
        }

        double durationSeconds = ReadOptionalNumber(arguments, "duration_seconds", 5d);
        double intervalSeconds = ReadOptionalNumber(arguments, "interval_seconds", 0.5d);
        if (durationSeconds < 0.1d || durationSeconds > 30d)
        {
            throw ToolFailure.Argument("Argument 'duration_seconds' must be from 0.1 to 30.");
        }

        if (intervalSeconds < 0.05d || intervalSeconds > 5d)
        {
            throw ToolFailure.Argument("Argument 'interval_seconds' must be from 0.05 to 5.");
        }

        int plannedSamples = (int)Math.Ceiling(durationSeconds / intervalSeconds) + 1;
        if (plannedSamples > 120)
        {
            throw ToolFailure.Argument(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"duration_seconds {durationSeconds} at interval_seconds {intervalSeconds} asks for {plannedSamples} samples (the first at 0 s included); at most 120."));
        }

        JsonElement readArguments = JsonSerializer.SerializeToElement(new
        {
            gateway_id = gatewayId,
            reads = targets
        }, JsonOptions);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        System.Diagnostics.Stopwatch stopwatch = new();
        Dictionary<int, string> previous = new();
        List<object> changes = new();
        int sampleCount = 0;

        while (true)
        {
            GameResponse response = await SendToGameAsync(transport, "read_logic_many", readArguments);
            if (!response.Ok)
            {
                return response;
            }

            if (response.Result.TryGetProperty("gateway_id", out JsonElement scopeElement) &&
                scopeElement.ValueKind == JsonValueKind.String)
            {
                gatewayId = scopeElement.GetString();
            }

            List<JsonElement> changedReadings = new();
            if (response.Result.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement reading in results.EnumerateArray())
                {
                    int index = reading.GetProperty("index").GetInt32();
                    string serialized = reading.GetRawText();
                    if (!previous.TryGetValue(index, out string? prior) || prior != serialized)
                    {
                        previous[index] = serialized;
                        changedReadings.Add(reading.Clone());
                    }
                }
            }

            if (changedReadings.Count > 0)
            {
                changes.Add(new
                {
                    elapsed_seconds = sampleCount == 0 ? 0d : Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                    readings = changedReadings
                });
            }

            sampleCount++;
            if (!stopwatch.IsRunning)
            {
                stopwatch.Start();
            }

            double remainingSeconds = durationSeconds - stopwatch.Elapsed.TotalSeconds;
            if (remainingSeconds <= 0d)
            {
                break;
            }

            double delaySeconds = Math.Min(intervalSeconds, remainingSeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        JsonElement result = JsonSerializer.SerializeToElement(new
        {
            gateway_id = gatewayId,
            started_at_utc = startedAt,
            duration_seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            interval_seconds = intervalSeconds,
            sample_count = sampleCount,
            change_count = changes.Count,
            changes
        }, JsonOptions);
        return new GameResponse(true, result, default);
    }

    private static double ReadOptionalNumber(JsonElement arguments, string name, double defaultValue)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
            double.IsNaN(number) || double.IsInfinity(number))
        {
            throw ToolFailure.Argument($"Argument '{name}' must be a finite number.");
        }

        return number;
    }

    private static string SerializeRpcResult(object? id, object result)
    {
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOptions);
    }

    // An error reply always carries id, null when the request's id could not be read (JSON-RPC 2.0).
    private static string SerializeRpcError(object? id, int code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id ?? NullId,
            error = new { code, message }
        }, JsonOptions);
    }

    private static GameTransportSettings ReadTransportSettings(string[] args)
    {
        string? pipeEnvironment = Environment.GetEnvironmentVariable("STATIONGODMCP_PIPE_NAME");
        string pipeName = ReadArgument(args, "--pipe") ??
                          (string.IsNullOrWhiteSpace(pipeEnvironment) ? "StationGodMCP" : pipeEnvironment.Trim());
        string? host = ReadArgument(args, "--host") ?? Environment.GetEnvironmentVariable("STATIONGODMCP_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return GameTransportSettings.ForPipe(pipeName);
        }

        string portText = ReadArgument(args, "--port") ?? Environment.GetEnvironmentVariable("STATIONGODMCP_PORT") ?? "8765";
        if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
        {
            throw new ArgumentException($"StationGodMCP remote port '{portText}' must be between 1 and 65535.");
        }

        string secretEnvironmentName = ReadArgument(args, "--secret-env") ?? "STATIONGODMCP_SECRET";
        string? secret = Environment.GetEnvironmentVariable(secretEnvironmentName);
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException($"Remote StationGodMCP requires a shared secret in environment variable '{secretEnvironmentName}'.");
        }

        return GameTransportSettings.ForTcp(host, port, secret);
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string ReadRequestedProtocolVersion(JsonElement root)
    {
        if (root.TryGetProperty("params", out JsonElement parameters) &&
            parameters.TryGetProperty("protocolVersion", out JsonElement requested) &&
            requested.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(requested.GetString()))
        {
            return requested.GetString()!;
        }

        return ProtocolVersion;
    }

    private sealed record GameResponse(bool Ok, JsonElement Result, JsonElement Error, bool Shaped = false);

    internal sealed record GameTransportSettings(string PipeName, string? Host, int Port, string? Secret)
    {
        internal bool IsRemote => !string.IsNullOrWhiteSpace(Host);
        internal string Description => IsRemote ? $"TCP endpoint {Host}:{Port}" : $"pipe '{PipeName}'";

        internal static GameTransportSettings ForPipe(string pipeName) => new(pipeName, null, 0, null);
        internal static GameTransportSettings ForTcp(string host, int port, string secret) => new(string.Empty, host, port, secret);
    }

    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
