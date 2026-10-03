using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StationGodMCP.Server;

/// <summary>
/// StationGodMCP.Server key new &lt;name&gt; &lt;level&gt;: makes a key on the host, outside the game, writes its entry into the
/// mod's clients file (BepInEx\config\net.xceled.stationeers.stationgodmcp.clients.json) and prints the key once, here
/// only, so it never reaches the game's console or logs. Options: --config &lt;BepInEx config folder&gt; (default: the
/// game's, from STATIONEERS_DIR or the Steam folder), --transports pipe,tcp (default pipe), --cheat armed|standing
/// (default armed), --grants a,b, --replace (an entry of that name is replaced; refused otherwise). The mod reads the
/// file again within seconds.
/// </summary>
internal static class KeyCommand
{
    internal const string FileName = "net.xceled.stationeers.stationgodmcp.clients.json";
    private const int KeyBytes = 32;
    private static readonly Regex NamePattern = new("^[A-Za-z0-9_.-]{1,64}$");
    private static readonly string[] Levels = { "read", "write", "cheat" };

    /// <summary>Whether the arguments are a key command; the exit code when they are.</summary>
    internal static int? TryRun(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 2 || args[0] != "key" || args[1] != "new")
        {
            return null;
        }

        try
        {
            KeyEntry entry = KeyEntry.From(args);
            string path = Path.Combine(ConfigFolder(Option(args, "--config")), FileName);
            string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));
            Write(path, entry, key, args.Contains("--replace"));
            output.WriteLine($"Key for '{entry.Name}' ({entry.Level}, {string.Join(",", entry.Transports)}) written to {path}.");
            output.WriteLine("The key, shown only now; give it to the client in its key variable (for the pipe");
            output.WriteLine($"StationGodMCP that is STATIONGOD_KEY_STATIONGODMCP):");
            output.WriteLine(key);
            return 0;
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine("Usage: StationGodMCP.Server key new <name> <read|write|cheat> [--config <BepInEx config folder>] " +
                            "[--transports pipe,tcp] [--cheat armed|standing] [--grants method,method] [--replace]");
            return 2;
        }
    }

    internal static void Write(string path, KeyEntry entry, string key, bool replace)
    {
        JsonObject root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new ArgumentException($"{path} is not a JSON object.")
            : new JsonObject();
        JsonArray clients = root["clients"] as JsonArray ?? new JsonArray();
        root["clients"] = clients;
        for (int index = clients.Count - 1; index >= 0; index--)
        {
            if ((string?)clients[index]?["name"] != entry.Name)
            {
                continue;
            }

            if (!replace)
            {
                throw new ArgumentException($"{path} already has a key named '{entry.Name}'; pass --replace to replace it.");
            }

            clients.RemoveAt(index);
        }

        clients.Add(new JsonObject
        {
            ["name"] = entry.Name,
            ["key"] = key,
            ["level"] = entry.Level,
            ["grants"] = new JsonArray(entry.Grants.Select(grant => (JsonNode)JsonValue.Create(grant)!).ToArray()),
            ["cheat"] = entry.Cheat,
            ["transports"] = new JsonArray(entry.Transports.Select(transport => (JsonNode)JsonValue.Create(transport)!).ToArray())
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temporary, path, overwrite: true);
    }

    private static string ConfigFolder(string? given)
    {
        if (!string.IsNullOrWhiteSpace(given))
        {
            return Path.GetFullPath(given);
        }

        string game = Environment.GetEnvironmentVariable("STATIONEERS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common",
                "Stationeers");
        return Path.Combine(game, "BepInEx", "config");
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>One clients-file entry, read from the command's arguments and checked as the mod checks it.</summary>
    internal sealed record KeyEntry(string Name, string Level, string Cheat, string[] Transports, string[] Grants)
    {
        internal static KeyEntry From(string[] args)
        {
            if (args.Length < 4)
            {
                throw new ArgumentException("key new needs a name and a level.");
            }

            string name = args[2];
            string level = args[3].ToLowerInvariant();
            if (!NamePattern.IsMatch(name))
            {
                throw new ArgumentException($"'{name}' is not a name: 1 to 64 letters, digits, '-', '_' or '.'.");
            }

            if (!Levels.Contains(level))
            {
                throw new ArgumentException($"'{args[3]}' is not a level: read, write or cheat.");
            }

            string cheat = (Option(args, "--cheat") ?? "armed").ToLowerInvariant();
            if (cheat != "armed" && cheat != "standing")
            {
                throw new ArgumentException("--cheat is armed or standing.");
            }

            string[] transports = List(Option(args, "--transports") ?? "pipe");
            if (transports.Length == 0 || transports.Any(transport => transport != "pipe" && transport != "tcp"))
            {
                throw new ArgumentException("--transports is pipe, tcp or pipe,tcp.");
            }

            return new KeyEntry(name, level, cheat, transports, List(Option(args, "--grants") ?? string.Empty));
        }

        private static string[] List(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
    }
}
