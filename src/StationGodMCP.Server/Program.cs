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
    private const string ServerVersion = "1.2.0";
    private const string ProtocolVersion = "2025-06-18";
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

    private static async Task<string?> HandleMcpMessageAsync(string line, GameTransportSettings transport)
    {
        object? requestId = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("id", out JsonElement idElement))
            {
                requestId = idElement.Clone();
            }

            string method = root.GetProperty("method").GetString() ?? string.Empty;
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

    private static async Task<object> CallToolAsync(JsonElement root, GameTransportSettings transport)
    {
        JsonElement parameters = root.GetProperty("params");
        string toolName = parameters.GetProperty("name").GetString() ?? string.Empty;
        if (!ToolDefinitions.Names.Contains(toolName))
        {
            throw new McpException(-32602, $"Unknown StationGodMCP tool '{toolName}'.");
        }

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement value)
            ? value.Clone()
            : JsonSerializer.SerializeToElement(new { });

        GameResponse response;
        try
        {
            response = toolName == "sample_logic"
                ? await SampleLogicAsync(transport, arguments)
                : await SendToGameAsync(transport, toolName, arguments);
        }
        catch (Exception exception)
        {
            string failure = JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = "game_unavailable",
                    message = $"Could not reach the Stationeers mod through {transport.Description}: {exception.Message}"
                }
            }, JsonOptions);
            return new
            {
                content = new[] { new { type = "text", text = failure } },
                isError = true
            };
        }

        string text = response.Ok
            ? response.Result.GetRawText()
            : response.Error.GetRawText();

        return new
        {
            content = new[] { new { type = "text", text } },
            structuredContent = response.Ok ? response.Result : response.Error,
            isError = !response.Ok
        };
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
        using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(3));
        await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeout.Token);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(35));
        using StreamReader reader = new(pipe, new UTF8Encoding(false), true, 4096, leaveOpen: true);
        using StreamWriter writer = CreateWriter(pipe);
        await writer.WriteLineAsync(request.AsMemory(), timeout.Token);

        string? responseLine = await reader.ReadLineAsync(timeout.Token);
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
        using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(transport.Host!, transport.Port, connectTimeout.Token);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(35));
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
        string? responseLine = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new IOException("The remote StationGodMCP bridge closed the connection without returning a response.");
        }

        return responseLine;
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
                throw new McpException(-32602, "sample_logic gateway_id must be a string.");
            }

            gatewayId = string.IsNullOrWhiteSpace(gatewayElement.GetString()) ? null : gatewayElement.GetString();
        }

        if (!arguments.TryGetProperty("targets", out JsonElement targetsElement) ||
            targetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new McpException(-32602, "sample_logic requires a targets array.");
        }

        JsonElement[] targets = targetsElement.EnumerateArray().Select(target => target.Clone()).ToArray();
        if (targets.Length == 0 || targets.Length > 32)
        {
            throw new McpException(-32602, "sample_logic targets must contain between 1 and 32 entries.");
        }

        double durationSeconds = ReadOptionalNumber(arguments, "duration_seconds", 5d);
        double intervalSeconds = ReadOptionalNumber(arguments, "interval_seconds", 0.5d);
        if (durationSeconds < 0.1d || durationSeconds > 30d)
        {
            throw new McpException(-32602, "duration_seconds must be between 0.1 and 30.");
        }

        if (intervalSeconds < 0.05d || intervalSeconds > 5d)
        {
            throw new McpException(-32602, "interval_seconds must be between 0.05 and 5.");
        }

        int plannedSamples = (int)Math.Ceiling(durationSeconds / intervalSeconds) + 1;
        if (plannedSamples > 120)
        {
            throw new McpException(-32602, "The requested duration and interval exceed the 120-sample limit.");
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
            throw new McpException(-32602, $"{name} must be a finite number.");
        }

        return number;
    }

    private static string SerializeRpcResult(object? id, object result)
    {
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOptions);
    }

    private static string SerializeRpcError(object? id, int code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
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

    private sealed record GameTransportSettings(string PipeName, string? Host, int Port, string? Secret)
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
    private const string IcHolderIdDescription = "The circuit holder, by any of: an IC Housing or a worn one (suit, Programmable Visor) from list_devices; a Console or Computer holding a ScriptedScreens Lua board (Circuitboard or Motherboard (Lua Chip)), or a held tablet holding one Lua cartridge; the board or cartridge itself; or the chip in any holder. inspect_slots on the console or tablet lists the board, cartridge and chip ids. Replies give reference_id (the device or worn item the scope reaches), holder (the circuit holder itself) and chip.";

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
        "remove_structure",
        "grid_survey",
        "plan_cable_route",
        "plan_pipe_route",
        "plan_chute_route",
        "plan_removal",
        "feed_paths",
        "trader_buy",
        "trader_sell",
        "paste_blueprint"
    ];

    internal static readonly object[] All =
    [
        Tool(
            "list_gateways",
            "List the scopes device tools accept as gateway_id: first 'world', every device in the world (status 'bypass', kept for older clients), then every StationGod Gateway in the loaded world with its availability and network status; a gateway id only narrows a call to that gateway's data networks. bypass_gateway is always true, kept for older clients.",
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
                    gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
            "Write a generic logic value to a device using a logic-type name or numeric ID. Any device in the world that supports writing that type; a gateway_id limits it to devices on that gateway's data networks.",
            DeviceInputSchema(includeLogicType: true, includeValue: true),
            readOnly: false),
        Tool(
            "read_logic_many",
            "Read up to 256 generic logic values in one main-thread request. Each operation returns its own success or error result.",
            BulkLogicSchema("reads", includeValue: false),
            readOnly: true),
        Tool(
            "write_logic_many",
            "Write up to 256 generic logic values in order in one main-thread request. Each operation returns its own success or error result.",
            BulkLogicSchema("writes", includeValue: true),
            readOnly: false),
        Tool(
            "read_memory",
            "Read a contiguous range of up to 512 values from a visible device implementing Stationeers readable memory.",
            MemoryReadSchema(),
            readOnly: true),
        Tool(
            "write_memory",
            "Write a contiguous range of up to 512 finite values to a visible device implementing Stationeers writable memory.",
            MemoryWriteSchema(),
            readOnly: false),
        Tool(
            "inspect_slots",
            "Inspect generic device slots, occupants, slot constraints, and every readable LogicSlotType value without changing inventory contents.",
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
            "Read the source stored in the programmable chip of a visible circuit holder: IC10, or Lua for a StationeersLua chip (Integrated Circuit (Lua)) in an IC Housing, a ScriptedScreens board in a Console or Computer, a tablet cartridge or a Programmable Visor. reference_id may name the holder, the device or worn item holding it, or the chip (see the reference_id description). Returns source, source_length, language (ic10 or lua), holder, chip, line_number (IC10 only) and, for a Lua chip, lua (see get_ic_status).",
            IcHolderSchema(),
            readOnly: true),
        Tool(
            "set_ic_source",
            "Write source to the programmable chip of a visible circuit holder, as the IC editor's export does. IC10: compiled at once and restarted at line 0. Lua (a StationeersLua chip in an IC Housing, a ScriptedScreens Console or Computer board, a tablet cartridge or a Programmable Visor): no IC10 line or byte limit; up to 262144 characters (larger is refused with source_too_large). StationeersLua stores it compressed, drops the old runtime and compiles the new source on a worker thread, running its module-level code once and then tick(dt) every game tick; a source that failed before is compiled again. No separate restart is needed. The reply's lua.compiling is usually still true: call get_ic_status until it is false, then check lua.running and lua.last_error. A holder that is off or unpowered compiles when it runs again. Writes; sends the chip to multiplayer clients.",
            IcSourceInputSchema(),
            readOnly: false),
        Tool(
            "get_ic_status",
            "Inspect a visible circuit holder's source, current instruction, registers, stack window, aliases, defines, jump tags, power state, pause state, compile/runtime diagnostics, and its device pins d0..d5 (pins: each pin's device reference ID, prefab and display name or null when empty, the alias the chip gave the pin, and reachable, false when the chip cannot reach the device because it is not on the housing's data network). Holders: IC Housings, suits and other worn holders, and StationeersLua / ScriptedScreens Lua holders (a Console or Computer board, a tablet cartridge, a Programmable Visor). housing.kind is ic_housing, worn_item, computer_board, cartridge or inserted_item; operable says whether the holder runs its chip now (a board: its computer on, powered and fully built; a cartridge: its tablet on and powered; a suit or visor: a charged battery). Also language (ic10 or lua), holder, chip and source_length. For a Lua chip, lua: compiling (a worker thread is compiling it; read again), has_runtime, init_complete (module-level code done, tick(dt) running), running (all of these, no error, not a library), library (a --@module chip other chips require), source_version, last_error {kind compile or runtime, line, message, traceback} or null, log {lines (the last log_lines print() lines), line_count, truncated}, and unavailable when StationeersLua's internals could not be read. Registers, stack and line fields are IC10's and mean nothing for a Lua chip.",
            IcStatusSchema(),
            readOnly: true),
        Tool(
            "control_ic_execution",
            "Pause, execute exactly one IC10 instruction while remaining paused, or resume the chip of a visible circuit holder; or restart a Lua chip. Pausing holds only IC Housings and suits; other holders (toolbelts, tablets, mining robots, logic I/O devices) report paused but keep running. A Lua chip (StationeersLua) cannot pause or step (lua_chip_unsupported): restart compiles its current source again and runs it from the start, clearing a latched error, as the Lua debugger's restart does; like set_ic_source it compiles on a worker thread, so poll get_ic_status. restart on an IC10 chip is refused (not_a_lua_chip).",
            IcExecutionControlSchema(),
            readOnly: false),
        Tool(
            "resolve_ic_selectors",
            "Resolve a circuit holder's db/d0... pins and compiled aliases (IC10), and report unique prefab/name-hash selectors for visible network devices. For a Console or Computer Lua board, db is the computer.",
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
            "Find anything in the world by name, not only items: portable tanks and canisters, crates and other movable things, structures and devices (tanks, lockers, pipes, frames), items, players and animals. name_contains matches the name the game shows, which is the Labeller name when the thing has one, and also the game's own name under a label, so 'T1' and 'Portable Liquid Tank' both find a tank labelled T1. Each thing reports reference_id, prefab_name, display_name, custom_name (the Labeller name, null if none), game_name (the game's name for the prefab), kind (item, dynamic = a movable thing that is not an item: portable tanks, crates, generators, rovers; structure, entity or other), runtime_type, labelable (the Labeller can rename this class of thing: portable things, every device, in-line tanks, hydroponic trays, plants, IC chips, flags and a few more; plain pipes, cables, frames and ordinary items cannot), location (ground, player or stored as find_items has it, built for a structure), carried_by, held_in (holders from its own slot outwards), position (the outermost holder's), distance_m, is_device (the device tools take it) and has_atmosphere (atmosphere_contents has something for it). Also scanned (things in the world). Sorted nearest first. Examples: all portable tanks, gas and liquid, whatever their prefab or label: runtime_type DynamicGasCanister; everything that holds gas: has_atmosphere true; every label in the world: labelled_only true. find_items stays the tool for item quantities and machine stock. Organs are left out. Read only; no gateway is needed.",
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
                    has_atmosphere = new { type = "boolean", description = "true: only things atmosphere_contents has something for (an internal atmosphere, a pipe network, a connected network, or something with an atmosphere in a slot); false: only things without." },
                    near_player_m = new { type = "number", exclusiveMinimum = 0, description = "Only things (or their outermost holder) within this many metres of the local player." },
                    limit = new { type = "integer", minimum = 1, maximum = 500, description = "Things per page, default 100." },
                    offset = new { type = "integer", minimum = 0, description = "Things to skip, for paging." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "label",
            "Rename a thing as the hand Labeller does, with the game's own rename (Thing.RenameThing on the host, which syncs to clients and is saved; a multiplayer client sends the rename to the host). The name is written as the Labeller writes it: an empty name becomes the game's name for the prefab (the game stores that name rather than clearing the label), cut to 200 characters, rich-text tags stripped except on signs and labels. Only things the Labeller can rename: portable tanks and other movable things, every device, in-line tanks, hydroponic trays, plants, IC chips, flags and a few more (find_things reports labelable). One rename: reference_id and name. Or labels: up to 64 {reference_id, name}, applied in order. A rename returns {index, ok, reference_id, prefab_name, written (the name stored), sent_to_host (true on a multiplayer client: current shows the name once the host's update arrives), previous {display_name, custom_name}, current {display_name, custom_name}}, read back from the thing; a refusal {index, ok: false, reference_id, error {code, message}} changes nothing. The batch form returns results, count, success_count and error_count; the single form returns the rename, or the refusal as the error. Refusals: thing_not_found, not_labelable (a class the Labeller cannot rename: pipes, cables, frames, ordinary items), invalid_argument. No gateway is needed.",
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
            "List every holder that has at least one item in it (lockers, crates, machines, a backpack on the floor), not held by a player, with slots used, item totals per type, position and distance, nearest first. Empty containers are not listed. Read only; no gateway is needed.",
            ContainerListSchema(),
            readOnly: true),
        Tool(
            "container_contents",
            "Show the slots of any one thing in the world and what is in them, nested: a locker, crate, machine, suit, backpack, or 'player' for the local player's whole inventory. Works whether or not the thing is on a data network. Read only; no gateway is needed.",
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
            "What gas or liquid one thing holds: a canister, portable tank, tank, suit, a pipe (its whole pipe network), a device (every pipe network it is connected to) and the canisters in its slots (a tank storage, an air conditioner), or a pipe network by its own reference id, or an atmosphere_id from water_sources. Each atmosphere lists every gas and liquid with amount_mol and, for liquids, liquid_l, plus volume_l, pressure_kpa, temperature_k and total_mol, and a water summary: liquid water (liquid_mol, liquid_l and the hydration drinking it would give), polluted water (polluted_mol, polluted_l) and steam (steam_mol, steam_if_condensed_l). Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of a thing (find_items, list_devices, list_containers), of a pipe network, or an atmosphere_id (water_sources)." }
                },
                required = new[] { "reference_id" },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "water_sources",
            "Every canister, tank, device and pipe network in the world that holds water, polluted water or steam, with its holder or its network's devices, location, pressure, temperature and the water in moles, litres and hydration; largest first, with totals. Room and world air, bodies and organs are left out, and so are bottles and packets (see consumables). Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    min_mol = new { type = "number", exclusiveMinimum = 0, description = "Skip sources holding less water, polluted water and steam together than this; default 1 mol (0.018 litres). The old name min_moles is still read." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "trader_contacts",
            "Returns game_time_s, contacts and dishes. List every trader contact in the sky (trader type, display name, shuttle, landing pad size it needs (pad_size_tiles), unit direction vector from the base with y up, elevation, watts needed to resolve and to contact (min_power_to_resolve_w, min_power_to_contact_w), whether it is already contacted, seconds left before it leaves (time_left_s)) and every satellite dish with where it points: forward is the vector the game scores contacts against (angle error = acos(dot(forward, direction))), only updated when the dish moves, and transform_up is its current pose. Also the dish's current horizontal and vertical in degrees, its field_of_view_deg and its wattage range (min_power_w, max_power_w). Read only; no gateway is needed. Whether a contact fits and can land on a pad: see landing_pads.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "dish_aim",
            "Work out the exact Horizontal and Vertical (logic degrees) that point a satellite dish at a trader contact, found on the dish's own model: the mod poses the dish's animator at trial angles, reads where it would point and restores it within one frame, so nothing moves or changes. Returns horizontal, vertical, the angle left over (error_deg; under 2 degrees the contact gets the dish's whole Setting), the dish's current angles and error, and stale_pose_deg (above 1 means the dish's animator was not being evaluated, so the game's own pointing may lag). To turn the dish, write the returned values to its Horizontal and Vertical with write_logic. Read only; no gateway is needed.",
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
            "What each trader contact in the sky buys and sells: item or gas name, prefab, credits per unit, how many it wants or has in stock, the conditions a sold-to-it item or gas must meet (purity, moles per unit, temperature range), and for each item it buys how many of that item exist in the world outside the trader as items (have; ingots loaded into a machine as stock are not counted, since the trader cannot take them until they are ejected) and, for the landed trader only, how many it would take right now (sellable: what the pad network's vending machines and you carry that meets its conditions, or the pad network's gas in units, the same count trader_sell checks; null for a contact not landed). The game rolls a trader's inventory when the contact appears, so this works before the trader is interrogated. Omit contact_id for every contact. Read only; no gateway is needed.",
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
            "Every plant growing in a hydroponics tray, planter or station, read from the plant itself, so it works for trays with no data port: its tray, growth stage (index, count, first mature and seeding stages, progress and length of the current stage, every stage's length), whether it is mature, seeding, dead or ready to harvest, growth efficiency and its parts (breathing, temperature, hydration, pressure, light), active problems in plain words (dry, too cold, harmful gas...), each condition's running time (time_s) against the time after which it starts damaging the plant, damage per type with damage_ratio (0 unharmed, 1 dead) and health_percent, harvest and seed counts with a forecast of the next harvest from recorded stress, nutrition, fertiliser, light exposure, the air it breathes (pressure, temperature_k, gas ratios), its tray's water, its needs (ideal and survivable temperature_k and pressure_kpa, gases taken in (mol_per_tick) with the share of the air each needs, water_mol_per_tick, light and dark seconds per day, harmful gas limits) and forecasts at the current efficiency: seconds to the next stage, to harvest, to seeds and, for perennials, to regrow after a harvest (null when it is not growing). Times are game seconds (game_time_s); efficiency parts are multipliers (growth_factor, breathing_factor...); temperatures are kelvin. include_unplanted adds every other plant item (harvested crops, seed bags). Read only; no gateway is needed.",
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
            "The exact Horizontal and Vertical (logic degrees) that point a solar panel's cells straight at the sun, worked out from the panel's own pivots and the game's sun vector: no daylight sensor and no calibration. Returns reference_id, prefab_name, can_turn, horizontal and vertical (logic degrees, as write_logic takes them), off_sun_deg (the angle still off at that pose, non-zero only when the sun is outside the tilt range), alignment_ratio (1 - 2 sin(off / 2), the panel's Ratio at that pose before shading), sun {x, y, z, above_horizon, eclipse} and current {horizontal, vertical, ratio} (the panel's aim and Ratio now); a panel without pivots returns note instead of the angles. the sun vector and whether it is above the horizon, and the panel's current aim and Ratio. With the sun below the horizon it gives the pose closest to it, leaning toward where the sun will rise. A panel without pivots (the Flat panel) reports can_turn false. Read only: write the angles with write_logic. No gateway is needed.",
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
            "Set an IC Housing's device pins d0..d5, as turning its screws with a screwdriver would: each listed pin gets a device reference ID, or null to clear it, and pins not listed keep their device. When the housing is on a data network, a device must be on that network, because the chip only reaches a pin's device there; otherwise the call is refused unless allow_off_network is true. A housing with no data network reaches any pinned device, so any device is accepted. Also refused: a device outside the gateway's scope, the housing itself, and a thing with no readable logic. Every pin is checked before any is written, so a refused call changes nothing. A running chip uses the new devices from its next instruction; nothing is recompiled or reset, and a multiplayer host sends the new pins to clients. Returns what changed and all six pins as get_ic_status lists them. Works on rocket IC Housings too; suits and other worn circuit holders are refused.",
            IcPinsSchema(),
            readOnly: false),
        Tool(
            "thing_health",
            "The damage state of any thing (solar panel, pipe, cable, wall, frame, door, vent, device, item), read from the game's own DamageState: no LogicType exposes damage. Three forms. reference_id: that one thing. reference_ids: up to 256 things, a result per id with index and ok, and a per-item error (thing_not_found, invalid_argument for an id that is not a decimal string) instead of failing the call. Neither: scan every thing in the world and list the damaged ones, worst first and paged; the scan skips things being destroyed, indestructible things, players and animals, and organs, and counts decaying food and hurt plants as damaged items (use structures_only for the base alone). Each thing has reference_id, prefab_name, display_name, kind (structure, item or other), type (runtime class), damage_state (destructible, indestructible or none), damage_state_class, max_damage (health capacity), total_damage (the sum the game counts: brute, burn, oxygen, hydration, starvation, toxic, radiation and decay, clamped to max_damage; stun is not counted), damage_ratio (total_damage / max_damage: 0 like new, 1 destroyed), health_percent (100 - damage_ratio * 100 rounded, as the solar panel tooltip shows), damage (each type), is_broken (at max damage, or a structure in its broken build state), being_destroyed, band (solar panels only: the tooltip colour green, yellow over 0.25, red over 0.75), pipe_burst (pipes only: none, pressure, liquid or solid), position and distance_m from the local player. An indestructible thing (the modded Force-Field Door, anything marked Indestructable) reports damage_state indestructible with total_damage, damage_ratio and health_percent null, never a fake 0. A solar panel generates (1 - damage_ratio) of its undamaged output. The scan also returns count, total (matches on all pages), structures, broken, scanned, min_damage_ratio, offset, limit, has_more and local_player. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of one thing (from find_items, list_devices, list_containers)." },
                    reference_ids = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "string" }, description = "Reference IDs of up to 256 things, for one call per card." },
                    min_damage_ratio = new { type = "number", minimum = 0, exclusiveMaximum = 1, description = "Scan only: list things whose damage_ratio is above this (0.25 = the yellow band, 0.75 = red). Default 0, any damage. The older name min_ratio is still read." },
                    structures_only = new { type = "boolean", description = "Scan only: leave items out. Default false." },
                    near_player_m = new { type = "number", exclusiveMinimum = 0, description = "Scan only: only things within this many metres of the local player." },
                    limit = new { type = "integer", minimum = 1, maximum = 500, description = "Scan only: things per page, default 200." },
                    offset = new { type = "integer", minimum = 0, description = "Scan only: things to skip, for paging." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "paint",
            "Paint things with the game's own paint, as a spray can does (the same OnServer.SetCustomColor call, but no paint is used), or list the colours. Two forms. reference_ids (up to 256) with color: paint them all one colour. items (up to 256), each {reference_id, color}: a colour per thing, so a caller can restore the previous_color of an earlier call. color is a colour name (case-insensitive, as colors lists it), a colour index, or 'default' for each thing's own prefab colour. With neither form it only lists the colours. The paint is synced to clients and saved with the world. With no targets it returns colors: [{index, name, paint_only}] and count (paint_only colours, the metallic cans, cannot be set through logic but can be sprayed). With targets it returns results, one per thing: {index, ok, reference_id, previous_color, color} where a colour is {index, name, is_default} (is_default: the prefab's own colour, which the save stores as -1; index null when the thing has no colour), or on failure {index, ok: false, reference_id, previous_color, error: {code, message}}; plus count, success_count and error_count. Per-thing error codes: invalid_argument (reference_id not a decimal string, color not a string), thing_not_found, not_paintable (no paintable material, or a batched structure the game cannot repaint), has_color_state (the colour is a state, e.g. lights, which the spray can does not paint either), invalid_color (unknown name or index, or 'default' on a thing with no prefab colour), paint_failed (the colour did not change, or the game threw while painting). No gateway is needed.",
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
            "Which frames (iron, steel, corner, side) are outer frames: frames with a face on a cell whose gas is the world's, i.e. the planet's outside air. A face counts as exposed when the 2 m cell across it is in no room (the game's own meaning of outside: rooms are closed spaces of up to 1200 cells, walled by anything that blocks walking), can hold air (no airtight frame or other air-blocking structure in it, not buried in terrain), is open to the frame through the shared face (no terrain and no air-blocking wall on it), and, when that cell holds a one-sheet frame (never part of a room), its air passes the game's storm exposure test (the planet's atmosphere, or within 1 kPa of it). Limits: a sealed space bigger than 1200 cells has no room, so frames facing into it count as outer; a space closed only by one-sheet frames is a room, so its faces do not count though it leaks. Each frame has reference_id, prefab_name, display_name, position, distance_m from the local player, exposed_faces (any of +x, -x, +y, -y, +z, -z; +z is north, +y up), exposed_face_count, blocks_air (whether the frame's current build state is airtight: 2 sheets), build_state (0 bare, 1 one sheet, 2 airtight) and color {index, name} (index null when unpainted and colourless). Nearest first when there is a local player. Also count, total_frames (frames considered, after near_player_m), total_outer, and paging: offset, limit, total (frames listed across all pages: the outer ones, or every frame with include_inner) and has_more; and local_player {reference_id, display_name, position}. Read only; no gateway is needed.",
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
            "The game's closed rooms measured cell by cell: every room in the world, or with reference_id only the room that thing (a device, item or player) is in. A room is a closed flood fill of 2 m cells (at most 1200; a bigger or open space is outside and has no room). Each room: room_id, room_type (the game's RoomType), cell_count, volume_l, pressure_kpa, temperature_k, total_mol, heat_capacity_j_per_k, thermal_energy_j (the air's total energy; sample twice and its change over the seconds between is the room's net heat flow in watts, gas moved in or out included), gases [{gas, amount_mol, ratio}] largest first (traces under 1e-9 mol left out), bounds {min, max} (the box of the cell centres, world metres), contains_local_player, devices [{reference_id, prefab_name, display_name}] (devices whose cell is in the room; left out with include_devices false) and, with include_cells, cells [{x, y, z}] (every cell centre). The air is summed fresh from each cell's atmosphere, as the game pools a room. Largest room first. Also count and local_player_room_id (null outside a room or without a player). Errors: thing_not_found, not_in_room. Read only; no gateway is needed.",
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
            "What the local player's crosshair is on: the game's own CursorManager.CursorThing, the thing its interaction ray (3 m from the camera) hits this frame. Returns player ({reference_id, display_name, position}, null without one), target (null when looking at nothing, terrain, or past 3 m; else {reference_id, prefab_name, display_name, custom_name (the labeller name, null if none), kind (item, dynamic = a movable thing that is not an item such as a portable tank, structure, entity or other), runtime_type, position, distance_m, is_device (a logic device describe_device, read_logic and write_logic take), has_atmosphere (atmosphere_contents has something to report for it), parent (for a thing in a slot: {reference_id, prefab_name, display_name, slot_index, slot_name} of its holder, else null)}) and interactable (null, or the button, switch, port or slot under the crosshair as the game's hand logic takes it: {action (the game's InteractableType: Activate, OnOff, Open, Slot1...), display_name, contextual_name (the name the tooltip shows on this thing), state, slot (for a slot: {slot_index, slot_name, occupant ({reference_id, prefab_name, display_name}, null when empty)}, else null)}). Hand the reference_id to any other tool. Read only; no gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "connections",
            "How pipes, cables and chutes connect, read from the game's own connection ends and networks. Two forms. reference_id: a pipe, cable, chute or device's ends, returning thing {reference_id, prefab_name, display_name}, position, own_network (a pipe, cable or chute's own network {kind, id}, else null) and ends: [{index, type (the game's NetworkType: Pipe, PipeLiquid, Power, Data, PowerAndData, Chute, Elevator, LandingPad, LaunchPad, RoboticArmRail), type_name (its display name), role (ConnectionRole: None, Input, Input2, Output, Output2, Waste), role_name, position {x,y,z}, network ({kind: pipe, cable or chute, id}: for a pipe, cable or chute its own network at every end; for a device the network of the pipe, cable or chute attached at that end, null when none is), connected: [{reference_id, prefab_name, display_name}] (everything attached at that end, by the game's own IsConnected test)}]; a thing that is not a pipe, cable, chute or device is refused with not_connectable. network_id with kind (pipe, cable or chute): the network's members, pipes, cables or chutes first then devices, paged, each {reference_id, prefab_name, display_name, member (pipe, cable, chute or device), position}; with count, structure_count, device_count, offset, limit, total, has_more, network {kind, id} and summary. Pipe summary: content (Gas or Liquid), volume_l, pressure_kpa, temperature_k, total_mol, liquid_volume_l and gases [{gas, state (gas or liquid), amount_mol}] (null on a multiplayer client, which does not simulate pipe atmospheres). Cable summary, from the last power tick as the Cable Analyser shows it: required_w, potential_w, actual_w (power actually delivered), shortfall_w, lowest_cable_max_w (the weakest cable's rating), lowest_fuse_break_w, overloaded (min(potential, required) is above the weakest cable's rating: the game burns one such cable per tick), fuse_overloaded (the same against the weakest fuse), cable_count and fuse_count. Chute summary: member_count. Network ids come from ends[].network.id or atmosphere_contents. Errors: thing_not_found, not_connectable, network_not_found. Read only; no gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of a pipe, cable, chute or device." },
                    network_id = new { type = "string", description = "Id of a pipe, cable or chute network (with kind)." },
                    kind = new { type = "string", @enum = new[] { "pipe", "cable", "chute" }, description = "The kind of network_id." },
                    limit = new { type = "integer", minimum = 1, maximum = 1000, description = "network_id only: members per page, default 200." },
                    offset = new { type = "integer", minimum = 0, description = "network_id only: members to skip, for paging." }
                },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "plant_genes",
            "Read or edit the genes of plants, seeds and harvested produce. A plant item holds one gene set per unit of its stack; the top set (the last unit) is the one planting copies, the planted plant's stats read, harvests inherit (with mutation) and the network sends. Read: reference_id (one thing, optionally unit: which unit's set, 0-based, default the top) or reference_ids (up to 256, top sets; a result per id with index and ok, or error {code, message}). Each read returns thing {reference_id, prefab_name, display_name}, holder (planted_plant, seed or produce), gene_set_count, unit, is_top and genes: all 19, each {gene, value, min -1, max 1, stability, stability_min -1, stability_max 1 (positive = steadier: smaller random mutation, and it decays toward 0 each generation), meaning (what it does in the game), effect}. effect is the stat the gene sets now and at both ends of the range: for the fifteen scalar genes {stat, base_<unit>, now_<unit>, at_min_<unit>, at_max_<unit>} with unit s (seconds) or factor (a multiplier); for the four band genes {stat (GrowTemperature or GrowPressure), now, at_min, at_max}, each {ideal_min_k and min_k} or {ideal_max_k and max_k} (kPa for pressure). Genes: GrowthSpeedMultiplier (the save calls it GrowthTimeMultiplier; both are accepted), DarkPerDay, LightPerDay, DroughtTolerance, WaterUsage, LowPressureResistance, LowTemperatureResistance, UndesiredGasTolerance, GasProduction, HighPressureResistance, HighTemperatureResistance, SuffocationTolerance, LowPressureTolerance, LowTemperatureTolerance, HighPressureTolerance, HighTemperatureTolerance, UndesiredGasResistance, LightTolerance, DarknessTolerance. Write: reference_id plus genes {name: value}, as the Gene Splicer does; optional unit and force. Each gene gets a result {index, ok, gene, previous_value, value} or {index, ok: false, gene, previous_value, error {code, message}}: unknown_gene, out_of_range (outside -1..1, the game's range; force writes it anyway), invalid_argument (not a number); the others are still written. Plus thing, holder, unit, is_top, count, success_count, error_count. The edit is saved with the world and inherited by harvested fruit and seeds like a natural gene (then mutated); stats read the genes live, and a write to the top set recomputes the cached temperature and pressure efficiency curves and marks the genes for multiplayer sync. A plant's harvest quantity, fixed when it first matures, is not recomputed. Errors: thing_not_found, not_a_plant, no_genes, unit_out_of_range, not_host (a multiplayer client cannot write). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    reference_id = new { type = "string", description = "Reference ID of a plant (planted or not), seed or plant produce." },
                    reference_ids = new { type = "array", minItems = 1, maxItems = 256, items = new { type = "string" }, description = "Read only: up to 256 reference IDs." },
                    unit = new { type = "integer", minimum = 0, description = "With reference_id: which unit of the stack, 0-based; default the top (the last unit)." },
                    genes = new { type = "object", additionalProperties = new { type = "number" }, description = "With reference_id: gene name to new value, e.g. {\"GrowthSpeedMultiplier\": 1}. Range -1 to 1." },
                    force = new { type = "boolean", description = "With genes: write values outside -1..1 too. Default false." }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "mod_info",
            "The running mod's identity and health: mod_id, mod_version, assembly_version, informational_version, pipe_name (the local named pipe this game listens on, the sidecar's --pipe: tells which game a sidecar reached when two run on one machine), and methods: every method the mod answers, each {method, calls, errors, total_ms, mean_ms (null before the first call), max_ms} counted in memory since the mod loaded (main-thread time per request; errors are replies with ok false). Also count, reflection: [{member, resolved, optional}] (every game member the mod reaches by reflection and every Harmony target it patches; optional ones belong to other mods such as Terraforming Reloaded and BlueprintMod) and missing_count (required members not found: methods that need one answer game_changed). Every reply envelope also carries elapsed_ms, that request's main-thread time. Read only; no gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "move_gas",
            "Move gas and liquid from one atmosphere to another, or delete it, with the game's own gas calls: each gas leaves with its own share of its heat and arrives with it. from and to are each a reference id: a thing with an internal atmosphere (canister, portable tank, tank, suit), a pipe (its network), a pipe network id, or an atmosphere id as atmosphere_contents reports; a device without its own atmosphere is refused (no_atmosphere), and so are the planet and world cells (refused). Rooms are not supported. Pass to, or delete: true to destroy the gas. gases: names as atmosphere_contents reports them (Oxygen, Nitrogen, CarbonDioxide, LiquidOxygen, Steam...); omit for every gas and liquid. amount_mol: moles of each listed gas, capped at what is there; omit for all of it. "
            + "Joined sets (joined, default true): the game mixes some atmospheres to one composition every tick, so gas taken from one flows back from the others. The joins followed: a Gas Tank Storage's canisters with its pipe networks; a portables connector's tank with its gas network (gases) and liquid network (liquids); a connector pipe's tank with its network; a portable tank with the canister in its slot; a tank or other internal-atmosphere device with its network; a hydroponics tray with its networks; an open valve's two networks. From a joined set, each gas is taken from every member in proportion to what it holds (amount_mol caps the set's total), each with its own share of energy, in the same tick; a liquid follows the joins that carry liquids. Into a joined set, the gas goes into the named atmosphere and the game spreads it. to inside from's set is refused (same_joined_set). joined: false moves from and to the named atmospheres only. "
            + "Burst check (refused with would_burst, predicted and limit kPa in the message; force skips it): each side's settled pressure, its members pooled as if mixed, against every member's rating (a canister's MaxPressure or a portable tank's MaxSetting across its walls, or each pipe network member's MaxPressure at its cells, each against the pressure outside); things without a rating are not checked. "
            + "Timing: the game only changes gas on its atmospherics thread, so the move is queued and applied at the start of the next atmospherics tick (about half a second; never while paused). The reply is the prediction from last tick's values: {transfer_id, status: queued, predicted: true, joined, from, to (null when deleted), deleted, moved, error: null}. from and to are {members: [{owner {kind (thing or pipe_network), reference_id, prefab_name, display_name}, atmosphere_id, before, after}] (the named atmosphere first), total {before, after} (the members pooled: pressure and temperature as if mixed), joined_by: [{reference_id, prefab_name, display_name}] (the joining devices)}; before and after are {pressure_kpa, temperature_k, total_mol, gases: [{gas, amount_mol}]}; moved is [{gas, amount_mol, energy_j}] summed over the members. Call again with only transfer_id for the outcome: status queued, applied (the same shape from live values, predicted: false) or failed (error {code, message}); the last 64 are kept. To undo, move each gas back by amount_mol; it returns at the other side's temperature by then, so energy_j tells you how far that differs. Errors: invalid_argument, unknown_gas, atmosphere_not_found, no_atmosphere, refused, same_joined_set, joined_too_large (over 64 joined atmospheres), nothing_to_move, would_burst, busy (64 moves waiting), transfer_not_found, not_host (multiplayer clients cannot move gas). No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    from = new { type = "string", description = "Reference id of the source: a thing with an internal atmosphere, a pipe, a pipe network or an atmosphere." },
                    to = new { type = "string", description = "Reference id of the target, same forms. Omit and pass delete: true to destroy the gas." },
                    delete = new { type = "boolean", description = "Destroy the gas instead of moving it. Default false." },
                    gases = new { type = "array", minItems = 1, items = new { type = "string" }, description = "Gas names, e.g. [\"Oxygen\"]; omit for all gases and liquids." },
                    amount_mol = new { type = "number", exclusiveMinimum = 0, description = "Moles of each listed gas, capped at what is there; omit for all." },
                    force = new { type = "boolean", description = "Move even if an end would pass its burst rating. Default false." },
                    joined = new { type = "boolean", description = "Treat atmospheres the game mixes every tick as one unit. Default true; false moves from and to the named atmospheres only." },
                    transfer_id = new { type = "string", description = "Alone: the outcome of an earlier move." }
                },
                additionalProperties = false
            },
            readOnly: false),
        Tool(
            "landing_pads",
            "Every trader landing pad in the world, measured by the game's own checks: no guessing from tiles. Each pad: reference_id, prefab_name, display_name, position, forward (+x, -x, +z or -z; +z is north), is_network_center (a pad network with more or fewer than one centre refuses every landing), piece_count (pieces on its landing-pad network), extent {x_tiles, z_tiles} (the box of its pad tiles and centre, 2 m tiles), largest_square_tiles (the biggest n up to 15 for which the game's CheckPadSize passes an n x n pad, allowing the one-tile shifted centre the game allows on even sides), runway_ok (a plane's runway threshold check: exactly one switched-on threshold on the network; null without a network), fits_by_ship (for each ShuttleType: shuttle_type, pad_size_tiles [x, y] from the game's table, runway_tiles (the approach length the game uses for planes, not checked against the pad), needs_threshold (planes), fits), and contacts (every trader contact in the sky: reference_id (the contact's id, as trader_contacts gives it), name, shuttle_type, pad_size_tiles, fits, obstructed (something above the pad in the ship's footprint; the game's CanTraderLand does not check this), can_land (the game's CanTraderLand now: power, error, one centre, storm (planes are exempt), pad size, runway threshold) and reason (the game's message, empty when it can land)). Also count. Read only: the game's pad check moves the landing point of the pad it measures, and every check here puts it back, so a landing in progress is never affected. No gateway is needed.",
            new { type = "object", properties = new { }, additionalProperties = false },
            readOnly: true),
        Tool(
            "move_item",
            "Move an item, or part of a stack, into a slot with the game's own moves, as an inventory click does: slot to slot, never through the world, so ice and other perishables are never loose in the air. A whole item moves with OnServer.MoveToSlot (the source container's bookkeeping, e.g. a vending machine's, is kept by the game); part of a stack is split straight into the slot (Stackable.SplitStack, as the Stacker does); a whole stack can join a matching stack in the slot (Stackable.Merge). One move: reference_id (the item), optional quantity (take that many off the stack; the rest stays), to_id (the thing holding the slot: a container, a belt, a suit, a player) and to_slot (the slot index as inspect_slots and container_contents give it, or \"auto\": a matching stack first when merge allows and the whole stack moves, else the first empty slot that takes it); merge (default true) lets the items join a matching stack. Or moves: up to 64 such objects, applied in order. A move returns {index, ok, reference_id, from {id, slot} (null when the item lay in the world), to {id, slot}, quantity_moved, merged_into (the stack it joined, or null), destination_reference_id (the stack now in the slot: the item, the new stack a split made, or merged_into)}; a refused move {index, ok: false, reference_id, error {code, message}} and nothing is changed for it. The batch form returns results, count, success_count and error_count; the one-move form returns the move itself, or the refusal as the error. Refusals: thing_not_found (item or destination gone), not_movable (a structure), invalid_destination (the destination is the item or inside it), no_slots, slot_not_found, same_slot, slot_locked (source or destination), slot_refuses (the game's slot rules: slot class and CanEnter, with the game's reason), slot_occupied (by something it cannot join, or merge false), partial_merge (part of a stack can only go into an empty slot), stack_full (the joined stack would pass its maximum), no_free_slot, invalid_argument (e.g. more than the stack holds), move_failed (the game did not do it; read the slots), not_host (multiplayer clients cannot move items). No gateway is needed.",
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
            "Replace cable pieces with heavy (default) or super heavy cable in place, piece for piece, as the coil's own merge placement builds them (the game refuses to place heavy cable over normal: Cable.CanReplace). Name a whole cable network with network_id (from connections) or pieces with reference_ids (up to 4096). Pieces already at or above the target stay. Each piece's replacement is the first piece of the target coil (found by what the coil places, e.g. Cable Coil (Heavy)) whose cells and connection ends (type, role, cell and facing cell) match the old piece exactly, at the old rotation or turned; that is how straight, corner, T, cross, 5- and 6-way junctions and the 3-, 5- and 10-long straights map. DRY RUN BY DEFAULT: dry_run true (default) changes nothing and reports every piece (reference_id, prefab_name, position, rotation_deg, target_prefab_name, target_rotation_deg, rotation_kept, cost in coils, network_id; up to limit, default 200), by_prefab (count, cost_each, cost_total, refund_each, max_power_w_before/after), kept_pieces and unmatched_pieces with reasons, coils (needed, available, the stacks and where they are in the source), refund (the old coils deconstruction would give back), networks (loads, lowest_cable_max_w_before and lowest_cable_max_w_after: the new weakest cable, lowest fuse, devices), devices next to or mounted on the pieces with their networks, connectivity (the game's links now, the links predicted with every replacement in place; added and lost must be empty; model_matches_game proves the prediction method on these very pieces; mounted fuses and analysers; null when no piece has a replacement, since links are surveyed only around pieces to swap) and ready. Every problem is listed at once in problems (code, message, reference_id): e.g. unmatched_pieces (no replacement; skip_unmatched true leaves those pieces as they are), not_enough_coils, link_added, link_lost, connectivity_model_mismatch, mounted_device_turned, shape_model_mismatch, not_complete, rocket_internal, no_local_player, thing_not_found. A real run needs dry_run false AND confirm true; it is refused (status refused, nothing changed) unless the checks find nothing. It then returns a job (status waiting, job_id, preflight): the mod holds the game tick as a save does, runs every check again once the tick has stopped, swaps every piece in that one frame (replacement built first, old piece removed after, coils taken from the source as placing takes them), checks the result the next frame and lets the tick go. Poll with job_id alone: status applied (verification ok), applied_with_differences (verification lists each link, mount, device or network that differs), stopped (a piece failed: swapped lists old and new ids, not_swapped the rest, stopped_at the piece and whether it is intact; run the same call again to resume, since swapped pieces are then already heavy), applied_unchecked, or refused (final_check says why; nothing changed). from_id: the thing whose inventory gives the coils (default the local player; any container or a coil stack). refund (default true): the old coils, as deconstructing gives them, go into from_id's inventory (default the local player): first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, and only what nothing takes on the ground a metre in front of the holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground. Devices, APCs, batteries and transformers connect to any cable type (CableType is only read when placing); network ids stay the same. Host only (not_host on a client). No gateway is needed.",
            UpgradeSchema(["heavy", "super_heavy"], "heavy"),
            readOnly: false),
        Tool(
            "upgrade_pipes",
            "Replace normal pipe pieces with insulated pipe in place, gas pipe to insulated gas pipe and liquid pipe to insulated liquid pipe (never mixed), piece for piece as the kit's own merge placement builds them, keeping the network's gas and liquid: each replacement joins the network before its old piece leaves, so the network's Atmosphere (moles, energy, temperature) is never split, vented or divided; only its volume changes by the pieces' volume difference. Name a whole pipe network with network_id (from connections) or pieces with reference_ids (up to 4096). Pieces already insulated and non-pipe members (vents, drains, radiators) stay. The mapping, the dry run, the problems, the confirm, the job and its polling are exactly as upgrade_cables, with these pipe specifics: by_prefab gives max_pressure_kpa_before/after (Pipe.MaxPressure, the same for normal and insulated), volume_l_before/after and heat_exchange_factor_before/after (the prefab's ThermodynamicsScale; insulated pipe exchanges little or no heat with its surroundings); networks give content, total_mol, energy_j, temperature_k, volume_l_before/after, pressure_kpa_before/after (the same contents in the new volume) and lowest_max_pressure_kpa_after. Extra problems: burst_pipe (repair first), no_atmosphere, atmosphere_busy (a gas change is waiting for the next atmospherics tick: try again), content_mismatch, would_burst (the pressure after would exceed the lowest pipe rating). After a run the verification also checks that the network kept the same Atmosphere, the predicted volume and the same contents. Coils here are the pipe kits (Kit (Insulated Pipe), Kit (Insulated Liquid Pipe)); refund gives back the normal kits. to must be insulated (the default). Host only. No gateway is needed.",
            UpgradeSchema(["insulated"], "insulated"),
            readOnly: false),
        Tool(
            "clean_cables",
            "Tidy cable networks in place with one or more operations, each usable alone or together (operations, default [\"simplify_junctions\"]; they run in the order remove_dead_ends, remove_loops, split_long_straights or merge_straights, simplify_junctions, each on what the earlier ones leave). remove_dead_ends: removes stubs (one connected end) and isolated pieces (none), in rounds until no new stub appears (removing a stub can make its neighbour one); each removed piece lists its round. It stops at a stub whose connection is a device (device_connected) and never removes a piece a fuse, analyser or other device is mounted on (device_mounted; the game itself refuses to deconstruct a piece with an attached device), nor indestructible or rocket pieces; such dead ends are listed in dead_end_pieces with stopped_by. Removed pieces give back what deconstructing them would. remove_loops (never by default: a loop may be redundancy kept on purpose against a burnt cable): finds loops, groups of pieces joined to the rest in more than one way by the game's own connection rule, and breaks each by removing, one at a time, the shortest run of plain two-ended pieces whose two ends stay joined without it, until no cycle is left; a run holding a piece linked to a device, a piece with a device mounted, an indestructible, rocket or kit-less piece never goes, so no device loses a link and nothing is cut off; the junctions a cut leaves with an open end become the piece with only their connected ends (as simplify_junctions). keep_ids: pieces whose loops are spared whole. The report lists loops [{index, pieces [{reference_id, prefab_name, position, cut}], cut, spared, unbroken (why a cycle stays)}]. split_long_straights: each 3-, 5- or 10-long straight becomes one single straight per cell, same line and cells; it costs coils (e.g. 5 singles for a 5-long). merge_straights: runs of single straights of one grade, colour and owner in one line (no junction, device or mounted device inside) become the fewest long straights the coil offers that cover them exactly, longest first (10, 5, 3; leftovers stay); gives coils back (e.g. a 10-long costs less than 10 singles). split_long_straights and merge_straights together are refused. simplify_junctions: each piece with open ends becomes the piece of its own coil with only the connected ends, turned to match: a 3-way joining two neighbours becomes a straight (opposite) or a corner (adjacent); 4-, 5-, 6-ways and corner variants the smallest piece with exactly those ends. Every replacement is read from the loaded coil, never a table. The grade (normal, heavy, super heavy), colour and owner stay. Name a whole cable network with network_id (from connections) or pieces with reference_ids (up to 4096; only those pieces change, and pieces outside them only connect). DRY RUN BY DEFAULT: upgrade_cables' report (target minimal) where each piece gives operation (remove_dead_end, remove_loop, split_long_straight, merge_straights, simplify_junction), ends, connected_ends, replacement_count (0 for a removal), round (removals), merged_reference_ids (merges), cost (coils taken) and refund_count; dead_end_pieces {reference_id, prefab_name, position, reason dead_end or isolated, ends, connected_ends, stopped_by} lists dead ends left in place; kept_pieces and unmatched_pieces (no_smaller_piece, no_single_piece, no_long_piece, special_piece, no_kit; skip_unmatched true leaves those). Ends are world axes +x, -x, +y (up), -y, +z, -z. Connectivity: the links predicted after must equal the game's links now, counted per replaced group, less exactly the links of removed pieces; otherwise the run is refused (link_added, link_lost, device_link_lost), so no remaining neighbour or device loses a link and no networks merge. Coils: the new pieces' coil cost less the old ones' (the coil's own merge rule); a shortfall is taken from from_id's inventory (default the local player) and the run is refused with not_enough_coils if it holds too few; nothing is made for free; with refund (default true) surplus and removed pieces' materials go into from_id's inventory (default the local player): first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, and only what nothing takes on the ground a metre in front of the holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground. A real run needs dry_run false AND confirm true and is a job exactly as upgrade_cables (tick held, checks again, one-frame swap, verified the next frame: links, mounts, device networks, each network's devices and member count, which changes by exactly the planned additions and removals; swapped lists each old piece against the new piece in its cells, removed lists removed pieces); poll with job_id alone. Host only. No gateway is needed.",
            CleanSchema("cable"),
            readOnly: false),
        Tool(
            "clean_pipes",
            "clean_cables for pipe networks: the same five operations (remove_loops included, with keep_ids), alone or together, with the same order, reports, refusals, jobs and material rules, for every pipe grade (gas, liquid, insulated gas, insulated liquid; grade and content always stay; long pipes where the kit lists them). Contents: the network's gas or liquid stays in its own Atmosphere the whole time. Replacements join before old pieces leave, and a removed pipe leaves the network before it is destroyed, so its volume leaves and its share of the contents stays: moles and energy are unchanged and the pressure rises (networks show total_mol, energy_j, temperature_k, volume_l_before/after, pressure_kpa_before/after). The run is refused if that pressure would exceed the weakest remaining pipe (would_burst), and on burst_pipe, no_atmosphere, atmosphere_busy or content_mismatch. remove_dead_ends never deletes contents: the game's own removal of a network's last pipe divides its gas among no network (it is lost), so the pieces of a network that removal would empty while it still holds gas or liquid stay, with stopped_by holds_contents and the moles; an empty isolated pipe is removed. After a run the verification checks the same Atmosphere, the predicted volume, unchanged contents, links and member count. Kits instead of coils. Host only. No gateway is needed.",
            CleanSchema("pipe"),
            readOnly: false),
        Tool(
            "replace_walls",
            "Replace walls and windows in place with another wall or window prefab (to, required: e.g. StructureCompositeWall, StructureReinforcedWall, StructureCompositeWindow; iron wall to composite or reinforced wall or window, window to wall, and so on) without ever opening a face: with the game tick held, each old piece is marked as being destroyed, the new piece is built in its slot (owner and colour kept) and raised at once to its final build state, and only when it holds every slot the old one held and blocks what it blocked is the old one removed; nothing of the game ticks in between, so no gas moves and no room is re-evaluated until every piece stands. Name pieces with reference_ids (up to 4096) or a room with room_id (from rooms: every wall on a face of the room's cells); from_prefabs limits the pieces to those prefabs (the rest are kept as not_selected). Only exactly Wall or WallTransparent are taken or built: shuttered windows and their connectors, floors, ladder platforms and crew umbilical doors are kept as special_piece, and such a target is invalid_target. The target must be a loaded prefab that some kit builds (listed by a MultiConstructor), not a cursor. Footprint: the new wall must register in exactly the old one's slots (the same face points, in the same cell: its blockingGrids turned by the old rotation, and its CenterPosition on the same side) or the piece is unmatched as footprint_mismatch. Never open: if the old piece blocks air or gravity now, the target's final state must too (would_open refuses); a leaky old piece made airtight is allowed and reported as seals. Pressure: each face's current difference (the two cells' pressures, the planet's where a cell has no atmosphere) against the face's summed MaxPressureDelta with the new wall; at or above it the game would damage the new wall until it breaks, so the run is refused (would_overstress); above the new wall's stress mark (MaxPressureDelta x Thing.StressedRatio) the piece is flagged stressed (a warning); a face beside a frame is shielded (the game never stresses it). Kept (with a reason): already_at_target, special_piece, not_selected, indestructible, broken (a damaged build state), being_destroyed. DRY RUN BY DEFAULT: reports every piece (reference_id, prefab_name, position, rotation_deg, build_state, target_prefab_name, target_build_state, blocks_air_before/after, blocks_gravity_before/after, air_change keeps|seals|would_open, seals, stressed, faces [{position, cells a and b, pressure_kpa_a, pressure_kpa_b, difference_kpa, max_pressure_delta_kpa_before, max_pressure_delta_kpa_after, verdict ok|stressed|overstressed|shielded}], room_ids, materials [{prefab_name, cost, refund, net}]; up to limit, default 200), by_prefab, kept_pieces and unmatched_pieces with reasons, materials over the run (prefab_name, cost, refund, charge, give_back, available, stacks), rooms next to the pieces (room_id, cell_count, total_mol, energy_j) and ready. Every problem at once in problems: invalid_target, unmatched_pieces (skip_unmatched true leaves them), would_open, would_overstress, not_enough_materials (one per missing item), not_a_wall, thing_not_found, no_local_player, nothing_to_swap; room_not_found, too_many_pieces and not_host refuse the call. Materials by the game's deconstruct rule: each build state's ToolEntry and ToolEntry2 with their quantities, tools (welder, wrench, grinder) skipped; the new piece costs its states 0 to final, the old one gives back states 0 to its current one, netted per item per piece: the shortfall is taken from from_id (default the local player; its stacks at any slot depth) and the surplus given back with refund (default true), into from_id's inventory (default the local player): first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, and only what nothing takes on the ground a metre in front of the holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground; tool wear, welder fuel and battery are not charged. A real run needs dry_run false AND confirm true and is refused (status refused, nothing changed) unless the checks find nothing. It returns a job (status waiting, job_id, preflight): the tick is held, every check runs again once it has stopped, every piece is swapped in that one frame, then with the tick still held the result is checked (held_check: each new piece in the planned slots at its final state blocking as planned, old pieces gone, every room's and cell's air around the pieces exactly as before, since nothing ran), the tick is let go (status verifying) until the game has run its ticks and re-evaluated its rooms (at most 10 s), then held once more for the room check (room_check: each room next to a swapped piece under the same room id with the same cells, and total moles and energy within 1 % or 0.5 mol / 1 kJ; rooms with new ids there are new_rooms, not problems; problems room_gone, room_cells_changed, room_air_changed, and rooms_not_settled when the game did not run in time, e.g. paused). Poll with job_id alone: status applied, applied_with_differences, stopped (a swap failed: swapped lists old and new ids, not_swapped the rest, stopped_at the piece, why, piece_intact and rolled_back: a new piece that did not take every slot or did not seal is taken away and the old piece put back in its slots, so no face is ever left open), applied_unchecked, or refused (final_check says why). One swap job at a time across upgrade_*, clean_* and replace_* (busy). Multiplayer: host only; clients get the new walls through the game's own sync; players without the mod see normal walls. No gateway is needed.",
            ReplaceSchema("wall", targetRequired: true),
            readOnly: false),
        Tool(
            "replace_frames",
            "Replace frames in place with another frame prefab (to, e.g. StructureFrame steel or StructureFrameIron) or, without to, with each frame's own prefab at its final build state, which finishes unfinished frames in place (a frame already finished is kept as already_at_target). Everything as replace_walls: the same tick-held one-frame swap that never leaves a cell open (old piece marked as being destroyed, new frame built in its Center slot and raised to its final state, old one removed only once the new one holds every cell and blocks what it blocked; otherwise rolled back), the same dry run, confirm, job, polling, materials, refund, from_id, from_prefabs, limit, skip_unmatched, held_check and room_check. Only exactly Frame is taken or built (rocket towers are special_piece); the target must be a loaded prefab some kit builds. Scope: reference_ids, or room_id (every frame in a cell next to one of the room's cells, and frames inside the room that let gravity pass). Footprint: the same GridBounds cells at the old position and rotation (footprint_mismatch otherwise). Never open: a frame blocking air or gravity must be replaced by one whose final state blocks them too (would_open). Finishing a frame (the new one blocks air or gravity the old one let through, reported as seals) closes its cell: the run is refused with cell_occupied when anything else is in that cell (another structure, a small-grid piece such as a pipe, cable, chute or device inside it, a player, creature or loose item there); walls on the frame's faces are fine and must still be there after the swap (held_check face_structures_changed otherwise). As in the game when the last sheet is welded, a sealing frame's cell leaves its room and its gas is divided among its open neighbours; the room check expects those cells gone and allows that much gas to move. Frames are never pressure-stressed (the game only stresses face structures). Problems as replace_walls, with not_a_frame and cell_occupied instead of not_a_wall and would_overstress. Host only. No gateway is needed.",
            ReplaceSchema("frame", targetRequired: false),
            readOnly: false),
        Tool(
            "place_cables",
            "Lay a cable run, or one piece, the way a coil builds it, choosing for every small-grid cell (0.5 m) the one-cell piece of the grade whose ends exactly match that cell's connections (straight, corner, tee, corner3, cross, corner4, 5- and 6-way; turned as needed; read from the loaded coil, never a table; long straights are never used). Run: waypoints (positions in metres, joined by straight axis-aligned lines; the caller decides the route, nothing is searched; plan_cable_route finds one) or cells (every cell, each a neighbour of the one before), or piece {at, ends: [\"+x\", \"-y\", ...]} for a single piece. Positions snap to the small cell they fall in (centres on multiples of 0.5 m). grade: normal, heavy (default) or super_heavy. join: ends (default: the run's first and last cells also join every open cable end and device port pointing at them, and the cable straight ahead of a run end), none (only the run and extra_ends), all (every run cell joins every open end and port pointing at it). extra_ends [{at, toward}] add ends to run cells. Meeting existing cable: a run cell holding a cable keeps it when it already has the ends, else it is replaced by the piece of its own coil with its ends plus the run's (a straight crossed becomes a cross); a neighbour a run end joins that has no end towards it becomes a junction (the inverse of simplify_junctions); a piece with a fuse or analyser mounted, indestructible or rocket pieces are refused (cannot_change); a long straight is split (see below) or, with allow_split_long false, refused (long_piece). A run end with nothing to join gets a straight whose far end stays open (warning open_end). branches [{waypoints|cells, attach}]: side runs of a tree, each joining the run (or an earlier branch) at attach with a junction; their first cells join ports and ends as run ends do. A long straight the run must join in its middle or cross is split into singles in the same job (allow_split_long, default true; warning long_split; it is listed in removals and its cells as new singles of its own grade, colour and owner). would_loop (warning): a new link joins what is already joined another way (a run whose ends both reach one network); keep it only if the redundancy is meant, else plan with several starts or clean it later with clean_cables remove_loops. through_air (warning): new pieces on no frame and no wall plane (floating), listed; the report's air_cells counts them (0 for a run fully over frames or walls). Placement is checked as the game's cursor would (the server checks nothing itself): a cell with a device, chute or another small-grid thing is cell_blocked; a pipe in the cell blocks only along its own axis; frames and walls never block cables. remove_ids: pieces removed in the same job before building (a reroute: no power tick sees a device unpowered). assume_removed: ids of things (cables, or any other small-grid thing) checked as if already gone: their cells free, their links absent, cable pieces forecast as removed (listed in removals with assumed true, refund not counted); nothing of them is removed, and a real run is refused (assumed_present) while any still stands, so remove them first or pass the cable pieces as remove_ids; ids naming nothing standing are skipped. Guards, from a forecast of every network after the edit (links by the game's own connection rule; links.model_matches_game proves the model on these very pieces): would_bridge when the run would join two or more cable networks, or put two power ports of one device on one network (both sides of an APC or transformer, a battery's input and output), listing the networks and the devices on each; allowed only when allow_bridge names every network of that merge (or the device). would_split when removals split a network or leave a device port joined to nothing (allow_split). would_overload when a network after the edit would carry min(potential, required), pooled over what it joins, above its weakest cable, new pieces included (the game burns a cable every power tick). link_lost when a changed piece would lose a link. DRY RUN BY DEFAULT: cells [{at, action place|change|keep, prefab_name, rotation_deg, shape, ends, cost, refund, existing, joins [{toward, kind run|piece|port|open, thing, port}], open_end}], removals, materials {from, needed [{prefab_name, needed, available, stacks}], refund}, networks_before (cable_count, required_w, potential_w, actual_w, lowest_cable_max_w, lowest_fuse_break_w, devices), networks_after [{index, networks_before, new_pieces, devices [{device, port, bridging, network_before}], guard {potential_w, required_w, flow_w, lowest_cable_max_w, overloads}}], would_bridge [{kind networks|device, networks [{network_id, devices}], device, ports, allowed}], would_split, links {count_before, count_after, added, lost, model_matches_game}, problems, warnings, ready. Coils: each new piece costs its coil entry quantity, a change the difference (the coil's merge rule), taken from from_id (default the local player); not_enough_coils refuses; nothing is made for free; refund (default true) gives back what removed pieces and cheaper changes return, into from_id's inventory (default the local player): first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, and only what nothing takes on the ground a metre in front of the holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground. A real run needs dry_run false AND confirm true: a job (poll with job_id) that holds the game tick, runs every check again, removes, then builds every piece in one frame (changes as the coil's merge does, new pieces as a coil places them; the game merges networks itself), and verifies: links as predicted, the pieces of each forecast network on one network and different forecasts on different ones, every device port where forecast. Status applied, applied_with_differences, stopped, applied_unchecked or refused. One job at a time across upgrade_*, clean_*, replace_*, place_* and remove_*. Host only. No gateway is needed.",
            PlaceSchema("cable", ["normal", "heavy", "super_heavy"], "heavy"),
            readOnly: false),
        Tool(
            "remove_cables",
            "Remove cable pieces as wire cutters would: reference_ids, or the cable in each cell of waypoints or cells (positions in metres). Refused for a piece with a fuse or analyser mounted (the game refuses too), indestructible or rocket pieces. Guards and report as place_cables: would_split when a network would fall apart or a device port would be left joined to nothing, listing each part (networks_after) with its devices; allowed with allow_split. A network that only loses pieces keeps its id (pieces leave it before they are destroyed); a split is rebuilt by the game from the removed pieces' neighbours, with new ids. Refund (default true): what deconstructing gives back, into from_id's inventory (default the local player): first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, and only what nothing takes on the ground a metre in front of the holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground. Dry run by default; dry_run false and confirm true runs a tick-held job, polled with job_id. Host only. No gateway is needed.",
            RemoveSchema("cable"),
            readOnly: false),
        Tool(
            "place_pipes",
            "place_cables for pipes: the same run forms, joins, piece choice (from the kit of the grade: gas, liquid, insulated_gas or insulated_liquid; grade is required), placement check (a cable in the cell blocks only along its own axis), removals, dry run, job and checks, with the pipe guards. A pipe of other content is never joined (content_mismatch). would_bridge when the run would join two pipe networks (each listed in networks_before with content, total_mol, temperature, pressure, volume, main gases and devices) or put two pipe ports of one device on one network (a pump's or regulator's two sides); allow_bridge names the networks meant. would_burst when the pooled contents in the pooled volume (new pipes added, removed ones taken off) would exceed the weakest pipe. Removals never lose or move contents: removing every pipe of a network that holds gas or liquid is refused (holds_contents), and so is a removal that would split one (contents_would_move); empty it first. Placing into a network adds volume: contents stay, pressure falls. Kits instead of coils. Host only. No gateway is needed.",
            PlaceSchema("pipe", ["gas", "liquid", "insulated_gas", "insulated_liquid"], null),
            readOnly: false),
        Tool(
            "remove_pipes",
            "remove_cables for pipes, with the pipe guards: a removal that would empty a network holding gas or liquid (holds_contents) or split one (contents_would_move) is refused; otherwise removed pipes leave their network first, so its contents stay and the pressure rises (would_burst refuses). Refused for a pipe with a meter or other device mounted. Host only. No gateway is needed.",
            RemoveSchema("pipe"),
            readOnly: false),
        Tool(
            "place_chutes",
            "Lay a chute run, or one piece, from Kit (Chute) as a player builds it, choosing for every small-grid cell (0.5 m) the one-cell piece whose ends match that cell's connections: a straight or corner (1 kit each) or, where three ends meet in a T, a junction (2 kits) turned so its output faces downstream; chutes have no other shape (4 ends or a corner of three: no_piece_for_ends), and long straights, windows, valves, overflows, splitters, bins, inlets and outlets are never placed. Items travel along the run from its first cell to its last: start at the source (a device's chute Output port, a chute bin, a line that carries items towards it) and end at the sink (a device's chute Input port, a line that carries them away). The same run forms (waypoints, cells, piece {at, ends}), join (ends by default: the run's first and last cells join every open chute end and chute port pointing at them, and the chute straight ahead of a run end, which becomes a junction), extra_ends, remove_ids, dry run, job, materials and checks as place_cables. Flow guards, from the item flow of every chute network the edit touches (CODE: a straight or corner passes an item out of the end it did not come in by; a junction takes items in through its two inputs and lets them out only through its output; a device port with role Input takes items off the chute, Output pushes them in): flow_reversed when what the run joins pushes items against the run's direction (reverse the waypoints); flow_conflict when two flows would meet head-on or an item would have to enter a piece through its output (a junction merges, it cannot split a flow: that needs a splitter, which these tools do not build); flow_ambiguous when a junction is needed and nothing fixes which way it must face. Warnings: drops_items where items would leave an open end and fall to the ground (a run end with nothing to join gets a straight whose far end stays open: open_end); flow_unknown when no source or sink fixes the direction of new pieces. would_bridge when the run joins two chute networks (listing each one's chute count, riding items and devices), or two chute ports of one device (its output into its own input); allow_bridge names those meant. A piece with an item riding in it is never replaced (cannot_change) or removed. Placement is checked as the cursor would: chutes collide with cables, pipes, devices and other chutes in the same small cell (cell_blocked); frames and walls never block them. Each cell reports flow {in, out} (world axes). Chute network ids change after any removal or change (the game rebuilds them). Host only. No gateway is needed.",
            PlaceSchema("chute", ["chute"], "chute"),
            readOnly: false),
        Tool(
            "remove_chutes",
            "Remove chute pieces (straights, corners, junctions, and also valves, overflows and splitters) as a player's deconstruction does: reference_ids, or the chute in each cell of waypoints or cells (positions in metres). Refused for a piece with an item riding in it (cannot_remove: the game would lose the item with it; let it pass or take it out with move_item), indestructible or rocket pieces. would_split when a network would fall apart or a device port would be left joined to nothing (allow_split). drops_items warns where the removal leaves an end that items would now fall out of. Refund (default true): the kits the pieces were built from, into from_id's inventory (default the local player): first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, and only what nothing takes on the ground a metre in front of the holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground. The game rebuilds the networks around every removed chute, so they take new ids. Dry run by default; dry_run false and confirm true runs a tick-held job, polled with job_id. Host only. No gateway is needed.",
            RemoveSchema("chute"),
            readOnly: false),
        Tool(
            "place_structure",
            "Place any structure some kit builds (a light, sign, device, frame, wall, tank...) by prefab name or prefab hash, at a position with a turn, at a build state, with an optional label and colour; several placements are one job. Checked as the game's own placement cursor checks it (CanConstruct: blocked cells and faces, small-grid collisions, rocket cells, each class's own rules; a face-mounted piece needs its support; nothing loose, no creature or player inside a piece that fills its cell), and again just before each piece is built; a small-grid piece whose slot in a cell is taken is refused (a coil would merge, a plain build would stack). Position: at is a point in the cell, snapped as the cursor snaps it; a piece placed on a cell face (a wall: placement face) sits on the face opposite its facing. Turn: at most one of rotation [x, y, z] degrees (multiples of 90, Quaternion.Euler order), facing (+x, -x, +y up, -y, +z, -z) with optional up (default +y, or +z when facing is vertical), or face (face-placed pieces: the face of the cell it sits on; it faces into the cell). A grid-placed piece may only be turned about the axes its cursor turns it (invalid_rotation). build_state: finished (default), first (as a kit leaves it) or an index. Cost: every build state's items up to that state (state 0 is the kit), taken from the local player's inventory at any depth or from_id, as a kit's placement takes them; free: true places without materials and is refused unless the world is creative (not_creative). Cables, pipes and chutes are placed as exactly the piece given (warning network_piece; place_cables, place_pipes and place_chutes choose pieces by connections and guard merges). Refused per placement: invalid_prefab (not loaded, not a structure, no kit builds it, a rocket part), invalid_rotation, invalid_build_state, no_cursor, cannot_place (with the game's reason), not_labelable, not_paintable, invalid_color, overlaps_placement; for the run: not_enough_materials, not_creative. Dry run by default: each placement's snapped position, orientation (facing, up, Euler), face, build state, cost, and the materials needed and held. A real run needs dry_run false and confirm true and returns a job_id; poll with job_id alone. The job holds the game tick, runs every check again, builds in one frame, then verifies each piece stands with its prefab, position, turn, state, label and colour (status applied, applied_with_differences or stopped with stopped_at). Host only. No gateway is needed.",
            PlaceStructureSchema(),
            readOnly: false),
        Tool(
            "remove_structure",
            "Remove structures by reference id (up to 256, one job) as deconstructing them by hand would, giving back what that gives back: every build state's items down to the kit, to the source (refund_to source, the default: into from_id's inventory (default the local player): first onto matching stacks anywhere in it (belts, backpack, jetpack, suit and uniform storage, a stack in a hand), then as new stacks into empty slots that take the item, and only what nothing takes on the ground a metre in front of the holder, at rest (never inside the player); refunded lists each part with where: merged, slot or ground), on the ground where each piece stood (ground), or not at all (none). Minimal guards, each naming its reason. Refused: not_a_structure (items: use move_item), being_destroyed, indestructible, rocket, broken (a damaged state), game_refuses (the game's own CanDeconstruct), has_mounted (a device mounted on it). Refused unless allowed: holds_items and holds_gas (allow_contents: items drop where it stood as in the game; a tank lets its gas out into its cell, other devices lose it), would_breach (it blocks air and removing it joins spaces whose pressures differ by 1 kPa or more, e.g. a wall of a pressurised room; allow_breach). Warned: port_left_open (a device end that joins a cable, pipe, chute or device now). Cable, pipe and chute pieces are removed as remove_cables, remove_pipes and remove_chutes remove them (a network kept whole keeps its id and contents) and their checks apply: would_split is only a warning here, holds_contents and contents_would_move are lifted by allow_contents, the rest refuse; those tools also take cells and waypoints. Dry run by default (each piece, its refund, the total refund, problems and warnings); a real run needs dry_run false and confirm true and returns a job_id; poll with job_id alone. The job holds the game tick, checks again, removes everything in one frame, delivers the refund and verifies every piece is gone. Host only. No gateway is needed.",
            RemoveStructureSchema(),
            readOnly: false),
        Tool(
            "grid_survey",
            "Read the grid around a place before routing: the 2 m cells of a box (min and max, positions in metres) or of a room (room_id from rooms), a page at a time (limit default 27, max 125; offset). Each cell: at (its centre, odd metres), room_id (null outside), frame {reference_id, prefab_name, build_state, build_states, blocks_air, blocks_gravity}, walls [{face +x..-z, reference_id, prefab_name, blocks_air}] and small: 64 characters for its small-grid cells (0.5 m) at -1, -0.5, 0, +0.5 m from the centre on each axis (index 0 to 3; index 0 lies on the cell's minimum face plane, shared with the neighbour), character index x + 4y + 16z: '.' empty, 'c' cable, 'p' pipe, 'b' both, 'h' chute, 'd' device, 'o' another small-grid thing, 'r' a rocket's cell (legend repeats this); support: the same 64 cells by what holds a piece there up: 'i' inside a frame (every 2 m cell the small cell touches holds a frame, so a piece there is hidden in the frame's body), 'e' a frame edge or corner, 'f' on a frame's face (a frame's top face is the minimum plane of the cell above), 'w' on a wall's plane, 'a' air (plan_*_route frames_first avoids 'a'). Then pieces [{reference_id, kind cable|pipe|chute, prefab_name, at, cells (long pieces), ends (world axes), network_id, grade; chutes also flow {in, out} (which ends take items in and let them out, where a device port or directed piece fixes it) and carries (the item riding in it)}], devices [{reference_id, prefab_name, display_name, at, ports [{index, at (the cell a piece joining it stands in), toward (the end that piece needs, into the device), type (PowerAndData, Pipe, Chute...), role (Input, Output...), network_id}]}] and networks (include_networks, default true: cable networks with loads and lowest ratings, pipe networks with contents, pressure and gases, chute networks with chute_count, items riding and devices). network_visibility [{network_id, kind, pieces, cells {inside, frame_surface, wall, air}, air_at (floating cells, up to 32), refund}]: per network of the pieces listed, their cells by visibility and the floating ones. include_refund (default false): each piece's refund and each network's total, what remove_* would give back (read only; plan_removal adds would_split). Placement rules (CODE): frames and walls never block cables or pipes, so runs may pass through frame cells and along wall planes; a device, chute or 'o' blocks both; a pipe blocks a cable only along its own axis (their ends would meet), and the other way round; a chute is blocked by any cable, pipe, device, chute or 'o' in its cell. Read only. No gateway is needed.",
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
            "Find a cable route on the small grid and dry-run it. from and to: {at: [x, y, z]} (a cell; a cable there is joined), {reference_id} of a cable (its cell; leaving through one of its open ends is free, another direction makes it a junction) or {reference_id, port} of a device (the cell a piece joining that port stands in; port may be left out when the device has one cable port). Or reroute: {reference_ids: the old run's pieces} or {between: [end, end]} (the shortest run between two devices or pieces on one network; an end is a reference id or {reference_id, port} of a device; a device on several networks of the kind, such as an APC's input and output, uses the one network both ends share, and when they share none or several the error (not_on_one_network, ambiguous_port) lists each end's ports and networks so a port can be named): the old run must meet the rest at exactly two cells; the new route runs between them with the old pieces removed in the same job. The search (A*) never passes through a cell holding cable (it would join it), a device, chute or other thing, nor along a pipe's axis in a cell; frames and walls never block. Rules: frames_first (default true: a cell in air, on no frame and no wall plane, costs as much as 50 more supported cells, so a route over frames or along walls wins whenever the search box holds one, even a much longer one; only when none exists is a route through air returned, with the fewest air cells, and notes say through_air; false drops the rule); prefer none|frame_edges (small cells on two or three face planes of a frame cell they touch: its edges and corners, the top edges of a beam included)|walls (on or beside a wall's plane)|hidden (graded by visibility, a softer inside_frames that never gives no_route for it: a cell inside a frame costs 1, on a frame's surface or edge 3, on a wall's plane only 5, in air 9, plus frames_first's air penalty); inside_frames (only cells inside a frame cell or on its surface, judged over every 2 m cell a small cell touches as frames_first does: the top of a beam counts; a wall plane alone does not); avoid_room_interior (extra cost for room cells on no face plane); avoid_walkways (extra cost for room cells above the floor plane on no vertical face plane); avoid_networks (true: never beside another cable network than the ends' own; or a list of network ids); min_bends (turns cost much more); axis_order any|vertical_first|horizontal_first; max_length (cells, default 400); margin_m (search box around the ends, default 6, max 32). grade (default heavy), join, allow_bridge, allow_split and from_id pass on to the dry run. assume_removed: ids of things to plan as if already gone (their cells free, their links absent, never joined or avoided), e.g. old cable around a port that a refactor will remove: the cable pieces among them go into place_arguments.remove_ids, so the dry run and the job build and remove together (the guards see the final networks); other things (a pipe, a device) are only freed for the plan and go into place_arguments.assume_removed (remove them first with their own tool); route.assumed_removed lists pieces, others, missing (already gone) and in_the_way (pieces whose cells the route takes: they must go in the same job or before; with none in the way the route could also be built first and the old pieces removed later, by dropping remove_ids). Several starts (up to 16; from: {reference_id, ports: [..]} or an array) grow one tree: the first start to the target, each other start to the nearest cell of the tree so far (a branch joined by a junction; a start the tree already passes gets an extra end), never to the network a second time, so a device with separate power and data ports gets one run instead of two parallel runs and a loop. to may also be {network_id} (the nearest cell of any piece of that network) or a long straight (any of its cells; the place tool splits it in the same job, allow_split_long, default true). Bus mode: trunk {waypoints|cells} instead of to (e.g. a trunk planned with this tool, not built yet): the trunk is laid as given and every start (up to 16) branches to the nearest cell of the tree so far, junctions included, so a trunk and its drops are one job and one guard forecast; the trunk's own ends join what they meet (join). would_loop in the dry run means the route joins what is already joined another way: with join all the planner tries once more keeping away from the ends' own networks; when the start and target are already on one network it says so in notes. Returns found, route {waypoints, length, bends, cost, expanded, removes, air_cells (new cells in air: 0 for a clean route), air (their positions, when any), branches [{waypoints, length, attach}], extra_ends, visibility {inside, frame_surface, wall, air} (the new cells by how visible a piece in them is), assumed_removed, removal_refund (what the removed pieces give back)}, place_arguments (for place_cables: add dry_run false and confirm true to build) and dry_run (place_cables' own report with every guard), or failure (no_route, too_long, search_limit). Read only. Host only. No gateway is needed.",
            RouteSchema("cable", ["normal", "heavy", "super_heavy"]),
            readOnly: true),
        Tool(
            "plan_pipe_route",
            "plan_cable_route for pipes: the same ends, reroute, rules and search (never through a cell holding a pipe, nor along a cable's axis in a cell), dry-run with place_pipes and its pipe guards. grade is required (gas, liquid, insulated_gas, insulated_liquid). Read only. Host only. No gateway is needed.",
            RouteSchema("pipe", ["gas", "liquid", "insulated_gas", "insulated_liquid"]),
            readOnly: true),
        Tool(
            "plan_chute_route",
            "plan_cable_route for chutes: from is the source and to the sink, since items travel from the route's first cell to its last. Ends: {reference_id, port} of a device's chute port (connections or grid_survey lists each port's index and role: an Output port or a chute bin is a from, an Input port a to; a device with one chute port needs no port), {reference_id} of a chute (leaving through one of its open ends is free, another direction makes it a junction, which only works where the route merges into a line: to), or {at}. The same rules and search as plan_cable_route (never through a cell holding a chute, cable, pipe, device or other small-grid thing; frames and walls never block), reroute keeps the old run's direction where the flow fixes it, and the dry run is place_chutes' own with every flow guard. grade is chute (the default). Read only. Host only. No gateway is needed.",
            RouteSchema("chute", ["chute"]),
            readOnly: true),
        Tool(
            "plan_removal",
            "Price a removal without doing it: the dry run of remove_cables, remove_pipes or remove_chutes (kind cable, the default, pipe or chute) under a read-only name. reference_ids, waypoints, cells (positions in metres) or network_id (every piece of that network, up to 1024). Returns the remove tools' own report: removals (each piece with its refund), materials.refund (the total remove_* would give back), would_split (networks that would fall apart, ports left joined to nothing), networks_before and networks_after, problems (cannot_remove...) and warnings. Changes nothing, ever. Host only. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "cable", "pipe", "chute" }, description = "Default cable." },
                    reference_ids = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = "Pieces to price. Give one of reference_ids, waypoints, cells or network_id." },
                    waypoints = new { type = "array", minItems = 1, maxItems = 1024, items = new { description = "[x, y, z] or {x, y, z} in metres." }, description = "The pieces in every cell along these straight lines." },
                    cells = new { type = "array", minItems = 1, maxItems = 1024, items = new { description = "[x, y, z] or {x, y, z} in metres." }, description = "The piece in each cell." },
                    network_id = new { type = "string", description = "Every piece of this network." },
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
            "Trace how a network feeds its devices from a root device (an APC's output, a generator): kind cable (default), pipe or chute; root (reference id); network_id or port (the root's port) when the root is on several networks of the kind (an APC's input and output). Breadth first over the network's links (the shortest path to each device; a device is an end, never passed through). Returns root, network_id, root_room_id, devices [{reference_id, prefab_name, display_name, room_id, pieces (between the root and the device), rooms (the rooms those pieces pass through in order; pieces in no room, e.g. inside a floor frame or outdoors, are skipped), through (rooms neither the root's nor the device's own), fed_through_other_rooms}], daisy_chains (how many devices are fed through another room), rooms [{room_id, devices, entries (the pieces where the feeds enter the room), entry_at, multiple_feeds}], multiple_feeds, unreached (devices on the network no run from the root reaches). A piece is in the room of a 2 m cell it touches (its own cell first); a device in the room of its grid cell (as rooms). Read only. No gateway is needed.",
            new
            {
                type = "object",
                properties = new
                {
                    root = new { type = "string", description = "The device the network is fed from." },
                    network_id = new { type = "string", description = "The network to trace, when the root is on several." },
                    port = new { type = "integer", minimum = 0, maximum = 64, description = "Or the root's port whose network to trace (connections lists them)." },
                    kind = new { type = "string", @enum = new[] { "cable", "pipe", "chute" }, description = "Default cable." }
                },
                required = new[] { "root" },
                additionalProperties = false
            },
            readOnly: true),
        Tool(
            "trader_buy",
            "Buy from the trader that has landed and is ready at the landing pad (any other contact is refused with not_landed, naming the landed one), exactly as the trade window's Buy button does on the host (TradeDataHelper.BuyItem): the card is charged, the trader's stock drops, and the goods are made straight into the first empty tradable slots of the vending machines on the pad's data network, then of the card holder's inventory, stacked up to their maximum (gas goes into the pad network's atmosphere). The price per unit is the trader's, divided by the respawn stress penalty while you have respawn stress, as the window shows it. Returns {reference_id (the contact), name, dry_run, credit_card_id, credits_before, credits_after, results, count, success_count, error_count, delivered, gas_atmosphere_id}: results has one entry per line, {index, ok, name, prefab_name, gas, quantity, credits_each, credits_spent, stock_after}, or {index, ok: false, name, prefab_name, quantity (bought before the game stopped), credits (paid for those), error {code, message}}; delivered lists each new item where it landed, {reference_id (vending machine or player), slot, item {reference_id, prefab_name, display_name}, quantity}. A dry run checks each line on its own against the stock, the card and free slots; it does not add lines up. The game keeps no trader currency. Refusals for the whole trade: not_host, contact_not_found, not_landed (not landed or not ready at a pad), pad_unavailable (pad off or in error), card_not_found, no_credit_card. Per line: not_sold, ambiguous_item, insufficient_stock, insufficient_credits, no_room, trade_failed (the game's own message, e.g. an incomplete trade when the slots ran out: it charges only for what it delivered). No gateway is needed.",
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
            "Sell to the trader that has landed and is ready at the landing pad (any other contact is refused with not_landed), exactly as the trade window's Sell button does on the host (TradeDataHelper.SellItem): the trader must still want the item, the goods are taken from the vending machines on the pad's data network and then the card holder's inventory (whole stacks destroyed, the last one trimmed; gas taken from the pad network's atmosphere), and the card is paid. The price per unit is the trader's, times the respawn stress penalty while you have respawn stress. Returns {reference_id (the contact), name, dry_run, credit_card_id, credits_before, credits_after, results, count, success_count, error_count, delivered, gas_atmosphere_id}: results has one entry per line, {index, ok, name, prefab_name, gas, quantity, credits_each, credits_earned, wanted_after}, or the same refused shape as trader_buy. delivered is always empty. Refusals: as trader_buy, and per line not_wanted (not bought, or more than the trader still wants), insufficient_available (not enough on the pad's network and on you that meet the trader's conditions), trade_failed. No gateway is needed.",
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
            "Paste a BlueprintMod blueprint at an exact place and turn, with no player needed (console bppaste takes both from the local player, so it cannot run on a dedicated server). Needs BlueprintMod loaded; host only. Makes the D.B.P.U.'s own call: the blueprint's copy angle plus rotation 0, 90, 180 or 270, so every piece lands on the grid. Three forms. Paste: name (a file in BlueprintMod's Blueprints folder, with or without .blueprint, or an absolute path), anchor [x, y, z] (the world position in metres where the blueprint's own reference point lands: the large-grid point BlueprintMod snapped the copying player to, x and z odd whole metres, y even; a paste lines up with the grid when the anchor is such a point) and rotation (default 0); returns {started: true, file (full path), entries, anchor, rotation, copy_y_angle, expected_duration_s}. The pieces are then placed over 2 to 30 s (0.15 s per entry) by BlueprintMod's coroutine, so the reply comes before they exist. status: true alone: progress of the last paste this tool started, {known (false until this tool has started one; the other fields are then null), active (still placing), complete, cancelled, created, failed, skipped, pasted (things kept for undo), fingerprint, file, entries, other_active (a paste this tool did not start is running)}; the counts are kept after the paste ends, and are null when the paste finished inside the call that started it. undo: true alone: BlueprintMod's bpundo (cancels a running paste and removes what it placed, else removes the last completed paste) and returns {message}, its own answer. Rooms are not re-evaluated after a paste or an undo: run the console command regeneraterooms before rooms. In survival BlueprintMod charges DeanamicMatter from the local player, which a dedicated server does not have, so use a creative world there. Errors: invalid_argument (a missing file names the path it looked at; a blueprint with no entries; a rotation not 0, 90, 180 or 270), paste_refused (BlueprintMod's own message verbatim: the same blueprint already pasted at this position and rotation, no local player in survival, not enough DeanamicMatter), blueprint_failed (a BlueprintMod call threw: its exception type and message, e.g. a file that is not a blueprint), mod_missing (BlueprintMod is not loaded), game_changed (BlueprintMod no longer has a member this tool calls, named), not_host. No gateway is needed.",
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
            readOnly: false)
    ];

    private static object CleanSchema(string kind)
    {
        return new
        {
            type = "object",
            properties = new
            {
                network_id = new { type = "string", description = $"A {kind} network id from connections: every piece of it. Give this or reference_ids." },
                reference_ids = new { type = "array", minItems = 1, maxItems = 4096, items = new { type = "string" }, description = $"{kind} pieces to clean up, by reference id. Give this or network_id." },
                keep_ids = new { type = "array", minItems = 1, maxItems = 4096, items = new { type = "string" }, description = "remove_loops only: pieces whose loops are spared whole (a loop's pieces are listed in a dry run)." },
                operations = new { type = "array", minItems = 1, maxItems = 5, items = new { type = "string", @enum = new[] { "remove_dead_ends", "remove_loops", "split_long_straights", "merge_straights", "simplify_junctions" } }, description = "What to do, alone or together (not split_long_straights with merge_straights). Default [\"simplify_junctions\"]." },
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
                to = new { type = "string", description = targetRequired ? $"Required: the prefab name of the new {kind} (e.g. StructureCompositeWall)." : $"The prefab name of the new {kind}. Default each {kind}'s own prefab, which finishes unfinished {kind}s." },
                from_prefabs = new { type = "array", minItems = 1, maxItems = 64, items = new { type = "string" }, description = "Only pieces of these prefab names are replaced; the others are kept as not_selected." },
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
                waypoints = new { type = "array", minItems = 1, maxItems = 1024, items = position, description = "Points joined by straight axis-aligned lines, both ends included. Give one of waypoints, cells or piece." },
                cells = new { type = "array", minItems = 1, maxItems = 1024, items = position, description = "Every cell of the run, each a neighbour of the one before." },
                piece = new { type = "object", properties = new { at = position, ends = new { type = "array", minItems = 1, maxItems = 6, items = new { type = "string", @enum = axes } } }, required = new[] { "at", "ends" }, description = "One piece at a cell with these ends (a neighbour without an end back becomes a junction)." },
                grade = new { type = "string", @enum = grades, description = defaultGrade != null ? $"Default {defaultGrade}." : "Required." },
                join = new { type = "string", @enum = new[] { "ends", "none", "all" }, description = $"How the run joins existing {kind} ends and device ports. Default ends." },
                branches = new { type = "array", maxItems = 16, items = new { type = "object", properties = new { waypoints = new { type = "array", items = position }, cells = new { type = "array", items = position }, attach = position } }, description = "Side runs of a tree: each {waypoints or cells, attach}: its first cell is a free end (joins ports and ends around it as a run end does), its last cell joins the cell attach names (a cell of the run or an earlier branch next to it; default the first found), which becomes a junction. plan_*_route with several starts fills this in." },
                allow_split_long = new { type = "boolean", description = "Default true: a long straight (3, 5 or 10 cells) the run must join in its middle or cross is split into singles in the same job (same line, grade, colour and owner; removal refund and single costs as split_long_straights) and then joined; warning long_split. false refuses with long_piece." },
                extra_ends = new { type = "array", maxItems = 64, items = new { type = "object", properties = new { at = position, toward = new { type = "string", @enum = axes } }, required = new[] { "at", "toward" } }, description = "Extra ends on run cells." },
                remove_ids = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = $"{kind} pieces removed in the same job before building (a reroute)." },
                assume_removed = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = "Things checked as if already gone (their cells free, their links absent) but not removed; a real run is refused while any still stands (assumed_present)." },
                allow_bridge = new { type = "array", minItems = 1, maxItems = 64, items = new { type = "string" }, description = "Network ids (every network of a merge) and device ids whose joining is meant." },
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
        object position = new { description = "[x, y, z] or {x, y, z} in metres: a point in the cell; snapped as the cursor snaps it." };
        string[] axes = ["+x", "-x", "+y", "-y", "+z", "-z"];
        object prefab = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } }, description = "Prefab name (e.g. StructureWallLight, as find_things and looking_at report them) or prefab hash." };
        object rotation = new { type = "array", minItems = 3, maxItems = 3, items = new { type = "number" }, description = "[x, y, z] degrees, each a multiple of 90, Quaternion.Euler order (z, then x, then y). Give at most one of rotation, facing and face." };
        object facing = new { type = "string", @enum = axes, description = "Where the piece's front points." };
        object up = new { type = "string", @enum = axes, description = "Where its top points, with facing or face; default +y, or +z when facing is vertical." };
        object face = new { type = "string", @enum = axes, description = "For pieces placed on a cell face (walls): the face of the cell holding at that it sits on; it faces into the cell." };
        object buildState = new { oneOf = new object[] { new { type = "string", @enum = new[] { "finished", "first" } }, new { type = "integer", minimum = 0 } }, description = "finished (default), first (as a kit leaves it) or a state index." };
        object label = new { type = "string", description = "A name, as the Labeller writes it (devices, signs, tanks...)." };
        object color = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } }, description = "A colour name or index (paint lists them)." };
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
                        properties = new { prefab, at = position, rotation, facing, up, face, build_state = buildState, label, color },
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
                build_state = buildState,
                label,
                color,
                from_id = new { type = "string", description = "The thing whose inventory pays. Default the local player." },
                free = new { type = "boolean", description = "Place without materials; creative worlds only (not_creative otherwise). Default false." },
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
                refund_to = new { type = "string", @enum = new[] { "source", "ground", "none" }, description = "Default source (from_id or the local player)." },
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
        object end = new { type = "object", description = $"{{at: [x, y, z]}}, {{reference_id}} of a {kind} piece, or {{reference_id, port}} of a device." };
        object from = new { description = $"A start: {{at}}, {{reference_id}} of a {kind} piece or {{reference_id, port}} of a device; or several starts (up to 16) as one tree: {{reference_id, ports: [..]}} (several ports of one device) or an array of starts. The first start routes to the target; each other start routes to the nearest cell of the tree so far and joins it with a junction, so a device with separate ports gets one run, never a loop." };
        object to = new { type = "object", description = $"{{at}}, {{reference_id}} of a {kind} piece (a long straight: any of its cells, split in the same job), {{reference_id, port}} of a device, or {{network_id}}: the nearest cell of any piece of that network." };
        return new
        {
            type = "object",
            properties = new
            {
                from,
                to,
                reroute = new { type = "object", description = "{reference_ids: [...]} or {between: [end, end]} (an end is a reference id or {reference_id, port} of a device): replace an old run; leave out from and to." },
                grade = new { type = "string", @enum = grades },
                frames_first = new { type = "boolean", description = "Default true: prefer any route over frames or along walls to one through air (a cell on no frame and no wall plane); through air only when no supported route fits the search box. false: no preference." },
                prefer = new { type = "string", @enum = new[] { "none", "frame_edges", "walls", "hidden" }, description = "hidden: least visible route (inside a frame 1, frame surface 3, wall plane 5, air 9 per cell)." },
                assume_removed = new { type = "array", minItems = 1, maxItems = 1024, items = new { type = "string" }, description = "Things to plan as if already gone; the kind's pieces among them are removed in the same job (place_arguments.remove_ids)." },
                trunk = new { type = "object", description = "Bus mode, instead of to: {waypoints} or {cells} of a trunk laid as given; every start branches from it (one job).", properties = new { waypoints = new { type = "array", items = new { description = "[x, y, z] or {x, y, z} in metres." } }, cells = new { type = "array", items = new { description = "[x, y, z] or {x, y, z} in metres." } } } },
                inside_frames = new { type = "boolean" },
                avoid_room_interior = new { type = "boolean" },
                avoid_walkways = new { type = "boolean" },
                avoid_networks = new { description = "true, or a list of network ids to keep away from." },
                min_bends = new { type = "boolean" },
                axis_order = new { type = "string", @enum = new[] { "any", "vertical_first", "horizontal_first" } },
                max_length = new { type = "integer", minimum = 2, maximum = 1024 },
                margin_m = new { type = "number", description = "Search box margin around the ends, default 6, max 32." },
                join = new { type = "string", @enum = new[] { "ends", "none", "all" } },
                allow_bridge = new { type = "array", items = new { type = "string" } },
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
                network_id = new { type = "string", description = "A network id from connections: every piece of it. Give this or reference_ids." },
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
            ["within_id"] = new { type = "string", description = "Only items inside this thing at any depth (a locker, crate, suit, backpack or player reference ID), and a machine's own stock." },
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
            ["gateway_id"] = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
                reference_id = new { type = "string", description = IcHolderIdDescription },
                target_reference_ids = ReferenceIdArraySchema("Optional visible target devices for which stable selectors should be evaluated; defaults to every visible device.")
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
            ["gateway_id"] = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                gateway_id = new { type = "string", description = "Optional filter. Omit, or pass 'world', for any device in the world; a StationGod Gateway id from list_gateways limits the call to devices on that gateway's data networks." },
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
                interval_seconds = new { type = "number", minimum = 0.05, maximum = 5.0, description = "Sampling interval; defaults to 0.5 seconds. At most 120 samples may be requested." }
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
