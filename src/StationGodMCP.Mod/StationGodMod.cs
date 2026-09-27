#nullable enable

using System;
using System.IO;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using StationeersMods.Interface;
using StationGodMCP.Api.Shared.Game;
using UnityEngine;

namespace StationGodMCP;

/// <summary>
/// The mod: at load it checks every reflected game member (GameMembers.CheckAll), applies each Harmony patch class,
/// reads the remote settings and registers the gateway prefab. Every frame on a host (NetworkManager.IsServer) it
/// keeps the named pipe (and, when configured, the TCP transport) listening and runs the queued requests on the main
/// thread.
/// </summary>
[StationeersMod(ModId, DisplayName, Version)]
public sealed class StationGodMod : ModBehaviour
{
    public const string ModId = "net.xceled.stationeers.stationgodmcp";
    public const string DisplayName = "StationGod MCP";
    public const string Version = "1.0.0";

    private const string PipeName = "StationGodMCP";

    private readonly StationGodRequestDispatcher _dispatcher = new StationGodRequestDispatcher();
    private Harmony? _harmony;
    private StationGodPipeServer? _pipeServer;
    private StationGodTcpServer? _tcpServer;
    private RemoteSettings? _remote;

    public override void OnLoaded(ContentHandler contentHandler)
    {
        base.OnLoaded(contentHandler);

        try
        {
            _harmony = new Harmony(ModId);
            GameMembers.CheckAll();
            PatchEachClass(_harmony);
            _remote = RemoteSettings.Load();
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
            if (!NetworkManager.IsServer)
            {
                StopServers();
                return;
            }

            if (_pipeServer == null)
            {
                _pipeServer = new StationGodPipeServer(PipeName, _dispatcher);
                _pipeServer.Start();
            }

            if (_remote != null && _remote.Enabled && _tcpServer == null)
            {
                StartTcpServer(_remote);
            }

            _dispatcher.ProcessPendingRequests();
            HeldTickJobs.Tick();
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
        _harmony?.UnpatchSelf();
    }

    private void StopServers()
    {
        _pipeServer?.Dispose();
        _pipeServer = null;
        _tcpServer?.Dispose();
        _tcpServer = null;
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

    internal static RemoteSettings Load()
    {
        string path = Path.Combine(Paths.ConfigPath, $"{StationGodMod.ModId}.cfg");
        ConfigFile configuration = new ConfigFile(path, true);
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
                $"Remote MCP is enabled but no secret is configured. Set Remote MCP/Secret in {path} or " +
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
