using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// How the game's mod differs from this server's verb model (LIVE 2026-10-25: help listed show ghost look and xray, the
/// game ran 2.10.3 and refused them as "not an option"). Built from the mod's own catalogue answer: the whats and
/// options this server has that the mod lacks (they need a newer mod), and those the mod has that this server lacks
/// (they need a newer server). Whats the hub answers itself never reach the mod and are not compared.
/// </summary>
internal sealed class ModSkew
{
    internal const string NeedsNewerMod = "needs_newer_mod";
    internal const string NeedsNewerServer = "needs_newer_server";

    private readonly Dictionary<string, List<string>> _lacking = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lackingWhats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _modOptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modWhats = new(StringComparer.Ordinal);

    private ModSkew(string modVersion, string serverVersion)
    {
        ModVersion = modVersion;
        ServerVersion = serverVersion;
    }

    /// <summary>The game's mod version, or "unknown".</summary>
    internal string ModVersion { get; }

    internal string ServerVersion { get; }

    /// <summary>The mod lacks something this server has.</summary>
    internal bool ModIsBehind => _lacking.Count > 0 || _lackingWhats.Count > 0;

    /// <summary>
    /// The differences between this server's model and the mod's catalogue document ({shared, errors, verbs}); null
    /// when the mod's whats and options are this server's.
    /// </summary>
    internal static ModSkew? Of(VerbCatalogue server, JsonElement modDocument, string? modVersion, string serverVersion)
    {
        if (modDocument.ValueKind != JsonValueKind.Object ||
            !modDocument.TryGetProperty("verbs", out JsonElement modVerbs) || modVerbs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        ModSkew skew = new(modVersion ?? "unknown", serverVersion);
        foreach (JsonProperty file in modVerbs.EnumerateObject())
        {
            if (file.Value.ValueKind != JsonValueKind.Object ||
                !file.Value.TryGetProperty("whats", out JsonElement whats) || whats.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string verb = file.Value.TryGetProperty("verb", out JsonElement named) && named.ValueKind == JsonValueKind.String
                ? named.GetString()!
                : file.Name;
            foreach (JsonProperty what in whats.EnumerateObject())
            {
                string key = Key(verb, what.Name);
                skew._modWhats.Add(key);
                HashSet<string> options = new(StringComparer.Ordinal);
                if (what.Value.TryGetProperty("options", out JsonElement listed) && listed.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty option in listed.EnumerateObject())
                    {
                        options.Add(option.Name);
                    }
                }

                skew._modOptions[key] = options;
            }
        }

        bool differs = false;
        foreach (VerbEntry verb in server.Verbs)
        {
            foreach (string whatName in verb.WhatNames)
            {
                WhatEntry what = verb.Whats[whatName];
                string key = Key(verb.Name, whatName);
                if (what.Hub || !verb.TakesShaping)
                {
                    continue;
                }

                if (!skew._modOptions.TryGetValue(key, out HashSet<string>? modOptions))
                {
                    skew._lackingWhats.Add(key);
                    differs = true;
                    continue;
                }

                List<string> lacking = [];
                foreach (string option in what.OptionNames)
                {
                    if (!modOptions.Contains(option))
                    {
                        lacking.Add(option);
                    }
                }

                if (lacking.Count > 0)
                {
                    skew._lacking[key] = lacking;
                    differs = true;
                }

                foreach (string option in modOptions)
                {
                    differs |= !what.Options.ContainsKey(option) && !what.WireOptions.Contains(option);
                }
            }
        }

        foreach (string key in skew._modWhats)
        {
            int dot = key.IndexOf('.');
            differs |= server.Verb(key[..dot])?.Whats.ContainsKey(key[(dot + 1)..]) != true;
        }

        return differs ? skew : null;
    }

    /// <summary>The options of the what this server has and the mod lacks, in catalogue order; empty when none.</summary>
    internal IReadOnlyList<string> Lacking(WhatEntry what) =>
        _lacking.TryGetValue(Key(what.Verb, what.Name), out List<string>? lacking) ? lacking : [];

    /// <summary>The mod has no such what.</summary>
    internal bool LacksWhat(WhatEntry what) => _lackingWhats.Contains(Key(what.Verb, what.Name));

    /// <summary>The mod has the verb's what, which this server's model does not.</summary>
    internal bool ModHasWhat(string verb, string? what) => _modWhats.Contains(Key(verb, what ?? VerbCatalogue.WhatlessKey));

    /// <summary>The mod's what takes the option.</summary>
    internal bool ModHasOption(string verb, string? what, string option) =>
        _modOptions.TryGetValue(Key(verb, what ?? VerbCatalogue.WhatlessKey), out HashSet<string>? options) &&
        options.Contains(option);

    /// <summary>
    /// needs_newer_mod for a call this server would send and the mod would refuse: its what, or options it gives that
    /// the mod lacks. Null when the mod takes it.
    /// </summary>
    internal VerbReply.Failed? Refusal(WhatEntry what, JsonObject args)
    {
        if (LacksWhat(what))
        {
            return Failed(NeedsNewerMod, $"{what.Label} needs a newer StationGod mod: the game runs {ModVersion}, " +
                $"this MCP server is {ServerVersion}. Update the mod in the game", what, null);
        }

        List<string> given = [];
        foreach (string option in Lacking(what))
        {
            if (args[option] != null)
            {
                given.Add(option);
            }
        }

        return given.Count == 0
            ? null
            : Failed(NeedsNewerMod, $"{what.Label}: {string.Join(", ", given)} need{(given.Count == 1 ? "s" : "")} a newer " +
                $"StationGod mod: the game runs {ModVersion}, this MCP server is {ServerVersion}. Leave " +
                $"{(given.Count == 1 ? "it" : "them")} out, or update the mod in the game", what, given);
    }

    /// <summary>
    /// needs_newer_server for a refused call whose verb's what, or option, the mod has and this server lacks; null when
    /// the mod lacks them too.
    /// </summary>
    internal VerbReply.Failed? ServerBehind(string verb, string? whatName, WhatEntry? what, JsonObject args)
    {
        if (what == null)
        {
            return ModHasWhat(verb, whatName)
                ? Failed(NeedsNewerServer, $"{verb} {whatName} is in the game's mod ({ModVersion}) but not in this MCP server " +
                    $"({ServerVersion}). Update the MCP server", null, null)
                : null;
        }

        List<string> newer = [];
        foreach ((string name, JsonNode? value) in args)
        {
            if (value != null && !what.Options.ContainsKey(name) && ModHasOption(what.Verb, whatName, name))
            {
                newer.Add(name);
            }
        }

        return newer.Count == 0
            ? null
            : Failed(NeedsNewerServer, $"{what.Label}: {string.Join(", ", newer)} {(newer.Count == 1 ? "is" : "are")} in the " +
                $"game's mod ({ModVersion}) but not in this MCP server ({ServerVersion}). Update the MCP server", what, newer);
    }

    /// <summary>The lines help adds to a page: what of it the game's mod lacks, and why; empty when nothing.</summary>
    internal string Note(VerbEntry verb, WhatEntry? what)
    {
        StringBuilder text = new();
        string runs = $"The game runs StationGod {ModVersion}; this MCP server is {ServerVersion}.";
        if (what != null)
        {
            if (LacksWhat(what))
            {
                text.Append(runs).Append(' ').Append(what.Label).Append(" needs a newer mod.");
            }
            else if (Lacking(what) is { Count: > 0 } lacking)
            {
                text.Append(runs).Append(" Not in the game's mod (they need a newer one): ")
                    .Append(string.Join(", ", lacking)).Append('.');
            }

            return text.ToString();
        }

        List<string> lines = [];
        foreach (string name in verb.WhatNames)
        {
            WhatEntry entry = verb.Whats[name];
            if (LacksWhat(entry))
            {
                lines.Add($"{entry.Label}: needs a newer mod");
            }
            else if (Lacking(entry) is { Count: > 0 } lacking)
            {
                lines.Add($"{entry.Label}: {string.Join(", ", lacking)} need a newer mod");
            }
        }

        return lines.Count == 0 ? string.Empty : runs + "\n" + string.Join("\n", lines);
    }

    /// <summary>The help index's line: the two versions, and where the pages mark what the game lacks.</summary>
    internal string IndexNote() => ModIsBehind
        ? $"The game runs StationGod {ModVersion}; this MCP server is {ServerVersion}. Some whats and options need a newer " +
          "mod: each verb's help names them, and calls that give them are refused needs_newer_mod."
        : $"The game runs StationGod {ModVersion}; this MCP server is {ServerVersion}. The mod has whats or options this " +
          "server lacks: update the MCP server to use them.";

    private VerbReply.Failed Failed(string code, string message, WhatEntry? what, List<string>? options)
    {
        JsonObject data = new() { ["mod_version"] = ModVersion, ["server_version"] = ServerVersion };
        if (what != null)
        {
            data["what"] = what.Label;
        }

        if (options != null)
        {
            JsonArray names = [];
            foreach (string option in options)
            {
                names.Add(option);
            }

            data["options"] = names;
        }

        return new VerbReply.Failed(code, message + ".", data: data);
    }

    private static string Key(string verb, string what) => verb + "." + what;
}
