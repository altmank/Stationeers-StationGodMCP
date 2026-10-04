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
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Serialization;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Subscriptions;
using UnityEngine;

namespace StationGodMCP;

/// <summary>
/// The mod: at load it checks every reflected game member (GameMembers.CheckAll), applies each Harmony patch class,
/// reads the pipe and remote settings, registers its multiplayer messages (Net/StationGodNet) and the gateway prefab.
/// Every frame on a host (NetworkManager.IsServer) it keeps the named pipe (and, when configured, the TCP transport)
/// listening and runs the queued requests on the main thread. On a client of a server it serves no requests: it shares
/// its player's view with a server that runs StationGod (ViewReporter) and draws what that server sends it.
/// </summary>
[StationeersMod(ModId, DisplayName, Version)]
public sealed class StationGodMod : ModBehaviour
{
    public const string ModId = "net.xceled.stationeers.stationgodmcp";
    public const string DisplayName = "StationGod MCP";
    public const string Version = "1.22.0";

    private static readonly DeadlineWatch Deadlines = new DeadlineWatch();

    private StationGodRequestDispatcher _dispatcher =
        new StationGodRequestDispatcher(Deadlines, new LaneScheduler(SchedulerSettings.Default));
    private Harmony? _harmony;
    private PipeListener? _pipeListener;
    private bool _pipeUnavailable;
    private string? _publishedState;
    private string _publishedWorld = string.Empty;
    private TcpAcceptor? _tcpListener;
    private RemoteSettings? _remote;
    private ServerSettings _server = ServerSettings.Defaults;
    private SubscriptionHub? _subscriptions;

    /// <summary>Real time since the mod loaded (mod_info runtime uptime_s).</summary>
    internal static System.Diagnostics.Stopwatch SinceLoad { get; } = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The local pipe's name, read once at load ([Pipe] Name, STATIONGODMCP_PIPE_NAME).</summary>
    internal static PipeName Pipe { get; private set; } = PipeName.Default;

    /// <summary>The overlapped pipe's connections while it listens; null otherwise.</summary>
    internal static ProtocolHost? Connections { get; private set; }

    /// <summary>The TCP listener's connections while it listens; null otherwise.</summary>
    internal static ProtocolHost? TcpConnections { get; private set; }

    /// <summary>Every open connection on the pipe and over TCP.</summary>
    internal static System.Collections.Generic.IEnumerable<StationGodMCP.Protocol.Connection> AllConnections()
    {
        if (Connections != null)
        {
            foreach (StationGodMCP.Protocol.Connection connection in Connections.Open)
            {
                yield return connection;
            }
        }

        if (TcpConnections != null)
        {
            foreach (StationGodMCP.Protocol.Connection connection in TcpConnections.Open)
            {
                yield return connection;
            }
        }
    }

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
            Net.StationGodNet.Register(MultiplayerSettings.ShareViews(configuration));
            _dispatcher = new StationGodRequestDispatcher(Deadlines, new LaneScheduler(PerformanceSettings.Scheduler));
            _subscriptions = new SubscriptionHub(Subscriptions.ReadDevicesReader.Instance,
                Subscriptions.ReadLogicManyReader.Instance, SubscriptionLimits.From(PerformanceSettings.Scheduler.SubscriptionBudgetMs, PerformanceSettings.RequestBudgetMs));
            _dispatcher.Subscriptions = _subscriptions;
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
            _subscriptions?.BeginFrame(new SamplingTick(Time.frameCount, Time.time),
                new Pure.Sampling.RealTimeTick(Time.frameCount, SinceLoad.Elapsed.TotalSeconds, DateTimeOffset.UtcNow));
            PublishFacts();
            _subscriptions?.ObserveGameState(ReportedGameState());
            if (!NetworkManager.IsServer)
            {
                StopServers("world_unloaded");
                Net.ViewReporter.Tick();
                Previews.Tick();
                Highlights.Tick();
                return;
            }

            Net.RemoteViews.Tick();

            if (_pipeListener == null && !_pipeUnavailable)
            {
                StartPipe();
            }

            if (_remote != null && _tcpListener == null && TcpAcceptor.ShouldListen(_remote.Enabled, _remote.Secret))
            {
                StartTcpServer(_remote);
            }

            _dispatcher.RunFrame(HeldTickJobs.HoldsTick, _subscriptions);
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
        StopServers("shutting_down");
        GatewayRegistry.Clear();
        IcExecutionController.Clear();
        HeldTickJobs.Abandon();
        Previews.Clear();
        Highlights.Clear();
        _harmony?.UnpatchSelf();
    }

    // reason goes to clients in their goodbye: world_unloaded when the host leaves the world, shutting_down
    // when the mod stops.
    private void StopServers(string reason)
    {
        _pipeListener?.ShutDown(reason);
        _pipeListener = null;
        Connections = null;
        _tcpListener?.ShutDown(reason);
        _tcpListener = null;
        TcpConnections = null;
    }

    // The overlapped pipe: one reader and one writer per connection. It needs Windows' overlapped pipe calls; where
    // they are missing the pipe stays off (logged once) and only TCP serves.
    private void StartPipe()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            PipeUnavailable("this platform is not Windows");
            return;
        }

        try
        {
            ProtocolHost host = new ProtocolHost(
                new ProtocolSettings(_server.MaxPipeConnections, strictArguments: _server.StrictArguments), _dispatcher,
                Deadlines, ApiHost.CatalogueFile, subscriptions: _subscriptions);
            PipeListener listener = new PipeListener(Pipe.Value, host);
            listener.Start();
            _pipeListener = listener;
            Connections = host;
        }
        catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
        {
            PipeUnavailable(exception.Message);
        }
    }

    private void PipeUnavailable(string reason)
    {
        _pipeUnavailable = true;
        LogWarning($"The named pipe is not available ({reason}); only the TCP listener can serve clients.");
    }

    // What welcome says about the server, published when it changes; a world that starts running is announced to every
    // connection.
    private void PublishFacts()
    {
        string state = ReportedGameState();
        string worldId = WorldStores.WorldId;
        if (state == _publishedState && worldId == _publishedWorld)
        {
            return;
        }

        bool entered = worldId != _publishedWorld;
        _publishedState = state;
        _publishedWorld = worldId;
        WorldFacts world = new WorldFacts(worldId, SaveName(), WorldStores.Epoch);
        ServerFacts.Current = new ServerFacts(Version, Pipe.Value, Application.isBatchMode, world, state);
        if (entered)
        {
            if (worldId.Length > 0)
            {
                _subscriptions?.WorldChanged(worldId);
            }

            foreach (StationGodMCP.Protocol.Connection connection in AllConnections())
            {
                connection.Session?.OnWorldChanged(world);
            }
        }
    }

    // The game never sets GameState.Paused (CODE: no assignment of it); a pause only sets WorldManager.IsGamePaused.
    private static string ReportedGameState() =>
        GameManager.GameState == GameState.Running && WorldManager.IsGamePaused
            ? nameof(GameState.Paused)
            : GameManager.GameState.ToString();

    private static string? SaveName()
    {
        string? name = XmlSaveLoad.Instance != null ? XmlSaveLoad.Instance.CurrentStationName : null;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private void StartTcpServer(RemoteSettings remote)
    {
        TcpAcceptor? server = null;
        try
        {
            ProtocolHost host = new ProtocolHost(
                new ProtocolSettings(_server.MaxTcpConnections, strictArguments: _server.StrictArguments),
                _dispatcher, Deadlines, ApiHost.CatalogueFile, remote.Secret, _subscriptions);
            server = new TcpAcceptor(remote.BindAddress, remote.Port, _server.MaxTcpConnections, host);
            server.Start();
            _tcpListener = server;
            TcpConnections = host;
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
            StationGodMod.Log(
                "Remote MCP is enabled without a shared secret: TCP listens once the clients file holds a key whose " +
                "transports include tcp, and the old sign-in stays refused.");
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
/// [Server]: how clients connect. MaxPipeConnections is the most pipe connections at once (each agent session, the
/// dashboard and every script keep one); MaxTcpConnections the most TCP connections, counted apart; StrictArguments
/// whether calls are checked against the catalogue in full. Read once at load.
/// </summary>
internal sealed class ServerSettings
{
    private const string Section = "Server";

    internal const int DefaultMaxTcpConnections = 8;

    private ServerSettings(int maxPipeConnections, bool strictArguments, int maxTcpConnections = DefaultMaxTcpConnections)
    {
        MaxTcpConnections = maxTcpConnections;
        MaxPipeConnections = maxPipeConnections;
        StrictArguments = strictArguments;
    }

    internal static ServerSettings Defaults { get; } = new ServerSettings(ProtocolSettings.DefaultMaxPipeConnections, true);

    internal int MaxPipeConnections { get; }

    /// <summary>Whether calls are checked against the catalogue in full.</summary>
    internal bool StrictArguments { get; }

    /// <summary>The most TCP connections at once, counted apart from the pipe's.</summary>
    internal int MaxTcpConnections { get; }

    internal static ServerSettings Load(ConfigFile configuration)
    {
        ConfigEntry<int> connections = configuration.Bind(Section, "MaxPipeConnections",
            ProtocolSettings.DefaultMaxPipeConnections,
            new ConfigDescription(
                "The most local pipe connections at once; a client past it waits until one closes. Restart the game " +
                "to apply.",
                new AcceptableValueRange<int>(ProtocolSettings.MinimumPipeConnections, ProtocolSettings.MaximumPipeConnections)));
        ConfigEntry<bool> strict = configuration.Bind(Section, "StrictArguments", true,
            "Check calls against the method catalogue in full (names at every depth, types, ranges, enums, patterns, " +
            "required arguments, the shape) before they run. false checks only top-level argument names. Restart " +
            "the game to apply.");
        ConfigEntry<int> tcp = configuration.Bind(Section, "MaxTcpConnections", DefaultMaxTcpConnections,
            new ConfigDescription(
                "The most TCP connections at once, counted apart from the pipe's, so remote clients and unfinished " +
                "sign-ins never take a local client's place. Restart the game to apply.",
                new AcceptableValueRange<int>(1, 64)));
        int maximum = connections.Value;
        if (maximum < ProtocolSettings.MinimumPipeConnections || maximum > ProtocolSettings.MaximumPipeConnections)
        {
            StationGodMod.LogWarning($"Ignoring invalid [Server] MaxPipeConnections {maximum}; using " +
                                     $"{ProtocolSettings.DefaultMaxPipeConnections}.");
            maximum = ProtocolSettings.DefaultMaxPipeConnections;
        }

        return new ServerSettings(maximum, strict.Value,
            tcp.Value >= 1 && tcp.Value <= 64 ? tcp.Value : DefaultMaxTcpConnections);
    }
}

/// <summary>
/// [Multiplayer]: whether StationGod shares views between games (a client's view to the server, the server's drawings
/// back). Read once at load.
/// </summary>
internal static class MultiplayerSettings
{
    internal static bool ShareViews(ConfigFile configuration) =>
        configuration.Bind("Multiplayer", "ShareViews", true,
            "Share views between games: a client tells a server running StationGod where its player looks, so the " +
            "server's camera tools (looking_at, crosshair placement, show_preview, highlight...) work for that " +
            "player, and the server's drawings show on that player's screen. StationGod stays optional on every " +
            "machine. It registers StationGod with LaunchPadBooster's networking, which makes a game expect " +
            "LaunchPadBooster networking on the other side of a join: on a server where no other mod uses it, " +
            "false lets players join whose game runs no such mod. Restart the game to apply.").Value;
}

/// <summary>
/// [Performance]: the main-thread milliseconds one frame may spend on requests (FrameBudget), the subscription lane's
/// share of them, and the heavy lane's threshold and waiting bound (SchedulerSettings). Read once at load; a negative
/// or unreadable value falls back to its default with a warning.
/// </summary>
internal static class PerformanceSettings
{
    private const string Section = "Performance";

    /// <summary>The configured budget; 0 = unlimited.</summary>
    internal static double RequestBudgetMs { get; private set; } = FrameBudget.DefaultMs;

    /// <summary>The frame scheduler's settings, from every [Performance] value.</summary>
    internal static SchedulerSettings Scheduler { get; private set; } = SchedulerSettings.Default;

    internal static void Load(ConfigFile configuration)
    {
        ConfigEntry<double> budget = configuration.Bind(Section, "RequestBudgetMs", FrameBudget.DefaultMs,
            "Main-thread milliseconds one frame may spend answering requests; the rest wait for the next frame. The " +
            "first request of a frame always runs. 0 = unlimited. While a job holds the game tick the budget is at " +
            $"most {FrameBudget.JobHeldMs} ms. Restart the game to apply.");
        double? configured = FrameBudget.Configured(budget.Value);
        if (configured == null)
        {
            StationGodMod.LogWarning(
                $"Ignoring invalid [Performance] RequestBudgetMs {budget.Value}; using {FrameBudget.DefaultMs} ms.");
        }

        RequestBudgetMs = configured ?? FrameBudget.DefaultMs;
        double subscriptions = NonNegative(configuration.Bind(Section, "SubscriptionBudgetMs",
            SchedulerSettings.DefaultSubscriptionBudgetMs,
            "Main-thread milliseconds of each frame for subscription and sample_logic samples, taken out of the same " +
            "frame and at most half of RequestBudgetMs. The first due sample of a frame always runs. 0 turns " +
            "subscriptions off. Restart the game to apply."), SchedulerSettings.DefaultSubscriptionBudgetMs);
        double heavy = NonNegative(configuration.Bind(Section, "HeavyThresholdMs", SchedulerSettings.DefaultHeavyThresholdMs,
            "A call predicted to take longer than this on the main thread waits in the heavy lane, which runs at most " +
            "one call a frame. Restart the game to apply."), SchedulerSettings.DefaultHeavyThresholdMs);
        ConfigEntry<int> wait = configuration.Bind(Section, "HeavyMaxWaitFrames", SchedulerSettings.DefaultHeavyMaxWaitFrames,
            "A heavy call passed over this many frames runs even when the frame is over budget. Restart the game to apply.");
        int frames = wait.Value >= 0 ? wait.Value : SchedulerSettings.DefaultHeavyMaxWaitFrames;
        if (wait.Value < 0)
        {
            StationGodMod.LogWarning(
                $"Ignoring invalid [Performance] HeavyMaxWaitFrames {wait.Value}; using {SchedulerSettings.DefaultHeavyMaxWaitFrames}.");
        }

        Scheduler = new SchedulerSettings(RequestBudgetMs, subscriptions, heavy, frames, Protocol.CallSession.MaxInFlight);
    }

    private static double NonNegative(ConfigEntry<double> entry, double fallback)
    {
        if (entry.Value >= 0.0 && !double.IsInfinity(entry.Value) && !double.IsNaN(entry.Value))
        {
            return entry.Value;
        }

        StationGodMod.LogWarning(
            $"Ignoring invalid [{entry.Definition.Section}] {entry.Definition.Key} {entry.Value}; using {fallback}.");
        return fallback;
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
