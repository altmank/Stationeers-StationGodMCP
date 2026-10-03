#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Pure.Access;

/// <summary>What a connection may do: each level includes the ones before it. None refuses the connection.</summary>
internal enum AccessLevel
{
    None = 0,
    Read = 1,
    Write = 2,
    Cheat = 3
}

internal static class AccessLevels
{
    /// <summary>A level by its name (none, read, write, cheat; trimmed, any case); null for anything else.</summary>
    internal static AccessLevel? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "none" => AccessLevel.None,
        "read" => AccessLevel.Read,
        "write" => AccessLevel.Write,
        "cheat" => AccessLevel.Cheat,
        _ => null
    };

    internal static string Name(AccessLevel level) => level switch
    {
        AccessLevel.Read => "read",
        AccessLevel.Write => "write",
        AccessLevel.Cheat => "cheat",
        _ => "none"
    };

    /// <summary>The level a call of this effective class needs.</summary>
    internal static AccessLevel Required(MethodClass effective) => effective switch
    {
        MethodClass.Read => AccessLevel.Read,
        MethodClass.Write => AccessLevel.Write,
        _ => AccessLevel.Cheat
    };
}

/// <summary>
/// What one connection was given when it signed in: its level, the methods granted beyond it, and whether its cheat is
/// standing (no approval needed) or armed (cheat calls need the owner's approval in the game).
/// </summary>
internal sealed class ConnectionAccess
{
    private static readonly HashSet<string> NoGrants = new HashSet<string>(StringComparer.Ordinal);

    internal ConnectionAccess(AccessLevel level, IReadOnlyCollection<string>? grants, bool standingCheat)
    {
        Level = level;
        Grants = grants ?? NoGrants;
        StandingCheat = standingCheat;
    }

    internal AccessLevel Level { get; }

    internal IReadOnlyCollection<string> Grants { get; }

    internal bool StandingCheat { get; }

    /// <summary>Everything, with no approval needed: today's behaviour, where nothing has to be checked.</summary>
    internal bool AllowsEverything => Level == AccessLevel.Cheat && StandingCheat;

    internal bool IsGranted(string method)
    {
        foreach (string granted in Grants)
        {
            if (string.Equals(granted, method, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Whether a call may run, and if not, why.</summary>
internal abstract class Permission
{
    private Permission()
    {
    }

    internal static readonly Permission Allowed = new AllowedCall();

    /// <summary>
    /// A call runs if the connection's level is at least the call's effective class, or the method is granted; a cheat
    /// call also needs cheat armed for the connection, unless its cheat is standing.
    /// </summary>
    internal static Permission Decide(ConnectionAccess access, string method, MethodClass effective, bool armed)
    {
        AccessLevel required = AccessLevels.Required(effective);
        if (access.Level < required && !access.IsGranted(method))
        {
            return new Denied(required);
        }

        if (required == AccessLevel.Cheat && !access.StandingCheat && !armed)
        {
            return new NotArmed();
        }

        return Allowed;
    }

    internal sealed class AllowedCall : Permission
    {
    }

    /// <summary>permission_denied: the call needs this level.</summary>
    internal sealed class Denied : Permission
    {
        internal Denied(AccessLevel required) => Required = required;

        internal AccessLevel Required { get; }
    }

    /// <summary>cheat_not_armed: the level allows cheat, but the owner has not approved it now.</summary>
    internal sealed class NotArmed : Permission
    {
    }
}

/// <summary>One key from the clients file.</summary>
internal sealed class ClientKey
{
    internal ClientKey(string name, byte[] key, AccessLevel level, HashSet<string> grants, bool standingCheat,
        HashSet<string> transports)
    {
        Name = name;
        Key = key;
        Level = level;
        Grants = grants;
        StandingCheat = standingCheat;
        Transports = transports;
        Fingerprint = FingerprintOf(key);
    }

    internal string Name { get; }

    internal byte[] Key { get; }

    internal AccessLevel Level { get; }

    internal HashSet<string> Grants { get; }

    internal bool StandingCheat { get; }

    /// <summary>pipe, tcp.</summary>
    internal HashSet<string> Transports { get; }

    /// <summary>The first 8 hex digits of the key's SHA-256: what the log shows instead of the key.</summary>
    internal string Fingerprint { get; }

    internal ConnectionAccess Access => new ConnectionAccess(Level, Grants, StandingCheat);

    /// <summary>Whether this key, changed to other, gives less than it did: a lower level, a grant or a transport gone.</summary>
    internal bool IsDowngradedBy(ClientKey? other)
    {
        if (other == null || other.Level < Level || (StandingCheat && !other.StandingCheat))
        {
            return true;
        }

        foreach (string grant in Grants)
        {
            if (!other.Grants.Contains(grant))
            {
                return true;
            }
        }

        foreach (string transport in Transports)
        {
            if (!other.Transports.Contains(transport))
            {
                return true;
            }
        }

        return false;
    }

    internal static string FingerprintOf(byte[] key)
    {
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(key);
        StringBuilder hex = new StringBuilder(8);
        for (int index = 0; index < 4; index++)
        {
            hex.Append(digest[index].ToString("x2"));
        }

        return hex.ToString();
    }
}

/// <summary>
/// The owner's keys (BepInEx\config\net.xceled.stationeers.stationgodmcp.clients.json): {"clients": [{name, key, level,
/// grants, cheat, transports}]}. An entry that is wrong (a name outside the grammar or given twice, a key shorter than
/// 32 bytes, an unknown level, cheat or transport) is left out and reported by name and fingerprint, never by key; the
/// others load. A missing file is no keys.
/// </summary>
internal sealed class ClientsFile
{
    internal const int MinimumKeyBytes = 32;

    internal static readonly ClientsFile Empty = new ClientsFile(new Dictionary<string, ClientKey>(StringComparer.Ordinal),
        new List<string>());

    private ClientsFile(Dictionary<string, ClientKey> clients, List<string> problems)
    {
        Clients = clients;
        Problems = problems;
    }

    internal IReadOnlyDictionary<string, ClientKey> Clients { get; }

    /// <summary>One line per entry left out, for the log.</summary>
    internal IReadOnlyList<string> Problems { get; }

    /// <summary>Whether any key may be used over TCP.</summary>
    internal bool AnyTcp
    {
        get
        {
            foreach (ClientKey client in Clients.Values)
            {
                if (client.Transports.Contains("tcp"))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static ClientsFile Parse(string json)
    {
        JObject root;
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonReaderException exception)
        {
            return new ClientsFile(new Dictionary<string, ClientKey>(StringComparer.Ordinal),
                new List<string> { $"The clients file is not JSON the mod reads ({exception.Message}); no keys are loaded." });
        }

        Dictionary<string, ClientKey> clients = new Dictionary<string, ClientKey>(StringComparer.Ordinal);
        List<string> problems = new List<string>();
        if (!(root["clients"] is JArray entries))
        {
            problems.Add("The clients file has no clients list; no keys are loaded.");
            return new ClientsFile(clients, problems);
        }

        HashSet<string> repeated = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < entries.Count; index++)
        {
            string? problem = Read(entries[index], index, out ClientKey? client);
            if (client == null)
            {
                problems.Add(problem!);
            }
            else if (clients.ContainsKey(client.Name) || repeated.Contains(client.Name))
            {
                repeated.Add(client.Name);
                clients.Remove(client.Name);
                problems.Add($"Client '{client.Name}' ({client.Fingerprint}) is listed more than once; every entry of that name is disabled.");
            }
            else
            {
                clients.Add(client.Name, client);
            }
        }

        return new ClientsFile(clients, problems);
    }

    private static string? Read(JToken token, int index, out ClientKey? client)
    {
        client = null;
        if (!(token is JObject entry))
        {
            return $"Clients entry {index} is not an object; disabled.";
        }

        string? name = entry["name"]?.Type == JTokenType.String ? (string)entry["name"]! : null;
        string label = name ?? $"entry {index}";
        byte[]? key = KeyBytes(entry["key"]);
        string fingerprint = key != null ? ClientKey.FingerprintOf(key) : "no key";
        string where = $"Client '{label}' ({fingerprint})";
        if (name == null || !ClientMessageNames.IsClientName(name))
        {
            return $"{where}: the name must be 1 to 64 letters, digits, '-', '_' or '.'; disabled.";
        }

        if (key == null || key.Length < MinimumKeyBytes)
        {
            return $"{where}: the key must be base64 of at least {MinimumKeyBytes} random bytes; disabled.";
        }

        AccessLevel? level = entry["level"]?.Type == JTokenType.String ? AccessLevels.Parse((string)entry["level"]!) : null;
        if (level == null || level == AccessLevel.None)
        {
            return $"{where}: the level must be read, write or cheat; disabled.";
        }

        string cheat = entry["cheat"]?.Type == JTokenType.String ? ((string)entry["cheat"]!).Trim().ToLowerInvariant() : "armed";
        if (entry["cheat"] != null && entry["cheat"]!.Type != JTokenType.Null && cheat != "armed" && cheat != "standing")
        {
            return $"{where}: cheat must be armed or standing; disabled.";
        }

        HashSet<string>? grants = Names(entry["grants"], null);
        if (grants == null)
        {
            return $"{where}: grants must be a list of method names; disabled.";
        }

        HashSet<string>? transports = Names(entry["transports"], "pipe");
        if (transports == null || transports.Count == 0)
        {
            return $"{where}: transports must be a list of pipe and tcp; disabled.";
        }

        foreach (string transport in transports)
        {
            if (transport != "pipe" && transport != "tcp")
            {
                return $"{where}: the transport '{transport}' is not pipe or tcp; disabled.";
            }
        }

        client = new ClientKey(name, key, level.Value, grants, cheat == "standing", transports);
        return null;
    }

    private static byte[]? KeyBytes(JToken? token)
    {
        if (token?.Type != JTokenType.String)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(((string)token!).Trim());
        }
        catch (FormatException)
        {
            // Not base64: the entry is reported without a fingerprint.
            return null;
        }
    }

    // A list of strings; absent or null gives the fallback (or nothing); anything else is null (wrong).
    private static HashSet<string>? Names(JToken? token, string? fallback)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        if (token == null || token.Type == JTokenType.Null)
        {
            if (fallback != null)
            {
                names.Add(fallback);
            }

            return names;
        }

        if (!(token is JArray list))
        {
            return null;
        }

        foreach (JToken item in list)
        {
            if (item.Type != JTokenType.String)
            {
                return null;
            }

            names.Add(((string)item!).Trim());
        }

        return names;
    }
}

/// <summary>The grammar of client names (hello.client.name, key names).</summary>
internal static class ClientMessageNames
{
    internal static bool IsClientName(string name)
    {
        if (name.Length == 0 || name.Length > 64)
        {
            return false;
        }

        foreach (char character in name)
        {
            bool allowed = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Proving a key without sending it: proof = lowercase hex of HMAC-SHA256 keyed by the key's bytes over the UTF-8 of
/// "stationgod-v2\n" + nonce (the base64 text as sent) + "\n" + client + "\n" + transport.
/// </summary>
internal static class KeyProof
{
    internal static string Compute(byte[] key, string nonce, string client, string transport)
    {
        using HMACSHA256 hmac = new HMACSHA256(key);
        byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes("stationgod-v2\n" + nonce + "\n" + client + "\n" + transport));
        StringBuilder hex = new StringBuilder(digest.Length * 2);
        foreach (byte value in digest)
        {
            hex.Append(value.ToString("x2"));
        }

        return hex.ToString();
    }

    /// <summary>Whether proof is right, compared in fixed time.</summary>
    internal static bool Matches(byte[] key, string nonce, string client, string transport, string proof)
    {
        byte[] expected = Encoding.ASCII.GetBytes(Compute(key, nonce, client, transport));
        byte[] supplied = Encoding.ASCII.GetBytes(proof.Trim());
        int difference = expected.Length ^ supplied.Length;
        for (int index = 0; index < expected.Length; index++)
        {
            difference |= expected[index] ^ (index < supplied.Length ? supplied[index] : 0);
        }

        return difference == 0;
    }

    /// <summary>A challenge's nonce: the base64 text of 32 fresh random bytes.</summary>
    internal static string NewNonce()
    {
        byte[] random = new byte[32];
        using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
        {
            generator.GetBytes(random);
        }

        return Convert.ToBase64String(random);
    }
}

/// <summary>
/// The owner's approvals for cheat, in memory only: for one connection (by client id) or for every present and future
/// connection of one key (by key name), until a time. The clock is injected so expiry is testable.
/// </summary>
internal sealed class Arming
{
    internal const int DefaultMinutes = 15;
    internal const int MaximumMinutes = 240;

    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new object();
    private readonly Dictionary<string, DateTime> _byClient = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _byKey = new Dictionary<string, DateTime>(StringComparer.Ordinal);

    internal Arming(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (static () => DateTime.UtcNow);

    internal DateTime Now => _utcNow();

    internal DateTime ArmClient(string clientId, int minutes) => Arm(_byClient, clientId, minutes);

    internal DateTime ArmKey(string keyName, int minutes) => Arm(_byKey, keyName, minutes);

    internal void DisarmClient(string clientId)
    {
        lock (_sync)
        {
            _byClient.Remove(clientId);
        }
    }

    internal void DisarmKey(string keyName)
    {
        lock (_sync)
        {
            _byKey.Remove(keyName);
        }
    }

    /// <summary>Until when this connection is armed (its own approval or its key's, the later one); null when it is not.</summary>
    internal DateTime? ArmedUntil(string clientId, string? keyName)
    {
        DateTime now = _utcNow();
        lock (_sync)
        {
            DateTime? until = null;
            if (_byClient.TryGetValue(clientId, out DateTime client) && client > now)
            {
                until = client;
            }

            if (keyName != null && _byKey.TryGetValue(keyName, out DateTime key) && key > now && (until == null || key > until))
            {
                until = key;
            }

            return until;
        }
    }

    private DateTime Arm(Dictionary<string, DateTime> table, string name, int minutes)
    {
        int clamped = Math.Max(1, Math.Min(MaximumMinutes, minutes));
        DateTime until = _utcNow().AddMinutes(clamped);
        lock (_sync)
        {
            table[name] = until;
        }

        return until;
    }
}
