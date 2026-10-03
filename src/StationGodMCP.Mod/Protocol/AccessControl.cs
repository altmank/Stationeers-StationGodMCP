#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using StationGodMCP.Pure.Access;

namespace StationGodMCP.Protocol;

/// <summary>
/// [Access]: the level a connection without a key gets on the pipe (and, at cheat, whether that cheat is standing),
/// the levels of the old protocol's clients on the pipe and over TCP (their cheat is standing, as it always was), and the
/// test-only switch that lets run_console_command arm cheat on a server nobody can type into.
/// </summary>
internal sealed class AccessSettings
{
    internal static readonly AccessSettings Defaults = new AccessSettings(AccessLevel.Write, false, AccessLevel.Cheat,
        AccessLevel.Cheat, false);

    internal AccessSettings(AccessLevel anonymousPipeLevel, bool anonymousStanding, AccessLevel legacyPipeLevel,
        AccessLevel legacyTcpLevel, bool allowArmingFromToolConsole)
    {
        AnonymousPipeLevel = anonymousPipeLevel;
        AnonymousStanding = anonymousStanding;
        LegacyPipeLevel = legacyPipeLevel;
        LegacyTcpLevel = legacyTcpLevel;
        AllowArmingFromToolConsole = allowArmingFromToolConsole;
    }

    internal AccessLevel AnonymousPipeLevel { get; }

    /// <summary>At cheat level, whether a keyless pipe connection's cheat needs no approval ([Access] AnonymousCheat standing).</summary>
    internal bool AnonymousStanding { get; }

    internal AccessLevel LegacyPipeLevel { get; }

    internal AccessLevel LegacyTcpLevel { get; }

    internal bool AllowArmingFromToolConsole { get; }

    internal ConnectionAccess AnonymousPipe => new ConnectionAccess(AnonymousPipeLevel, null, AnonymousStanding);

    internal ConnectionAccess LegacyPipe => new ConnectionAccess(LegacyPipeLevel, null, true);

    internal ConnectionAccess LegacyTcp => new ConnectionAccess(LegacyTcpLevel, null, true);
}

/// <summary>
/// Who may do what, shared by every listener: the settings, the keys (reloaded when the clients file changes, which
/// revokes connections whose key was removed or lowered), the owner's approvals for cheat, and the stationgod console
/// command's work (allow, deny, clients).
/// </summary>
internal sealed class AccessControl
{
    private readonly object _sync = new object();
    private readonly List<ProtocolHost> _hosts = new List<ProtocolHost>();
    private readonly string? _path;
    private ClientsFile _clients;
    private DateTime _fileTime = DateTime.MinValue;
    private bool _fileSeen;

    internal AccessControl(AccessSettings settings, string? clientsPath, Arming? arming = null, ClientsFile? clients = null)
    {
        Settings = settings;
        _path = clientsPath;
        Arming = arming ?? new Arming();
        _clients = clients ?? ClientsFile.Empty;
    }

    internal AccessSettings Settings { get; }

    internal Arming Arming { get; }

    internal ClientsFile Clients
    {
        get
        {
            lock (_sync)
            {
                return _clients;
            }
        }
    }

    internal void Register(ProtocolHost host)
    {
        lock (_sync)
        {
            _hosts.Add(host);
        }
    }

    internal void Unregister(ProtocolHost host)
    {
        lock (_sync)
        {
            _hosts.Remove(host);
        }
    }

    /// <summary>Every open connection of every listener.</summary>
    internal List<Connection> Connections()
    {
        List<Connection> all = new List<Connection>();
        lock (_sync)
        {
            foreach (ProtocolHost host in _hosts)
            {
                all.AddRange(host.Open);
            }
        }

        return all;
    }

    /// <summary>
    /// Reads the clients file again when its time changed (or it appeared or went), logs every entry it left out, and
    /// closes, with goodbye revoked, each connection whose key is gone or now gives less. True when it reloaded.
    /// </summary>
    internal bool ReloadIfChanged()
    {
        if (_path == null)
        {
            return false;
        }

        bool exists = File.Exists(_path);
        DateTime time = exists ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (exists == _fileSeen && time == _fileTime)
        {
            return false;
        }

        _fileSeen = exists;
        _fileTime = time;
        ClientsFile loaded;
        try
        {
            loaded = exists ? ClientsFile.Parse(File.ReadAllText(_path)) : ClientsFile.Empty;
        }
        catch (IOException exception)
        {
            // The owner is saving the file right now: tried again on the next check.
            _fileTime = DateTime.MinValue;
            ProtocolLog.Warning($"Could not read the clients file: {exception.Message}");
            return false;
        }

        Replace(loaded);
        ProtocolLog.Info($"Clients file loaded: {loaded.Clients.Count} key(s).");
        return true;
    }

    /// <summary>New keys in place of the old; every connection signed in with a key that now gives less is revoked.</summary>
    internal void Replace(ClientsFile loaded)
    {
        foreach (string problem in loaded.Problems)
        {
            ProtocolLog.Warning(problem);
        }

        lock (_sync)
        {
            _clients = loaded;
        }

        foreach (Connection connection in Connections())
        {
            if (connection.Session is CallSession session && session.Key != null)
            {
                loaded.Clients.TryGetValue(session.Key.Name, out ClientKey? now);
                if (session.Key.IsDowngradedBy(now))
                {
                    session.Revoke();
                }
            }
        }
    }

    /// <summary>Tells every connection whose approval for cheat began or ended (armed, denied, or run out).</summary>
    internal void RefreshCheat()
    {
        foreach (Connection connection in Connections())
        {
            (connection.Session as CallSession)?.RefreshCheat();
        }
    }

    /// <summary>
    /// stationgod allow: arms one connection (a client id such as c9) or every connection of one key (its name) for the
    /// given minutes. The answer is the console's text.
    /// </summary>
    internal string Allow(string target, int minutes)
    {
        int clamped = Math.Max(1, Math.Min(Arming.MaximumMinutes, minutes));
        Connection? connection = FindConnection(target);
        if (connection != null)
        {
            if (!(connection.Session is CallSession session) || session.Access.Level < AccessLevel.Cheat)
            {
                return $"{target} cannot use cheat tools (its level is {connection.Session?.Level ?? "none"}); nothing armed.";
            }

            if (session.Access.StandingCheat)
            {
                return $"{target} needs no approval: its cheat is standing.";
            }

            DateTime until = Arming.ArmClient(target, clamped);
            RefreshCheat();
            return $"Cheat armed for {target} ({session.Client}) until {Utc(until)} ({clamped} min).";
        }

        if (Clients.Clients.TryGetValue(target, out ClientKey? key))
        {
            if (key.Level < AccessLevel.Cheat)
            {
                return $"The key {target} is {AccessLevels.Name(key.Level)} level; nothing armed.";
            }

            DateTime until = Arming.ArmKey(target, clamped);
            RefreshCheat();
            return $"Cheat armed for every connection of the key {target} until {Utc(until)} ({clamped} min).";
        }

        return $"No connection or key named '{target}'. 'stationgod clients' lists them.";
    }

    /// <summary>stationgod deny: disarms a connection or a key at once.</summary>
    internal string Deny(string target)
    {
        Arming.DisarmClient(target);
        Arming.DisarmKey(target);
        RefreshCheat();
        return $"Cheat disarmed for {target}.";
    }

    /// <summary>stationgod clients: every connection, one line each.</summary>
    internal string Report()
    {
        List<Connection> connections = Connections();
        connections.Sort(static (left, right) => string.CompareOrdinal(left.ClientId, right.ClientId));
        StringBuilder text = new StringBuilder();
        text.Append(connections.Count).Append(" connection(s)");
        foreach (Connection connection in connections)
        {
            Session? session = connection.Session;
            string? key = (session as CallSession)?.Key?.Name;
            DateTime? until = Arming.ArmedUntil(connection.ClientId, key);
            text.Append('\n').Append(connection.ClientId)
                .Append("  key ").Append(session?.Client ?? "-")
                .Append("  ").Append(connection.Transport)
                .Append("  protocol ").Append(session?.Protocol.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append("  level ").Append(session?.Level ?? "-")
                .Append("  armed until ").Append(until.HasValue ? Utc(until.Value) : "-")
                .Append("  in flight ").Append(session?.InFlight ?? 0)
                .Append("  subscriptions 0");
        }

        return text.ToString();
    }

    internal static string Utc(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private Connection? FindConnection(string clientId)
    {
        foreach (Connection connection in Connections())
        {
            if (connection.ClientId == clientId)
            {
                return connection;
            }
        }

        return null;
    }
}
