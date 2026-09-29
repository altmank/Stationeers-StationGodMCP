using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StationGodMCP.Server;

internal static class Program
{
    private const string ServerName = "StationGodMCP";
    // Reported in the initialize response. build.ps1 checks it matches StationGodMCP.Server.csproj and the mod.
    private const string ServerVersion = "1.4.4";
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

            string? response = await HandleMcpMessageAsync(line, transport);
            if (response != null)
            {
                await writer.WriteLineAsync(response);
            }
        }
    }

    // Every tool's input schema as tools/list publishes it, by tool name: what ArgumentCheck holds arguments to.
    internal static readonly Dictionary<string, JsonElement> InputSchemas = JsonSerializer
        .SerializeToElement(ToolDefinitions.All, JsonOptions)
        .EnumerateArray()
        .ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => tool.GetProperty("inputSchema").Clone());

    internal static async Task<string?> HandleMcpMessageAsync(string line, GameTransportSettings transport)
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
            return await HandleRequestAsync(document.RootElement, transport);
        }
    }

    private static async Task<string?> HandleRequestAsync(JsonElement root, GameTransportSettings transport)
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
                    serverInfo = new { name = ServerName, version = ServerVersion },
                    instructions = "Device and IC tools reach every device in the world: omit gateway_id or pass 'world'. A StationGod Gateway id narrows a call to the devices on that gateway's data networks. Start with list_devices or describe_device before reading or writing logic. Inventory, clock and console tools need no gateway."
                },
                "ping" => new { },
                "tools/list" => new { tools = ToolDefinitions.All },
                "tools/call" => await CallToolAsync(root, transport),
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
    private static async Task<object> CallToolAsync(JsonElement root, GameTransportSettings transport)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            throw new McpException(-32602, "tools/call needs params with a string name.");
        }

        string toolName = nameElement.GetString()!;
        if (!ToolDefinitions.Names.Contains(toolName))
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
            GameResponse response = toolName == "sample_logic"
                ? await SampleLogicAsync(transport, arguments)
                : await SendToGameAsync(transport, toolName, arguments);
            return ToolReplies.Of(response.Ok ? response.Result : response.Error, isError: !response.Ok);
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

    private static async Task<GameResponse> SendToGameAsync(GameTransportSettings transport, string method, JsonElement arguments)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string request = JsonSerializer.Serialize(new
        {
            id = requestId,
            method,
            @params = arguments
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
        return new GameResponse(
            ok,
            ok ? response.GetProperty("result").Clone() : default,
            ok ? default : response.GetProperty("error").Clone());
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

    private sealed record GameResponse(bool Ok, JsonElement Result, JsonElement Error);

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

internal static class ToolDefinitions
{
    private const string IcHolderIdDescription = "The circuit holder, by any of: an IC Housing or a worn one (suit, Programmable Visor) from list_devices; a Console or Computer holding a ScriptedScreens Lua board (Circuitboard or Motherboard (Lua Chip)), or a held tablet holding one Lua cartridge; the board or cartridge itself; or the chip in any holder. inspect_slots on the console or tablet lists the board, cartridge and chip ids. Anything else, and a chip in no holder, is refused with not_ic_housing. Replies give reference_id (the device or worn item the scope reaches), holder (the circuit holder itself) and chip.";

    private const string GatewayIdDescription = "Optional filter. Omit, or pass 'world', for any device in the world (an empty string counts as omitted; spaces around an id are ignored); a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks. Refused with gateway_not_found when no StationGod Gateway has that id, and with gateway_unavailable when the gateway cannot scope a call now; the message names why: incomplete (not fully built) or no_data_network (no data cable on either port).";

    // move_gas from and to: a reference id or "planet", or a room by room_id or by a thing in it.
    private static readonly object[] GasPlaceSchema =
    {
        new { type = "string" },
        new
        {
            type = "object",
            properties = new
            {
                room_id = new { type = "string", description = "A room's room_id, as the rooms tool reports it." },
                room_of = new { type = "string", description = "Reference id of a thing in the room (the player: the current room)." }
            },
            minProperties = 1,
            maxProperties = 1,
            additionalProperties = false
        }
    };

    internal static readonly HashSet<string> Names =
    [
        "list_gateways",
        "list_devices",
        "describe_device",
        "read_logic",
        "write_logic",
        "read_logic_many",
        "write_logic_many",
        "read_memory",
        "write_memory",
        "inspect_slots",
        "network_snapshot",
        "sample_logic",
        "get_ic_source",
        "set_ic_source",
        "get_ic_status",
        "control_ic_execution",
        "resolve_ic_selectors",
        "run_console_command",
        "read_console",
        "game_clock",
        "find_items",
        "find_things",
        "label",
        "item_totals",
        "list_containers",
        "container_contents",
        "player_vitals",
        "consumables",
        "atmosphere_contents",
        "water_sources",
        "trader_contacts",
        "dish_aim",
        "trader_inventory",
        "plants",
        "reagents",
        "planet",
        "set_ic_pins",
        "solar_aim",
        "thing_health",
        "paint",
        "outer_frames",
        "rooms",
        "weather",
        "ignition_risk",
        "looking_at",
        "connections",
        "plant_genes",
        "mod_info",
        "move_gas",
        "landing_pads",
        "move_item",
        "upgrade_cables",
        "upgrade_pipes",
        "clean_cables",
        "clean_pipes",
        "replace_walls",
        "replace_frames",
        "place_cables",
        "remove_cables",
        "place_pipes",
        "remove_pipes",
        "place_chutes",
        "remove_chutes",
        "place_structure",
        "describe_prefab",
        "wall_map",
        "find_spot",
        "lint_layout",
        "check_replaceable",
        "show_preview",
        "undo_job",
        "remove_structure",
        "grid_survey",
        "plan_cable_route",
        "plan_pipe_route",
        "plan_chute_route",
        "plan_removal",
        "feed_paths",
        "trader_buy",
        "trader_sell",
        "paste_blueprint",
        "vault_contents",
        "vault_deposit",
        "vault_withdraw"
    ];

    internal static readonly object[] All =
    [
        Tool(
            "list_gateways",
            "List the scopes device tools accept as gateway_id: first 'world', every device in the world (status 'bypass', kept for older clients), then every StationGod Gateway in the loaded world with its availability and network status; a gateway id only narrows a call to that gateway's data networks. status is bypass (world), ready (available true: its id scopes a call), incomplete (not fully built) or no_data_network (no data cable on either port); a device tool given an unavailable gateway answers gateway_unavailable naming that status, an unknown id gateway_not_found. bypass_gateway is always true, kept for older clients.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "list_devices",
            "List every device in the world, or with a gateway_id only those on that gateway's data networks. Reference IDs are returned as strings.",
            new
            {
                type = "object",
                properties = new
                {
                    gateway_id = new { type = "string", description = GatewayIdDescription },
                    prefab_hash = new { type = "integer", description = "Optional exact signed PrefabHash filter." },
                    name_contains = new { type = "string", description = "Optional case-insensitive DisplayName substring filter." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "describe_device",
            "Describe one network device and dynamically enumerate every vanilla logic type it reports as readable or writable.",
            DeviceInputSchema(includeLogicType: false, includeValue: false),
            readOnly: true),
        Tool(
            "read_logic",
            "Read a generic logic value from a device using a logic-type name or numeric ID. Any device in the world; a gateway_id limits it to devices on that gateway's data networks.",
            DeviceInputSchema(includeLogicType: true, includeValue: false),
            readOnly: true),
        Tool(
            "write_logic",
            "Write a generic logic value to a device using a logic-type name or numeric ID. Any device in the world that supports writing that type; a gateway_id limits it to devices on that gateway's data networks. Returns requested_value, previous_value and current_value: current_value is read back at once and can differ from the request, because the device clamps it (On 7 reads 1), ignores it (a paint-only colour index), or moves toward it over time (a solar panel's angle); the write still succeeds.",
            DeviceInputSchema(includeLogicType: true, includeValue: true),
            readOnly: false),
        Tool(
            "read_logic_many",
            "Read up to 256 generic logic values in one main-thread request. Each operation returns its own result, by index: {index, ok, reference_id, logic_type, value}, or on failure {index, ok: false, reference_id, logic_type, error: {code, message}} where reference_id and logic_type are null when the entry's own could not be read.",
            BulkLogicSchema("reads", includeValue: false),
            readOnly: true),
        Tool(
            "write_logic_many",
            "Write up to 256 generic logic values in order in one main-thread request. Each operation returns its own result, by index: {index, ok, reference_id, logic_type, requested_value, previous_value, current_value} (current_value as write_logic reads it back), or on failure {index, ok: false, reference_id, logic_type, error: {code, message}} where reference_id and logic_type are null when the entry's own could not be read.",
            BulkLogicSchema("writes", includeValue: true),
            readOnly: false),
        Tool(
            "read_memory",
            "Read a contiguous range of up to 512 values from a visible device with readable memory, as a chip's get instruction does: an IC Housing or suit (its chip's stack), a Logic Sorter, a satellite dish, a fabricator, rocket avionics and the like. A Logic Memory has only its Setting, no memory (memory_not_readable). Refused: a range past the memory's last address (stack_size in the reply; invalid_argument), and an IC Housing or suit with no chip (no_programmable_chip).",
            MemoryReadSchema(),
            readOnly: true),
        Tool(
            "write_memory",
            "Write a contiguous range of up to 512 finite values to a visible device with writable memory, as a chip's put instruction does (the devices read_memory lists; a Logic Memory has none: memory_not_writable). Refused before anything is written: a range past the memory's last address (invalid_argument), and an IC Housing or suit with no chip (no_programmable_chip).",
            MemoryWriteSchema(),
            readOnly: false),
        Tool(
            "inspect_slots",
            "Inspect generic device slots, occupants, slot constraints (interactable false: a hidden slot, which move_item refuses), and every readable LogicSlotType value without changing inventory contents. Devices only (things with logic): a crate, a lander or a tool answers device_not_found saying it is not a device; container_contents shows the slots of any thing.",
            SlotInspectionSchema(),
            readOnly: true),
        Tool(
            "network_snapshot",
            "Capture one main-thread snapshot of filtered network devices and their readable logic values. Filters may use reference IDs, prefab hash, display-name text, and selected logic types.",
            NetworkSnapshotSchema(),
            readOnly: true),
        Tool(
            "sample_logic",
            "Sample up to 32 logic values for at most 30 seconds and return their initial readings plus timestamped changes.",
            SampleLogicSchema(),
            readOnly: true),
        Tool(
            "get_ic_source",
            "Read the source stored in the programmable chip of a visible circuit holder: IC10, or Lua for a StationeersLua chip (Integrated Circuit (Lua)) in an IC Housing, a ScriptedScreens board in a Console or Computer, a tablet cartridge or a Programmable Visor. reference_id may name the holder, the device or worn item holding it, or the chip (see the reference_id description). Returns source, source_length, language (ic10 or lua), holder, chip, line_number (IC10 only) and, for a Lua chip, lua (see get_ic_status). A holder with no programmable chip is refused with no_programmable_chip.",
            IcHolderSchema(),
            readOnly: true),
        Tool(
            "set_ic_source",
            "Write source to the programmable chip of a visible circuit holder, as the IC editor's export does. IC10: compiled at once and restarted at line 0; a paused chip stays paused (at line 0), and registers and the stack are kept (sp is reset to 0). The chip stores IC10 source as ASCII and runs what it stores: CRLF line ends become LF and each non-ASCII character '?' (warnings crlf_normalised, non_ascii_replaced). The chip runs source of any length, but the in-game editor holds 128 lines of up to 90 characters and 4096 characters in all, and cuts a longer source when a player opens and submits it: warnings over_editor_lines, over_editor_line_length, over_editor_size (the source is still written). compilation_error true comes with compile_error_line (0-based) and compile_error_type; error_line and error_type are the runtime error's. warnings is [] for Lua. Lua (a StationeersLua chip in an IC Housing, a ScriptedScreens Console or Computer board, a tablet cartridge or a Programmable Visor): no IC10 line or byte limit; up to 262144 characters (larger is refused with source_too_large). StationeersLua stores it compressed, drops the old runtime and compiles the new source on a worker thread, running its module-level code once and then tick(dt) every game tick; a source that failed before is compiled again. No separate restart is needed. The reply's lua.compiling is usually still true: call get_ic_status until it is false, then check lua.running and lua.last_error. A holder that is off or unpowered compiles when it runs again. A holder with no programmable chip is refused with no_programmable_chip. Writes; sends the chip to multiplayer clients.",
            IcSourceInputSchema(),
            readOnly: false),
        Tool(
            "get_ic_status",
            "Inspect a visible circuit holder's source, current instruction, registers, stack window, aliases, defines, jump tags, power state, pause state, compile/runtime diagnostics, and its device pins d0..d5 (pins: each pin's device reference ID, prefab and display name or null when empty, the alias the chip gave the pin, and reachable, false when the chip cannot reach the device because it is not on the housing's data network). Holders: IC Housings, suits and other worn holders, and StationeersLua / ScriptedScreens Lua holders (a Console or Computer board, a tablet cartridge, a Programmable Visor). housing.kind is ic_housing, worn_item, computer_board, cartridge or inserted_item; operable says whether the holder runs its chip now (a board: its computer on, powered and fully built; a cartridge: its tablet on and powered; a suit or visor: a charged battery). Also language (ic10 or lua), holder, chip and source_length. For a Lua chip, lua: compiling (a worker thread is compiling it; read again), has_runtime, init_complete (module-level code done, tick(dt) running), running (all of these, no error, not a library), library (a --@module chip other chips require), source_version, last_error {kind compile or runtime, line, message, traceback} or null, log {lines (the last log_lines print() lines), line_count, truncated}, and unavailable when StationeersLua's internals could not be read. compile_error_line (0-based) and compile_error_type say where compiling failed (null while the source compiles); error_line and error_type are the last runtime error's. A register or stack value that is not finite is a string (\"NaN\", \"Infinity\", \"-Infinity\"). Registers, stack and line fields are IC10's and mean nothing for a Lua chip. A holder with no chip answers only has_chip false, reference_id, gateway_id, holder and pins (no housing, power or runtime fields); stack_start, stack_count and log_lines are still checked.",
            IcStatusSchema(),
            readOnly: true),
        Tool(
            "control_ic_execution",
            "Pause, execute exactly one IC10 instruction while remaining paused, or resume the chip of a visible circuit holder; or restart a Lua chip. Pausing holds only IC Housings and suits; other holders (toolbelts, tablets, mining robots, logic I/O devices) report paused but keep running. A Lua chip (StationeersLua) cannot pause or step (lua_chip_unsupported): restart compiles its current source again and runs it from the start, clearing a latched error, as the Lua debugger's restart does; like set_ic_source it compiles on a worker thread, so poll get_ic_status. restart on an IC10 chip is refused (not_a_lua_chip). step on a running chip pauses it first; step is refused, with nothing changed, while the source has a compile error (ic_compile_error) or the holder is off, unpowered or not built (ic_not_operable). action ignores case. set_ic_source leaves a paused chip paused. A holder with no programmable chip is refused with no_programmable_chip.",
            IcExecutionControlSchema(),
            readOnly: false),
        Tool(
            "resolve_ic_selectors",
            "Resolve a circuit holder's db/d0... pins and compiled aliases (IC10), and report prefab/name-hash selectors (lbn/sbn) for the devices its batch instructions reach: lb, lbn, sb and sbn walk only the holder's data network (an IC Housing's), so stable_selectors defaults to the devices on it the scope shows, batch_device_count is how many it holds (null: no data network, and batch instructions fail with DeviceListNull), and a selector is unique when exactly one device on that network has the pair. target_reference_ids lists just those devices, each of which must be a device the scope shows (device_not_found otherwise); one off the network is listed with reachable false and unique false. For a Console or Computer Lua board, db is the computer. Needs a chip in the holder (no_programmable_chip otherwise); get_ic_status lists the pins of an empty holder. Arguments are checked first, so a malformed id in target_reference_ids is invalid_argument on any holder; the holder itself is checked (not_ic_housing, no_programmable_chip) before the ids are looked up.",
            IcSelectorSchema(),
            readOnly: true),
        Tool(
            "run_console_command",
            "Run any Stationeers console command on the game's main thread and return the console lines it printed. No gateway is needed. This is unrestricted: the console can spawn and delete things, teleport players, start storms, change world settings and quit the game, and nothing here is undoable. Scope refusals ('requires creative mode', 'not in game') and bad arguments come back as red output lines rather than as a call error. On a network client some commands only queue a request to the host; run_simulation reports which case applies. Output covers only what the command itself printed: lines a background thread logged at the same moment (Unity mirrors errors into the console from any thread) are reported as concurrent_log_lines and can be read with read_console. Commands that finish on a background task (save, load, new, file, reset, log, difficulty, exportworld, upnp, steam) return before printing anything: the reply sets completes_asynchronously and output_complete=false, and an empty output then proves nothing at all. Never report such a command as succeeded on the strength of this call; read_console a moment later and quote what it printed.",
            ConsoleCommandSchema(),
            readOnly: false),
        Tool(
            "read_console",
            "Read the most recent lines from the in-game console, oldest first, including command output, Unity errors and their stack traces. No gateway is needed.",
            ConsoleReadSchema(),
            readOnly: true),
        Tool(
            "game_clock",
            "Read the game clock: game_time_s (seconds, stops while paused, restarts from 0 on every launch), paused, time_of_day_ratio (fraction of the local day, 0 to 1) and days_past. No gateway is needed. Values that stop changing between reads mean the game is paused, not that a device is stuck.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "find_items",
            "Find items anywhere in the world, not only on a gateway's network: on the ground, in lockers, crates and machines, and carried by players (suit, backpack, toolbelt, hands, nested to any depth). Each item reports its quantity (stack size for stackables, 1 otherwise), location (ground, player or stored), the chain of holders from its own slot outwards, the outermost holder's position and the distance from the local player. Also lists material machines hold as reagent stock rather than as items, with location machine_stock: ingots loaded into a fabricator (Autolathe, Electronics Printer, Pipe Bender, Tool Manufactory...) listed as the ingot it ejects (1 reagent unit = 1 ingot), and a furnace's, centrifuge's or mixer's working load listed under its reagent name (prefab_name null). A stock entry has reference_id null, held_in naming the machine with slot_index -1, and machine_stock {reagent, reagent_name, kind (fabricator or processing), movable false, how_to_get}: move_item cannot move it; open the fabricator (on, powered, not printing) to eject it as ingots. Sorted nearest first. Organs are left out. Read only; no gateway is needed.",
            ItemFilterSchema(includePaging: true),
            readOnly: true),
        Tool(
            "find_things",
            "Find anything in the world by name, not only items: portable tanks and canisters, crates and other movable things, structures and devices (tanks, lockers, pipes, frames), items, players and animals. name_contains matches the name the game shows, which is the Labeller name when the thing has one, and also the game's own name under a label, so 'T1' and 'Portable Liquid Tank' both find a tank labelled T1. Each thing reports reference_id, prefab_name, display_name, custom_name (the Labeller name, null if none), game_name (the game's name for the prefab), kind (item, dynamic = a movable thing that is not an item: portable tanks, crates, generators, rovers; structure, entity or other), runtime_type, labelable (the Labeller can rename this class of thing: portable things, every device, the big in-line tanks (StructureInLineTank; the pipe-size InLineTank ones cannot), hydroponic trays, plants, IC chips, flags and a few more; plain pipes, cables, frames and ordinary items cannot), location (ground, player or stored as find_items has it, built for a structure), carried_by, held_in (holders from its own slot outwards), position (the outermost holder's), distance_m, is_device (the device tools take it), has_atmosphere (atmosphere_contents has something for it) and, for structures (1.3.5+), rotation {facing (its front: +x, -x, +y, -y, +z, -z), up, euler {x, y, z} (degrees)} in the forms place_structure takes, so it can be placed again as it stands or turned (facing reversed: 180 degrees; facing and up are null for a piece turned off the grid's axes). Also scanned (things in the world). Sorted nearest first. Examples: all portable tanks, gas and liquid, whatever their prefab or label: runtime_type DynamicGasCanister; everything that holds gas: has_atmosphere true; every label in the world: labelled_only true; every wrecked structure: broken true, kind structure. Each thing also reports is_broken and condition (1.4.3+): is_broken is the game's own broken state (Thing.IsBroken: at maximum damage, or a structure in its broken build state; a burst pipe too, whose damage stays 0), and condition is broken, damaged, intact, indestructible or none (no damage state). A structure the game has broken is healed to 0 damage and reads 100 % health, so is_broken, not a health number, is the signal; remove_structure removes one with allow_broken. find_items stays the tool for item quantities and machine stock. Organs are left out. Read only; no gateway is needed. Print provenance (1.4.3+): every item a fabricator, printer or other machine makes is recorded as it is made (host, in memory since the game started; a stack split off a printed one keeps its record): each thing reports made {maker_id, maker_prefab, maker_name, game_time_s, quantity, split_from} when the log has it, and made_by / made_since filter on it.",
            new
            {
                type = "object",
                properties = new
                {
                    name_contains = new { type = "string", description = "Case-insensitive substring of the shown name (the label when there is one) or of the game's own name, e.g. 'T1', 'Nitrogen', 'Liquid Tank'." },
                    prefab_contains = new { type = "string", description = "Case-insensitive substring of the prefab name, e.g. 'DynamicGasCanister', 'Locker', 'StructurePipe'." },
                    kind = new { type = "string", @enum = new[] { "any", "item", "dynamic", "structure", "entity", "other" }, description = "Only things of this kind. Default any." },
                    labelled_only = new { type = "boolean", description = "Only things that carry a Labeller name. Default false." },
                    runtime_type = new { type = "string", description = "Only things whose class is this or derives from it, by exact class name ignoring case, e.g. 'DynamicGasCanister' (every portable gas or liquid tank and canister), 'PortableAtmospherics', 'DraggableThing', 'Device', 'Pipe'. runtime_type in the reply names each thing's own class." },
                    broken = new { type = "boolean", description = "true: only things in the game's broken state (is_broken; e.g. burnt-out vents, which read 100 % health, burst pipes, and burnt cables an overload left); false: only things not broken." },
                    has_atmosphere = new { type = "boolean", description = "true: only things atmosphere_contents has something for (an internal atmosphere, a pipe network, a landing pad network, a connected network, or something with an atmosphere in a slot); false: only things without." },
                    made_by = new { description = "1.4.3+: only items the print log has from this maker: its reference id, or text in its prefab or shown name (e.g. 'Fabricator', 'Autolathe')." },
                    made_since = new { type = "number", description = "1.4.3+: only items the print log has made at or after this game time (game_clock game_time_s), or, negative, within that many seconds before now." },
                    near_player_m = new { type = "number", exclusiveMinimum = 0, description = "Only things (or their outermost holder) within this many metres of the local player." },
                    limit = new { type = "integer", minimum = 1, maximum = 500, description = "Things per page, default 100." },
                    offset = new { type = "integer", minimum = 0, description = "Things to skip, for paging." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "label",
            "Rename a thing as the hand Labeller does, with the game's own rename (Thing.RenameThing on the host, which syncs to clients and is saved; a multiplayer client sends the rename to the host). The name is written as the Labeller writes it: an empty name becomes the game's name for the prefab (the game stores that name rather than clearing the label), cut to 200 characters, rich-text tags stripped except on signs and labels. Only things the Labeller can rename: portable tanks and other movable things, every device, the big in-line tanks (StructureInLineTank), hydroponic trays, plants, IC chips, flags and a few more (find_things reports labelable). One rename: reference_id and name. Or labels: up to 64 {reference_id, name}, applied in order. A rename returns {index, ok, reference_id, prefab_name, written (the name stored), sent_to_host (true on a multiplayer client: current shows the name once the host's update arrives), previous {display_name, custom_name}, current {display_name, custom_name}}, read back from the thing; a refusal {index, ok: false, reference_id, error {code, message}} changes nothing. The batch form returns results, count, success_count and error_count; the single form returns the rename, or the refusal as the error. Refusals: thing_not_found, not_labelable (a class the Labeller cannot rename: pipes, cables, frames, ordinary items, and the pipe-size in-line tanks, class InLineTank, e.g. StructureInLineTankGas1x2: the game has no rename for them and StationGod keeps no names of its own; label a sign or device beside one instead), invalid_argument. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of the thing to rename (find_things, looking_at, list_devices)." },
                    name = new { type = "string", description = "The new name; empty restores the game's name." },
                    labels = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 64,
                        description = "Several renames, applied in order; use instead of reference_id and name.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                reference_id = new { type = "string" },
                                name = new { type = "string" }
                            },
                            required = new[] { "reference_id", "name" },
                            additionalProperties = false
                        }
                    }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "item_totals",
            "Total quantity of each item type in the world, split into on_ground, carried, stored and machine_stock (material held as reagents inside machines, e.g. electrum ingots loaded into a Pipe Bender, counted as the ingots the machine ejects; a furnace's or centrifuge's working load gets a row of its own with prefab_name null and reagent set), with the five holders that hold the most, each with kind (player, stored or machine_stock) and position. quantity includes machine_stock; item_count counts items only and machine_stock_entries the machine and reagent pairs. Takes the same filters as find_items, so it answers 'how much iron ingot do I have and where'. Read only; no gateway is needed.",
            ItemFilterSchema(includePaging: false),
            readOnly: true),
        Tool(
            "list_containers",
            "List every outermost holder that has at least one item in it (lockers, crates, machines, a backpack on the floor), not held by a player, with slots used, item totals per type, position and distance, nearest first. A holder inside another (a crate in a lander, a box in a locker) is not listed on its own: its items count towards the outermost holder, and prefab_contains and name_contains match that holder; use container_contents or find_items within_id to look inside. Empty containers are not listed. Read only; no gateway is needed.",
            ContainerListSchema(),
            readOnly: true),
        Tool(
            "container_contents",
            "Show the slots of any one thing in the world and what is in them, nested: a locker, crate, machine, suit, backpack, or 'player' for the local player's whole inventory. Works whether or not the thing is on a data network. position and distance_m are where the thing is in the world: for a thing in a slot, its outermost holder's (a stored item keeps no position of its own). Read only; no gateway is needed.",
            ContainerContentsSchema(),
            readOnly: true),
        Tool(
            "player_vitals",
            "The local player's hunger and thirst: nutrition and hydration now and their capacities, food quality and its multiplier, mood, sleeping, brain online, helmet closed, the temperature thirst depends on right now and where it is taken (suit, room or world), the difficulty's hunger rate, hydration rate and offline metabolism, and the drain per game second for hunger and for thirst computed with the game's own per-tick formulas (Human.LifeNutrition, Human.LifeDehydrate): right now (0 while lying in a bed, a powered sleeper or a working cryo tube, halved while sleeping elsewhere) and when up and about, each with the seconds until the body's store reaches 0 at that rate (time_left_s, time_left_awake_s; thirst_temperature has temperature_k). Food and water carried or stored are not counted here; see consumables and water_sources. Read only; no gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "consumables",
            "Every food and drink in the world wherever it is (carried, stored, on the ground, inside boxes and packages), with its holders and location like find_items. Food: nutrition per unit and in total, food quality and seconds until it decays (decay_time_left_s). Drinks (water bottles and packets): liquid_l and the hydration they give. Boxes and packages (cereal bar boxes, water bottle packages) hold real items, so their contents are counted and listed per package. Decayed food, seeds, plants still growing in a tray, pills and empty drinks are listed as not counted. Totals of nutrition and hydration, and how much of each is still packed. Read only; no gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "atmosphere_contents",
            "What gas or liquid one thing holds: a canister, portable tank, tank, suit, a pipe (its whole pipe network), any landing pad piece (the pad network's one shared atmosphere, source landing_pad_network), a device (every pipe network it is connected to) and the canisters in its slots (a tank storage, an air conditioner), or a pipe or landing pad network by its own reference id, or an atmosphere_id from water_sources or an earlier reply. Each atmosphere has a source (internal, pipe_network, landing_pad_network, connected_network or slot) and lists every gas and liquid with amount_mol and, for liquids, liquid_l, plus volume_l, pressure_kpa, temperature_k and total_mol, and a water summary: liquid water (liquid_mol, liquid_l and the hydration drinking it would give), polluted water (polluted_mol, polluted_l) and steam (steam_mol, steam_if_condensed_l). Owners have kind item, structure, dynamic (a portable tank: find_things' kinds) or pipe_network. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of a thing (find_items, list_devices, list_containers), of a pipe or landing pad network, or an atmosphere_id (water_sources)." }
                },
                required = new[] { "reference_id" },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "water_sources",
            "Every canister, tank, device and pipe network in the world that holds water, polluted water or steam, with its holder or its network's devices, location, pressure, temperature and the water in moles, litres and hydration; largest first, with totals. Room and world air, bodies and organs are left out, and so are bottles and packets (see consumables). A pipe network is listed from the moment it exists, even while paused; a thing's own atmosphere made since the last atmospherics tick (a canister spawned while paused) is listed from the next tick on. owner.kind: item, structure, dynamic (a portable tank) or pipe_network. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    min_mol = new { type = "number", exclusiveMinimum = 0, description = "Skip sources holding less water, polluted water and steam together than this; default 1 mol (0.018 litres). The old name min_moles is still read." },
                    min_moles = new { type = "number", exclusiveMinimum = 0, deprecated = true, description = "Deprecated: the old name of min_mol, still read (min_mol wins when both are given)." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "trader_contacts",
            "Returns game_time_s, contacts and dishes. List every trader contact in the sky (trader type, display name, shuttle, landing pad size it needs (pad_size_tiles), unit direction vector from the base with y up, elevation, watts needed to resolve and to contact (min_power_to_resolve_w, min_power_to_contact_w), whether it is already contacted, seconds left before it leaves (time_left_s)) and every satellite dish with where it points: forward is the vector the game scores contacts against (angle error = acos(dot(forward, direction))), only updated when the dish moves, and transform_up is its current pose. Also the dish's current horizontal and vertical in degrees, whether it turns now (finished to its last build state, powered, on, and can_rotate: all three, the game's own check; a dish that cannot rotate never moves toward a written angle), its field_of_view_deg and its wattage range (min_power_w, max_power_w). Read only; no gateway is needed. Whether a contact fits and can land on a pad: see landing_pads.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "dish_aim",
            "Work out the exact Horizontal and Vertical (logic degrees) that point a satellite dish at a trader contact, found on the dish's own model: the mod poses the dish's model at trial angles (through its animator; the Small Satellite Dish, which has none, through its pivots), reads where it would point and restores it within one frame, so nothing moves or changes. Returns horizontal, vertical, the angle left over (error_deg; under 2 degrees the contact gets the dish's whole Setting), finished, powered, on and can_rotate (all three: the game's own check for whether the dish turns now), the dish's current angles and error (current.forward is the game's DishForward; the Small dish sets it at each step of a turn just before moving, so it trails the model by that last step, up to a few tenths of a degree once the turn ends), stale_pose_deg (above 1 means the dish's animator was not being evaluated, so the game's own pointing may lag) and samples (how many trial poses the search took). To turn the dish, write the returned values to its Horizontal and Vertical with write_logic, with the dish powered and on first: a dish that cannot rotate stores the angles as its target but never turns, and the game ignores a write of the target it already holds, so writing the same angles again after powering it does nothing (write another value first). Refusals: invalid_argument, thing_not_found (no satellite dish has that id, including an id that is not a dish), contact_not_found, dish_not_ready (the dish's model has no pivots or does not move when posed). Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    dish_id = new { type = "string", description = "Reference ID of the satellite dish (see trader_contacts dishes)." },
                    contact_id = new { type = "string", description = "Reference ID of the trader contact (see trader_contacts contacts)." }
                },
                required = new[] { "dish_id", "contact_id" },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "trader_inventory",
            "What each trader contact in the sky buys and sells: item or gas name, prefab, credits per unit, how many it wants or has in stock, the conditions a sold-to-it item or gas must meet (purity, moles per unit, temperature range), and for each item it buys how many of that item's prefab exist in the world outside the trader as items (have: 0 when none, null for a gas line; counted by prefab without the trader's conditions, so every 'Box of ...' line counts all CardboardBox items; ingots loaded into a machine as stock are not counted, since the trader cannot take them until they are ejected) and, for the landed trader only, how many it would take right now (sellable: what the pad network's vending machines and you carry that meets its conditions, or the pad network's gas in units, the trade window's own count; trader_sell counts the same with the card holder's inventory in place of yours, so the two agree when the card is yours; null for a contact not landed). The game rolls a trader's inventory when the contact appears, so this works before the trader is interrogated. Omit contact_id for every contact. Refusals: invalid_argument, contact_not_found. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    contact_id = new { type = "string", description = "Reference ID of one trader contact (see trader_contacts); omit for all." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "plants",
            "Every plant growing in a hydroponics tray, planter or station, read from the plant itself, so it works for trays with no data port: its tray, growth stage (index, count, first mature and seeding stages, progress and length of the current stage, every stage's length), whether it is mature, seeding, dead or ready to harvest, growth efficiency and its parts (breathing, temperature, hydration, pressure, light), active problems in plain words (dry, too cold, harmful gas...), each condition's running time (time_s) against the time after which it starts damaging the plant, damage per type with damage_ratio (0 unharmed, 1 dead) and health_percent, harvest and seed counts with a forecast of the next harvest from recorded stress, nutrition, fertiliser, light exposure, the air it breathes (pressure, temperature_k, gas ratios), its tray's water, its needs (ideal and survivable temperature_k and pressure_kpa, gases taken in (mol_per_tick) with the share of the air each needs, water_mol_per_tick, light and dark seconds per day, harmful gas limits) and forecasts at the current efficiency: seconds to the next stage, to harvest, to seeds and, for perennials, to regrow after a harvest (null when it is not growing). Times are game seconds (game_time_s); efficiency parts are multipliers (growth_factor, breathing_factor...); temperatures are kelvin. include_unplanted adds every other plant item (harvested crops, seed bags). Errors: plant_not_found (no plant has that reference_id: no such thing, or not a plant), invalid_argument. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of one plant (see find_items or container_contents of its tray); omit for all." },
                    include_unplanted = new { type = "boolean", description = "Also list plant items that are not planted: crops in lockers, seed bags. Default false." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "reagents",
            "The reagents a thing holds, one by one: a furnace's or arc furnace's melted load (Iron, Carbon, Silicon...), a centrifuge's, a mixer's, a microwave's. Logic only gives the total (Reagents). Returns reference_id, prefab_name, display_name, total (every reagent's quantity added, as the game adds them, units mixed) and reagents: [{reagent (type name), name, quantity, unit (the game's unit for that reagent: g, or ml for alcohol, milk and oil)}]. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of the furnace or other thing." }
                },
                required = new[] { "reference_id" },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "planet",
            "The planet's own atmosphere, the one every outdoor cell relaxes toward. Before a world is loaded it returns only loaded false and terraforming_mod. Otherwise: loaded true, world_time_s (days past plus time of day, in seconds), day_length_s, volume_l, gas_volume_l, cells (outdoor 2 m cells), size_ratio (of the size it shipped with), gas_mol, liquid_mol, liquid_l, sea {liquid_l, threshold_l (the volume at which the sea is drawn), shown}, pressure_kpa, game_pressure_kpa, temperature_k, temperature_parts {sun_angle_k, sun_distance_k, greenhouse_k, density_k, weather_k, latent_k, external_k}, sun_angle_deg, solar_energy_percent, range {today_min_k, today_min_sun_angle_deg, today_max_k, today_max_sun_angle_deg, orbit_min_k, orbit_max_k} (the game's own formula swept over every sun angle), storm {id} or null, oxygen_kpa, toxins_kpa (human toxins), fuel_mol, oxidiser_mol (fire risk outdoors), carbon_dioxide_ratio, gases [{gas, display_name, state, amount_mol, per_cell_mol, partial_kpa, freezes_below_k, min_liquid_pressure_kpa, condenses_below_today_coldest_k, condenses_below_orbit_coldest_k}], reservoirs {liquid_clouds, ice_clouds, ice_caps}, each null or {volume_l, liquid_l, amount_mol, contents [{gas, amount_mol}]}, starting_air (the air this world ships with at the planet's present size, the baseline Terraforming Reloaded measures from: {per_cell_mol, mol, gases [{gas, amount_mol}]}), and terraforming_mod (null, or Terraforming Reloaded's {name, version, state, live, temperature_adjustment_k}). Read only; no gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "solar_aim",
            "The exact Horizontal and Vertical (logic degrees) that point a solar panel's cells straight at the sun, worked out from the panel's own pivots and the game's sun vector: no daylight sensor and no calibration. Returns reference_id, prefab_name, can_turn, operable (finished to its last build state and not broken; the game generates nothing from a panel that is not, at any angle), horizontal and vertical (logic degrees, as write_logic takes them), off_sun_deg (the angle still off at that pose, non-zero only when the sun is outside the tilt range), alignment_ratio (1 - 2 sin(off / 2), the panel's Ratio at that pose before shading, from the facing alone; with the sun below the horizon the game's Ratio is 0 whatever this says, since the ground shades the panel: read it then as how well the pose meets the sun once it is up), sun {x, y, z, above_horizon, eclipse (OrbitalSimulation.IsEclipse; always false on a dedicated server, where the game does not work out eclipses)} and current {horizontal, vertical, ratio} (the panel's aim and Ratio now). Every pose (H, V) has a twin (H + 180, 180 - V) that faces the cells the same way; when both reach the sun equally (within 0.1 degree) it answers the one needing the smaller turn from current, so a tracker writing every answer never swings the panel round. With the sun below the horizon it gives the pose closest to the sun now: tilted toward where it set until midnight, toward where it will rise after. The game stops a turning panel within its rotation tolerance of the target, up to about 0.6 degrees short in Horizontal (under 1% of Ratio). A panel without pivots (the Flat panel) reports can_turn false and a note instead of the angles. Refusals: invalid_argument, not_solar_panel (no thing has that id, or it is not a solar panel). Read only: write the angles with write_logic. No gateway is needed.",
            new
            {
                type = "object",
                properties = new { reference_id = new { type = "string", description = "Reference ID of the solar panel." } },
                required = new[] { "reference_id" },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "set_ic_pins",
            "Set an IC Housing's device pins d0..d5, as turning its screws with a screwdriver would: each listed pin gets a device reference ID, or null to clear it, and pins not listed keep their device. When the housing is on a data network, a device must be on that network, because the chip only reaches a pin's device there; otherwise the call is refused unless allow_off_network is true. A housing with no data network reaches any pinned device, so any device is accepted. Also refused: a device outside the gateway's scope, the housing itself, and a thing with no readable logic. Every pin is checked before any is written, so a refused call changes nothing. A running chip uses the new devices from its next instruction; nothing is recompiled or reset, and a multiplayer host sends the new pins to clients. Returns what changed and all six pins as get_ic_status lists them. Works on rocket IC Housings too; suits and other worn circuit holders are refused. Refusal codes: not_ic_housing (not an IC Housing, e.g. a suit), device_not_found (the housing, or a pin's device, is outside the scope), thing_not_found (nothing has a pin's id), not_logic_device (a pin's id is not a device: a frame, a pipe), logic_not_readable (a device with no readable logic), not_on_data_network (off the housing's data network without allow_off_network), invalid_argument (bad pins object, pin name or id, a pin listed twice, the housing itself).",
            IcPinsSchema(),
            readOnly: false),
        Tool(
            "thing_health",
            "The damage state of any thing (solar panel, pipe, cable, wall, frame, door, vent, device, item), read from the game's own DamageState: no LogicType exposes damage. Three forms. reference_id: that one thing. reference_ids: up to 256 things, a result per id with index and ok, and a per-item error (thing_not_found, invalid_argument for an id that is not a decimal string) instead of failing the call. Neither: scan every thing in the world and list the damaged and broken ones, broken first, then worst first, paged (broken_only: only broken ones); the scan skips things being destroyed, indestructible things, players and animals, and organs, and counts decaying food and hurt plants as damaged items (use structures_only for the base alone). Each thing has reference_id, prefab_name, display_name, kind (structure, item or other), type (runtime class), damage_state (destructible, indestructible or none), damage_state_class, max_damage (health capacity), total_damage (the sum the game counts: brute, burn, oxygen, hydration, starvation, toxic, radiation and decay, clamped to max_damage; stun is not counted), damage_ratio (total_damage / max_damage: 0 like new, 1 destroyed), health_percent (100 - damage_ratio * 100 rounded, as the solar panel tooltip shows), damage (each type), is_broken (at max damage, a structure in its broken build state, a burst pipe: pipe_burst not none; bursting leaves a pipe's damage at 0, or a burnt cable: the separate undamaged piece, e.g. StructureCableStraightBurnt, an overload leaves, carrying no power), broken_build_state (structures: below build state 0, the broken mesh the game swaps in; null for other things), condition (1.4.3+: broken, damaged, intact, indestructible or none; broken wins, because the game heals a structure it breaks to 0 damage, so a broken vent reads 100 % health, and a burst pipe or a burnt cable reads 0 damage and 100 % health: the numbers stay the game's own, condition and is_broken are the signal), custom_name (the Labeller name), networks (structures: [{kind, id}] of the cable, pipe and chute networks it is part of or its ports join), being_destroyed, band (solar panels only: the tooltip colour green, yellow over 0.25, red over 0.75), pipe_burst (pipes only: none, pressure, liquid or solid), position (a thing in a slot, e.g. a stored item or a planted plant, is placed by its outermost holder) and distance_m from the local player. An indestructible thing (the modded Force-Field Door, anything marked Indestructable) reports damage_state indestructible with total_damage, damage_ratio and health_percent null, never a fake 0. A solar panel generates (1 - damage_ratio) of its undamaged output. The scan also returns count, total (matches on all pages), structures, broken, scanned, min_damage_ratio, offset, limit, has_more and local_player. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of one thing (from find_items, list_devices, list_containers)." },
                    reference_ids = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "string" }, description = "Reference IDs of up to 256 things, for one call per card." },
                    min_damage_ratio = new { type = "number", minimum = 0, exclusiveMaximum = 1, description = "Scan only: list things whose damage_ratio is above this (0.25 = the yellow band, 0.75 = red). Default 0, any damage. The older name min_ratio is still read." },
                    min_ratio = new { type = "number", minimum = 0, exclusiveMaximum = 1, deprecated = true, description = "Deprecated: the older name of min_damage_ratio, still read (min_damage_ratio wins when both are given)." },
                    structures_only = new { type = "boolean", description = "Scan only: leave items out. Default false." },
                    broken_only = new { type = "boolean", description = "Scan only: list only things in the game's broken state (condition broken), whatever their damage numbers. Default false." },
                    near_player_m = new { type = "number", exclusiveMinimum = 0, description = "Scan only: only things within this many metres of the local player." },
                    limit = new { type = "integer", minimum = 1, maximum = 500, description = "Scan only: things per page, default 200." },
                    offset = new { type = "integer", minimum = 0, description = "Scan only: things to skip, for paging." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "paint",
            "Paint things with the game's own paint, as a spray can does (the same OnServer.SetCustomColor call, but no paint is used), or list the colours. Two forms. reference_ids (up to 256) with color: paint them all one colour. items (up to 256), each {reference_id, color}: a colour per thing, so a caller can restore the previous_color of an earlier call. color is a colour name (case-insensitive, as colors lists it), a colour index, or 'default' for each thing's own prefab colour. With neither form it only lists the colours. The paint is synced to clients and saved with the world. With no targets it returns colors: [{index, name, paint_only}] and count (paint_only colours, the metallic cans, cannot be set through logic but can be sprayed). With targets it returns results, one per thing: {index, ok, reference_id, previous_color, color} where a colour is {index, name, is_default} (is_default: the prefab's own colour, which the save stores as -1; index and name null when the thing has no colour; for a thing whose colour is a state, the state colour it shows, as the Color logic type reads it), or on failure {index, ok: false, reference_id, previous_color, error: {code, message}}; plus count, success_count and error_count. Per-thing error codes: invalid_argument (reference_id not a decimal string, color not a string), thing_not_found, not_paintable (no paintable material, or a batched structure the game cannot repaint), has_color_state (the colour is a state set through the Color logic type, e.g. the LED display, which the spray can does not paint either; lights paint), invalid_color (unknown name or index, or 'default' on a thing with no prefab colour), paint_failed (the colour did not change, or the game threw while painting). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_ids = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "string" }, description = "Reference IDs of up to 256 things to paint the one color." },
                    color = new { type = "string", description = "With reference_ids: a colour name (e.g. 'Blue'), a colour index (e.g. '3'), or 'default' for each thing's prefab colour." },
                    items = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 256,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                reference_id = new { type = "string", description = "Reference ID of the thing." },
                                color = new { type = "string", description = "Colour name, colour index, or 'default'." }
                            },
                            required = new[] { "reference_id", "color" },
                            additionalProperties = false
                        },
                        description = "Up to 256 {reference_id, color} pairs, e.g. to restore each thing's previous_color."
                    }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "outer_frames",
            "Which frames (iron, steel, corner, side) are outer frames: frames with a face on a cell whose gas is the world's, i.e. the planet's outside air. A face counts as exposed when the 2 m cell across it is in no room (the game's own meaning of outside: rooms are closed spaces of up to 1200 cells, walled by anything that blocks walking), can hold air (no airtight frame or other air-blocking structure in it, not buried in terrain), is open to the frame through the shared face (no terrain and no air-blocking wall on it), and, when that cell holds a one-sheet frame (never part of a room), its air passes the game's storm exposure test (the planet's atmosphere, or within 1 kPa of it). Limits: a sealed space bigger than 1200 cells has no room, so frames facing into it count as outer; a space closed only by one-sheet frames is a room, so its faces do not count though it leaks. Each frame has reference_id, prefab_name, display_name, position, distance_m from the local player, exposed_faces (any of +x, -x, +y, -y, +z, -z; +z is north, +y up), exposed_face_count, blocks_air (whether the frame's current build state is airtight), build_state (the current build state index: 0 bare, up to the prefab's finished state, which differs by frame: a finished steel frame is 3; blocks_air says whether it holds air) and color {index, name} (index null when unpainted and colourless). Nearest first when there is a local player. Also count, total_frames (frames considered, after near_player_m), total_outer, and paging: offset, limit, total (frames listed across all pages: the outer ones, or every frame with include_inner) and has_more; and local_player {reference_id, display_name, position}. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    near_player_m = new { type = "number", exclusiveMinimum = 0, description = "Only frames within this many metres of the local player." },
                    include_inner = new { type = "boolean", description = "List every frame, with exposed_faces empty for inner ones. Default false: outer frames only." },
                    limit = new { type = "integer", minimum = 1, maximum = 1000, description = "Frames per page, default 200." },
                    offset = new { type = "integer", minimum = 0, description = "Frames to skip, for paging." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "rooms",
            "The game's closed rooms measured cell by cell: every room in the world, or with reference_id only the room that thing (a device, item or player; an item in a slot counts where its outermost holder is) is in. A room is a closed flood fill of 2 m cells (at most 1200; a bigger or open space is outside and has no room). Each room: room_id, room_type (the game's RoomType), cell_count, volume_l, pressure_kpa, temperature_k, total_mol, heat_capacity_j_per_k, thermal_energy_j (the air's total energy; sample twice and its change over the seconds between is the room's net heat flow in watts, gas moved in or out included), gases [{gas, amount_mol, ratio}] largest first (traces under 1e-9 mol left out), bounds {min, max} (the box of the cell centres, world metres), contains_local_player, devices [{reference_id, prefab_name, display_name}] (devices whose cell is in the room; left out with include_devices false) and, with include_cells, cells [{x, y, z}] (every cell centre). The air is summed fresh from each cell's atmosphere, as the game pools a room. Largest room first. Also count and local_player_room_id (null outside a room or without a player). Errors: thing_not_found, not_in_room. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Any thing: return only the room its cell is in." },
                    include_cells = new { type = "boolean", description = "List every cell centre of each room. Default false." },
                    include_devices = new { type = "boolean", description = "List the devices in each room. Default true." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "weather",
            "The storm schedule, the world's weather events and the season, from the game's own weather manager. Returns world_has_weather, state (the game's WeatherState: None, StormScheduled, Storm, RainScheduled, Rain, SnowScheduled, Snow), current (null, or the scheduled or running event: {id, name, running, starts_in_s (game seconds until it starts; negative once running), length_s, ends_in_s (null unless running)}), days_past, days_since_last (days since the last event ended), cooldown_days (the cooldown drawn when it ended), world_start_delay_days (7 x the difficulty's starting weather multiplier), can_schedule_now and schedulable_in_days (whole days until both day rules pass: days_since_last > cooldown_days and days_past > world_start_delay_days; 0 once they do). The game picks an event at random the moment both pass and no event is current, and draws its start delay and length then, so starts_in_s is exact from that moment; while an event is current nothing new schedules, and when it ends days_since_last goes to 0 and a new cooldown is drawn. events: every weather event this world can roll, {id, name, cooldown_days_min, cooldown_days_max (the largest cooldown the game can actually draw), start_delay_s_min, start_delay_s_max, duration_s_min, duration_s_max, temperature_offset_day_k, temperature_offset_night_k (null when none or a curve), solar_ratio, active_in_orbit}. season: null before the orbit loads, else {true_anomaly_deg (the orbit angle, 0 at perihelion, advancing evenly in time), year_length_days (solar days per orbit, as the game counts days), solar_energy_percent (as planet reports it), day_of_year (solar days since perihelion, fractional)}. Terraforming Reloaded can stop a stripped world scheduling its own storms (its own setting); this does not model that. Read only; no gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "ignition_risk",
            "Whether the things the local player carries would catch fire, by the game's own fire rule. Returns player ({reference_id, display_name, position}, null without one), cell (the air of the cell the player stands in: {temperature_k, pressure_kpa, energy_j, inflamed (the air is burning), oxygen_mol, in_room}, null without a player), autoignition_min_energy_j (1e7) and minimum_ignition_pressure_kpa (1.5), and items: every burnable thing the player carries at any depth, {reference_id, prefab_name, display_name, slot (the slot's name), in_hand (directly in a hand), holder ({reference_id, prefab_name, display_name} of the thing whose slot holds it), atmosphere (the air its fire check reads: world, internal for a slot that uses its holder's own air such as a suit, or none), burning, hidden (in a slot that hides it: never burns), flashpoint_k, flashpoint_effective_k (the flashpoint divided by the air's pressure as a share of one atmosphere, clamped to 1: what burning air must pass), autoignition_k (null when unset), ignites_now (the game's ShouldIgnite on that air after its fire tick's checks: burnable, not hidden, air at least 1.5 kPa), health_ratio}. A thing lights when the air is burning and hotter than its effective flashpoint, or when the air is hotter than its autoignition and holds more than 10 MJ; once burning it keeps burning while its cell holds over 0.3 mol of oxygen. include_prefabs adds prefabs: every prefab with a flashpoint or autoignition temperature, {prefab_name, display_name, flashpoint_k, autoignition_k}. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    include_prefabs = new { type = "boolean", description = "Also list every prefab's ignition temperatures. Default false." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "looking_at",
            "What the local player's crosshair is on: the game's own CursorManager.CursorThing, the thing its interaction ray (3 m from the camera) hits this frame. Returns player ({reference_id, display_name, position}, null without one), target (null when looking at nothing, terrain, or past 3 m; else {reference_id, prefab_name, display_name, custom_name (the labeller name, null if none), kind (item, dynamic = a movable thing that is not an item such as a portable tank, structure, entity or other), runtime_type, position, distance_m, is_device (a logic device describe_device, read_logic and write_logic take), has_atmosphere (atmosphere_contents has something to report for it), parent (for a thing in a slot: {reference_id, prefab_name, display_name, slot_index, slot_name} of its holder, else null), and for a structure (1.3.5+) rotation {facing (its front: +x, -x, +y, -y, +z, -z), up, euler {x, y, z} (degrees)} in the forms place_structure takes, so it can be placed again as it stands or turned (facing reversed: 180 degrees; facing and up are null for a piece turned off the grid's axes)}) and interactable (null, or the button, switch, port or slot under the crosshair as the game's hand logic takes it: {action (the game's InteractableType: Activate, OnOff, Open, Slot1...), display_name, contextual_name (the name the tooltip shows on this thing), state, slot (for a slot: {slot_index, slot_name, occupant ({reference_id, prefab_name, display_name}, null when empty)}, else null)}). Hand the reference_id to any other tool. Read only; no gateway is needed. v2 (1.4.3+): view {eye, forward, right, up (unit vectors), yaw_deg (0 looking along +z, 90 +x, 180 -z, 270 -x), pitch_deg (up positive), axes {forward, right, up, back, left, down: the world axes nearest the player's LEVEL forward/right, so 'a metre forward' runs along the floor whatever the pitch; look: the axis nearest the look direction itself}, ambiguous (heading within 10 degrees of a diagonal: forward/right could be either axis), third_person, seated}. hit (the same ray cast on the cursor's layers up to max_distance_m, default 10, max 50, so a wall or floor beyond the 3 m reach is found; left out when nothing is hit): {point, distance_m, normal, face (the surface's facing as an axis, null off-axis), face_plane (\"z=668\" when the point lies within 0.3 m of a 2 m face plane: a floor or wall plate's surface stands a little off its plane, 1.4.4+), cell_2m (the 2 m cell on the looker's side), small_cell (the 0.5 m cell a piece mounted there would stand in), support (that cell's grid_survey support character: i, e, f, w, a; x a door's keep-out, g a window), thing (what the ray hit), local_on_target {right_m, up_m, forward_m} (the hit point in the target's own frame)}. For a structure target: body {origin, render_box {min, max, size} (the box its meshes fill, world), centre_offset (render_box centre minus origin), grid_box (the box of the small cells the game registers it in: its real footprint; null for 2 m structures), grid_cells} and facing_me (its front points toward the camera).",
            new { type = "object", properties = new { max_distance_m = new { type = "number", exclusiveMinimum = 0, maximum = 50, description = "How far hit casts the look ray; default 10 m." } }, additionalProperties = false },
            readOnly: true),
        Tool(
            "connections",
            "How pipes, cables and chutes connect, read from the game's own connection ends and networks. Two forms. reference_id: a pipe, cable, chute or device's ends, returning thing {reference_id, prefab_name, display_name}, position, rotation {facing (its front: +x, -x, +y, -y, +z, -z), up, euler {x, y, z} (degrees)} in the forms place_structure takes, so it can be placed again as it stands or turned (facing reversed: 180 degrees; facing and up are null for a piece turned off the grid's axes) (1.3.5+), own_network (a pipe, cable or chute's own network {kind, id}, else null) and ends: [{index, type (the game's NetworkType: Pipe, PipeLiquid, Power, Data, PowerAndData, Chute, Elevator, LandingPad, LaunchPad, RoboticArmRail), type_name (its display name), role (ConnectionRole: None, Input, Input2, Output, Output2, Waste), role_name, position {x,y,z}, network ({kind: pipe, cable or chute, id}: for a pipe, cable or chute its own network at every end; for a device the network of the pipe, cable or chute attached at that end, null when none is), connected: [{reference_id, prefab_name, display_name}] (everything attached at that end, by the game's own IsConnected test)}]; a thing that is not a pipe, cable, chute or device is refused with not_connectable. network_id with kind (pipe, cable or chute): the network's members, pipes, cables or chutes first then devices, paged, each {reference_id, prefab_name, display_name, member (pipe, cable, chute or device), position}; with count, structure_count, device_count, offset, limit, total, has_more, network {kind, id} and summary. Pipe summary: content (Gas or Liquid), volume_l, pressure_kpa, temperature_k, total_mol, liquid_volume_l and gases [{gas, state (gas or liquid), amount_mol}] (null on a multiplayer client, which does not simulate pipe atmospheres). Cable summary, from the last power tick as the Cable Analyser shows it: required_w, potential_w, actual_w (power actually delivered), shortfall_w, lowest_cable_max_w (the weakest cable's rating), lowest_fuse_break_w, overloaded (min(potential, required) is above the weakest cable's rating: the game burns one such cable per tick), fuse_overloaded (the same against the weakest fuse), cable_count and fuse_count. Chute summary: member_count. Network ids come from ends[].network.id or atmosphere_contents. Errors: thing_not_found, not_connectable, network_not_found. Read only; no gateway is needed. network_id (1.3.0+) also takes a piece or device reference id or {reference_id, port}; resolved_networks names the id it resolved to.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of a pipe, cable, chute or device." },
                    network_id = NetworkHandle("A pipe, cable or chute network (with kind): its id, a piece or device on it, or {reference_id, port}."),
                    kind = new { type = "string", @enum = new[] { "pipe", "cable", "chute" }, description = "The kind of network_id." },
                    limit = new { type = "integer", minimum = 1, maximum = 1000, description = "network_id only: members per page, default 200." },
                    offset = new { type = "integer", minimum = 0, description = "network_id only: members to skip, for paging." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "plant_genes",
            "Read or edit the genes of plants, seeds and harvested produce. A plant item holds the game's list of gene sets, one per unit of its stack, though the game sometimes keeps one more (seed bags from crates: gene_set_count is then the quantity + 1, and the extra set is unit 0, which planting never reaches, since planting copies and using takes the top set); the top set (the last in the list) is the one planting copies, the planted plant's stats read, harvests inherit (with mutation) and the network sends. Read: reference_id (one thing, optionally unit: which set, 0-based, default the top) or reference_ids (up to 256, top sets; a result per id with index and ok, or error {code, message}). Each read returns thing {reference_id, prefab_name, display_name}, holder (planted_plant, seed or produce), gene_set_count, unit, is_top and genes: all 19, each {gene, value, min -1, max 1, stability, stability_min -1, stability_max 1 (positive = steadier: smaller random mutation, and it decays toward 0 each generation), meaning (what it does in the game), effect}. effect is the stat the gene sets now and at both ends of the range: for the fifteen scalar genes {stat, base_<unit>, now_<unit>, at_min_<unit>, at_max_<unit>} with unit s (seconds) or factor (a multiplier); for the four band genes {stat (GrowTemperature or GrowPressure), now, at_min, at_max}, each {ideal_min_k and min_k} or {ideal_max_k and max_k} (kPa for pressure). Genes: GrowthSpeedMultiplier (the save calls it GrowthTimeMultiplier; both are accepted), DarkPerDay, LightPerDay, DroughtTolerance, WaterUsage, LowPressureResistance, LowTemperatureResistance, UndesiredGasTolerance, GasProduction, HighPressureResistance, HighTemperatureResistance, SuffocationTolerance, LowPressureTolerance, LowTemperatureTolerance, HighPressureTolerance, HighTemperatureTolerance, UndesiredGasResistance, LightTolerance, DarknessTolerance. Write: reference_id plus genes {name: value}, as the Gene Splicer does; optional unit and force. Each gene gets a result {index, ok, gene, previous_value, value} or {index, ok: false, gene, previous_value, error {code, message}}: unknown_gene, out_of_range (outside -1..1, the game's range; force writes it anyway), the others are still written. A value that is not a number (null included) refuses the whole call (invalid_argument) and nothing is written. Plus thing, holder, unit, is_top, count, success_count, error_count. The edit is saved with the world and inherited by harvested fruit and seeds like a natural gene (then mutated); stats read the genes live, and a write to the top set recomputes the cached temperature and pressure efficiency curves and marks the genes for multiplayer sync. A plant's harvest quantity, fixed when it first matures, is not recomputed. Errors: thing_not_found, not_a_plant, no_genes, unit_out_of_range, not_host (a multiplayer client cannot write). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of a plant (planted or not), seed or plant produce." },
                    reference_ids = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "string" }, description = "Read only: up to 256 reference IDs." },
                    unit = new { type = "integer", minimum = 0, description = "With reference_id: which gene set, 0-based; default the top (the last one)." },
                    genes = new { type = "object", additionalProperties = new { type = "number" }, description = "With reference_id: gene name to new value, e.g. {\"GrowthSpeedMultiplier\": 1}. Range -1 to 1." },
                    force = new { type = "boolean", description = "With genes: write values outside -1..1 too. Default false." }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "mod_info",
            "The running mod's identity and health: mod_id, mod_version, assembly_version, informational_version, pipe_name (the local named pipe this game listens on, the sidecar's --pipe: tells which game a sidecar reached when two run on one machine), and methods: every method the mod answers, each {method, calls, errors, total_ms, mean_ms (null before the first call), max_ms} counted in memory since the mod loaded (main-thread time per request; errors are replies with ok false). Also count, reflection: [{member, resolved, optional}] (every game member the mod reaches by reflection and every Harmony target it patches; optional ones belong to other mods such as Terraforming Reloaded and BlueprintMod) and missing_count (required members not found: methods that need one answer game_changed). The mod's own pipe reply envelope (read by pipe clients such as the script dashboard) also carries elapsed_ms, that request's main-thread time; MCP tool results do not carry it, so use the methods' mean_ms and max_ms here. Read only; no gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "move_gas",
            "A cheat tool: it bypasses the game's physics. Gas and liquid jump between atmospheres with no pipe, pump or valve between them, no flow time and no power; nothing a player can build does this. Move gas and liquid from one atmosphere to another, or delete it, with the game's own gas calls: each gas leaves with its own share of its heat and arrives with it. from and to are each a reference id: a thing with an internal atmosphere (canister, portable tank, tank, suit), a pipe (its network), any landing pad piece (the pad network's shared atmosphere: every piece of one pad holds the same gas; content type all, so it holds liquids and never changes their state), a pipe or landing pad network id, or an atmosphere id as atmosphere_contents reports; a device without its own atmosphere is refused (no_atmosphere), and so is a single world cell (refused). A room is {\"room_id\": \"<id>\"} (room_id as the rooms tool reports it) or {\"room_of\": \"<reference id>\"} (the room that thing is in, e.g. the player's id for the current room; an item in a slot is in the room of its outermost holder): every cell of the closed room with air of its own (cells without are left out; none at all is no_atmosphere). From a room, gases is required (a room's breathable air is never emptied by omission) and each named gas is taken from every cell in proportion to what the cell holds (amount_mol caps the room's total), each with its own share of energy, in one atmospherics tick; into a room, each gas is spread over the cells by volume with its energy, so the room is at once where the game's mixing would settle it (no one-cell pressure spike). A room has no burst rating; the other side keeps every check. Into a room, liquid the room's air would lose is refused (would_burst; force moves it anyway): a room's cells freeze any amount out of their air (ice per 50 mol in a cell, smaller amounts held out of the air), so the move is refused when more would freeze at the settled temperature than before, or when an arriving liquid would sit under its minimum liquid pressure (Water 6.3 kPa of gas around it) in a room without the heat to boil it all: it keeps evaporating and cooling the room until the rest freezes. from: \"planet\" with delete: true and named gases takes those gases out of the planet's own air (the mix Terraforming Reloaded reads), and out of its clouds and ice caps; it needs Terraforming Reloaded (terraforming_mod_required without it, since the stock game keeps the planet read-only; the arguments are checked first, so a malformed call is invalid_argument either way), refuses without gases, and with no amount_mol repeats for 30 ticks to take back what outdoor cells hand back; the reply is queued, read the planet tool for the result. Pass to, or delete: true to destroy the gas. gases: names as atmosphere_contents reports them (Oxygen, Nitrogen, CarbonDioxide, NitrousOxide, LiquidOxygen, Steam...); omit for every gas and liquid (not from a room). amount_mol: moles of each listed gas, capped at what is there; omit for all of it. "
            + "Joined sets (joined, default true): the game mixes some atmospheres to one composition every tick, so gas taken from one flows back from the others. The joins followed: a Gas Tank Storage's canisters with its pipe networks; a portables connector's tank with its gas network (gases) and liquid network (liquids); a connector pipe's tank with its network; a portable tank with the canister in its slot; a tank or other internal-atmosphere device with its network; a hydroponics tray with its networks; an open valve's two networks. From a joined set, each gas is taken from every member in proportion to what it holds (amount_mol caps the set's total), each with its own share of energy, in the same tick; a liquid follows the joins that carry liquids. Into a joined set, the gas goes into the named atmosphere and the game spreads it. to inside from's set is refused (same_joined_set). joined: false moves from and to the named atmospheres only. "
            + "Burst check (refused with would_burst, predicted and limit kPa in the message; force skips it): each side's settled pressure, its members pooled as if mixed, against every member's rating (a canister's MaxPressure or a portable tank's MaxSetting across its walls, or each pipe network member's MaxPressure at its cells, each against the pressure outside; a landing pad's pieces at the gas pipe rating); things without a rating are not checked. The receiving side is also refused when the move makes it worse: liquid over 2% of a gas pipe network's volume (the game damages it every tick; the liquid spreads over the members joined for liquids by volume), gas or liquid freezing in a network at the settled temperature, and the settled pressure once arriving liquids have boiled (where the atmosphere changes state: not a landing pad), each liquid turned into its gas and paying its latent heat. "
            + "Timing: the game only changes gas on its atmospherics thread, so the move is queued and applied at the start of the next atmospherics tick (about half a second; never while paused). The reply is the prediction from last tick's values: {transfer_id, status: queued, predicted: true, joined, from, to (null when deleted), deleted, moved, error: null}. from and to are {members: [{owner {kind (thing, pipe_network or landing_pad_network), reference_id, prefab_name, display_name}, atmosphere_id, before, after}] (the named atmosphere first), total {before, after, after_boiling} (the members pooled: pressure and temperature as if mixed; after_boiling is after once every liquid that can boil has boiled; null when nothing would boil, when any one of them would not all boil (it stays liquid at the pressure after, or its boiling would cool it under 0.5 K above its freezing point, where the game freezes the last of it out of a room; one such liquid, e.g. water beside boiling liquid nitrogen, makes it null for the whole side, and the pressure check after boiling is then skipped for that side), or when the atmosphere never changes state), joined_by: [{reference_id, prefab_name, display_name}] (the joining devices), room (null, or for a room {room_id, room_type, cell_count, cells_with_air, volume_l}: then members is empty and total is the room's air over its cells with air, so total.before.pressure_kpa and total.after.pressure_kpa are the room's pressure before and after)}; before and after are {pressure_kpa, temperature_k, total_mol, gases: [{gas, amount_mol}]}; moved is [{gas, amount_mol, energy_j}] summed over the members. dry_run: true (1.3.5+): the same prediction with the same checks (a refusal is the same error), nothing queued: status dry_run, transfer_id null; not with from planet. Call again with only transfer_id for the outcome: status queued (also while it is being applied), applied (the same shape from live values, predicted: false) or failed (error {code, message}: atmosphere_not_found when an atmosphere was destroyed before the tick, nothing_to_move when the source held none of the gases by then, move_failed when the game's gas call threw); the last 64 are kept, so transfer_not_found means an id never issued or older than those. To undo, move each gas back by amount_mol; it returns at the other side's temperature by then, so energy_j tells you how far that differs. Errors: invalid_argument (also from and to the same atmosphere or room), unknown_gas, atmosphere_not_found, no_atmosphere, refused, room_not_found, not_in_room, thing_not_found (room_of), not_ready, same_joined_set, joined_too_large (over 64 joined atmospheres), nothing_to_move, would_burst, busy (64 moves waiting), transfer_not_found, terraforming_mod_required (from planet), not_host (multiplayer clients cannot move gas). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    from = new { oneOf = GasPlaceSchema, description = "The source: a reference id (a thing with an internal atmosphere, a pipe, a landing pad piece, a pipe or landing pad network or an atmosphere), \"planet\" (with delete: true and gases), or a room: {\"room_id\": \"<id>\"} or {\"room_of\": \"<reference id>\"} (needs gases)." },
                    to = new { oneOf = GasPlaceSchema, description = "The target: a reference id, same forms, or a room: {\"room_id\": \"<id>\"} or {\"room_of\": \"<reference id>\"}. Omit and pass delete: true to destroy the gas." },
                    delete = new { type = "boolean", description = "Destroy the gas instead of moving it. Default false." },
                    gases = new { type = "array", minItems = 1, items = new { type = "string" }, description = "Gas names, e.g. [\"Oxygen\"]; omit for all gases and liquids." },
                    amount_mol = new { type = "number", exclusiveMinimum = 0, description = "Moles of each listed gas, capped at what is there; omit for all." },
                    force = new { type = "boolean", description = "Move even if an end would pass its burst rating. Default false." },
                    joined = new { type = "boolean", description = "Treat atmospheres the game mixes every tick as one unit. Default true; false moves from and to the named atmospheres only." },
                    dry_run = new { type = "boolean", description = "Predict and check only; nothing is queued (status dry_run, transfer_id null). Default false." },
                    transfer_id = new { type = "string", description = "Alone: the outcome of an earlier move." }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "landing_pads",
            "Every trader landing pad in the world, measured by the game's own checks: no guessing from tiles. Each pad: reference_id, prefab_name, display_name, position, forward (+x, -x, +z or -z; +z is north), is_network_center (a pad network with more or fewer than one centre refuses every landing), piece_count (pieces on its landing-pad network), extent {x_tiles, z_tiles} (the box of its pad tiles and centre, 2 m tiles), largest_square_tiles (the biggest n up to 15 for which the game's CheckPadSize passes an n x n pad, allowing the one-tile shifted centre the game allows on even sides; every cell of the square must be a pad tile or the centre, so a Data And Power or other connection piece inside it shrinks the square), runway_ok (a plane's runway threshold check: exactly one switched-on threshold on the network; null without a network), fits_by_ship (for each ShuttleType: shuttle_type, pad_size_tiles [x, y] from the game's table, runway_tiles (the approach length the game uses for planes, not checked against the pad), needs_threshold (planes), fits), and contacts (every trader contact in the sky: reference_id (the contact's id, as trader_contacts gives it), name, shuttle_type, pad_size_tiles, fits, obstructed (the game's own LandingPadCenter.IsObstructed as it answers: a structure or closed face looking up from the pad cells in the ship's footprint; the game never calls it, CanTraderLand does not check it, and it is unconfirmed whether it counts the pad's own pieces, so treat it as advisory), can_land (the game's CanTraderLand now: power, error, one centre, storm (planes are exempt), pad size, runway threshold) and reason (the game's message, empty when it can land)). Also count. Read only: the game's pad check moves the landing point of the pad it measures, and every check here puts it back, so a landing in progress is never affected. No gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "move_item",
            "Move an item, or part of a stack, into a slot with the game's own moves, as an inventory click does: slot to slot, never through the world, so ice and other perishables are never loose in the air. A whole item moves with OnServer.MoveToSlot (the source container's bookkeeping, e.g. a vending machine's, is kept by the game); part of a stack is split straight into the slot (Stackable.SplitStack, as the Stacker does); a whole stack can join a matching stack in the slot (Stackable.Merge). One move: reference_id (the item), optional quantity (take that many off the stack; the rest stays; an item that is not a stack, e.g. a water packet or a canister, moves whole and counts as 1 whatever it holds), to_id (the thing holding the slot: a container, a belt, a suit, a player) and to_slot (the slot index as container_contents gives it for any thing, and inspect_slots for a device, or \"auto\": a matching stack first when merge allows and the whole stack moves, else the first empty slot that takes it); only slots a player can reach are used: a hidden slot (not interactable in the game, e.g. a cable coil's internal slot, whose contents the game destroys with the coil, or a vending machine's store) is refused, and auto also skips slots the game's quick moves skip (not swappable); an item may still be moved out of a hidden slot (meant to rescue one put there by mistake, but it also unpacks a package's items one by one and takes items out of a vending machine's store, and since nothing can be put back into a hidden slot such a move cannot be undone); a grower's plant and fertiliser slots follow its hand interactions instead, hidden or not (a planter's slots and a hydroponics station's fertiliser slots are hidden in the inventory window but filled by hand): a seed or plant into a grower's plant slot (a hydroponics tray, planter, station or device) is planted as a player plants it: one unit is used off the stack and a new plant grows in the slot with that unit's genes, so quantity must be 1 (or the stack hold 1), an occupied plant slot is refused slot_occupied and auto never merges into one; a plant slot takes nothing but a seed or plant (fertiliser or any other item slot_refuses: a player's hand sends fertiliser to the grower's fertiliser slot, and auto puts it there); a grower's fertiliser slot takes only fertiliser, one unit (quantity 1) into an empty slot, as a player adds it (anything else slot_refuses, a seed or plant because the game would take it there for the grower's plant; occupied slot_occupied), and auto never puts anything else there nor merges into it; a plant growing in a plant slot is never taken out (planted: a player only harvests or clears it), though a seed bag left in one may be; merge (default true) lets the items join a matching stack. Or moves: up to 64 such objects, applied in order. A move returns {index, ok, reference_id, from {id, slot} (null when the item lay in the world), to {id, slot}, quantity_moved, merged_into (the stack it joined, or null), destination_reference_id (the stack now in the slot: the item, the new stack a split made, the plant planting made, or merged_into), warning (null, or the game's error when one of its calls threw part way but the slot holds the result: the move is done and quantity_moved is read back, so do not repeat it)}; a refused move {index, ok: false, reference_id, error {code, message}} and nothing is changed for it. The batch form returns results, count, success_count and error_count; the one-move form returns the move itself, or the refusal as the error. Refusals: thing_not_found (item or destination gone), not_movable (a structure), invalid_destination (the destination is the item or inside it), no_slots, slot_not_found, same_slot, slot_locked (source or destination), slot_refuses (the game's slot rules: a hidden slot, the slot class, CanEnter with the game's reason, or a draggable such as a crate or portable tank, which the game only drags into a slot), slot_occupied (by something it cannot join, or merge false, or a grower's plant or fertiliser slot already filled), planted (the item is a plant growing in a grower's plant slot), partial_merge (part of a stack can only go into an empty slot), stack_full (the joined stack would pass its maximum), no_free_slot, invalid_argument (e.g. more than the stack holds, or a quantity other than 1 for an item that is not a stack), move_failed (the game did not do it, with its error when it threw; read the slots), not_host (multiplayer clients cannot move items). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of the item to move." },
                    quantity = new { type = "integer", minimum = 1, description = "How many to take off the stack; omit for the whole item." },
                    to_id = new { type = "string", description = "Reference ID of the thing holding the destination slot." },
                    to_slot = new { oneOf = new object[] { new { type = "integer", minimum = 0 }, new { type = "string", @enum = new[] { "auto" } } }, description = "Slot index, or \"auto\"." },
                    merge = new { type = "boolean", description = "Let the items join a matching stack in the slot. Default true." },
                    moves = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 64,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                reference_id = new { type = "string" },
                                quantity = new { type = "integer", minimum = 1 },
                                to_id = new { type = "string" },
                                to_slot = new { oneOf = new object[] { new { type = "integer", minimum = 0 }, new { type = "string", @enum = new[] { "auto" } } } },
                                merge = new { type = "boolean" }
                            },
                            required = new[] { "reference_id", "to_id", "to_slot" },
                            additionalProperties = false
                        },
                        description = "Up to 64 moves, applied in order, each with its own result."
                    }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "upgrade_cables",
            "Replace cable pieces with heavy (default) or super heavy cable in place, piece for piece, as the coil's own merge placement builds them (the game refuses to place heavy cable over normal: Cable.CanReplace). Name a whole cable network with network_id (from connections) or pieces with reference_ids (up to 4096). Pieces already at or above the target stay. Each piece's replacement is the first piece of the target coil (found by what the coil places, e.g. Cable Coil (Heavy)) whose cells and connection ends (type, role, cell and facing cell) match the old piece exactly, at the old rotation or turned; that is how straight, corner, T, cross, 5- and 6-way junctions and the 3-, 5- and 10-long straights map. DRY RUN BY DEFAULT: dry_run true (default) changes nothing and reports every piece (reference_id, prefab_name, position, rotation_deg, target_prefab_name, target_rotation_deg, rotation_kept, cost in coils, network_id; up to limit, default 200), by_prefab (count, cost_each, cost_total, refund_each (0 with refund false, as every refund in the reply: nothing comes back), max_power_w_before/after), kept_pieces and unmatched_pieces with reasons, coils (needed, available, the stacks and where they are in the source), refund (the old coils deconstruction would give back), networks (loads, lowest_cable_max_w_before and lowest_cable_max_w_after: the new weakest cable, lowest fuse, devices), devices next to or mounted on the pieces with their networks, connectivity (the game's links now, the links predicted with every replacement in place; added and lost must be empty; model_matches_game proves the prediction method on these very pieces; mounted fuses and analysers; null when no piece has a replacement, since links are surveyed only around pieces to swap) and ready. Every problem is listed at once in problems (code, message, reference_id): e.g. unmatched_pieces (no replacement; skip_unmatched true leaves those pieces as they are), not_enough_coils, link_added, link_lost, connectivity_model_mismatch, mounted_device_turned, shape_model_mismatch, not_complete, rocket_internal, no_local_player, thing_not_found. A real run needs dry_run false AND confirm true; it is refused (status refused, nothing changed) unless the checks find nothing. It then returns a job (status waiting, job_id, preflight): the mod holds the game tick as a save does, runs every check again once the tick has stopped, swaps every piece in that one frame (replacement built first, old piece removed after, coils taken from the source as placing takes them), checks the result the next frame and lets the tick go. Poll with job_id alone: status applied (verification ok), applied_with_differences (verification lists each link, mount, device or network that differs), stopped (a piece failed: swapped lists old and new ids, not_swapped the rest, stopped_at the piece and whether it is intact; run the same call again to resume, since swapped pieces are then already heavy), applied_unchecked, or refused (final_check says why; nothing changed). from_id: the thing whose inventory gives the coils (default the local player; any container or a coil stack). refund (default true): the old coils, as deconstructing gives them, go into from_id's inventory (default the local player): first onto matching stacks: from_id itself when it is one (a coil stack), then anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, a holder's own slots before those of the items in them, never a hidden slot or a stack's own slot (a cable coil's, whose contents the game destroys with it); only what nothing takes goes on the ground a metre in front of the outermost holder, at rest (never inside the player); the job's refund_delivered lists each part with where: merged, slot or ground. Devices, APCs, batteries and transformers connect to any cable type (CableType is only read when placing); network ids stay the same. Host only (not_host on a client). No gateway is needed. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}.",
            UpgradeSchema(["heavy", "super_heavy"], "heavy"),
            readOnly: false),
        Tool(
            "upgrade_pipes",
            "Replace normal pipe pieces with insulated pipe in place, gas pipe to insulated gas pipe and liquid pipe to insulated liquid pipe (never mixed), piece for piece as the kit's own merge placement builds them, keeping the network's gas and liquid: each replacement joins the network before its old piece leaves, so the network's Atmosphere (moles, energy, temperature) is never split, vented or divided; only its volume changes by the pieces' volume difference. Name a whole pipe network with network_id (from connections) or pieces with reference_ids (up to 4096). Pieces already insulated and non-pipe members (vents, drains, radiators) stay. The mapping, the dry run, the problems, the confirm, the job and its polling are exactly as upgrade_cables, with these pipe specifics: by_prefab gives max_pressure_kpa_before/after (Pipe.MaxPressure, the same for normal and insulated), volume_l_before/after and heat_exchange_factor_before/after (the prefab's ThermodynamicsScale; insulated pipe exchanges little or no heat with its surroundings); networks give content, total_mol, energy_j, temperature_k, volume_l_before/after, pressure_kpa_before/after (the same contents in the new volume) and lowest_max_pressure_kpa_after. Extra problems: burst_pipe (repair first), no_atmosphere, atmosphere_busy (a gas change is waiting for the next atmospherics tick, a game event or a queued move_gas that takes from or gives to the network: try again), content_mismatch, would_burst (the pressure after would exceed the lowest pipe rating). After a run the verification also checks that the network kept the same Atmosphere, the predicted volume and the same contents. Coils here are the pipe kits (Kit (Insulated Pipe), Kit (Insulated Liquid Pipe)); refund gives back the normal kits. to must be insulated (the default). Host only. No gateway is needed. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}. Gas check (1.4.1+): the job applies the game's queued gas changes after every piece it builds (the game applies them only at the next tick, and two network merges in one tick lost a network's gas), then compares every family of pipe networks it changed (1.4.4+: networks sharing a pipe, or a cell a pipe filled before and one fills after, whichever id the game's merges kept), moles and energy, before and after: gas_check {checked, ok, summary, families [{networks_before, networks_after, mol_before, mol_after, energy_before_j, energy_after_j, planned_loss_mol, missing_mol, emptied, ok}], ghosts (networks without pipes left holding gas, with the devices still on them), recovered [{into, mol, energy_j}] (gas the merge lost, put back; never past a network's weakest pipe), withheld (1.4.4+: [{network_id, family_networks, mol, pressure_after_kpa, rating_kpa}] gas a family lacks that was not put back because it would take that network over its weakest pipe, which would burst; the check fails), ghosts_cleared, old_ghosts (pipeless networks that already held that gas before), orphans (1.4.4+: networks the game no longer lists that pipes still name [{network_id, pipes, mol, energy_j, volume_l}]; nothing simulates them and their gas counts where it sits, so a copy shows as gas that appeared; any fails the check), old_orphans (such networks already there before the job)}. Status gas_lost when anything is still missing; every later pipe job (place_structure and remove_structure runs too when they place or remove a pipe piece, in-line tank, passive vent or anything with a pipe end) is then refused (gas_check_failed) until the world is loaded again (a save loaded, or back to the menu; nothing else lifts it). Dry runs still answer.",
            UpgradeSchema(["insulated"], "insulated"),
            readOnly: false),
        Tool(
            "clean_cables",
            "Tidy cable networks in place with one or more operations, each usable alone or together (operations, default [\"simplify_junctions\"]; they run in the order remove_dead_ends, remove_loops, split_long_straights or merge_straights, simplify_junctions, each on what the earlier ones leave). remove_dead_ends: removes stubs (one connected end) and isolated pieces (none), in rounds until no new stub appears (removing a stub can make its neighbour one); each removed piece lists its round. It stops at a stub whose connection is a device (device_connected) and never removes a piece a fuse, analyser or other device is mounted on (device_mounted; the game itself refuses to deconstruct a piece with an attached device), nor indestructible or rocket pieces; such dead ends are listed in dead_end_pieces with stopped_by. Removed pieces give back what deconstructing them would. remove_loops (never by default: a loop may be redundancy kept on purpose against a burnt cable): finds loops, groups of pieces joined to the rest in more than one way by the game's own connection rule, and breaks each by removing, one at a time, the shortest run of plain two-ended pieces whose two ends stay joined without it, until no cycle is left (a ring hanging off one junction is one run whose ends meet there, so it goes whole, and a junction left with one connected end stays a dead end for a later remove_dead_ends run); a run holding a piece linked to a device, a piece with a device mounted, an indestructible, rocket or kit-less piece never goes, so no device loses a link and nothing is cut off; the junctions a cut leaves with an open end become the piece with only their connected ends (as simplify_junctions). keep_ids: pieces whose loops are spared whole. The report lists loops [{index, pieces [{reference_id, prefab_name, position, cut}], cut, spared, unbroken (why a cycle stays)}]. split_long_straights: each 3-, 5- or 10-long straight becomes one single straight per cell, same line and cells; it costs coils (e.g. 5 singles for a 5-long). merge_straights: runs of single straights of one grade, colour and owner in one line (no junction, device or mounted device inside) become the fewest long straights the coil offers that cover them exactly, longest first (10, 5, 3; leftovers stay); gives coils back (e.g. a 10-long costs less than 10 singles). split_long_straights and merge_straights together are refused. simplify_junctions: each piece with open ends becomes the piece of its own coil with only the connected ends, turned to match: a 3-way joining two neighbours becomes a straight (opposite) or a corner (adjacent); 4-, 5-, 6-ways and corner variants the smallest piece with exactly those ends. Every replacement is read from the loaded coil, never a table. The grade (normal, heavy, super heavy), colour and owner stay. Name a whole cable network with network_id (from connections) or pieces with reference_ids (up to 4096; only those pieces change, and pieces outside them only connect). DRY RUN BY DEFAULT: upgrade_cables' report (target minimal) where each piece gives operation (remove_dead_end, remove_loop, split_long_straight, merge_straights, simplify_junction), ends, connected_ends, replacement_count (0 for a removal), round (removals), merged_reference_ids (merges), cost (coils taken) and refund_count (0 with refund false); dead_end_pieces {reference_id, prefab_name, position, reason dead_end or isolated, ends, connected_ends, stopped_by} lists dead ends left in place; kept_pieces and unmatched_pieces (no_smaller_piece, no_single_piece, no_long_piece, special_piece, no_kit; skip_unmatched true leaves those). Ends are world axes +x, -x, +y (up), -y, +z, -z. Connectivity: the links predicted after must equal the game's links now, counted per replaced group, less exactly the links of removed pieces; otherwise the run is refused (link_added, link_lost, device_link_lost), so no remaining neighbour or device loses a link and no networks merge. Coils: the new pieces' coil cost less the old ones' (the coil's own merge rule); a shortfall is taken from from_id's inventory (default the local player) and the run is refused with not_enough_coils if it holds too few; nothing is made for free; with refund (default true) surplus and removed pieces' materials go into from_id's inventory (default the local player): first onto matching stacks: from_id itself when it is one (a coil stack), then anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, a holder's own slots before those of the items in them, never a hidden slot or a stack's own slot (a cable coil's, whose contents the game destroys with it); only what nothing takes goes on the ground a metre in front of the outermost holder, at rest (never inside the player); the job's refund_delivered lists each part with where: merged, slot or ground. A real run needs dry_run false AND confirm true and is a job exactly as upgrade_cables (tick held, checks again, one-frame swap, verified the next frame: links, mounts, device networks, each network's devices and member count, which changes by exactly the planned additions and removals; swapped lists each old piece against the new piece in its cells, removed lists removed pieces); poll with job_id alone. Host only. No gateway is needed. remove_redundant (1.3.0+, never by default): removes every piece no device needs: the whole network is read (every piece of each selected piece's network, with its devices) and each candidate goes, oldest (lowest reference id) first, when every remaining piece stays joined to the rest without it; a candidate refused because it holds the network together is tried again when a neighbour goes, so dead branches are removed whole. Candidates: the selected pieces, narrowed by only_ids and older_than_id (lower ids only, i.e. built before that piece); never a piece joined to a device port, a keep_ids piece, one with a fuse or analyser mounted, indestructible or rocket pieces, or one no coil places. Devices never carry a network, so loops that pass through a device's port piece (an old and a new feed meeting there), which remove_loops cannot see, are found. Junctions a removal leaves with an open end become the piece with only their connected ends. Report redundant {candidates, removed, root, kept_count, kept_by_reason, kept [{reference_id, prefab_name, position, reason device_port|keep_ids|blocked:(why)|needed, devices (for needed: the devices it keeps on the root; device_port: the devices it joins), pieces_cut_off}]}; a candidate it leaves that no later operation changes is also in kept_pieces with that reason (1.4.4+; before, it was counted by its ends: minimal, unchanged or a dead end). root: the device the network is fed from (default every supplier on it) for naming what a needed piece keeps. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}.",
            CleanSchema("cable"),
            readOnly: false),
        Tool(
            "clean_pipes",
            "clean_cables for pipe networks: the same five operations (remove_loops included, with keep_ids), alone or together, with the same order, reports, refusals, jobs and material rules, for every pipe grade (gas, liquid, insulated gas, insulated liquid; grade and content always stay; long pipes where the kit lists them). Contents: the network's gas or liquid stays in its own Atmosphere the whole time. Replacements join before old pieces leave, and a removed pipe leaves the network before it is destroyed, so its volume leaves and its share of the contents stays: moles and energy are unchanged and the pressure rises (networks show total_mol, energy_j, temperature_k, volume_l_before/after, pressure_kpa_before/after). The run is refused if that pressure would exceed the weakest remaining pipe (would_burst), and on burst_pipe, no_atmosphere, atmosphere_busy (a game event or a queued move_gas touching the network is waiting for the next atmospherics tick) or content_mismatch. remove_dead_ends never deletes contents: the game's own removal of a network's last pipe divides its gas among no network (it is lost), so the pieces of a network that removal would empty while it still holds gas or liquid stay, with stopped_by holds_contents and the moles; an empty isolated pipe is removed. The hold is per network, not per stub: a line with two open ends whose rounds would end in emptying it keeps every stub (it is never trimmed down to fewer pipes). remove_redundant holds back the same way (kept reason blocked:holds_contents), counting what earlier operations of the request remove, and treats a member that is not a pipe piece (an in-line tank, a passive vent) as part of the network that stays joined, so the pipes that join it stay (1.4.4+). After a run the verification checks the same Atmosphere, the predicted volume, unchanged contents, links and member count; when the game renumbered a network (a new piece that registered with no connected neighbour got a network of its own, into which the old one was merged), it checks the survivor, whose entry names the old id in renumbered_from (1.4.4+). split_long_straights builds each long straight's singles from its connected tip, so each joins the network as it stands and the network keeps its id. Kits instead of coils. Host only. No gateway is needed. remove_redundant (with keep_ids, only_ids, older_than_id, root) as clean_cables (1.3.0+). Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}. Gas check (1.4.1+): the job applies the game's queued gas changes after every piece it builds (the game applies them only at the next tick, and two network merges in one tick lost a network's gas), then compares every family of pipe networks it changed (1.4.4+: networks sharing a pipe, or a cell a pipe filled before and one fills after, whichever id the game's merges kept), moles and energy, before and after: gas_check {checked, ok, summary, families [{networks_before, networks_after, mol_before, mol_after, energy_before_j, energy_after_j, planned_loss_mol, missing_mol, emptied, ok}], ghosts (networks without pipes left holding gas, with the devices still on them), recovered [{into, mol, energy_j}] (gas the merge lost, put back; never past a network's weakest pipe), withheld (1.4.4+: [{network_id, family_networks, mol, pressure_after_kpa, rating_kpa}] gas a family lacks that was not put back because it would take that network over its weakest pipe, which would burst; the check fails), ghosts_cleared, old_ghosts (pipeless networks that already held that gas before), orphans (1.4.4+: networks the game no longer lists that pipes still name [{network_id, pipes, mol, energy_j, volume_l}]; nothing simulates them and their gas counts where it sits, so a copy shows as gas that appeared; any fails the check), old_orphans (such networks already there before the job)}. Status gas_lost when anything is still missing; every later pipe job (place_structure and remove_structure runs too when they place or remove a pipe piece, in-line tank, passive vent or anything with a pipe end) is then refused (gas_check_failed) until the world is loaded again (a save loaded, or back to the menu; nothing else lifts it). Dry runs still answer.",
            CleanSchema("pipe"),
            readOnly: false),
        Tool(
            "replace_walls",
            "Replace walls and windows in place with another wall or window prefab (to, required: e.g. StructureCompositeWall, StructureReinforcedWall, StructureCompositeWindow; iron wall to composite or reinforced wall or window, window to wall, and so on) without ever opening a face: with the game tick held, each old piece is marked as being destroyed, the new piece is built in its slot (owner and colour kept) and raised at once to its final build state, and only when it holds every slot the old one held and blocks what it blocked is the old one removed; nothing of the game ticks in between, so no gas moves and no room is re-evaluated until every piece stands. Name pieces with reference_ids (up to 4096) or a room with room_id (from rooms: every wall on a face of the room's cells); from_prefabs limits the pieces to those prefabs (the rest are kept as not_selected). Only exactly Wall or WallTransparent are taken or built: shuttered windows and their connectors, floors of their own classes (a floor grating is a plain WallTransparent and swaps like a window), ladder platforms and crew umbilical doors are kept as special_piece, and such a target is invalid_target. The target must be a loaded prefab that some kit builds (listed by a MultiConstructor), not a cursor. Footprint: the new wall must register in exactly the old one's slots (the same face points, in the same cell: its blockingGrids turned by the old rotation, and its CenterPosition on the same side) or the piece is unmatched as footprint_mismatch. Never open: if the old piece blocks air or gravity now, the target's final state must too (would_open refuses); a leaky old piece made airtight is allowed and reported as seals. Player-placeable (1.4.5+): a player's cursor must accept the new piece where the old one stands once the old one is gone (check_replaceable's rule: the old piece must also stand where the cursor snaps it, at a quarter turn the cursor gives the new prefab), else cannot_place with the game's reason; without a placement cursor for the target (the game makes one per structure prefab at start, a dedicated server too; a prefab registered after that has none) this is not checked. Pressure: each face's current difference (the two cells' pressures, the planet's where a cell has no atmosphere) against the face's summed MaxPressureDelta with the new wall; at or above it the game would damage the new wall until it breaks, so the run is refused (would_overstress); above the new wall's stress mark (MaxPressureDelta x Thing.StressedRatio) the piece is flagged stressed (pieces[].stressed and the face's verdict; not a refusal); a face beside a frame is shielded (the game never stresses it). Kept (with a reason): already_at_target, special_piece, not_selected, indestructible, broken (a damaged build state), being_destroyed. DRY RUN BY DEFAULT: reports every piece (reference_id, prefab_name, position, rotation_deg, build_state, target_prefab_name, target_build_state, blocks_air_before/after, blocks_gravity_before/after, air_change keeps|seals|would_open, seals, stressed, faces [{position, cells a and b, pressure_kpa_a, pressure_kpa_b, difference_kpa, max_pressure_delta_kpa_before, max_pressure_delta_kpa_after, verdict ok|stressed|overstressed|shielded}], room_ids, materials [{prefab_name, cost, refund (what deconstructing the old piece returns, which pays toward the new one with refund on or off), net (cost less refund; negative = given back, only with refund on: never below 0 with refund false)}]; up to limit, default 200), by_prefab, kept_pieces and unmatched_pieces with reasons, materials over the run (prefab_name, cost, refund, charge, give_back (0 with refund false: nothing is given back), available, stacks), rooms next to the pieces (room_id, cell_count, total_mol, energy_j) and ready. Every problem at once in problems: invalid_target, unmatched_pieces (skip_unmatched true leaves them), would_open, would_overstress, not_enough_materials (one per missing item), not_a_wall, thing_not_found, no_local_player, nothing_to_swap; room_not_found, too_many_pieces and not_host refuse the call. Materials by the game's deconstruct rule: each build state's ToolEntry and ToolEntry2 with their quantities, tools (welder, wrench, grinder) skipped; the new piece costs its states 0 to final, the old one gives back states 0 to its current one, netted per item per piece: the shortfall is taken from from_id (default the local player; its stacks at any slot depth) and the surplus given back with refund (default true), into from_id's inventory (default the local player): first onto matching stacks: from_id itself when it is one (a coil stack), then anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, a holder's own slots before those of the items in them, never a hidden slot or a stack's own slot (a cable coil's, whose contents the game destroys with it); only what nothing takes goes on the ground a metre in front of the outermost holder, at rest (never inside the player); the job's refund_delivered lists each part with where: merged, slot or ground; tool wear, welder fuel and battery are not charged. A real run needs dry_run false AND confirm true and is refused (status refused, nothing changed) unless the checks find nothing. It returns a job (status waiting, job_id, preflight): the tick is held, every check runs again once it has stopped, every piece is swapped in that one frame, then with the tick still held the result is checked (held_check: each new piece in the planned slots at its final state blocking as planned, old pieces gone, every room's and cell's air around the pieces as before, since nothing ran, to within the gas audit's rounding: 0.01 mol and 10 J plus 0.001 % of the total), the tick is let go (status verifying) until the game has run its ticks and re-evaluated its rooms (at most 10 s), then held once more for the room check (room_check: each room next to a swapped piece under the same room id with the same cells, and total moles and energy within 1 % or 0.5 mol / 1 kJ; rooms with new ids there are new_rooms, not problems; problems room_gone, room_cells_changed, room_air_changed, and rooms_not_settled when the game did not run in time, e.g. paused). Poll with job_id alone: status applied, applied_with_differences, stopped (a swap failed: swapped lists old and new ids, not_swapped the rest, stopped_at the piece, why, piece_intact and rolled_back: a new piece that did not take every slot or did not seal is taken away and the old piece put back in its slots, so no face is ever left open), applied_unchecked, or refused (final_check says why). One swap job at a time across upgrade_*, clean_* and replace_* (busy). Multiplayer: host only; clients get the new walls through the game's own sync; players without the mod see normal walls. No gateway is needed. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left.",
            ReplaceSchema("wall", targetRequired: true),
            readOnly: false),
        Tool(
            "replace_frames",
            "Replace frames in place with another frame prefab (to, e.g. StructureFrame steel or StructureFrameIron) or, without to, with each frame's own prefab at its final build state, which finishes unfinished frames in place (a frame already finished is kept as already_at_target). Everything as replace_walls: the same tick-held one-frame swap that never leaves a cell open (old piece marked as being destroyed, new frame built in its Center slot and raised to its final state, old one removed only once the new one holds every cell and blocks what it blocked; otherwise rolled back), the same dry run, confirm, job, polling, materials, refund, from_id, from_prefabs, limit, skip_unmatched, held_check and room_check. Only exactly Frame is taken or built (rocket towers are special_piece); the target must be a loaded prefab some kit builds. Scope: reference_ids, or room_id (every frame in a cell next to one of the room's cells, and frames inside the room that let gravity pass). Footprint: the same GridBounds cells at the old position and rotation (footprint_mismatch otherwise). Never open: a frame blocking air or gravity must be replaced by one whose final state blocks them too (would_open). Finishing a frame (the new one blocks air or gravity the old one let through, reported as seals) closes its cell: the run is refused with cell_occupied when anything else is in that cell (another structure, a small-grid piece such as a pipe, cable, chute or device inside it, a player, creature or loose item there); walls on the frame's faces are fine and must still be there after the swap (held_check face_structures_changed otherwise). As in the game when the last sheet is welded, a sealing frame's cell leaves its room and its gas is divided among its open neighbours; the room check expects those cells gone and allows that much gas to move. Frames are never pressure-stressed (the game only stresses face structures). Problems as replace_walls, with not_a_frame and cell_occupied instead of not_a_wall and would_overstress. Host only. No gateway is needed. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left.",
            ReplaceSchema("frame", targetRequired: false),
            readOnly: false),
        Tool(
            "place_cables",
            "Lay a cable run, or one piece, the way a coil builds it, choosing for every small-grid cell (0.5 m) the one-cell piece of the grade whose ends exactly match that cell's connections (straight, corner, tee, corner3, cross, corner4, 5- and 6-way; turned as needed; read from the loaded coil, never a table; long straights are never used). Run: waypoints (positions in metres, joined by straight axis-aligned lines; the caller decides the route, nothing is searched; plan_cable_route finds one) or cells (every cell, each a neighbour of the one before), or piece {at, ends: [\"+x\", \"-y\", ...]} for a single piece (each end named once; a repeated end is invalid_argument; a single end gets a straight whose far end stays open, warning open_end), or (1.4.4+) pieces [{at, ends}] for up to 256 separate pieces in one job, each as piece lays it (undo_job builds removed pieces again this way); an end pointing into another piece of the job that has no end back stays open, warning open_end. Positions snap to the small cell they fall in (centres on multiples of 0.5 m). grade: normal, heavy (default) or super_heavy. join: ends (default: the run's first and last cells also join every open cable end and device port pointing at them, and the cable straight ahead of a run end that joins no device port and no piece: a run end standing on an existing piece or joining a piece's open end has reached it, so the piece beyond it, which may be across a transformer, is never joined; a run end standing on an existing piece joins that piece and ports pointing at it, but no other piece's open end pointing into its cell from the side, which may be another network's), none (only the run and extra_ends), all (every run cell joins every open end and port pointing at it). extra_ends [{at, toward}] add ends to run cells. Meeting existing cable: a run cell holding a cable keeps it when it already has the ends, else it is replaced by the piece of its own coil with its ends plus the run's (a straight crossed becomes a cross); a neighbour a run end joins that has no end towards it becomes a junction (the inverse of simplify_junctions); a piece with a fuse or analyser mounted, indestructible or rocket pieces are refused (cannot_change); a long straight is split (see below) or, with allow_split_long false, refused (long_piece). A run end with nothing to join gets a straight whose far end stays open (warning open_end); a one-cell run that joins nothing has no direction at all: nothing_to_join (name its ends with piece), or content_mismatch when a pipe of other content points at it. A new piece in a free device port's joining cell with no end towards the port warns blocks_port (nothing could join that port afterwards; plan_*_route reserve_ports keeps a route out of such cells). branches [{waypoints|cells, attach}]: side runs of a tree, each joining the run (or an earlier branch) at attach with a junction; their first cells join ports and ends as run ends do. A long straight the run must join in its middle or cross is split into singles in the same job (allow_split_long, default true; warning long_split; it is listed in removals and its cells as new singles of its own grade, colour and owner). would_loop (warning): a new link joins what is already joined another way (a run whose ends both reach one network); keep it only if the redundancy is meant, else plan with several starts or clean it later with clean_cables remove_loops. through_air (warning): new pieces on no frame and no wall plane (floating), listed; the report's air_cells counts them (0 for a run fully over frames or walls). Placement is checked as the game's cursor would (the server checks nothing itself): a cell with a device, chute or another small-grid thing is cell_blocked; a pipe in the cell blocks only along its own axis; frames and walls never block cables. remove_ids: pieces removed in the same job before building (a reroute: no power tick sees a device unpowered); a burnt cable (StructureCableStraightBurnt and the other CableRuptured pieces an overload leaves) may be named too, and its cell is then free for the run. assume_removed: ids of things (cables, or any other small-grid thing) checked as if already gone: their cells free, their links absent, cable pieces forecast as removed (listed in removals with assumed true, refund not counted); nothing of them is removed, and a real run is refused (assumed_present) while any still stands, so remove them first or pass the cable pieces as remove_ids (1.4.4+: a real run queued with wait behind another job is checked when it starts instead, so the job ahead may remove them); ids naming nothing standing are skipped. Guards, from a forecast of every network after the edit (links by the game's own connection rule; links.model_matches_game proves the model on these very pieces): would_bridge when the run would join two or more cable networks, or put two power ports of one device on one network (both sides of an APC or transformer, a battery's input and output), listing the networks and the devices on each; allowed only when allow_bridge names every network of that merge (or the device). A bare allow_bridge id naming no thing and no network is network_not_found, one naming neither a piece, a device nor a network, or a device with fewer than two ports of the kind (it bridges nothing: a locker), invalid_argument; join_to naming no network is network_not_found. would_split when removals split a network or leave a device port joined to nothing (allow_split). would_overload when a network after the edit would carry min(potential, required), pooled over what it joins, above its weakest cable, new pieces included (the game burns a cable every power tick); pooled over the networks it keeps pieces of, a network the edit removes whole whose devices it keeps (a reroute replacing every piece carries the old load), and every device port it newly joins, estimated from the device's own state (a battery's charge on its output and its free capacity on its input, an APC's or transformer's output supply and its input demand, a solar panel's or generator's rate, a consumer's UsedPower while on and built; with each device's own on/off and error checks as the game makes them: an output gives nothing while off or in error, a consumer in error still draws, a solar panel gives its rate on or off; only ports in the device's power role count: a data-only port (a station battery's port 0) carries nothing, and a device side is counted once per network however many of its ports are on it), so a run of new pieces only between two devices on no network is guarded too; where a removal splits a network, each part is checked with the whole old network's numbers (its networks_after guard shows them, even for a part with no devices left): an upper bound, harmless since a removal cannot raise the load. would_overload_when_on (warning, 1.4.4+): the edit is safe as the devices stand, but the same count with every device on that network that is off now switched on (each at the game's ceiling, not at the charge now, which drifts between a dry run and the real run: a station battery's PowerMaximum on its output and on its input, since the game has no battery rate limit and a battery gives all it holds and takes all it lacks in one power tick; an APC's output its MaximumPower, input potential plus a full cell; a power transmitter's MaxPowerTransmission (5000 W), its output at most its input network's potential; a consumer's UsedPower, a generator's rate; the off devices already on a pooled network included, whose load the game leaves out while they are off; and, 1.4.4+, behind every APC, transformer or power transmitter whose input is on the network: what the devices off on its output network would then require, and on through the next such device there, up to 8 deep, each network counted once and never one of the edit's own, a transformer passing at most its Setting and a transmitter at most MaxPowerTransmission in all; a battery passes nothing on, its input taking only its own charge; so joining an APC's input while an empty battery sits off on its output warns) would be over the weakest cable, so switching them on burns a cable at once (e.g. two full batteries joined while both are off); the message names the off devices, and with none off there is no warning. The job is not refused: keep them off, use a higher grade, or keep the networks apart. Where the edit splits a network, each part is counted switched on from its own devices only (a removal that parts an off source from its consumer warns nothing). link_lost when a changed piece would lose a link. DRY RUN BY DEFAULT: cells [{at, action place|change|keep, prefab_name, rotation_deg, shape, ends, cost, refund, existing, joins [{toward, kind run|piece|port|open, thing, port}], open_end}], removals, materials {from, needed [{prefab_name, needed, available, stacks}], refund_enabled, refund (empty with refund false: nothing comes back, and each cell's refund and each removal's refund are 0 / empty too)}, networks_before (cable_count, required_w, potential_w, actual_w, lowest_cable_max_w, lowest_fuse_break_w, devices), networks_after (the networks the edit changes; a device's other network it leaves as it is is not listed) [{index, networks_before, new_pieces, devices [{device, port, bridging (1.4.4+: true only for a port the edit puts on one network with another port of its device that it was not on one network with; a port whose network does not change never), network_before}], guard {potential_w, required_w, flow_w, lowest_cable_max_w, overloads}}], would_bridge [{kind networks|device, networks [{network_id, devices}], device, ports, allowed}], would_split, links {count_before, count_after, added, lost, model_matches_game}, problems, warnings, ready. Coils: each new piece costs its coil entry quantity, a change the difference (the coil's merge rule), taken from from_id (default the local player); not_enough_coils refuses; nothing is made for free; refund (default true) gives back what removed pieces and cheaper changes return, into from_id's inventory (default the local player): first onto matching stacks: from_id itself when it is one (a coil stack), then anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, a holder's own slots before those of the items in them, never a hidden slot or a stack's own slot (a cable coil's, whose contents the game destroys with it); only what nothing takes goes on the ground a metre in front of the outermost holder, at rest (never inside the player); a from_id stack stored in a holder (a coil in a locker) takes the refund onto itself only, and what does not fit on it goes on the ground in front of that holder, never into the holder's other slots; refunded lists each part with where: merged, slot or ground. A real run needs dry_run false AND confirm true: a job (poll with job_id) that holds the game tick, runs every check again, removes, then builds every piece in one frame (changes as the coil's merge does, new pieces as a coil places them; the game merges networks itself), and verifies: links as predicted, the pieces of each forecast network on one network and different forecasts on different ones, every device port where forecast. Status applied, applied_with_differences, stopped, applied_unchecked or refused. One job at a time across upgrade_*, clean_*, replace_*, place_* and remove_*. Host only. No gateway is needed. would_split (1.3.0+; ports left joined to nothing give one entry per network they were on, with its network_id) also lists per resulting network its devices (components [{index, devices, holds_root}]), root (the devices feeding it: root, else every supplier found: an APC, transformer or battery output, a generator, a solar panel) and cut_off (the devices no root reaches after the edit; null when no root is on the network); the problem message names them too. Tap check (1.3.0+): a run end left with an open end that stops next to, or one free cell short of, a piece of another network it could join (never a pipe of other content) warns not_joined naming the piece, the tap cells and the direction the run's own open end there points. join_to (a network handle): the network the run must end up on; not_joined when it does not (the nearest near miss and every run cell beside one listed); join_trunk: true adds the missing tap (up to one free cell, into the target piece, which becomes a junction; warning tap_added) instead. root: the device would_split measures against. A finished job's log lists created_ids (every piece built, new and changed) and created_by_part [{part: run, branch N, joined, fill, ids}]. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}. Doors and windows (1.4.3+): a door's face (jambs, top edge, threshold) and a band either side of it (mod config [Layout] DoorKeepOutBand, default 0.5 m) inside its rectangle is its keep-out; a new piece there is refused with in_door_keepout (allow_door_keepout: true makes it a warning; a piece hidden inside the floor slab under a threshold is not in it; the door's own port cells are released). A new piece on a window's face (inside its square: glass, composite, padded and shuttered windows and window shutters; floor gratings are not windows) warns crosses_window.",
            PlaceSchema("cable", ["normal", "heavy", "super_heavy"], "heavy"),
            readOnly: false),
        Tool(
            "remove_cables",
            "Remove cable pieces as wire cutters would: reference_ids, or the cable in each cell of waypoints or cells (positions in metres). A burnt cable (a CableRuptured piece such as StructureCableStraightBurnt, left by an overload: on no network, built by no coil) is removed too, by id or by its cell, and refunds nothing. Refused for a piece with a fuse or analyser mounted (the game refuses too), indestructible or rocket pieces. Guards and report as place_cables: would_split when a network would fall apart or a device port would be left joined to nothing, listing each part (networks_after) with its devices; allowed with allow_split. A network that only loses pieces keeps its id (pieces leave it before they are destroyed); a split is rebuilt by the game from the removed pieces' neighbours, with new ids. Refund (default true): what deconstructing gives back (refund false moves no item, so it needs neither a local player nor from_id), into from_id's inventory (default the local player): first onto matching stacks: from_id itself when it is one (a coil stack), then anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, a holder's own slots before those of the items in them, never a hidden slot or a stack's own slot (a cable coil's, whose contents the game destroys with it); only what nothing takes goes on the ground a metre in front of the outermost holder, at rest (never inside the player); a from_id stack stored in a holder (a coil in a locker) takes the refund onto itself only, and what does not fit on it goes on the ground in front of that holder, never into the holder's other slots; refunded lists each part with where: merged, slot or ground. Dry run by default; dry_run false and confirm true runs a tick-held job, polled with job_id. Host only. No gateway is needed. would_split (1.3.0+; ports left joined to nothing give one entry per network they were on, with its network_id) lists per resulting network its devices, the root (root, else every supplier on it) and cut_off, the devices no root reaches after the removal; root names the root device. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}.",
            RemoveSchema("cable"),
            readOnly: false),
        Tool(
            "place_pipes",
            "place_cables for pipes: the same run forms, joins, piece choice (from the kit of the grade: gas, liquid, insulated_gas or insulated_liquid; grade is required), placement check (a cable in the cell blocks only along its own axis), removals, dry run, job and checks, with the pipe guards. A pipe of other content is never joined (content_mismatch). would_bridge when the run would join two pipe networks (each listed in networks_before with content, total_mol, temperature, pressure, volume, main gases and devices) or put two pipe ports of one device on one network (a pump's or regulator's two sides); allow_bridge names the networks meant. would_burst when the pooled contents in the pooled volume (new pipes added, removed ones taken off) would exceed the weakest pipe. A long straight split to be joined keeps its network and contents even when it is the network's only pipe: its singles are built over it and take its network on (id kept, its contents pooled in networks_after), and the run's new pieces are built outward from them (1.4.4+), in either form (pieces or waypoints). Removals never lose or move contents: removing every pipe of a network that holds gas or liquid is refused (holds_contents), and so is a removal that would split one (contents_would_move); empty it first. Placing into a network adds volume: contents stay, pressure falls. Kits instead of coils. Host only. No gateway is needed. 1.3.0+: would_split device detail, the tap check (not_joined, join_to, join_trunk), created_ids, the busy/wait job slot and network handles as place_cables. Gas check (1.4.1+): the job applies the game's queued gas changes after every piece it builds (the game applies them only at the next tick, and two network merges in one tick lost a network's gas), then compares every family of pipe networks it changed (1.4.4+: networks sharing a pipe, or a cell a pipe filled before and one fills after, whichever id the game's merges kept), moles and energy, before and after: gas_check {checked, ok, summary, families [{networks_before, networks_after, mol_before, mol_after, energy_before_j, energy_after_j, planned_loss_mol, missing_mol, emptied, ok}], ghosts (networks without pipes left holding gas, with the devices still on them), recovered [{into, mol, energy_j}] (gas the merge lost, put back; never past a network's weakest pipe), withheld (1.4.4+: [{network_id, family_networks, mol, pressure_after_kpa, rating_kpa}] gas a family lacks that was not put back because it would take that network over its weakest pipe, which would burst; the check fails), ghosts_cleared, old_ghosts (pipeless networks that already held that gas before), orphans (1.4.4+: networks the game no longer lists that pipes still name [{network_id, pipes, mol, energy_j, volume_l}]; nothing simulates them and their gas counts where it sits, so a copy shows as gas that appeared; any fails the check), old_orphans (such networks already there before the job)}. Status gas_lost when anything is still missing; every later pipe job (place_structure and remove_structure runs too when they place or remove a pipe piece, in-line tank, passive vent or anything with a pipe end) is then refused (gas_check_failed) until the world is loaded again (a save loaded, or back to the menu; nothing else lifts it). Dry runs still answer.",
            PlaceSchema("pipe", ["gas", "liquid", "insulated_gas", "insulated_liquid"], null),
            readOnly: false),
        Tool(
            "remove_pipes",
            "remove_cables for pipes, with the pipe guards: a removal that would empty a network holding gas or liquid (holds_contents) or split one (contents_would_move) is refused; otherwise removed pipes leave their network first, so its contents stay and the pressure rises (would_burst refuses). Refused for a pipe with a meter or other device mounted. Host only. No gateway is needed. 1.3.0+: would_split device detail (root), the busy/wait job slot and network handles as remove_cables. Gas check (1.4.1+): the job applies the game's queued gas changes after every piece it builds (the game applies them only at the next tick, and two network merges in one tick lost a network's gas), then compares every family of pipe networks it changed (1.4.4+: networks sharing a pipe, or a cell a pipe filled before and one fills after, whichever id the game's merges kept), moles and energy, before and after: gas_check {checked, ok, summary, families [{networks_before, networks_after, mol_before, mol_after, energy_before_j, energy_after_j, planned_loss_mol, missing_mol, emptied, ok}], ghosts (networks without pipes left holding gas, with the devices still on them), recovered [{into, mol, energy_j}] (gas the merge lost, put back; never past a network's weakest pipe), withheld (1.4.4+: [{network_id, family_networks, mol, pressure_after_kpa, rating_kpa}] gas a family lacks that was not put back because it would take that network over its weakest pipe, which would burst; the check fails), ghosts_cleared, old_ghosts (pipeless networks that already held that gas before), orphans (1.4.4+: networks the game no longer lists that pipes still name [{network_id, pipes, mol, energy_j, volume_l}]; nothing simulates them and their gas counts where it sits, so a copy shows as gas that appeared; any fails the check), old_orphans (such networks already there before the job)}. Status gas_lost when anything is still missing; every later pipe job (place_structure and remove_structure runs too when they place or remove a pipe piece, in-line tank, passive vent or anything with a pipe end) is then refused (gas_check_failed) until the world is loaded again (a save loaded, or back to the menu; nothing else lifts it). Dry runs still answer.",
            RemoveSchema("pipe"),
            readOnly: false),
        Tool(
            "place_chutes",
            "Lay a chute run, or one piece, from Kit (Chute) as a player builds it, choosing for every small-grid cell (0.5 m) the one-cell piece whose ends match that cell's connections: a straight or corner (1 kit each) or, where three ends meet in a T, a junction (2 kits) turned so its output faces downstream; chutes have no other shape (4 ends or a corner of three: no_piece_for_ends), and long straights, windows, valves, overflows, splitters, bins, inlets and outlets are never placed. Items travel along the run from its first cell to its last: start at the source (a device's chute Output port, a chute bin, a line that carries items towards it) and end at the sink (a device's chute Input port, a line that carries them away). The same run forms (waypoints, cells, piece {at, ends}, pieces [{at, ends}] 1.4.4+), join (ends by default: the run's first and last cells join every open chute end and chute port pointing at them, and the chute straight ahead of a run end that joins no chute port and no piece, which becomes a junction), extra_ends, remove_ids, dry run, job, materials and checks as place_cables, except that too few kits in from_id is not_enough_kits (not not_enough_coils). Flow guards, from the item flow of every chute network the edit touches (CODE: a straight or corner passes an item out of the end it did not come in by; a junction takes items in through its two inputs and lets them out only through its output; a device port with role Input takes items off the chute, Output pushes them in): flow_reversed when what the run joins pushes items against the run's direction (reverse the waypoints); flow_conflict when two flows would meet head-on or an item would have to enter a piece through its output (a junction merges, it cannot split a flow: that needs a splitter, which these tools do not build); flow_ambiguous when a junction is needed and nothing fixes which way it must face. Warnings: drops_items where items would leave an open end and fall to the ground (a run end with nothing to join gets a straight whose far end stays open: open_end); flow_unknown when no source or sink fixes the direction of new pieces. would_bridge when the run joins two chute networks (listing each one's chute count, riding items and devices), or two chute ports of one device (its output into its own input); allow_bridge names those meant. A piece with an item riding in it is never replaced (cannot_change) or removed. Placement is checked as the cursor would: chutes collide with cables, pipes, devices and other chutes in the same small cell (cell_blocked); frames and walls never block them. Each cell reports flow {in, out} (world axes). Chute network ids change after any removal or change (the game rebuilds them). Host only. No gateway is needed. 1.3.0+: would_split device detail, the tap check (not_joined, join_to, join_trunk), created_ids, the busy/wait job slot and network handles as place_cables. Doors and windows (1.4.3+): as place_cables, in_door_keepout (allow_door_keepout) and crosses_window.",
            PlaceSchema("chute", ["chute"], "chute"),
            readOnly: false),
        Tool(
            "remove_chutes",
            "Remove chute pieces (straights, corners, junctions, and also valves, overflows and splitters) as a player's deconstruction does: reference_ids, or the chute in each cell of waypoints or cells (positions in metres). Refused for a piece with an item riding in it (cannot_remove: the game would lose the item with it; let it pass or take it out with move_item), indestructible or rocket pieces. would_split when a network would fall apart or a device port would be left joined to nothing (allow_split). drops_items warns where the removal leaves an end that items would now fall out of. Refund (default true): the kits the pieces were built from, into from_id's inventory (default the local player): first onto matching stacks: from_id itself when it is one (a coil stack), then anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, a holder's own slots before those of the items in them, never a hidden slot or a stack's own slot (a cable coil's, whose contents the game destroys with it); only what nothing takes goes on the ground a metre in front of the outermost holder, at rest (never inside the player); a from_id stack stored in a holder (a coil in a locker) takes the refund onto itself only, and what does not fit on it goes on the ground in front of that holder, never into the holder's other slots; refunded lists each part with where: merged, slot or ground. The game rebuilds the networks around every removed chute, so they take new ids. Dry run by default; dry_run false and confirm true runs a tick-held job, polled with job_id. Host only. No gateway is needed. 1.3.0+: would_split device detail (root), the busy/wait job slot and network handles as remove_cables.",
            RemoveSchema("chute"),
            readOnly: false),
        Tool(
            "show_preview",
            "Draw in-game wire boxes for a planned layout (1.4.3+), on your screen only (other players see nothing; never the game's construction cursor): the same placement fields as place_structure (placements, or prefab and at, with rotation/facing/face/orient/above_floor_m...) are dry-run and each placement's footprint (green, red when it has a problem), render box (white) and port joining cells (cyan, red when blocked) drawn; cells [[x, y, z], ...] (up to 512, yellow 0.5 m cubes, e.g. a planned route's waypoints or cells) and boxes [{min, max, color red|green|white|cyan|yellow}] (up to 64) too. seconds (default 30, max 300); a new call replaces the last unless keep: true; clear: true only removes them. Returns shown, cleared, seconds, dry_run (place_structure's own dry run of the placements) and notes. Changes nothing in the world. Needs a player camera (not on a dedicated server).",
            new { type = "object", properties = new { placements = new { type = "array", maxItems = 64, items = new { type = "object" }, description = "As place_structure's placements." }, prefab = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } } }, at = new { description = "As place_structure's at." }, rotation = new { type = "array", items = new { type = "number" } }, facing = new { type = "string" }, up = new { type = "string" }, face = new { type = "string" }, orient = new { type = "object" }, above_floor_m = new { type = "number" }, build_state = new { }, allow_door_keepout = new { type = "boolean" }, cells = new { type = "array", maxItems = 512 }, boxes = new { type = "array", maxItems = 64, items = new { type = "object", properties = new { min = new { }, max = new { }, color = new { type = "string", @enum = new[] { "red", "green", "white", "cyan", "yellow" } } }, required = new[] { "min", "max" } } }, seconds = new { type = "number", exclusiveMinimum = 0, maximum = 300 }, keep = new { type = "boolean" }, clear = new { type = "boolean" } }, additionalProperties = false },
            readOnly: true),
        Tool(
            "undo_job",
            "Undo a finished place or remove job (1.4.3+): place_cables, place_pipes, place_chutes, remove_cables, remove_pipes, remove_chutes, place_structure, remove_structure, among the last 16 jobs. The inverse: remove every thing the job built (its log's created ids: new and changed pieces), then build again every thing it removed (changed pieces' old ones included) as it stood, from a snapshot taken when the job started (position, turn, build state, label): cable, pipe and chute pieces through place_cables, place_pipes and place_chutes (1.4.4+: the pieces form, one call per tool and grade, each piece with the ends it had; a long straight comes back as singles), so would_bridge, would_split, the burst and gas guards apply; everything else through place_structure. Who removes: the pieces of a tool that also builds pieces again are removed by that tool's first piece run itself (its remove_ids; with refund_to none, refund false), in the same job that builds the old pieces back, so a job that changed a piece in the middle of a network (a tee added onto a trunk, a long straight crossed) is undone in one step, the network is never left cut between two jobs, and that run's dry run shows the networks as they end up (1.4.4+); everything else is removed by remove_structure first (refund_to ground applies to it only). A piece restore that needs several grades of one tool is built by several runs, and only the first removes; when the first alone cannot rejoin the network its dry run says would_split and the undo is refused. Refused, status refused with plan.diverged naming why, when the world is no longer as the job left it: something it built is gone or is another prefab now, something it removed has no snapshot (the job was started before 1.4.3), stood off the grid's axes or is a network piece no coil or kit lays, or the job had not finished; or the job was already undone (an earlier real undo_job of it started jobs not all refused): plan.diverged says so and plan.undone_by lists those jobs (undo them, the last first, to have it back). A burnt cable the job removed is never built again: plan.notes says so and the rest of the undo goes ahead. allow_bridge (1.4.4+): passed to the piece runs (network or device handles of the pieces' kind), e.g. to rejoin a network a removal split. Dry run by default: status dry_run, plan {remove, restore, diverged, notes, ready}, remove_arguments (remove_structure's, for what no piece run removes; null when there is none), place_arguments and piece_runs [{tool, arguments, reply}] (every tool's arguments), removal (remove_structure's own dry run), placement (place_structure's dry run when nothing is removed first; else it is checked when its job starts) and each piece run's reply (its dry run, checked as if the removal were done: assume_removed). plan.ready (1.4.4+) is true only when the plan and every one of those dry runs are ready. A real run (dry_run false, confirm true) makes the same checks and is refused unless all are ready; then status scheduled, removal (remove_structure's job), placement (place_structure's job) and piece_runs[].reply (each place tool's job), each queued behind the one before with wait, so it is checked against the world the jobs before it left; poll each with its own tool's job_id. Every guard of every tool applies (a pipe network holding gas refuses the removal, as it would by hand; materials are paid and refunded as by hand). Source: every call takes the job's own from_id (the coil, kit or refund holder it named; recorded when the job started), so an undo runs on a dedicated server whenever the job did; from_id overrides it (needed when the job used the local player, or its record is gone); removing what a free placement built gives nothing back (refund_to none), refund_to overrides the removal's refund target (source, ground, none); plan.notes says which applied. paste_blueprint has its own undo. Host only. No gateway is needed.",
            new { type = "object", properties = new { job_id = new { type = "string", description = "The job to undo (run-N, place-N, remove-N)." }, dry_run = new { type = "boolean", description = "Default true." }, confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." }, allow_bridge = new { type = "array", minItems = 1, maxItems = 64, items = NetworkHandle("A network (its id, or {reference_id, port} of a device on it) or a device id, of the restored pieces' kind."), description = "Passed to every piece run (place_cables, place_pipes, place_chutes): networks and devices whose joining is meant." }, from_id = new { type = "string", description = "The source for every call (materials taken, refunds given); default the job's own from_id, else the local player." }, refund_to = new { type = "string", @enum = new[] { "source", "ground", "none" }, description = "The removal's refund target; default none when the job placed for free, else source." } }, required = new[] { "job_id" }, additionalProperties = false },
            readOnly: false),
        Tool(
            "lint_layout",
            "Check a room (room_id) or a box (min, max; the 2 m cells it overlaps, a side on a face plane taking nothing beyond it; at most 4000) against the layout rules (1.4.3+), from what stands there now. Rules and levels: run_in_door_keepout (warning: a cable, pipe or chute piece in a door's keep-out), port_into_doorway (warning: a device port joins in a door's keep-out), port_cell_foreign_network (warning: a port's joining cell holds a piece of its kind that does not join it, so nothing can), floating_run (warning: a cable, pipe or chute piece in air, on no frame and no wall plane; in-line tanks and passive vents are not runs and are not checked), run_crosses_window (warning: a piece on a window's face), device_visual_overlap (warning, 1.4.4+: two devices whose mesh boxes run more than 0.1 m into each other, or one inside the other, e.g. a sensor under a console's overhang; flush neighbours only touch or overlap by a rim; things sharing a cell skipped), mounted_faces_out_of_room (warning: a mounted device's back is in the room and its front is not), device_crosses_seam (warning: a mounted device's mesh box spans more than one 2 m wall section by more than 0.1 m, though neither side is wider than 2.2 m, so it could fit one), run_along_door (info: a cable, pipe or chute piece hugging a door's jamb, just outside its keep-out; pipe_along_door before 1.4.4), controls_not_on_wall (info: a console, computer, display, dial, button, switch, lever or keypad not mounted on a wall); not_replaceable (problem, reported first, 1.4.5+: a player could not place the thing again where it stands with its neighbours present, e.g. a vent with nothing behind it, a battery with no frame below, a thing no kit builds; check_replaceable's rule on every piece, device and 2 m structure there; the message names the rule and the game's reason) and replaceable_unchecked (info: the rule could not be asked, e.g. no placement cursor for the prefab). Returns region, cells, pieces, devices, structures (2 m frames, plates, doors and large devices, 1.4.5+), doors (checked), counts {code: n}, findings [{code, level, message, reference_id, other_id (the door, the other device, the foreign piece), at}] warnings first (limit default 100, max 500), total, has_more. Read only. No gateway is needed.",
            new { type = "object", properties = new { room_id = new { type = "string" }, min = new { description = "Box corner, [x, y, z] metres." }, max = new { description = "The other corner." }, limit = new { type = "integer", minimum = 1, maximum = 500 } }, additionalProperties = false },
            readOnly: true),
        Tool(
            "check_replaceable",
            "For each thing (reference_ids, 1 to 1024), could a player place it again exactly where it stands, with every neighbour present (1.4.5+)? The game's own placement cursor for its prefab is put at the thing's position and turn and asked as a player's cursor asks: the class's CanConstruct (a frame below for batteries, printers and machines; a frame one grid down for dishes, radiators, wind turbines, landing pads, stairs; the pipe or cable a pipe- or cable-mounted device sits on, straight, along it, of its content; terrain, outside, a rocket; no port straight onto another device's port; walls and doors on free faces; small-grid and cell collisions) and CanMountOnWall for every face-mounted piece (a frame in the cell behind its back, or a wall or floor plate on that plane; a frame only for pieces that require one). The thing itself is treated as gone: a refusal naming only it means its rules held, and its neighbours are then checked again without it. It must also stand where the cursor snaps it, at a quarter turn the cursor gives that prefab. Returns results [{reference_id, prefab_name (null for an unknown id), replaceable (true; false: the cursor would refuse it; null: not checked), rule (support: nothing to stand on or mount to; mount: behind it is something that does not allow mounting, or the wrong face; host: the pipe, cable, valve or machine it mounts on is missing or wrong; location: terrain, outside, rocket; adjacent: a port straight onto another device's port; collision: something else takes its cells, slot or face; rotation: its cursor never turns it that way; no_kit: no kit builds it; off_grid: not where the cursor snaps it, or not at a quarter turn; null when none applies or the text is not a known one), reason (the game's text, or the rule's own words; for null, why it was not checked: no placement cursor for the prefab (the game makes one per structure prefab at start, a dedicated server too, so this is rare: a prefab registered after that), not a structure, no such thing)}] in the order asked. Not seen: a device whose own CanConstruct stops at itself before the adjacent-port rule (vents, lights, consoles, APCs) is not checked for ports onto other devices' ports; loose things inside are ignored. The game's frame cursor does not look at small-grid devices, so a frame can be placed (by a player, and by place_structure) around a station battery or another device that needs a frame below; that device then answers false (support: it stands inside a frame, not on one). BlueprintMod's paste skips all of this, so run it over every pasted piece. The same rule refuses placements in place_structure and replace_walls/replace_frames (cannot_place). Read only. No gateway is needed.",
            new { type = "object", properties = new { reference_ids = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = "Things to check, as reference ids." } }, required = new[] { "reference_ids" }, additionalProperties = false },
            readOnly: true),
        Tool(
            "wall_map",
            "A text elevation of one 2 m face plane as seen from one side (1.4.3+), 0.5 m per character: plane (e.g. \"z=668\", an even metre) with around (a point) and side (+z/-z: the side you view from; default the side in a room), or looking: true (the plane of the face your look ray hits, around the hit, seen from your side); radius_m (default 4, max 16). Returns plane, side, right and up (the world axes a column and a row step run along), rows (a ruler line marking 2 m seams with |, then one line per 0.5 m, top first, left to right as seen: W wall, G window, D door, F a frame with no plate, . open, x a door's keep-out, c cable, p pipe, b both, h chute, a capital or digit a device or mounted thing, over every cell its mesh box covers by more than 0.1 m (1.4.4+: a console's frame overhangs its registered cells; before, only those cells), which free_rects then treats as taken; each character covers the small cell on the plane and the one in front of it; a small cell on a 2 m seam belongs to the section on its plus side, whichever side it is seen from; a passive vent or in-line tank is a thing with a key, not p), top_left (the world point of the first cell's centre, on the plane), sections [{face, look wall|window|door|frame|open, reference_id, prefab_name}], things [{key, reference_id, prefab_name, display_name, position}], legend, and with free_rects {w, h (metres, rounded up to 0.5), one_section (default true: never across a seam), require_wall (default true: only on wall, not window, frame or open), limit (default 20, max 50)}: free_rects [{row, column (top-left on the map), centre (world point on the plane), cells_wide, cells_high}] where that rectangle fits on free cells. Read only. No gateway is needed.",
            new { type = "object", properties = new { plane = new { type = "string", description = "\"x=716\", \"z=668\"...: an axis and an even metre." }, side = new { type = "string", @enum = new[] { "+x", "-x", "+y", "-y", "+z", "-z" }, description = "The side you view from (on the plane's axis)." }, looking = new { type = "boolean", description = "Use the face your look ray hits instead of plane." }, around = new { description = "[x, y, z]: the map's centre (with plane)." }, radius_m = new { type = "number", exclusiveMinimum = 0, maximum = 16 }, free_rects = new { type = "object", properties = new { w = new { type = "number" }, h = new { type = "number" }, one_section = new { type = "boolean" }, require_wall = new { type = "boolean" }, limit = new { type = "integer", minimum = 1, maximum = 50 } }, required = new[] { "w", "h" } } }, additionalProperties = false },
            readOnly: true),
        Tool(
            "find_spot",
            "Ranked places for a prefab near a point (1.4.3+), on one face plane (plane with side, or looking: true) or on the walls of a room (room_id: every level face of a room cell toward outside the room carrying a wall or window, seen from inside; its floor and ceiling are not searched, name one with plane: 1.4.4+ that plane is searched too, seen from side or else from the room's side of it (seen from the side away from the room, e.g. under its floor, its spots are those with the room behind them); a plane no room cell touches is invalid_argument). near: a point, \"player\", \"crosshair\" (default; no_camera on a dedicated server, which has no look ray) or {reference_id}; radius_m (default 4, max 12). The turn: a mounted piece faces out of the plane with its top up (facing overrides); a standing piece on a floor stands on it. Every 0.5 m spot within the radius (nearest first over all the planes, at most 4000) is aimed as the cursor snaps it and filtered on geometry first (its small cells free of devices, mounted things and chutes; 1.4.4+: its mesh box clear of every other thing's mesh box, as visual_overlap, when no_visual_overlap; require flags; one_section by its mesh box), then, nearest first and at most max_checks (default 40, max 200), checked with the game's own placement cursor and the layout preview. require: {one_section (default true: one wall section, no seam), min_bottom_above_floor_m, ports_reachable (default false: every port's joining cell free or joining), avoid_doors (default true: nothing in a door's keep-out), front_clear_m (default 0: that many metres in front free), no_visual_overlap (default true)}. Returns prefab_name, planes, spots best first (layout penalty, then distance; limit default 5, max 20) [{position, orientation, distance_m, penalty, plane, conflicts, place_arguments (place_structure's arguments: prefab, at, facing, up)}], tried, filtered, checked, rejected, reasons (1.4.4+: why spots were filtered or rejected [{reason, count}], most frequent first, up to 8; a reason is worded as its first spot had it, spots whose wording differs only in numbers counted together). Read only. No gateway is needed.",
            new { type = "object", properties = new { prefab = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } }, description = "Prefab name or prefab hash." }, near = new { description = "[x, y, z], \"player\", \"crosshair\" (default) or {reference_id}." }, plane = new { type = "string" }, side = new { type = "string", @enum = new[] { "+x", "-x", "+y", "-y", "+z", "-z" } }, looking = new { type = "boolean" }, around = new { description = "[x, y, z]: a point near the plane, to choose the side in a room." }, room_id = new { type = "string" }, facing = new { type = "string", @enum = new[] { "+x", "-x", "+y", "-y", "+z", "-z" } }, radius_m = new { type = "number", exclusiveMinimum = 0, maximum = 12 }, require = new { type = "object", properties = new { one_section = new { type = "boolean" }, min_bottom_above_floor_m = new { type = "number" }, ports_reachable = new { type = "boolean" }, avoid_doors = new { type = "boolean" }, front_clear_m = new { type = "number" }, no_visual_overlap = new { type = "boolean" } } }, limit = new { type = "integer", minimum = 1, maximum = 20 }, max_checks = new { type = "integer", minimum = 1, maximum = 200 } }, required = new[] { "prefab" }, additionalProperties = false },
            readOnly: true),
        Tool(
            "describe_prefab",
            "What a buildable prefab is before it stands anywhere (1.4.3+), in its own frame (unturned, x right, y up, z forward, origin snapped as the cursor snaps it): prefab_name, prefab_hash, display_name, build_states, runtime_type, placement (grid, face or face_mount), grid_size_m, small_grid, rotation_axes (the axes the cursor turns a grid-placed prefab about), allowed_rotations [{facing, up, euler}] (the turns place_structure accepts; a face-mounted piece: every turn, the cursor check decides), small_cells (offsets of the small cells it takes), render_box (the box its meshes fill), grid_box (the small cells' box: its real footprint; null for 2 m structures), ports [{index, type, role, flow in|out|null, at (the joining cell's offset), outward (the way a run leaves)}], visual_up {local (the own axis that reads as its top), lying_allowed, source, verified (false: a guess not yet seen live)}, reversible_flow (turbo volume pumps: Mode 0 and 1 move gas opposite ways) and has_cursor. Read only. No gateway is needed.",
            new { type = "object", properties = new { prefab = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } }, description = "Prefab name or prefab hash." } }, required = new[] { "prefab" }, additionalProperties = false },
            readOnly: true),
        Tool(
            "place_structure",
            "Place any structure some kit builds (a light, sign, device, frame, wall, tank...) by prefab name or prefab hash, at a position with a turn, at a build state, with an optional label and colour; several placements are one job. Checked as the game's own placement cursor checks it (CanConstruct: blocked cells and faces, small-grid collisions, rocket cells, each class's own rules; a face-mounted piece needs its support; nothing loose, no creature or player inside a piece that fills its cell), and again just before each piece is built; a small-grid piece whose slot in a cell is taken is refused (a coil would merge, a plain build would stack). Position: at is a point in the cell, snapped as the cursor snaps it (a 2 m device snaps to its cell's centre; 1.3.1+: when a 0.5 m-grid device, not a cable, pipe or chute, cannot be built at the point as given, it is tried set down on the surface behind it, as the cursor's ray lands on a surface: the floor plane below for a standing device, the face at its back for a mounted one, so a cell's centre works; only onto a surface that is there, a plate on that face or a frame behind it, so a taken spot is refused rather than moved into the air below; resolved.at_how says when it was set down and why the point as given was not buildable); a piece placed on a cell face (a wall: placement face) sits on the face opposite its facing. Turn: at most one of rotation [x, y, z] degrees (multiples of 90, Quaternion.Euler order), facing (+x, -x, +y up, -y, +z, -z) with optional up (default +y, or +z when facing is vertical), or face (face-placed pieces: the face of the cell it sits on; it faces into the cell). A grid-placed piece may only be turned about the axes its cursor turns it (invalid_rotation); one the cursor turns itself as it autoplaces (cables, pipes and some devices, such as lockers) is kept at any turn with the warning unusual_rotation. build_state: finished (default), first (as a kit leaves it) or an index. Cost: every build state's items up to that state (state 0 is the kit), taken from the local player's inventory at any depth or from_id, as a kit's placement takes them; free: true places without materials and is refused unless the world is creative (not_creative); it never waives the placement rule: every placement must be one a player's cursor would accept there (the rule check_replaceable asks of standing things). Cables, pipes and chutes are placed as exactly the piece given (warning network_piece; place_cables, place_pipes and place_chutes choose pieces by connections and guard merges). A piece that needs a frame below it (a station battery, a dish, landing pad parts) may stand on a frame an earlier placement of the same request puts there (the dry run finds it in the cell the game looks in for that piece, and a point above it is set down onto it as onto a standing frame): warning supported_by_placement, and the job checks it again once that frame stands; 1.4.4+ likewise a face-mounted piece (a solar panel, a wall light) whose support behind it, a frame in the cell behind or a plate on that face (a frame only for a piece that requires one), an earlier placement of the request puts there. Refused per placement: invalid_prefab (not loaded, not a structure, no kit builds it, a rocket part: a prefab the game places only in a rocket, a fuselage or launch mount; ordinary devices that could also be fitted in a rocket, e.g. batteries, tanks, pipes, valves, are placed as usual), invalid_rotation, invalid_build_state, no_cursor, cannot_place (with the game's reason; 1.4.4+: when the game's reason is 'requires a Frame below' and a frame fills the spot's own cell, it says so: such pieces, e.g. a station battery, stand in the free cell on top of a frame, not inside one; also when setting it down was tried after), no_camera, no_crosshair_hit, no_face, ambiguous_axis (a relative at or named facing read against the world), not_labelable, not_paintable, invalid_color, overlaps_placement (two placements in one slot; a face holds one wall per side, so two plates back to back are two slots; or one the game would refuse once an earlier placement of the request stands, which the cursor check of a dry run cannot see: two small-grid pieces taking the same slot of a cell, e.g. overlapping long pipes, or a frame and another piece registering in its 2 m cell, e.g. a wall facing into it, in either order; the ports and port_checks of a placement still read only what stands now); for the run: not_enough_materials, not_creative. Dry run by default: each placement's snapped position, orientation (facing, up, euler: the quarter turns rotation takes, 1.3.5+), face, build state, cost, and the materials needed and held; for a device, or another thing with ends that is not a cable, pipe or chute piece (an in-line tank, a passive vent), ports (1.3.5+): [{index, at (the cell a piece joining it would stand in), toward (the end that piece needs, into the device), type, role, network_id (null)}] where the ports would land at that position and turn, in grid_survey's shape, so a layout can be checked before building. A real run needs dry_run false and confirm true and returns a job_id; poll with job_id alone. The job holds the game tick, runs every check again, builds in one frame, then verifies each piece stands with its prefab, position, turn, state, label and colour (status applied, applied_with_differences or stopped with stopped_at). Host only. No gateway is needed. Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Gas check (1.4.1+): the job applies the game's queued gas changes after every piece it builds (the game applies them only at the next tick, and two network merges in one tick lost a network's gas), then compares every family of pipe networks it changed (1.4.4+: networks sharing a pipe, or a cell a pipe filled before and one fills after, whichever id the game's merges kept), moles and energy, before and after: gas_check {checked, ok, summary, families [{networks_before, networks_after, mol_before, mol_after, energy_before_j, energy_after_j, planned_loss_mol, missing_mol, emptied, ok}], ghosts (networks without pipes left holding gas, with the devices still on them), recovered [{into, mol, energy_j}] (gas the merge lost, put back; never past a network's weakest pipe), withheld (1.4.4+: [{network_id, family_networks, mol, pressure_after_kpa, rating_kpa}] gas a family lacks that was not put back because it would take that network over its weakest pipe, which would burst; the check fails), ghosts_cleared, old_ghosts (pipeless networks that already held that gas before), orphans (1.4.4+: networks the game no longer lists that pipes still name [{network_id, pipes, mol, energy_j, volume_l}]; nothing simulates them and their gas counts where it sits, so a copy shows as gas that appeared; any fails the check), old_orphans (such networks already there before the job)}. Status gas_lost when anything is still missing; every later pipe job (place_structure and remove_structure runs too when they place or remove a pipe piece, in-line tank, passive vent or anything with a pipe end) is then refused (gas_check_failed) until the world is loaded again (a save loaded, or back to the menu; nothing else lifts it). Dry runs still answer. A place_structure or remove_structure run that places or removes nothing with a pipe (a locker, frame, wall, light) is neither checked (gas_check null) nor held (1.4.4+). Doors and windows (1.4.3+): a small-grid piece with a cell in a door's keep-out (its face and the configured band either side inside its rectangle) is refused with in_door_keepout (allow_door_keepout: true makes it a warning); one on a window's face warns crosses_window. Layout preview (1.4.3+), per placement in the dry run: layout {footprint {small_cells {count, cells} (the small cells the game would register it in, from its own GridBounds at that position and turn), large_cells (2 m structures), body {origin, render_box (the box its meshes fill), centre_offset, grid_box (the small cells' box: its real footprint), grid_cells}, mount {plane (the face plane behind it, e.g. z=668), outward (its front when mounted, its top when standing), axes, min, max (the rectangle its mesh box covers there, 1.4.4+; its small cells before)}}, sections {walls [{face, reference_id, prefab_name, kind wall|window|door|none}], count, crosses_seam}, conflicts [{code, level info|warning|problem, message, reference_id}], port_checks [{index, at, toward, type, role, flow in|out|null, occupant (what stands in the joining cell now), joins (a piece of the port's kind there has an end toward it: it joins on build), would_join_network, blocked (why not), in_door_keepout}]}. Conflict codes: visual_overlap (1.4.4+: its mesh box and another thing's run more than 0.1 m into each other, or one lies inside the other: a small device under a console's overhang clashes though their small cells do not; flush neighbours only touch or overlap by a rim; a thing sharing one of its cells, a device on a pipe, is skipped), crosses_section_seam (its mesh box's rectangle spans more than one 2 m wall or floor section by more than 0.1 m though it could fit one, neither side wider than 2.2 m, so a shift would fix it; a warning, never a refusal; not for cable, pipe and chute pieces, in-line tanks and passive vents, which rest on no section, nor, 1.4.4+, for a piece wider than a section, such as a medium dish or a landing pad part, which crosses a seam wherever it stands; sections.crosses_seam still says it spans more than one), in_door_keepout (problem unless allow_door_keepout), crosses_window, blocks_route_cells (it would stand in the joining cell of a free port of a device beside it), front_blocked (a device, mounted thing or chute right in front of a mounted piece, or its front into a frame's body), faces_out_of_room (a mounted piece whose back is in a room and front is not), not_upright (its visual top, describe_prefab's visual_up, does not point +y; info only for prefabs that are normally built lying, the in-line tanks). Warnings are also in the report's warnings.",
            PlaceStructureSchema(),
            readOnly: false),
        Tool(
            "remove_structure",
            "Remove structures by reference id (up to 256, one job) as deconstructing them by hand would, giving back what that gives back: every build state's items down to the kit, to the source (refund_to source, the default: into from_id's inventory (default the local player): first onto matching stacks: from_id itself when it is one (a coil stack), then anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, a holder's own slots before those of the items in them, never a hidden slot or a stack's own slot (a cable coil's, whose contents the game destroys with it); only what nothing takes goes on the ground a metre in front of the outermost holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground), on the ground where each piece stood (ground), or not at all (none). Minimal guards, each naming its reason. Refused: not_a_structure (items and other movable things: use move_item), being_destroyed, indestructible, rocket (part of a rocket: placed in one, a rocket-only piece, a fuselage or launch mount; not every device that could be fitted in one), game_refuses (the game's own CanDeconstruct), has_mounted (a device mounted on it, e.g. a light, sensor, console or vent on a wall, or standing on it, when nothing else would hold that face once the request is done: no other plate on the same face, no frame beside it; the game would leave the device hanging in the air; remove the device in the same request). Refused unless allowed: broken (is_broken as thing_health and find_things report it: the game's broken state, e.g. a vent burnt out by fire, which reads 0 damage and 100 % health, and 1.4.4+ also a burst pipe or a burnt cable; the game cannot repair it, only deconstruct it, and that gives nothing back; allow_broken (1.4.3+) removes it the same way: no refund, and the game's CanDeconstruct is not asked, as the game does not ask it for a broken thing; every other guard still applies), holds_items and holds_gas (allow_contents: items drop where it stood as in the game; a tank lets its gas out into its cell, other devices lose it; an in-line tank or passive vent that is, with the rest of the request, the last of its pipe network takes the network's gas with it, which the game deletes; so does one the job leaves as the last of a part of a network it splits, since pipe pieces go first, and so does a pipe piece of a request of pipe pieces alone that the job leaves as the last of such a part (1.4.4+), and holds_gas names the moles so deleted; the job's gas check expects exactly those moles gone, planned_loss_mol, and does not put them back; allowed, each guard becomes a warning: broken_removed, items_dropped, gas_released (a tank's gas let out), contents_deleted (1.4.4+: gas or liquid the game deletes with what is removed; not the job status gas_lost), breach), contents_would_move (1.4.4+: an in-line tank or passive vent whose removal splits a pipe network holding gas or liquid, as remove_pipes refuses a split; the game divides the contents among the networks left by volume, and the message names each share; allow_contents), would_breach (it blocks air and removing it joins spaces whose pressures differ by 1 kPa or more, e.g. a wall of a pressurised room; allow_breach). Warned: port_left_open (a device end that joins a cable, pipe, chute or device now). Refused (1.4.4+): would_burst (an in-line tank or passive vent that is not the last of its pipe network takes its volume away while the game keeps the network's gas in what is left, so the pressure rises; the pressure of what is left is forecast, its pipe pieces removed in the same request counted too, and over the weakest pipe left the removal is refused as remove_pipes refuses it, naming the forecast kPa and the share of the gas to take out first; no flag lifts it. Split, contents and burst checks read one model of what the job leaves, for every pipe network the request takes anything from (pipe pieces alone too, 1.4.4+; remove_pipes' holds_contents and would_burst for them are the model's holds_gas and would_burst), in its order (pipe pieces as remove_pipes removes them, then the other structures in reference_ids order): a network the request leaves in one piece keeps its id and all its gas in what is left (every removed member leaves it first), and remove_pipes' would_split and contents_would_move see it without the tank; where it splits, each network left gets its share by volume at each split, and would_burst names the one furthest over its weakest pipe), refund_holder_removed (from_id is removed by the same request, or is held inside something it removes, e.g. a stack in a locker it takes: the refund would be destroyed with it). With refund_to none the dry run lists no refund (refund and each piece's refund are empty), as the run gives none. Cable, pipe and chute pieces are removed as remove_cables, remove_pipes and remove_chutes remove them (a network kept whole keeps its id and contents) and their checks apply: would_split is only a warning here (1.4.4+ worded without the remove tools' advice to pass allow_split or root, which remove_structure does not take; plan_removal prices a split against a root), holds_contents and contents_would_move are lifted by allow_contents, the rest refuse (they run with remove_structure's own refund_to and from_id, so they need no local player); those tools also take cells and waypoints. Dry run by default (each piece, its refund, the total refund, problems and warnings); a real run needs dry_run false and confirm true and returns a job_id; poll with job_id alone. The job holds the game tick, checks again, removes everything in one frame, delivers the refund and verifies every piece is gone. Host only. No gateway is needed. would_breach (1.3.0+) judges the whole request at once and follows the game's air rule: a face stays sealed while anything left on it blocks air, or the structure filling a cell beside it does (a finished frame), so a plate on a frame's face never breaches, and two plates back to back on one face breach only when both are removed together (reported once per face, 1.3.1+; 1.4.4+: once per opening, so a wall and the frame behind it removed together are reported once, by the first of them in reference_ids, a face an earlier piece's breach named being left out of the next one's). Job slot (1.3.0+): while another job runs, a real run answers status busy with running_job_id (nothing changed); wait: true queues it instead (status queued, its own job_id, position; up to 8 wait), and it starts once the slot is free, its whole preflight run again on the world the earlier jobs left. Gas check (1.4.1+): the job applies the game's queued gas changes after every piece it builds (the game applies them only at the next tick, and two network merges in one tick lost a network's gas), then compares every family of pipe networks it changed (1.4.4+: networks sharing a pipe, or a cell a pipe filled before and one fills after, whichever id the game's merges kept), moles and energy, before and after: gas_check {checked, ok, summary, families [{networks_before, networks_after, mol_before, mol_after, energy_before_j, energy_after_j, planned_loss_mol, missing_mol, emptied, ok}], ghosts (networks without pipes left holding gas, with the devices still on them), recovered [{into, mol, energy_j}] (gas the merge lost, put back; never past a network's weakest pipe), withheld (1.4.4+: [{network_id, family_networks, mol, pressure_after_kpa, rating_kpa}] gas a family lacks that was not put back because it would take that network over its weakest pipe, which would burst; the check fails), ghosts_cleared, old_ghosts (pipeless networks that already held that gas before), orphans (1.4.4+: networks the game no longer lists that pipes still name [{network_id, pipes, mol, energy_j, volume_l}]; nothing simulates them and their gas counts where it sits, so a copy shows as gas that appeared; any fails the check), old_orphans (such networks already there before the job)}. Status gas_lost when anything is still missing; every later pipe job (place_structure and remove_structure runs too when they place or remove a pipe piece, in-line tank, passive vent or anything with a pipe end) is then refused (gas_check_failed) until the world is loaded again (a save loaded, or back to the menu; nothing else lifts it). Dry runs still answer. A place_structure or remove_structure run that places or removes nothing with a pipe (a locker, frame, wall, light) is neither checked (gas_check null) nor held (1.4.4+).",
            RemoveStructureSchema(),
            readOnly: false),
        Tool(
            "grid_survey",
            "Read the grid around a place before routing: the 2 m cells of a box (min and max, positions in metres) or of a room (room_id from rooms), a page at a time (limit default 27, max 125; offset); pieces and devices are those in the page's cells, so a device spanning cells of several pages (a locker) is listed on each. Each cell: at (its centre, odd metres), room_id (null outside), frame {reference_id, prefab_name, build_state, build_states, blocks_air, blocks_gravity}, walls [{face +x..-z, reference_id, prefab_name, blocks_air}] and small: 64 characters for its small-grid cells (0.5 m) at -1, -0.5, 0, +0.5 m from the centre on each axis (index 0 to 3; index 0 lies on the cell's minimum face plane, shared with the neighbour), character index x + 4y + 16z: '.' empty, 'c' cable, 'p' pipe, 'b' both, 'h' chute, 'd' device, 'o' another small-grid thing, 'r' a rocket's cell (legend repeats this); support: the same 64 cells by what holds a piece there up: 'i' inside a frame (every 2 m cell the small cell touches holds a frame, so a piece there is hidden in the frame's body), 'e' a frame edge or corner, 'f' on a frame's face (a frame's top face is the minimum plane of the cell above), 'w' on a wall's plane, 'a' air (plan_*_route frames_first avoids 'a'). Then pieces [{reference_id, kind cable|pipe|chute, prefab_name, at, cells (long pieces), ends (world axes), network_id, grade; chutes also flow {in, out} (which ends take items in and let them out, where a device port or directed piece fixes it) and carries (the item riding in it)}], devices [{reference_id, prefab_name, display_name, at, rotation {facing (its front: +x, -x, +y, -y, +z, -z), up, euler {x, y, z} (degrees)} in the forms place_structure takes, so it can be placed again as it stands or turned (facing reversed: 180 degrees; facing and up are null for a piece turned off the grid's axes) (1.3.5+), ports [{index, at (the cell a piece joining it stands in), toward (the end that piece needs, into the device), type (PowerAndData, Pipe, Chute...), role (Input, Output...), network_id}]}] and networks (include_networks, default true: cable networks with loads and lowest ratings, pipe networks with contents, pressure and gases, chute networks with chute_count, items riding and devices). network_visibility [{network_id, kind, pieces, cells {inside, frame_surface, wall, air}, air_at (floating cells, up to 32), refund}]: per network of the pieces listed, their cells by visibility and the floating ones. include_refund (default false): each piece's refund and each network's total, what remove_* would give back (read only; plan_removal adds would_split). Placement rules (CODE): frames and walls never block cables or pipes, so runs may pass through frame cells and along wall planes; a device, chute or 'o' blocks both; a pipe blocks a cable only along its own axis (their ends would meet), and the other way round; a chute is blocked by any cable, pipe, device, chute or 'o' in its cell. Read only. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    min = new { description = "Box corner, [x, y, z] or {x, y, z} in metres. With max." },
                    max = new { description = "The other corner. At most 20000 2 m cells." },
                    room_id = new { type = "string", description = "A room id from rooms, instead of a box." },
                    include_networks = new { type = "boolean", description = "Default true." },
                    include_refund = new { type = "boolean", description = "Default false: add each piece's removal refund and each network's total." },
                    limit = new { type = "integer", minimum = 1, maximum = 125, description = "2 m cells per page, default 27." },
                    offset = new { type = "integer", minimum = 0 }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "plan_cable_route",
            "Find a cable route on the small grid and dry-run it. from and to: {at: [x, y, z]} (a cell; a cable there is joined), {reference_id} of a cable (its cell; leaving through one of its open ends is free, another direction makes it a junction) or {reference_id, port} of a device (the cell a piece joining that port stands in; port may be left out when the device has one cable port); plan_pipe_route also takes {reference_id} of an in-line tank, passive vent or other pipe thing with its own ends that is neither a pipe piece nor a device (1.3.5+): the cell beyond its free end, the piece there getting an end toward it (port names the end when several are free; invalid_argument lists them). Or reroute: {reference_ids: the old run's pieces} or {between: [end, end]} (the shortest run between two devices or pieces on one network; an end is a reference id or {reference_id, port} of a device; a device on several networks of the kind, such as an APC's input and output, uses the one network both ends share, and when they share none or several the error (not_on_one_network, ambiguous_port) lists each end's ports and networks so a port can be named): the old run must meet the rest at exactly two cells; the new route runs between them with the old pieces removed in the same job. An end whose cell no piece may take (a device port whose joining cell another device fills) is not found: cell_blocked names it. The search (A*) never passes through a cell holding cable (it would join it), a device, chute or other thing, nor along a pipe's axis in a cell; frames and walls never block. Rules: frames_first (default true: a cell in air, on no frame and no wall plane, costs as much as 50 more supported cells, so a route over frames or along walls wins whenever the search box holds one, even a much longer one; only when none exists is a route through air returned, with the fewest air cells, and notes say through_air; false drops the rule); prefer none|frame_edges (small cells on two or three face planes of a frame cell they touch: its edges and corners, the top edges of a beam included)|walls (on or beside a wall's plane)|hidden (graded by visibility, a softer inside_frames that never gives no_route for it: a cell inside a frame costs 1, on a frame's surface or edge 3, on a wall's plane only 5, in air 9, plus frames_first's air penalty); inside_frames (only cells inside a frame cell or on its surface, judged over every 2 m cell a small cell touches as frames_first does: the top of a beam counts; a wall plane alone does not); avoid_room_interior (extra cost for room cells on no face plane); avoid_walkways (extra cost for room cells above the floor plane on no vertical face plane); avoid_networks (true: never beside another cable network than the ends' own; or a list of network ids, each a network handle; one naming no network of the kind is network_not_found, 1.4.4+); min_bends (turns cost much more); axis_order any|vertical_first|horizontal_first; max_length (cells, default 400); margin_m (search box around the ends, default 6, max 32). grade (default heavy), join, allow_bridge, allow_split and from_id pass on to the dry run. assume_removed: ids of things to plan as if already gone (their cells free, their links absent, never joined or avoided), e.g. old cable around a port that a refactor will remove: the cable pieces among them go into place_arguments.remove_ids, so the dry run and the job build and remove together (the guards see the final networks); other things (a pipe, a device) are only freed for the plan and go into place_arguments.assume_removed (remove them first with their own tool); route.assumed_removed lists pieces, others, missing (already gone) and in_the_way (pieces whose cells the route takes: they must go in the same job or before; with none in the way the route could also be built first and the old pieces removed later, by dropping remove_ids). Several starts (up to 16; from: {reference_id, ports: [..]} or an array) grow one tree: the first start to the target, each other start to the nearest cell of the tree so far (a branch joined by a junction; a start the tree already passes gets an extra end), never to the network a second time, so a device with separate power and data ports gets one run instead of two parallel runs and a loop. to may also be {network_id} (the nearest cell of any piece of that network) or a long straight (any of its cells; the place tool splits it in the same job, allow_split_long, default true). Bus mode: trunk {waypoints|cells} instead of to (e.g. a trunk planned with this tool, not built yet): the trunk is laid as given and every start (up to 16) branches to the nearest cell of the tree so far, junctions included, so a trunk and its drops are one job and one guard forecast; the trunk's own ends join what they meet (join). would_loop in the dry run means the route joins what is already joined another way: with join all the planner tries once more keeping away from the ends' own networks; when the start and target are already on one network it says so in notes. reserve_cells [[x, y, z], ...] and reserve_ports [{reference_id, port}] (1.3.5+): cells the search treats as blocked, a port's being the cell a piece joining it stands in (a device's port as grid_survey lists it, an in-line tank's end the cell beyond it), so one run cannot take the cell another port's run needs; a reserved cell that is one of this route's own ends is released, and notes say how many cells were kept free. Returns found, route {waypoints, length, bends, cost, expanded, removes, air_cells (new cells in air: 0 for a clean route), air (their positions, when any), branches [{waypoints, length, attach}] (when there are several starts), extra_ends (when any), visibility {inside, frame_surface, wall, air} (the new cells by how visible a piece in them is), assumed_removed, removal_refund (what the removed pieces give back)}, place_arguments (for place_cables: add dry_run false and confirm true to build) and dry_run (place_cables' own report with every guard), or failure (no_route, too_long, search_limit). Read only. Host only. No gateway is needed. 1.3.0+: when to names a network ({network_id}), a cable piece or a device port with a piece, place_arguments and the dry run get join_to = that handle, so the place tool checks the route really joins it (not_joined otherwise). join_to (a network handle) names the target in trunk mode; join_trunk: true adds a missing tap to it (tap_added); root passes on for would_split. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}. Doors and windows (1.4.3+): the search never enters a door's keep-out (its face, jambs, top edge and threshold, and a band either side, mod config [Layout] DoorKeepOutBand, default 0.5 m, inside the door's rectangle; the floor slab under a threshold is not in it; the door's own port cells and the route's own ends are released, with a note) unless allow_door_keepout: true; a door's face is no wall support; a cell on a window's face costs 6 more, and the dry run warns crosses_window where the route still crosses one.",
            RouteSchema("cable", ["normal", "heavy", "super_heavy"]),
            readOnly: true),
        Tool(
            "plan_pipe_route",
            "plan_cable_route for pipes: the same ends (and from or to {reference_id} of an in-line tank or passive vent: the cell beyond its free end, 1.3.5+), reserve_cells and reserve_ports, reroute, rules and search (never through a cell holding a pipe, nor along a cable's axis in a cell), dry-run with place_pipes and its pipe guards. grade is required (gas, liquid, insulated_gas, insulated_liquid). Read only. Host only. No gateway is needed. join_to, join_trunk, root and network handles as plan_cable_route (1.3.0+).",
            RouteSchema("pipe", ["gas", "liquid", "insulated_gas", "insulated_liquid"]),
            readOnly: true),
        Tool(
            "plan_chute_route",
            "plan_cable_route for chutes: from is the source and to the sink, since items travel from the route's first cell to its last. Ends: {reference_id, port} of a device's chute port (connections or grid_survey lists each port's index and role: an Output port or a chute bin is a from, an Input port a to, and the other way round is invalid_argument (1.4.4+); a device with one chute port needs no port), {reference_id} of a chute (as to, joining through one of its open ends is free and another direction makes it a junction, which only works where the route merges into a line; as from, the route leaves only through an open end items can leave by, never an Input end nor an open end the line's flow already sends items in by (a side run's open end upstream of its junction), and never through a junction, 1.4.4+; invalid_argument when it has none), or {at}. The same rules and search as plan_cable_route (never through a cell holding a chute, cable, pipe, device or other small-grid thing; frames and walls never block), reroute keeps the old run's direction where the flow fixes it, and the dry run is place_chutes' own with every flow guard. grade is chute (the default). Read only. Host only. No gateway is needed. join_to, join_trunk, root and network handles as plan_cable_route (1.3.0+).",
            RouteSchema("chute", ["chute"]),
            readOnly: true),
        Tool(
            "plan_removal",
            "Price a removal without doing it: the dry run of remove_cables, remove_pipes or remove_chutes (kind cable, the default, pipe or chute) under a read-only name. reference_ids, waypoints, cells (positions in metres) or network_id (every piece of that network, up to 1024). Returns the remove tools' own report: removals (each piece with its refund), materials.refund (the total remove_* would give back), would_split (networks that would fall apart, ports left joined to nothing), networks_before and networks_after, problems (cannot_remove...) and warnings. Changes nothing, ever. With no local player (a dedicated server) and no from_id, no_local_player is only a warning here (1.3.1+): the refund is priced, and a real remove run needs from_id (or refund false). Burnt cables are priced as remove_cables removes them (nothing back). Host only. No gateway is needed. would_split (1.3.0+) lists per resulting network its devices, the root (root, else every supplier on it) and cut_off (the devices cut off from it). root: the device to measure against. Network handles (1.3.0+): wherever a network id is taken (network_id, to.network_id, join_to, allow_bridge entries) a reference id of any piece or device on the network, or {reference_id, port} of a device port, works too and is resolved to the current id when the call runs (ids change after almost every edit); the reply lists each in resolved_networks [{argument, given, network_id}]. In allow_bridge a bare device id stays the device (its ports may share a network); name one of its networks with {reference_id, port}.",
            new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "cable", "pipe", "chute" }, description = "Default cable." },
                    reference_ids = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = "Pieces to price. Give one of reference_ids, waypoints, cells or network_id." },
                    waypoints = new { type = "array", minItems = 1, maxItems = 1024, items = new { description = "[x, y, z] or {x, y, z} in metres." }, description = "The pieces in every cell along these straight lines." },
                    cells = new { type = "array", minItems = 1, maxItems = 1024, items = new { description = "[x, y, z] or {x, y, z} in metres." }, description = "The piece in each cell." },
                    network_id = NetworkHandle("Every piece of this network: its id, a piece or device on it, or {reference_id, port}."),
                    root = new { type = "string", description = "The device the network is fed from, for would_split's cut_off. Default every supplier on it." },
                    allow_split = new { type = "boolean", description = "Report would_split as allowed. Default false." },
                    from_id = new { type = "string", description = "The thing that would take the refund. Default the local player." },
                    refund = new { type = "boolean", description = "Default true." },
                    limit = new { type = "integer", minimum = 1, maximum = 1024 }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "feed_paths",
            "Trace how a network feeds its devices from a root device (an APC's output, a generator): kind cable (default), pipe or chute; root (reference id); network_id or port (the root's port) when the root is on several networks of the kind (an APC's input and output). Breadth first over the network's links (the shortest path to each device; a device is an end, never passed through). Returns root, network_id, root_room_id, devices [{reference_id, prefab_name, display_name, room_id, pieces (between the root and the device), rooms (the rooms those pieces pass through in order; pieces in no room, e.g. inside a floor frame or outdoors, are skipped), through (rooms neither the root's nor the device's own), fed_through_other_rooms}], daisy_chains (how many devices are fed through another room), rooms [{room_id, devices, entries (the pieces where the feeds enter the room), entry_at, multiple_feeds}], multiple_feeds, unreached (devices on the network no run from the root reaches). A piece is in the room of a 2 m cell it touches (its own cell first); a device in the room of its grid cell (as rooms). Read only. No gateway is needed. network_id (1.3.0+) is a network handle: a piece or device reference id or {reference_id, port} work too.",
            new
            {
                type = "object",
                properties = new
                {
                    root = new { type = "string", description = "The device the network is fed from." },
                    network_id = NetworkHandle("The network to trace, when the root is on several: its id, a piece on it, or {reference_id, port}."),
                    port = new { type = "integer", minimum = 0, maximum = 64, description = "Or the root's port whose network to trace (connections lists them)." },
                    kind = new { type = "string", @enum = new[] { "cable", "pipe", "chute" }, description = "Default cable." }
                },
                required = new[] { "root" },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "trader_buy",
            "Buy from the trader that has landed and is ready at the landing pad (any other contact is refused with not_landed, naming the landed one), exactly as the trade window's Buy button does on the host (TradeDataHelper.BuyItem): the card is charged, the trader's stock drops, and the goods are made straight into the first empty tradable slots of the vending machines on the pad's data network, then of the card holder's inventory, stacked up to their maximum (gas goes into the pad network's atmosphere). The price per unit is the trader's, divided by the respawn stress penalty while you have respawn stress, as the window shows it. Returns {reference_id (the contact), name, dry_run, credit_card_id, credits_before, credits_after, results, count, success_count, error_count, delivered, gas_atmosphere_id}: results has one entry per line, {index, ok, name, prefab_name, gas, quantity, credits_each, credits_spent, stock_after}, or {index, ok: false, name, prefab_name, quantity (bought before the game stopped), credits (paid for those), error {code, message}}; delivered lists each new item where it landed, {reference_id (vending machine or player), slot, item {reference_id, prefab_name, display_name}, quantity}. A dry run checks each line on its own against the stock, the card and free slots; it does not add lines up. credits_after: the card's balance after the trade; in a dry run (1.4.4+) the predicted balance, credits_before less the credits_spent of every ok line (since buy lines are checked on their own, it falls below 0 when they do not add up, and the real run then refuses a later line insufficient_credits). A refused line's quantity is how many were bought (0 when refused up front, in a dry run always), not the quantity asked; the index names the line asked. Credits (credits_before, credits_after, credits_spent, credits) are rounded to the cent. The game keeps no trader currency. Refusals for the whole trade: not_host, contact_not_found, not_landed (not landed or not ready at a pad), pad_unavailable (pad off, which is unpowered: the pad is on only while its data and power connection has power; or in error), card_not_found, no_credit_card. Per line: not_sold, ambiguous_item, insufficient_stock, insufficient_credits, no_room, trade_failed (the game's own message, e.g. an incomplete trade when the slots ran out: it charges only for what it delivered). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "The trader contact (trader_contacts / trader_inventory reference_id)." },
                    credit_card_id = new { type = "string", description = "The credit card; default the one the local player carries, as the trade window uses." },
                    items = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 32,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                name = new { type = "string", description = "The entry's name as trader_inventory gives it; matched first." },
                                prefab_name = new { type = "string", description = "Breaks a tie between entries of the same name, or matches alone when no name is given." },
                                quantity = new { type = "integer", minimum = 1 }
                            },
                            required = new[] { "quantity" },
                            additionalProperties = false
                        },
                        description = "Up to 32 lines, each name and/or prefab_name, and quantity; traded in order. An entry still ambiguous after both is refused (ambiguous_item), never picked."
                    },
                    dry_run = new { type = "boolean", description = "Check and price only; nothing is traded. Default false." }
                },
                required = new[] { "reference_id", "items" },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "trader_sell",
            "Sell to the trader that has landed and is ready at the landing pad (any other contact is refused with not_landed), exactly as the trade window's Sell button does on the host (TradeDataHelper.SellItem): the trader must still want the item, the goods are taken from the vending machines on the pad's data network and then the card holder's inventory (whole stacks destroyed, the last one trimmed; gas taken from the pad network's atmosphere), and the card is paid. The card must be carried by a player or held by a vending machine: the game's sell reads the card holder's inventory and fails on any other holder. The price per unit is the trader's, times the respawn stress penalty while you have respawn stress. Lines add up: each line is checked against what the trader still wants and what is available after the lines before it in the same call (goods unit by unit, gas in moles), in a dry run and a real run alike, and the lines of one trader entry are then sold together in one game call (the game removes sold stacks and gas only at the end of the frame, so separate calls in one frame would sell the same goods twice); each line still gets its own result, credits_earned its quantity times credits_each. Returns {reference_id (the contact), name, dry_run, credit_card_id, credits_before, credits_after, results, count, success_count, error_count, delivered, gas_atmosphere_id}: results has one entry per line, {index, ok, name, prefab_name, gas, quantity, credits_each, credits_earned, wanted_after}, or the same refused shape as trader_buy (quantity is how many were sold: 0 when refused, in a dry run always). credits_after: the card's balance after the sale; in a dry run (1.4.4+) the predicted balance, credits_before plus the credits_earned of every ok line. Credits are rounded to the cent. delivered is always empty. Refusals: as trader_buy, and card_not_usable for the whole trade (the card lies loose or is in anything but a player or a vending machine); per line not_wanted (not bought, or more than the trader still wants after the earlier lines), insufficient_available (not enough that meet the trader's conditions on the pad's network and with the card holder, or gas in the pad network's atmosphere, after the earlier lines), sell_separately (real run only: an earlier line of another entry sold goods this line's entry also accepts, and the game would take them again before they leave their slots; sell it in a call of its own), trade_failed (nothing of that entry was sold). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "The trader contact (trader_contacts / trader_inventory reference_id)." },
                    credit_card_id = new { type = "string", description = "The credit card; default the one the local player carries, as the trade window uses." },
                    items = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 32,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                name = new { type = "string", description = "The entry's name as trader_inventory gives it; matched first." },
                                prefab_name = new { type = "string", description = "Breaks a tie between entries of the same name, or matches alone when no name is given." },
                                quantity = new { type = "integer", minimum = 1 }
                            },
                            required = new[] { "quantity" },
                            additionalProperties = false
                        },
                        description = "Up to 32 lines, each name and/or prefab_name, and quantity; traded in order. An entry still ambiguous after both is refused (ambiguous_item), never picked."
                    },
                    dry_run = new { type = "boolean", description = "Check and price only; nothing is traded. Default false." }
                },
                required = new[] { "reference_id", "items" },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "paste_blueprint",
            "Paste a BlueprintMod blueprint at an exact place and turn, with no player needed (BlueprintMod spawns every piece without the game's placement checks, so a paste can leave a piece no player could place there again, e.g. a vent with nothing behind it: run check_replaceable over the pasted pieces, or lint_layout for not_replaceable) (console bppaste takes both from the local player, so it cannot run on a dedicated server). Needs BlueprintMod loaded; host only. Makes the D.B.P.U.'s own call: the blueprint's copy angle plus rotation 0, 90, 180 or 270, so every piece lands on the grid. Three forms. Paste: name (a file in BlueprintMod's Blueprints folder, or an absolute path, either with or without .blueprint; an absolute path with another extension is taken as given), anchor [x, y, z] (the world position in metres where the blueprint's own reference point lands: the large-grid point BlueprintMod snapped the copying player to, x and z odd whole metres, y even; a paste lines up with the grid when the anchor is such a point) and rotation (default 0); returns {started: true, file (full path), entries, anchor, rotation, copy_y_angle, expected_duration_s}. The pieces are then placed over 2 to 30 s (0.15 s per entry) by BlueprintMod's coroutine, so the reply comes before they exist; the coroutine runs only while the game does, so in a paused world the paste waits. status: true alone: progress of the last paste this tool started, {known (false until this tool has started one; the other fields are then null), active (still placing), complete, cancelled, created, failed, skipped, pasted (things kept for undo), standing (of those, the ones that still exist: created and pasted are BlueprintMod's counts, which include a piece the game then refused to register, e.g. its spot taken, 'Grid face may be open' in the log), fingerprint, file, entries, other_active (a paste this tool did not start is running)}; the counts are kept after the paste ends, and are null when the paste finished inside the call that started it. undo: true alone: BlueprintMod's bpundo (cancels a running paste and removes what it placed, else removes the last completed paste) and returns {message}, its own answer. BlueprintMod keeps one undo stack for every paste in the world (bppaste, the D.B.P.U., every caller of this tool), so undo removes the newest paste of anyone, which need not be the paste status reports; in a shared world check status and what stands before undoing. Rooms are not re-evaluated after a paste or an undo: run the console command regeneraterooms before rooms. In survival BlueprintMod charges DeanamicMatter from the local player, which a dedicated server does not have, so use a creative world there. Errors: invalid_argument (a missing file names the path it looked at; a blueprint with no entries; a rotation not 0, 90, 180 or 270), paste_refused (BlueprintMod's own message verbatim: the same blueprint already pasted at this position and rotation, no local player in survival, not enough DeanamicMatter; 1.4.4+ also while a paste, anyone's, is still placing: nothing is started, wait until status is no longer active), blueprint_failed (a BlueprintMod call threw: its exception type and message, e.g. a file that is not a blueprint), mod_missing (BlueprintMod is not loaded), game_changed (BlueprintMod no longer has a member this tool calls, named), not_host. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Blueprint file: a name in BlueprintMod's Blueprints folder (.blueprint optional) or an absolute path." },
                    anchor = new { type = "array", minItems = 3, maxItems = 3, items = new { type = "number" }, description = "World position [x, y, z] in metres of the blueprint's large-grid anchor." },
                    rotation = new { type = "integer", @enum = new[] { 0, 90, 180, 270 }, description = "Degrees added to the blueprint's copy angle. Default 0." },
                    status = new { type = "boolean", description = "true alone: progress of the last paste this tool started." },
                    undo = new { type = "boolean", description = "true alone: undo the last paste (BlueprintMod's bpundo)." }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "vault_contents",
            "What each Ingot Vault (IngotVault Workshop mod 3749011679) stores, read from the vault's own store, exact to 1e-6 rather than the screen's one decimal: ingots as grams of reagent (kind ingot, reagent = the stored reagent, prefab_name = the ingot a vend makes), ores and ices as counts (kind ore). Returns {vaults: [{vault {reference_id, prefab_name, display_name}, position, on_off, powered, stock: [{kind, prefab_name, display_name, reagent, max_stack, quantity}], pending_vends (vends taken off the store and not yet made into the export slot)}], remote_vaults: [{remote, vault_id (the one vault on its data network, or null), connection (None, NoVaultDetected, MultipleVaultsDetected, VaultPoweredOff)}], count}. vault_id: one vault (a Remote Vault id reads the vault it reaches). find_items and item_totals list a vault's ingots only as machine stock of kind processing and do not see its ores; use this. Refusals: ingot_vault_mod_required (IngotVault not loaded), ingot_vault_changed (IngotVault no longer has a member these tools use, named), not_a_vault, vault_not_connected, thing_not_found. Read only. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    vault_id = new { type = "string", description = "One Ingot Vault or Remote Vault; default every vault." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "vault_deposit",
            "Put ingots, ores and ices straight into an Ingot Vault's store, as a move: taken from wherever they are (a player's inventory at any depth, a container, the ground) and added to the store with the vault's own import bookkeeping (IngotVault CollectResource: an ingot adds its reagents times its grams to the vault's reagent store, an ore or ice adds its count to the vault's ore store), then the item is destroyed as the import destroys it, or, for part of a stack, only that part is taken off it. No import slot, chute or door is used, so nothing waits in a queue or is ejected; the vault's totals, power use, screen, save and vault_contents agree at once. The vault's rule decides what it takes: ingots and ores (ices, slag and organics are ores) whose slot class fits its import slot; anything else is refused (not_vault_material). Three forms: items [{reference_id, quantity (optional: part of a stack; whole numbers for ores, grams for ingots)}] (up to 256), reference_ids [...] (whole items), or a filter over every item in the world: prefab_contains, name_contains, location (any, ground, player, stored), within_id (inside that holder at any depth, e.g. a backpack or the player), near_player_m, kind (any, ingot, ore (not ice), ice), limit (default 256, max 1000; nearest the player first); items the vault does not take are left out and counted as skipped. vault_id may be a Remote Vault: it deposits into the one vault on its data network. Dry run by default; dry_run false and confirm true to do it. Returns {dry_run, vault, via (the Remote Vault used, or null), matched (filter form: items the filter named), skipped, truncated, items: {results: [{index, ok, reference_id, prefab_name, display_name, kind, from {id, slot} (null on the ground), quantity, left_in_source}] or {index, ok: false, reference_id, prefab_name, error {code, message}}, count, success_count, error_count}, stock: [{kind, prefab_name, display_name, reagent, before, change, after}] (a real run reads after back from the vault)}. Per-item refusals: thing_not_found, not_vault_material, no_reagents (an ingot without reagents: the vault would destroy it and store nothing), in_vault_slot (in a vault's own import or export slot; let the vault finish), slot_locked, invalid_argument (more than the stack holds, a fraction of an ore, an item named twice). Whole-request refusals: thing_not_found (the filter's within_id names nothing), vault_unpowered (the vault is off or unpowered: it neither imports nor vends then), confirm_required, ingot_vault_mod_required, ingot_vault_changed, not_a_vault, vault_not_connected, not_host. Host only. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    vault_id = new { type = "string", description = "The Ingot Vault, or a Remote Vault linked to one." },
                    items = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 256,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                reference_id = new { type = "string" },
                                quantity = new { type = "number", exclusiveMinimum = 0, description = "Part of the stack: a whole count for ores, grams for ingots. Default all of it." }
                            },
                            required = new[] { "reference_id" },
                            additionalProperties = false
                        },
                        description = "Items by id, each whole or in part."
                    },
                    reference_ids = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "string" }, description = "Whole items by id." },
                    prefab_contains = new { type = "string", description = "Filter form: prefab name contains this (ignoring case)." },
                    name_contains = new { type = "string", description = "Filter form: display name contains this (ignoring case)." },
                    location = new { type = "string", @enum = new[] { "any", "ground", "player", "stored" }, description = "Filter form: where the item is. Default any." },
                    within_id = new { type = "string", description = "Filter form: inside this holder at any depth (a backpack, a locker, a player)." },
                    near_player_m = new { type = "number", exclusiveMinimum = 0, description = "Filter form: within this many metres of the local player." },
                    kind = new { type = "string", @enum = new[] { "any", "ingot", "ore", "ice" }, description = "Filter form: ingot, ore (not ice), ice, or any the vault takes. Default any." },
                    limit = new { type = "integer", minimum = 1, maximum = 1000, description = "Filter form: at most this many items, nearest first. Default 256." },
                    dry_run = new { type = "boolean", description = "Default true: check and report only." },
                    confirm = new { type = "boolean", description = "true with dry_run false to deposit." }
                },
                required = new[] { "vault_id" },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "vault_withdraw",
            "Take an amount of one stored thing out of an Ingot Vault's store and make it straight into a holder's slots, as a move: the store goes down exactly as the vault's own vend takes it (an ingot's reagent set to what is left; an ore entry removed when 0.01 or less is left) and the same amount is made as items (the ingot the vault vends for that reagent, with its grams as quantity, or the ore's stack), never more per item than a full stack. No vend queue, export slot or door is used, so nothing lands in front of the vault. Name what to take with one of prefab_name (e.g. ItemIronIngot, ItemIronOre), prefab_hash, or reagent (ingots: Iron, Steel, ...); quantity (grams of ingot, a whole count of ore) must be at most what the vault holds. to_id: the holder (default the local player); to_slot: a slot index, or \"auto\" (default): on a player, first onto matching stacks anywhere in the inventory (belts, backpack, suit storage, a stack in a hand), then new stacks into empty slots that take the item (never into the player's own body slots unless named by index); on anything else its own slots; never a hidden slot (not interactable, e.g. a cable coil's; refused with slot_refuses when named). What does not fit is refused (no_room, nothing changed) unless allow_ground is true, which puts the rest on the ground a metre in front of the holder. vault_id may be a Remote Vault. Dry run by default; dry_run false and confirm true to do it. Returns {dry_run, vault, via, to, quantity, placed: [{where (merged, slot or ground), slot {id, slot} (null on the ground), quantity, reference_id (the stack; null in a dry run for a new one)}], stock: [{kind, prefab_name, display_name, reagent, before, change, after}] (a real run reads after back from the vault)}. If a game call fails part way, what was not made goes back into the store. Refusals: not_in_vault (lists what the vault holds), not_enough_stock, invalid_argument (a fraction of an ore, not exactly one of prefab_name/prefab_hash/reagent), prefab_missing, no_room, slot_not_found, slot_locked, slot_occupied, slot_refuses, no_slots, invalid_destination (a vault as the holder), vault_unpowered, confirm_required, ingot_vault_mod_required, ingot_vault_changed, not_a_vault, vault_not_connected, no_local_player, not_host. Host only. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    vault_id = new { type = "string", description = "The Ingot Vault, or a Remote Vault linked to one." },
                    prefab_name = new { type = "string", description = "The item to take, as vault_contents names it (e.g. ItemIronIngot, ItemIronOre)." },
                    prefab_hash = new { type = "integer", description = "The item to take, by prefab hash." },
                    reagent = new { type = "string", description = "Ingots: the stored reagent (e.g. Iron, Steel)." },
                    quantity = new { type = "number", exclusiveMinimum = 0, description = "Grams of ingot, or a whole count of ore." },
                    to_id = new { type = "string", description = "The holder that gets the items; default the local player." },
                    to_slot = new { oneOf = new object[] { new { type = "integer", minimum = 0 }, new { type = "string", @enum = new[] { "auto" } } }, description = "A slot index of to_id, or \"auto\" (default)." },
                    allow_ground = new { type = "boolean", description = "Put what does not fit on the ground in front of the holder. Default false: refused instead." },
                    dry_run = new { type = "boolean", description = "Default true: check and report only." },
                    confirm = new { type = "boolean", description = "true with dry_run false to withdraw." }
                },
                required = new[] { "vault_id", "quantity" },
                additionalProperties = false
            },
            readOnly: false)
    ];

    // A network named so the name survives edits: a network id, a piece or device reference id, or a device port.
    private static object NetworkHandle(string description) => new
    {
        oneOf = new object[]
        {
            new { type = "string" },
            new
            {
                type = "object",
                properties = new { reference_id = new { type = "string" }, port = new { type = "integer", minimum = 0, maximum = 64 } },
                required = new[] { "reference_id" },
                additionalProperties = false
            }
        },
        description
    };

    private static object CleanSchema(string kind)
    {
        return new
        {
            type = "object",
            properties = new
            {
                network_id = NetworkHandle($"A {kind} network, every piece of it: its id, a piece or device on it, or {{reference_id, port}}. Give this or reference_ids."),
                reference_ids = new { type = "array", minItems = 1, maxItems = 4096, items = new { type = "string" }, description = $"{kind} pieces to clean up, by reference id. Give this or network_id." },
                keep_ids = new { type = "array", minItems = 1, maxItems = 4096, items = new { type = "string" }, description = "remove_loops: pieces whose loops are spared whole (a loop's pieces are listed in a dry run); remove_redundant: pieces never removed (e.g. a run's created_ids)." },
                only_ids = new { type = "array", minItems = 1, maxItems = 4096, items = new { type = "string" }, description = "remove_redundant only: the only pieces that may go." },
                older_than_id = new { type = "string", description = "remove_redundant only: only pieces with a lower reference id (built before this one) may go; any reference id works as the threshold, standing or not." },
                root = new { type = "string", description = "remove_redundant only: the device the network is fed from. Default every supplier on it." },
                wait = new { type = "boolean", description = "When another job runs: queue this run behind it (status queued, poll its job_id) instead of answering busy. Default false." },
                operations = new { type = "array", minItems = 1, maxItems = 6, items = new { type = "string", @enum = new[] { "remove_dead_ends", "remove_loops", "remove_redundant", "split_long_straights", "merge_straights", "simplify_junctions" } }, description = "What to do, alone or together (not split_long_straights with merge_straights). Default [\"simplify_junctions\"]." },
                dry_run = new { type = "boolean", description = "Default true: report only, change nothing." },
                confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." },
                from_id = new { type = "string", description = "The thing whose inventory gives the coils or kits needed and takes the refund. Default the local player." },
                skip_unmatched = new { type = "boolean", description = "Leave pieces without a replacement as they are instead of refusing. Default false." },
                refund = new { type = "boolean", description = "Give back coils or kits where the new pieces cost less. Default true." },
                limit = new { type = "integer", minimum = 1, maximum = 4096, description = "Pieces listed per list in the report, default 200 (all are counted and checked)." },
                job_id = new { type = "string", description = "Poll a confirmed run; give nothing else." }
            },
            additionalProperties = false
        };
    }

    private static object ReplaceSchema(string kind, bool targetRequired)
    {
        return new
        {
            type = "object",
            properties = new
            {
                reference_ids = new { type = "array", minItems = 1, maxItems = 4096, items = new { type = "string" }, description = $"The {kind}s to replace, by reference id. Give this or room_id." },
                room_id = new { type = "string", description = $"A room id from rooms: every {kind} around that room. Give this or reference_ids." },
                to = new { type = "string", description = targetRequired ? $"Required, except when polling with job_id: the prefab name of the new {kind} (e.g. StructureCompositeWall)." : $"The prefab name of the new {kind}. Default each {kind}'s own prefab, which finishes unfinished {kind}s." },
                from_prefabs = new { type = "array", minItems = 1, maxItems = 64, items = new { type = "string" }, description = "Only pieces of these prefab names are replaced; the others are kept as not_selected." },
                wait = new { type = "boolean", description = "When another job runs: queue this run behind it (status queued, poll its job_id) instead of answering busy. Default false." },
                dry_run = new { type = "boolean", description = "Default true: report only, change nothing." },
                confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." },
                from_id = new { type = "string", description = "The thing whose inventory gives the materials and takes the refund. Default the local player." },
                skip_unmatched = new { type = "boolean", description = "Leave pieces that cannot be swapped (footprint_mismatch, invalid_target) as they are instead of refusing. Default false." },
                refund = new { type = "boolean", description = "Give back what the old pieces return beyond what the new ones cost. Default true." },
                limit = new { type = "integer", minimum = 1, maximum = 4096, description = "Pieces listed per list in the report, default 200 (all are counted and checked)." },
                job_id = new { type = "string", description = "Poll a confirmed run; give nothing else." }
            },
            additionalProperties = false
        };
    }

    private static object PlaceSchema(string kind, string[] grades, string? defaultGrade)
    {
        object position = new { description = "[x, y, z] or {x, y, z} in metres; snapped to the small cell it falls in." };
        string[] axes = ["+x", "-x", "+y", "-y", "+z", "-z"];
        return new
        {
            type = "object",
            properties = new
            {
                waypoints = new { type = "array", minItems = 1, maxItems = 1024, items = position, description = "Points joined by straight axis-aligned lines, both ends included. Give one of waypoints, cells, piece or pieces." },
                cells = new { type = "array", minItems = 1, maxItems = 1024, items = position, description = "Every cell of the run, each a neighbour of the one before." },
                piece = new { type = "object", properties = new { at = position, ends = new { type = "array", minItems = 1, maxItems = 6, items = new { type = "string", @enum = axes } } }, required = new[] { "at", "ends" }, description = "One piece at a cell with these ends (a neighbour without an end back becomes a junction)." },
                pieces = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "object", properties = new { at = position, ends = new { type = "array", minItems = 1, maxItems = 6, items = new { type = "string", @enum = axes } } }, required = new[] { "at", "ends" } }, description = "1.4.4+: separate pieces in one job, each as piece lays it (a neighbour without an end back becomes a junction)." },
                grade = new { type = "string", @enum = grades, description = defaultGrade != null ? $"Default {defaultGrade}." : "Required, except when polling with job_id." },
                join = new { type = "string", @enum = new[] { "ends", "none", "all" }, description = $"How the run joins existing {kind} ends and device ports. Default ends." },
                branches = new { type = "array", maxItems = 16, items = new { type = "object", properties = new { waypoints = new { type = "array", items = position }, cells = new { type = "array", items = position }, attach = position } }, description = "Side runs of a tree: each {waypoints or cells, attach}: its first cell is a free end (joins ports and ends around it as a run end does), its last cell joins the cell attach names (a cell of the run or an earlier branch next to it; default the first found), which becomes a junction. plan_*_route with several starts fills this in." },
                allow_door_keepout = new { type = "boolean", description = "Default false: a new piece in a door's keep-out (its face and the configured band, 0.5 m by default, either side inside its rectangle; its own port cells released) is refused with in_door_keepout. true makes it a warning." },
                allow_split_long = new { type = "boolean", description = "Default true: a long straight (3, 5 or 10 cells) the run must join in its middle or cross is split into singles in the same job (same line, grade, colour and owner; removal refund and single costs as split_long_straights) and then joined; warning long_split. false refuses with long_piece." },
                extra_ends = new { type = "array", maxItems = 64, items = new { type = "object", properties = new { at = position, toward = new { type = "string", @enum = axes } }, required = new[] { "at", "toward" } }, description = "Extra ends on run cells." },
                remove_ids = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = $"{kind} pieces removed in the same job before building (a reroute)." },
                assume_removed = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = "Things checked as if already gone (their cells free, their links absent) but not removed; a real run is refused while any still stands (assumed_present)." },
                allow_bridge = new { type = "array", minItems = 1, maxItems = 64, items = NetworkHandle("A network (its id, or {reference_id, port} of a device on it) or a device id."), description = "Network ids (every network of a merge) and device ids whose joining is meant." },
                root = new { type = "string", description = "The device would_split measures cut-off devices against. Default every supplier on the network." },
                join_to = NetworkHandle($"The {kind} network the run must end up on (not_joined when it does not): its id, a piece or device on it, or {{reference_id, port}}."),
                join_trunk = new { type = "boolean", description = "With join_to: add the missing tap (a run end next to, or one free cell short of, a piece of join_to) instead of only warning. Default false." },
                wait = new { type = "boolean", description = "When another job runs: queue this run behind it (status queued, poll its job_id) instead of answering busy. Default false." },
                allow_split = new { type = "boolean", description = "Let removals split networks or leave ports joined to nothing. Default false." },
                dry_run = new { type = "boolean", description = "Default true: report only, change nothing." },
                confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." },
                from_id = new { type = "string", description = "The thing whose inventory gives the coils or kits and takes the refund. Default the local player." },
                refund = new { type = "boolean", description = "Give back what removed pieces and cheaper changes return. Default true." },
                limit = new { type = "integer", minimum = 1, maximum = 1024, description = "Cells listed, default 200." },
                job_id = new { type = "string", description = "Poll a confirmed run; give nothing else." }
            },
            additionalProperties = false
        };
    }

    private static object RemoveSchema(string kind)
    {
        object position = new { description = "[x, y, z] or {x, y, z} in metres." };
        return new
        {
            type = "object",
            properties = new
            {
                reference_ids = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = $"{kind} pieces to remove. Give one of reference_ids, waypoints or cells." },
                waypoints = new { type = "array", minItems = 1, maxItems = 1024, items = position, description = $"Remove the {kind} in every cell along these straight lines." },
                cells = new { type = "array", minItems = 1, maxItems = 1024, items = position, description = $"Remove the {kind} in each cell." },
                allow_split = new { type = "boolean", description = "Let the removal split networks or leave ports joined to nothing. Default false." },
                root = new { type = "string", description = "The device would_split measures cut-off devices against. Default every supplier on the network." },
                wait = new { type = "boolean", description = "When another job runs: queue this run behind it (status queued, poll its job_id) instead of answering busy. Default false." },
                dry_run = new { type = "boolean", description = "Default true: report only, change nothing." },
                confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." },
                from_id = new { type = "string", description = "The thing that takes the refund. Default the local player." },
                refund = new { type = "boolean", description = "Default true." },
                limit = new { type = "integer", minimum = 1, maximum = 1024 },
                job_id = new { type = "string", description = "Poll a confirmed run; give nothing else." }
            },
            additionalProperties = false
        };
    }

    private static object PlaceStructureSchema()
    {
        object position = new { description = "[x, y, z] or {x, y, z} in metres: a point in the cell; snapped as the cursor snaps it. 1.4.3+ also: {crosshair: true} (where the look ray hits, within 10 m; right_m/up_m/forward_m move it in the player frame), {relative_to: \"player\" | \"crosshair\" | a reference id | {reference_id}, frame: player (level: right and forward are the world axes nearest the player's own; ambiguous_axis within 10 degrees of a diagonal) | world | target (the thing's own turn; default for a thing), right_m, up_m, forward_m, from: origin | top | bottom | left | right | front | back (the middle of that side of the thing's footprint box, read in the frame)}, or {on_face_i_look_at: true, along_right_m, along_up_m} (on the 2 m face plane the look ray hits, right and up as you see them; on a floor, up is away from you). resolved in the reply says where it landed and how. The player frame (the default for relative_to player or crosshair), crosshair and on_face_i_look_at need a player camera: no_camera on a dedicated server (use frame world or target). A bad from or frame is invalid_argument." };
        string[] axes = ["+x", "-x", "+y", "-y", "+z", "-z"];
        string[] facings = ["+x", "-x", "+y", "-y", "+z", "-z", "toward_player", "away_from_player", "out_of_face", "into_room"];
        object prefab = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } }, description = "Prefab name (e.g. StructureWallLight, as find_things and looking_at report them) or prefab hash." };
        object rotation = new { type = "array", minItems = 3, maxItems = 3, items = new { type = "number" }, description = "[x, y, z] degrees, each a multiple of 90, Quaternion.Euler order (z, then x, then y). Give at most one of rotation, facing and face." };
        object facing = new { type = "string", @enum = facings, description = "Where the piece's front points: an axis, or (1.4.3+) toward_player / away_from_player (the level axis toward you; ambiguous_axis near a diagonal), out_of_face (the outward normal of the face you look at), into_room (the one level side of at whose next 2 m cell is in a room and the other not)." };
        object aboveFloor = new { type = "number", minimum = 0, maximum = 20, description = "1.4.3+: its bottom (the bottom of its mesh box, what stands on the floor) this many metres above the floor below at (the first plane down, within 10 m, with a floor plate or a frame under it; 1.4.4+: none there is cannot_place, not a floor assumed). The cursor snaps to 0.5 m: the snaps below and above are tried too and the one whose bottom lands nearest the height asked is kept; resolved.at_how says where it ended up." };
        object up = new { type = "string", @enum = axes, description = "Where its top points, with facing or face; default +y, or +z when facing is vertical." };
        object face = new { type = "string", @enum = axes, description = "For pieces placed on a cell face (walls): the face of the cell holding at that it sits on; it faces into the cell." };
        object buildState = new { oneOf = new object[] { new { type = "string", @enum = new[] { "finished", "first" } }, new { type = "integer", minimum = 0 } }, description = "finished (default), first (as a kit leaves it) or a state index." };
        object label = new { type = "string", description = "A name, as the Labeller writes it (devices, signs, tanks...)." };
        object color = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } }, description = "A colour name or index (paint lists them)." };
        object target = new { description = "An axis (+x .. -z), \"room\" (into the room), \"player\", a point [x, y, z] or {reference_id} (that thing's position)." };
        object orient = new
        {
            type = "object",
            description = "1.4.3+: choose the turn by intent instead of rotation/facing/face/up. Every turn the cursor allows is aimed and checked as a plain placement would be, scored (a turn the cursor refuses, or not resting on mount, is excluded; not upright +20; each target missed up to +10 by angle; the layout preview's conflicts: problem 100, warning 10, info 1) and the best used; orient in the reply echoes chosen {facing, up, euler, score, reasons}, alternatives (next 3) and tried. A turbo volume pump is also scored with its flow reversed; when that wins, mode_flip says to write Mode 1 after building.",
            properties = new
            {
                mount = new { type = "string", description = "wall, floor, ceiling, or an axis: the side the surface it rests on is on (its back for a mounted piece, its bottom for a standing one)." },
                upright = new { type = "boolean", description = "Default true: its visual top (describe_prefab visual_up) should point +y." },
                controls_toward = target,
                ports = new { type = "array", maxItems = 8, items = new { type = "object", properties = new { role = new { type = "string", description = "Input, Input2, Output, Output2, Waste, None." }, index = new { type = "integer", minimum = 0 }, type = new { type = "string", description = "Pipe, PowerAndData, Chute... (substring)." }, toward = target }, required = new[] { "toward" } }, description = "Ports to point: the way a run leaves each should head toward its target." },
                flow = new { type = "object", properties = new { from = target, to = target }, description = "Inputs should face from, outputs to." }
            },
            additionalProperties = false
        };
        return new
        {
            type = "object",
            properties = new
            {
                placements = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 64,
                    items = new
                    {
                        type = "object",
                        properties = new { prefab, at = position, rotation, facing, up, face, orient, above_floor_m = aboveFloor, build_state = buildState, label, color },
                        required = new[] { "prefab", "at" },
                        additionalProperties = false
                    },
                    description = "The placements, built in this order in one job. Or give one placement's fields at the top level."
                },
                prefab,
                at = position,
                rotation,
                facing,
                up,
                face,
                orient,
                above_floor_m = aboveFloor,
                build_state = buildState,
                label,
                color,
                from_id = new { type = "string", description = "The thing whose inventory pays. Default the local player." },

                free = new { type = "boolean", description = "Place without materials; creative worlds only (not_creative otherwise). Default false." },
                allow_door_keepout = new { type = "boolean", description = "Default false: a small-grid piece with a cell in a door's keep-out (its face and the configured band either side inside its rectangle) is refused with in_door_keepout; true makes it a warning." },
                wait = new { type = "boolean", description = "When another job runs: queue this run behind it (status queued, poll its job_id) instead of answering busy. Default false." },
                dry_run = new { type = "boolean", description = "Default true: report only, change nothing." },
                confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." },
                job_id = new { type = "string", description = "Poll a confirmed run; give nothing else." }
            },
            additionalProperties = false
        };
    }

    private static object RemoveStructureSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                reference_ids = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "string" }, description = "The structures to remove." },
                allow_contents = new { type = "boolean", description = "Remove pieces holding items or gas: items drop where it stood, a tank lets its gas out, other devices lose it. Default false." },
                allow_breach = new { type = "boolean", description = "Remove an airtight piece whose removal joins spaces whose pressures differ by 1 kPa or more. Default false." },
                allow_broken = new { type = "boolean", description = "Remove broken pieces (is_broken: the game's broken state, e.g. burnt-out vents; a burst pipe; a burnt cable), as the game deconstructs a broken thing: nothing is given back. Every other guard still applies. Default false." },
                refund_to = new { type = "string", @enum = new[] { "source", "ground", "none" }, description = "Default source (from_id or the local player)." },
                wait = new { type = "boolean", description = "When another job runs: queue this run behind it (status queued, poll its job_id) instead of answering busy. Default false." },
                from_id = new { type = "string", description = "Who takes the refund with refund_to source. Default the local player." },
                dry_run = new { type = "boolean", description = "Default true: report only, change nothing." },
                confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." },
                job_id = new { type = "string", description = "Poll a confirmed run; give nothing else." }
            },
            additionalProperties = false
        };
    }

    private static object RouteSchema(string kind, string[] grades)
    {
        object end = new { type = "object", description = $"{{at: [x, y, z]}}, {{reference_id}} of a {kind} piece, or {{reference_id, port}} of a device or of a {kind} thing with its own ends (an in-line tank, a passive vent: the cell beyond its free end)." };
        object from = new { description = $"A start: {{at}}, {{reference_id}} of a {kind} piece, {{reference_id, port}} of a device, or {{reference_id}} of an in-line tank or passive vent (its free end; port when several are free); or several starts (up to 16) as one tree: {{reference_id, ports: [..]}} (several ports of one device) or an array of starts. The first start routes to the target; each other start routes to the nearest cell of the tree so far and joins it with a junction, so a device with separate ports gets one run, never a loop." };
        object to = new { type = "object", description = $"{{at}}, {{reference_id}} of a {kind} piece (a long straight: any of its cells, split in the same job), {{reference_id, port}} of a device, {{reference_id}} of an in-line tank or passive vent (its free end), or {{network_id}}: the nearest cell of any piece of that network (network_id may be a piece or device id or {{reference_id, port}})." };
        return new
        {
            type = "object",
            properties = new
            {
                from,
                to,
                reroute = new { type = "object", description = "{reference_ids: [...]} or {between: [end, end]} (an end is a reference id or {reference_id, port} of a device): replace an old run; leave out from and to." },
                allow_door_keepout = new { type = "boolean", description = "Default false: the search never enters a door's keep-out (its face and the configured band either side inside its rectangle; the route's own end cells released). true lets it, and passes on to the dry run." },
                reserve_cells = new { type = "array", maxItems = 1024, items = new { description = "[x, y, z] or {x, y, z} in metres." }, description = "Cells the search treats as blocked (kept free for another run); a cell that is one of this route's own ends is released." },
                reserve_ports = new
                {
                    type = "array",
                    maxItems = 64,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            reference_id = new { type = "string", description = "A device, or an in-line tank or other thing with ends." },
                            port = new { type = "integer", minimum = 0, maximum = 64, description = "The end's index (connections or grid_survey lists them)." }
                        },
                        required = new[] { "reference_id", "port" },
                        additionalProperties = false
                    },
                    description = "Ports whose joining cell (where a piece joining the port stands) the search treats as blocked, e.g. the device's other ports."
                },
                grade = new { type = "string", @enum = grades },
                frames_first = new { type = "boolean", description = "Default true: prefer any route over frames or along walls to one through air (a cell on no frame and no wall plane); through air only when no supported route fits the search box. false: no preference." },
                prefer = new { type = "string", @enum = new[] { "none", "frame_edges", "walls", "hidden" }, description = "hidden: least visible route (inside a frame 1, frame surface 3, wall plane 5, air 9 per cell)." },
                assume_removed = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = "Things to plan as if already gone; the kind's pieces among them are removed in the same job (place_arguments.remove_ids)." },
                trunk = new { type = "object", description = "Bus mode, instead of to: {waypoints} or {cells} of a trunk laid as given; every start branches from it (one job).", properties = new { waypoints = new { type = "array", items = new { description = "[x, y, z] or {x, y, z} in metres." } }, cells = new { type = "array", items = new { description = "[x, y, z] or {x, y, z} in metres." } } } },
                inside_frames = new { type = "boolean" },
                avoid_room_interior = new { type = "boolean" },
                avoid_walkways = new { type = "boolean" },
                avoid_networks = new { description = "true, or a list of network ids (network handles) to keep away from; an id naming no network is network_not_found." },
                min_bends = new { type = "boolean" },
                axis_order = new { type = "string", @enum = new[] { "any", "vertical_first", "horizontal_first" } },
                max_length = new { type = "integer", minimum = 2, maximum = 1024 },
                margin_m = new { type = "number", description = "Search box margin around the ends, default 6, max 32." },
                join = new { type = "string", @enum = new[] { "ends", "none", "all" } },
                allow_bridge = new { type = "array", items = NetworkHandle("A network (its id, or {reference_id, port} of a device on it) or a device id.") },
                root = new { type = "string", description = "Passed on: the device would_split measures against." },
                join_to = NetworkHandle("The network the route must end up on (default: to's network, when to names one); its id, a piece or device on it, or {reference_id, port}."),
                join_trunk = new { type = "boolean", description = "Add a missing tap to join_to (tap_added). Default false." },
                allow_split = new { type = "boolean" },
                allow_split_long = new { type = "boolean", description = "Default true: long straights met are split in the same job." },
                from_id = new { type = "string" },
                limit = new { type = "integer", minimum = 1, maximum = 1024 }
            },
            additionalProperties = false
        };
    }

    private static object UpgradeSchema(string[] targets, string defaultTarget)
    {
        return new
        {
            type = "object",
            properties = new
            {
                network_id = NetworkHandle("A network, every piece of it: its id, a piece or device on it, or {reference_id, port}. Give this or reference_ids."),
                wait = new { type = "boolean", description = "When another job runs: queue this run behind it (status queued, poll its job_id) instead of answering busy. Default false." },
                reference_ids = new { type = "array", minItems = 1, maxItems = 4096, items = new { type = "string" }, description = "Pieces to replace, by reference id. Give this or network_id." },
                to = new { type = "string", @enum = targets, description = $"The target grade. Default {defaultTarget}." },
                dry_run = new { type = "boolean", description = "Default true: report only, change nothing." },
                confirm = new { type = "boolean", description = "Must be true, with dry_run false, for a real run." },
                from_id = new { type = "string", description = "The thing whose inventory gives the coils or kits. Default the local player." },
                skip_unmatched = new { type = "boolean", description = "Leave pieces without a replacement as they are instead of refusing. Default false." },
                refund = new { type = "boolean", description = "Give back what deconstructing the old pieces would. Default true." },
                limit = new { type = "integer", minimum = 1, maximum = 4096, description = "Pieces listed in the report, default 200 (all are counted and checked)." },
                job_id = new { type = "string", description = "Poll a confirmed run; give nothing else." }
            },
            additionalProperties = false
        };
    }

    private static object ItemFilterSchema(bool includePaging)
    {
        Dictionary<string, object> properties = new()
        {
            ["prefab_contains"] = new { type = "string", description = "Case-insensitive substring of the prefab name, e.g. 'IngotIron', 'Ore', 'Battery'." },
            ["name_contains"] = new { type = "string", description = "Case-insensitive substring of the display name, e.g. 'Iron Ingot'." },
            ["location"] = new { type = "string", @enum = new[] { "any", "ground", "player", "stored", "machine_stock" }, description = "ground = loose in the world, player = carried by a player at any depth, stored = in any other holder, machine_stock = held as reagents inside a machine (fabricator stock, a furnace's load). Default any, which includes machine_stock." },
            ["within_id"] = new { type = "string", description = "Only items inside this thing at any depth (a locker, crate, suit, backpack or player reference ID), and a machine's own stock. An id that names nothing is refused with thing_not_found, never read as an empty holder." },
            ["near_player_m"] = new { type = "number", exclusiveMinimum = 0, description = "Only items whose outermost holder is within this many metres of the local player." },
            ["limit"] = new { type = "integer", minimum = 1, maximum = 500, description = includePaging ? "Items per page, default 100." : "Item types returned, default 200." }
        };
        if (includePaging)
        {
            properties["offset"] = new { type = "integer", minimum = 0, description = "Items to skip, for paging." };
        }

        return new { type = "object", properties, additionalProperties = false };
    }

    private static object ContainerListSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                prefab_contains = new { type = "string", description = "Case-insensitive substring of the container's prefab name, e.g. 'Locker', 'Crate'." },
                name_contains = new { type = "string", description = "Case-insensitive substring of the container's display name (its label)." },
                near_player_m = new { type = "number", exclusiveMinimum = 0, description = "Only containers within this many metres of the local player." },
                limit = new { type = "integer", minimum = 1, maximum = 500, description = "Containers per page, default 100." },
                offset = new { type = "integer", minimum = 0, description = "Containers to skip, for paging." }
            },
            additionalProperties = false
        };
    }

    private static object ContainerContentsSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                reference_id = new { type = "string", description = "Reference ID of any thing (from find_items, list_containers or list_devices), or 'player' for the local player." },
                depth = new { type = "integer", minimum = 1, maximum = 6, description = "How many levels of nested slots to show, default 3." }
            },
            required = new[] { "reference_id" },
            additionalProperties = false
        };
    }

    private static object Tool(string name, string description, object inputSchema, bool readOnly)
    {
        return new
        {
            name,
            description,
            inputSchema,
            annotations = new
            {
                readOnlyHint = readOnly,
                destructiveHint = !readOnly,
                idempotentHint = readOnly,
                openWorldHint = false
            }
        };
    }

    private static object DeviceInputSchema(bool includeLogicType, bool includeValue)
    {
        Dictionary<string, object> properties = new()
        {
            ["gateway_id"] = new { type = "string", description = GatewayIdDescription },
            ["reference_id"] = new { type = "string", description = "Target device reference ID returned by list_devices." }
        };
        List<string> required = ["reference_id"];

        if (includeLogicType)
        {
            properties["logic_type"] = new
            {
                oneOf = new object[]
                {
                    new { type = "string" },
                    new { type = "integer", minimum = 0, maximum = 65535 }
                },
                description = "LogicType enum name or numeric ushort ID."
            };
            required.Add("logic_type");
        }

        if (includeValue)
        {
            properties["value"] = new { type = "number", description = "Finite double value to write." };
            required.Add("value");
        }

        return new
        {
            type = "object",
            properties,
            required = required.ToArray(),
            additionalProperties = false
        };
    }

    private static object ConsoleCommandSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                command = new
                {
                    type = "string",
                    minLength = 1,
                    description = "Full console command line exactly as it would be typed, for example 'addgas Oxygen 144 1000 293', 'storm start' or 'help'."
                },
                max_output_lines = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 500,
                    description = "Maximum console lines to return; defaults to 100. Truncation keeps the oldest lines, because an error prints its message before its stack trace."
                }
            },
            required = new[] { "command" },
            additionalProperties = false
        };
    }

    private static object ConsoleReadSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                lines = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 500,
                    description = "Number of recent console lines to return; defaults to 50."
                }
            },
            additionalProperties = false
        };
    }

    private static object IcSourceInputSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = IcHolderIdDescription },
                source = new { type = "string", description = "The complete source: IC10, or Lua for a Lua chip (up to 262144 characters)." }
            },
            required = new[] { "reference_id", "source" },
            additionalProperties = false
        };
    }

    private static object IcHolderSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = IcHolderIdDescription }
            },
            required = new[] { "reference_id" },
            additionalProperties = false
        };
    }

    private static object IcStatusSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = IcHolderIdDescription },
                stack_start = new { type = "integer", minimum = 0, description = "First stack address to include; defaults to 0." },
                stack_count = new { type = "integer", minimum = 0, maximum = 512, description = "Number of stack values to include; defaults to 64. Use 0 to omit values." },
                log_lines = new { type = "integer", minimum = 0, maximum = 200, description = "Lua chips: how many of the last print() log lines to include; defaults to 20. Use 0 to omit the log." }
            },
            required = new[] { "reference_id" },
            additionalProperties = false
        };
    }

    private static object IcPinsSchema()
    {
        object pin = new
        {
            type = new[] { "string", "null" },
            description = "Device reference ID (from list_devices) to put on this pin, or null to clear it."
        };

        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = "IC Housing reference ID returned by list_devices." },
                pins = new
                {
                    type = "object",
                    properties = new { d0 = pin, d1 = pin, d2 = pin, d3 = pin, d4 = pin, d5 = pin },
                    minProperties = 1,
                    additionalProperties = false,
                    description = "The pins to change, by name. Pins left out keep their device."
                },
                allow_off_network = new { type = "boolean", description = "Store a device that is not on the housing's data network. While the housing is on a data network its chip does not reach such a device (reachable is false and the pin reads as unset), so this is for a device about to be cabled in; a housing with no data network at all reaches its pin devices anywhere. Default false." }
            },
            required = new[] { "reference_id", "pins" },
            additionalProperties = false
        };
    }

    private static object IcExecutionControlSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = IcHolderIdDescription },
                action = new { type = "string", @enum = new[] { "pause", "step", "resume", "restart" }, description = "pause, step and resume: IC10 chips. restart: Lua chips." }
            },
            required = new[] { "reference_id", "action" },
            additionalProperties = false
        };
    }

    private static object IcSelectorSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = IcHolderIdDescription },
                target_reference_ids = ReferenceIdArraySchema("Optional devices to evaluate selectors for, each one the scope shows; defaults to the devices on the holder's data network.")
            },
            required = new[] { "reference_id" },
            additionalProperties = false
        };
    }

    private static object SlotInspectionSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = "Target device reference ID returned by list_devices." },
                slot_index = new { type = "integer", minimum = 0, description = "Optional single slot index; omit to inspect every slot." }
            },
            required = new[] { "reference_id" },
            additionalProperties = false
        };
    }

    private static object NetworkSnapshotSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_ids = ReferenceIdArraySchema("Optional exact device reference-ID filter."),
                prefab_hash = new { type = "integer", description = "Optional exact signed PrefabHash filter." },
                name_contains = new { type = "string", description = "Optional case-insensitive DisplayName substring filter." },
                logic_types = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 64,
                    items = LogicTypeSchema(),
                    description = "Optional logic types to read. Omit to read every logic type each matching device exposes."
                },
                max_devices = new { type = "integer", minimum = 1, maximum = 256, description = "Maximum returned devices; defaults to 256." }
            },
            additionalProperties = false
        };
    }

    private static object ReferenceIdArraySchema(string description)
    {
        return new
        {
            type = "array",
            minItems = 1,
            maxItems = 256,
            items = new { type = "string" },
            description
        };
    }

    private static object BulkLogicSchema(string arrayName, bool includeValue)
    {
        Dictionary<string, object> operationProperties = new()
        {
            ["reference_id"] = new { type = "string", description = "Target device reference ID returned by list_devices." },
            ["logic_type"] = LogicTypeSchema()
        };
        List<string> operationRequired = ["reference_id", "logic_type"];
        if (includeValue)
        {
            operationProperties["value"] = new { type = "number", description = "Finite double value to write." };
            operationRequired.Add("value");
        }

        Dictionary<string, object> properties = new()
        {
            ["gateway_id"] = new { type = "string", description = GatewayIdDescription },
            [arrayName] = new
            {
                type = "array",
                minItems = 1,
                maxItems = 256,
                items = new
                {
                    type = "object",
                    properties = operationProperties,
                    required = operationRequired.ToArray(),
                    additionalProperties = false
                }
            }
        };

        return new
        {
            type = "object",
            properties,
            required = new[] { arrayName },
            additionalProperties = false
        };
    }

    private static object MemoryReadSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = "Memory-device reference ID returned by list_devices." },
                start_address = new { type = "integer", minimum = 0 },
                count = new { type = "integer", minimum = 1, maximum = 512 }
            },
            required = new[] { "reference_id", "start_address", "count" },
            additionalProperties = false
        };
    }

    private static object MemoryWriteSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                reference_id = new { type = "string", description = "Memory-device reference ID returned by list_devices." },
                start_address = new { type = "integer", minimum = 0 },
                values = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 512,
                    items = new { type = "number" }
                }
            },
            required = new[] { "reference_id", "start_address", "values" },
            additionalProperties = false
        };
    }

    private static object SampleLogicSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                gateway_id = new { type = "string", description = GatewayIdDescription },
                targets = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 32,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            reference_id = new { type = "string", description = "Target device reference ID." },
                            logic_type = LogicTypeSchema()
                        },
                        required = new[] { "reference_id", "logic_type" },
                        additionalProperties = false
                    }
                },
                duration_seconds = new { type = "number", minimum = 0.1, maximum = 30.0, description = "Observation duration; defaults to 5 seconds." },
                interval_seconds = new { type = "number", minimum = 0.05, maximum = 5.0, description = "Least time between samples; defaults to 0.5 seconds. At most 120 samples may be requested. Each sample is a round trip to the game's main thread, so a short interval on a busy or slow game yields fewer samples than asked; sample_count says how many were taken." }
            },
            required = new[] { "targets" },
            additionalProperties = false
        };
    }

    private static object LogicTypeSchema()
    {
        return new
        {
            oneOf = new object[]
            {
                new { type = "string" },
                new { type = "integer", minimum = 0, maximum = 65535 }
            },
            description = "LogicType enum name or numeric ushort ID."
        };
    }
}
