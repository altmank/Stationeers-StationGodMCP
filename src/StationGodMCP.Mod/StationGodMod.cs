#nullable enable

using System;
using System.IO;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using StationeersMods.Interface;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP;

/// <summary>
/// The mod: at load it checks every reflected game member (GameMembers.CheckAll), applies each Harmony patch class,
/// reads the pipe and remote settings and registers the gateway prefab. Every frame on a host (NetworkManager.IsServer)
/// it keeps the named pipe (and, when configured, the TCP transport) listening and runs the queued requests on the
/// main thread.
/// </summary>
[StationeersMod(ModId, DisplayName, Version)]
public sealed class StationGodMod : ModBehaviour
{
    public const string ModId = "net.xceled.stationeers.stationgodmcp";
    public const string DisplayName = "StationGod MCP";
    public const string Version = "1.10.0";

    private static readonly DeadlineWatch Deadlines = new DeadlineWatch();

    private readonly StationGodRequestDispatcher _dispatcher = new StationGodRequestDispatcher(Deadlines);
    private Harmony? _harmony;
    private IDisposable? _pipeServer;
    private StationGodTcpServer? _tcpServer;
    private RemoteSettings? _remote;
    private ServerSettings _server = ServerSettings.Defaults;

    /// <summary>Real time since the mod loaded (mod_info runtime uptime_s).</summary>
    internal static System.Diagnostics.Stopwatch SinceLoad { get; } = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The local pipe's name, read once at load ([Pipe] Name, STATIONGODMCP_PIPE_NAME).</summary>
    internal static PipeName Pipe { get; private set; } = PipeName.Default;

    public override void OnLoaded(ContentHandler contentHandler)
    {
        base.OnLoaded(contentHandler);

        try
        {
            _harmony = new Harmony(ModId);
            GameMembers.CheckAll();
            PatchEachClass(_harmony);
            ConfigFile configuration = new ConfigFile(ConfigPath, true);
            Pipe = PipeSettings.Load(configuration);
            _remote = RemoteSettings.Load(configuration);
            _server = ServerSettings.Load(configuration);
            ProtocolLog.InfoSink = Log;
            ProtocolLog.WarningSink = LogWarning;
            ProtocolLog.ReplyWritten = MethodStats.RecordReply;
            Api.Shared.Game.Runs.LayoutSettings.Load(configuration);
            PerformanceSettings.Load(configuration);
            Api.ApiHost.Prepare();
            Prefab.OnPrefabsLoaded += RegisterPrefabs;
            if (Prefab.AllPrefabs != null && Prefab.AllPrefabs.Count > 0)
            {
                RegisterPrefabs();
            }

            Log("Loaded and waiting for vanilla prefabs.");
        }
        catch (Exception exception)
        {
            // BepInEx's ConfigFile and the game's Prefab list at load: the mod logs it and does nothing more.
            LogError("Failed during mod initialization", exception);
        }
    }

    // Each patch class on its own, so a missing target (already logged by GameMembers.CheckAll) costs only the patch
    // that needs it; PatchAll would stop at the first failure and leave the rest unpatched.
    private static void PatchEachClass(Harmony harmony)
    {
        foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(StationGodMod).Assembly))
        {
            if (!type.IsDefined(typeof(HarmonyPatch), false))
            {
                continue;
            }

            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception exception)
            {
                // Harmony's PatchClassProcessor.Patch on a target this game build changed: that patch is skipped.
                LogWarning($"Could not apply patch {type.Name}: {exception.Message}");
            }
        }
    }

    private void Update()
    {
        try
        {
            WorldStores.Tick();
            if (!NetworkManager.IsServer)
            {
                StopServers();
                return;
            }

            _pipeServer ??= StartPipe();

            if (_remote != null && _remote.Enabled && _tcpServer == null)
            {
                StartTcpServer(_remote);
            }

            _dispatcher.ProcessPendingRequests(
                FrameBudget.For(PerformanceSettings.RequestBudgetMs, HeldTickJobs.HoldsTick));
            HeldTickJobs.Tick();
            Previews.Tick();
            Highlights.Tick();
            RocketFlightRecorder.Tick();
        }
        catch (Exception exception)
        {
            // The pipe's start and the request queue, every frame: logged, and tried again next frame.
            LogWarning($"MCP bridge update failed: {exception.Message}");
        }
    }

    private void RegisterPrefabs()
    {
        PrefabRegistrar.TryRegister(this);
    }

    private void OnDestroy()
    {
        Prefab.OnPrefabsLoaded -= RegisterPrefabs;
        StopServers();
        GatewayRegistry.Clear();
        IcExecutionController.Clear();
        HeldTickJobs.Abandon();
        Previews.Clear();
        Highlights.Clear();
        _harmony?.UnpatchSelf();
    }

    private void StopServers()
    {
        _pipeServer?.Dispose();
        _pipeServer = null;
        _tcpServer?.Dispose();
        _tcpServer = null;
    }

    // The overlapped pipe (one reader and one writer per connection), unless [Server] OverlappedPipes is off, the
    // platform is not Windows, or the Windows calls it needs are missing: then today's synchronous pipe.
    private IDisposable StartPipe()
    {
        if (_server.OverlappedPipes && Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            try
            {
                ProtocolHost host = new ProtocolHost(new ProtocolSettings(_server.MaxPipeConnections), _dispatcher, Deadlines);
                PipeListener listener = new PipeListener(Pipe.Value, host);
                listener.Start();
                return listener;
            }
            catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
            {
                LogWarning($"Overlapped pipes are not available here ({exception.Message}); using the synchronous pipe.");
            }
        }

        StationGodPipeServer server = new StationGodPipeServer(Pipe.Value, _dispatcher);
        server.Start();
        return server;
    }

    private void StartTcpServer(RemoteSettings remote)
    {
        StationGodTcpServer? server = null;
        try
        {
            server = new StationGodTcpServer(remote.BindAddress, remote.Port, remote.Secret, _dispatcher);
            server.Start();
            _tcpServer = server;
        }
        catch (Exception exception)
        {
            // TcpListener.Start on a taken port or a bad address: remote stays off until the mod reloads.
            server?.Dispose();
            remote.Disable();
            LogWarning("Remote MCP listener could not start and has been disabled until the mod reloads: " +
                       exception.Message);
        }
    }

    internal static string ConfigPath => Path.Combine(Paths.ConfigPath, $"{ModId}.cfg");

    internal static void Log(string message)
    {
        Debug.unityLogger.Log(LogType.Log, $"[StationGodMCP] {message}");
    }

    internal static void LogWarning(string message)
    {
        Debug.unityLogger.Log(LogType.Warning, $"[StationGodMCP] {message}");
    }

    internal static void LogError(string message, Exception? exception = null)
    {
        string details = exception == null ? message : $"{message}: {exception}";
        Debug.unityLogger.Log(LogType.Error, $"[StationGodMCP] {details}");
    }
}

/// <summary>
/// The remote TCP transport's settings: BepInEx config (Remote MCP section of net.xceled.stationeers.stationgodmcp.cfg), each
/// overridable by an environment variable. Off without a secret.
/// </summary>
internal sealed class RemoteSettings
{
    private const string Section = "Remote MCP";
    private const string DefaultBindAddress = "0.0.0.0";
    private const int DefaultPort = 8765;
    private const int MinimumPort = 1;
    private const int MaximumPort = 65535;

    private RemoteSettings(bool enabled, string bindAddress, int port, string secret)
    {
        Enabled = enabled;
        BindAddress = bindAddress;
        Port = port;
        Secret = secret;
    }

    internal bool Enabled { get; private set; }

    internal string BindAddress { get; }

    internal int Port { get; }

    internal string Secret { get; }

    internal void Disable()
    {
        Enabled = false;
    }

    internal static RemoteSettings Load(ConfigFile configuration)
    {
        ConfigEntry<bool> enabled = configuration.Bind(Section, "Enabled", false,
            "Listen for authenticated StationGod MCP sidecars over plain TCP. Only enable this on the authoritative " +
            "server.");
        ConfigEntry<string> bindAddress = configuration.Bind(Section, "BindAddress", DefaultBindAddress,
            "IP address on which the remote StationGod MCP transport listens.");
        ConfigEntry<int> port = configuration.Bind(Section, "Port", DefaultPort,
            "TCP port used by remote StationGod MCP sidecars.");
        ConfigEntry<string> secret = configuration.Bind(Section, "Secret", string.Empty,
            "Shared secret required from every remote StationGod MCP sidecar. Sent over plain TCP.");
        RemoteSettings settings = new RemoteSettings(
            EnvironmentBoolean("STATIONGODMCP_REMOTE_ENABLED", enabled.Value),
            EnvironmentString("STATIONGODMCP_REMOTE_BIND_ADDRESS", bindAddress.Value),
            EnvironmentPort("STATIONGODMCP_REMOTE_PORT", port.Value),
            EnvironmentString("STATIONGODMCP_REMOTE_SECRET", secret.Value));
        if (settings.Enabled && string.IsNullOrEmpty(settings.Secret))
        {
            settings.Disable();
            StationGodMod.LogWarning(
                "Remote MCP is enabled but no secret is configured. Set Remote MCP/Secret in " +
                $"{StationGodMod.ConfigPath} or " +
                "STATIONGODMCP_REMOTE_SECRET; TCP listening is disabled.");
        }

        return settings;
    }

    private static bool EnvironmentBoolean(string name, bool fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        StationGodMod.LogWarning($"Ignoring invalid {name} value '{value}'. Expected true or false.");
        return fallback;
    }

    private static int EnvironmentPort(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (int.TryParse(value, out int parsed) && parsed >= MinimumPort && parsed <= MaximumPort)
        {
            return parsed;
        }

        StationGodMod.LogWarning(
            $"Ignoring invalid {name} value '{value}'. Expected a port between 1 and 65535.");
        return fallback;
    }

    private static string EnvironmentString(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value!;
    }
}

/// <summary>
/// [Server]: how local clients connect. MaxPipeConnections is the most pipe connections at once (each agent session,
/// the dashboard and every script keep one); OverlappedPipes false goes back to the synchronous pipe of 1.10 and
/// earlier (four connections, one request at a time each). Read once at load.
/// </summary>
internal sealed class ServerSettings
{
    private const string Section = "Server";

    private ServerSettings(int maxPipeConnections, bool overlappedPipes)
    {
        MaxPipeConnections = maxPipeConnections;
        OverlappedPipes = overlappedPipes;
    }

    internal static ServerSettings Defaults { get; } =
        new ServerSettings(ProtocolSettings.DefaultMaxPipeConnections, true);

    internal int MaxPipeConnections { get; }

    internal bool OverlappedPipes { get; }

    internal static ServerSettings Load(ConfigFile configuration)
    {
        ConfigEntry<int> connections = configuration.Bind(Section, "MaxPipeConnections",
            ProtocolSettings.DefaultMaxPipeConnections,
            new ConfigDescription(
                "The most local pipe connections at once; a client past it waits until one closes. Restart the game " +
                "to apply.",
                new AcceptableValueRange<int>(ProtocolSettings.MinimumPipeConnections, ProtocolSettings.MaximumPipeConnections)));
        ConfigEntry<bool> overlapped = configuration.Bind(Section, "OverlappedPipes", true,
            "Serve the pipe with overlapped I/O (reading and writing at once on each connection). false goes back to " +
            "the synchronous pipe of version 1.10 (four connections). Restart the game to apply.");
        int maximum = connections.Value;
        if (maximum < ProtocolSettings.MinimumPipeConnections || maximum > ProtocolSettings.MaximumPipeConnections)
        {
            StationGodMod.LogWarning($"Ignoring invalid [Server] MaxPipeConnections {maximum}; using " +
                                     $"{ProtocolSettings.DefaultMaxPipeConnections}.");
            maximum = ProtocolSettings.DefaultMaxPipeConnections;
        }

        return new ServerSettings(maximum, overlapped.Value);
    }
}

/// <summary>
/// [Performance] RequestBudgetMs: the main-thread milliseconds one frame may spend on requests (FrameBudget). Read once
/// at load; a negative or unreadable value falls back to the default with a warning.
/// </summary>
internal static class PerformanceSettings
{
    /// <summary>The configured budget; 0 = unlimited.</summary>
    internal static double RequestBudgetMs { get; private set; } = FrameBudget.DefaultMs;

    internal static void Load(ConfigFile configuration)
    {
        ConfigEntry<double> budget = configuration.Bind("Performance", "RequestBudgetMs", FrameBudget.DefaultMs,
            "Main-thread milliseconds one frame may spend answering requests; the rest wait for the next frame, in " +
            "order. The first request of a frame always runs. 0 = unlimited. While a job holds the game tick the " +
            $"budget is at most {FrameBudget.JobHeldMs} ms. Restart the game to apply.");
        double? configured = FrameBudget.Configured(budget.Value);
        if (configured == null)
        {
            StationGodMod.LogWarning(
                $"Ignoring invalid [Performance] RequestBudgetMs {budget.Value}; using {FrameBudget.DefaultMs} ms.");
        }

        RequestBudgetMs = configured ?? FrameBudget.DefaultMs;
    }
}

/// <summary>
/// The local named pipe's name: BepInEx config section Pipe, key Name, overridden by STATIONGODMCP_PIPE_NAME. Read
/// once at load; an invalid name falls back to the default with a warning.
/// </summary>
internal static class PipeSettings
{
    private const string EnvironmentName = "STATIONGODMCP_PIPE_NAME";

    internal static PipeName Load(ConfigFile configuration)
    {
        ConfigEntry<string> name = configuration.Bind("Pipe", "Name", PipeName.DefaultValue,
            @"Name of the local named pipe the mod listens on (\\.\pipe\<Name>). A second game or dedicated " +
            "server on the same machine needs its own name, or sidecars reach whichever started first. Must not be " +
            @"empty or contain \, / or :. Restart the game to apply.");
        PipeNameChoice choice = PipeName.Choose(Environment.GetEnvironmentVariable(EnvironmentName), EnvironmentName,
            name.Value);
        if (choice.Warning != null)
        {
            StationGodMod.LogWarning(choice.Warning);
        }

        StationGodMod.Log($"Pipe name: {choice.Name.Value}.");
        return choice.Name;
    }
}
