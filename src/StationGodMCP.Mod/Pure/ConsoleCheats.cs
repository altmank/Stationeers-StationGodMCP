#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Which console command lines are cheats (2.7.1; LU: "console commands should not be considered cheats en masse"):
/// server command is a cheat only for a line that changes the world outside normal play. That covers spawning,
/// deleting or teleporting things, bulk deletes, charging batteries or power, growing plants, setting the difficulty,
/// and every command the game itself scopes [creative] (CommandScope.CreativeOnly: deletenear, teleport,
/// voxelfillnear). Read-only and diagnostic lines run with cheats off. The table follows the game's own commands
/// (Util.Commands, CommandLine's map, game Update 0.2.6428) word by word for the multi-action ones. It also covers
/// Terraforming Reloaded's terraform. A command it does not know, or a subcommand a known command does not list, is a
/// cheat, and the reason names it.
/// </summary>
internal static class ConsoleCheats
{
    /// <summary>A rule: the reason a command's arguments (lower case, after its key) make it a cheat, or null.</summary>
    private delegate string? Rule(IReadOnlyList<string> arguments);

    private static readonly Rule Free = _ => null;

    private static readonly IReadOnlyDictionary<string, Rule> Rules = new Dictionary<string, Rule>(StringComparer.Ordinal)
    {
        // The game's [creative] commands, and the ones that change the world in one go.
        ["deletenear"] = Cheat("deletes every thing near a player [creative]"),
        ["teleport"] = Cheat("teleports the player [creative]"),
        ["voxelfillnear"] = Cheat("puts terrain back near a player [creative]"),
        ["deletelooseitems"] = Cheat("deletes every loose item in the world"),
        ["deleteoutofbounds"] = Cheat("deletes everything outside the playable bounds"),
        ["cleanupplayers"] = Cheat("deletes player bodies"),
        ["setbatteries"] = Cheat("sets the charge of every battery cell"),
        ["addgas"] = Cheat("makes gas out of nothing"),
        ["loworbitstation"] = Cheat("spawns a launch mount or teleports the player"),
        ["thing"] = Words(null,
            ("spawn", "makes things out of nothing"), ("spawnvel", "makes things out of nothing"),
            ("delete", "deletes a thing"),
            ("find", null), ("info", null), ("info-verbose", null), ("countanimator", null), ("checkslots", null),
            ("thumbnail", null), ("blueprint", null)),
        ["power"] = Words(null, ("chargeall", "fills every battery and chargeable to full")),
        ["structure"] = Words(null, ("completeall", "builds every structure to its last state")),
        ["plant"] = Words(null, ("grow", "grows plants a stage"), ("genes", null)),
        ["player"] = Words(null, ("list", null), ("sethydration", "sets a player's hydration"),
            ("sethygiene", "sets a player's hygiene"), ("setmood", "sets a player's mood"),
            ("setnutrition", "sets a player's nutrition")),
        ["difficulty"] = arguments => arguments.Count == 0 ? null : "sets the difficulty",
        ["rocket"] = Rocket,
        ["trader"] = Words(null, ("contacts", null), ("buys", null), ("sells", null), ("evaluate", null),
            ("checksum", null), ("showall", null), ("regenerate", "resets the trader contacts"),
            ("land", "summons a trader"), ("call", "summons a trader"), ("depart", "sends a trader away")),
        ["storm"] = Words(null, ("debug", null), ("start", "starts a storm"), ("stop", "stops a storm")),
        ["spacemap"] = Words(null, ("testpaths", null), ("regenerate", "rebuilds the space map"),
            ("fill", "charts and fills the space map"), ("chart", "charts the space map")),
        ["celestial"] = arguments => arguments.Count <= 1 ? null : "edits a body's orbit",
        ["orbit"] = Words(null, ("debug", null), ("view", null), ("celestials", null), ("makeoffset", null),
            ("set", "sets the orbital simulation"), ("simulate", "runs the orbital simulation on"),
            ("timescale", "changes the orbital time scale")),
        ["vegetation"] = Words(null, ("debug", null), ("set", "changes how many plants grow")),
        ["helperhints"] = Words(null, ("dismiss", null), ("complete", "completes world objectives"),
            ("trigger", "triggers world objectives")),
        ["liquid"] = Words(null, ("show", null), ("renderer", null), ("worldvolume", null),
            ("solver", "turns the liquid solver on or off")),
        ["roomevaluator"] = Words(null, ("status", null), ("pause", "pauses the room evaluator")),
        ["achievements"] = Words(null, ("award", "awards an achievement"), ("clear", "clears an achievement")),
        ["steam"] = arguments => arguments.Count == 0 ? null : "changes Steam achievements or stats",
        // serverrun sends the rest of the line to the server: it is what it runs.
        ["serverrun"] = arguments => arguments.Count == 0 ? null : WhyArguments(new List<string>(arguments)),
        // Terraforming Reloaded: the status and the listings are free, every change to the planet is a cheat.
        ["terraform"] = Terraform,

        // Read-only, diagnostic, interface and session commands: they change nothing in the world outside normal play.
        // stationgod is here so that ConsoleCommands refuses it as console_only, not as a cheat.
        ["stationgod"] = Free,
        ["help"] = Free, ["clear"] = Free, ["version"] = Free, ["status"] = Free, ["systeminfo"] = Free,
        ["world"] = Free, ["printgasinfo"] = Free, ["listnetworkdevices"] = Free, ["organs"] = Free,
        ["spacemapnode"] = Free, ["densepools"] = Free, ["debugthreads"] = Free, ["pylonlog"] = Free, ["atmos"] = Free,
        ["structurenetwork"] = Free, ["imgui"] = Free, ["camera"] = Free, ["keybindings"] = Free,
        ["logtoclipboard"] = Free, ["log"] = Free, ["windowheight"] = Free, ["discord"] = Free, ["upnp"] = Free,
        ["network"] = Free, ["masterserver"] = Free, ["proxy"] = Free, ["lod"] = Free, ["map"] = Free,
        ["region"] = Free, ["terrain"] = Free, ["minables"] = Free, ["reloadterraintexture"] = Free,
        ["terraineditor"] = Free, ["worldsetting"] = Free, ["thumbnail"] = Free, ["entity"] = Free,
        ["physics"] = Free, ["rocketbinary"] = Free, ["testbytearray"] = Free, ["testoctree"] = Free,
        ["prefabs"] = Free, ["localization"] = Free, ["dlc"] = Free, ["emote"] = Free, ["expression"] = Free,
        ["unstuck"] = Free, ["geyser"] = Free, ["exportworld"] = Free, ["regeneraterooms"] = Free,
        ["say"] = Free, ["announce"] = Free, ["kick"] = Free, ["ban"] = Free, ["clientinfo"] = Free,
        ["pause"] = Free, ["save"] = Free, ["file"] = Free, ["load"] = Free, ["loadgame"] = Free,
        ["loadlatest"] = Free, ["newgame"] = Free, ["new"] = Free, ["join"] = Free, ["joingame"] = Free,
        ["exit"] = Free, ["leave"] = Free, ["quit"] = Free, ["reset"] = Free, ["settings"] = Free,
        ["netconfig"] = Free, ["settingspath"] = Free, ["legacycpu"] = Free
    };

    /// <summary>
    /// The command a line runs, as a refusal names it: its key and, for a command with subcommands, the first one
    /// ("thing spawn"); null for an empty line.
    /// </summary>
    internal static string? Named(string? line)
    {
        List<string> words = Split(line);
        if (words.Count == 0)
        {
            return null;
        }

        if (words[0] == "serverrun" && words.Count > 1)
        {
            return "serverrun " + Named(string.Join(" ", words.GetRange(1, words.Count - 1)));
        }

        return words.Count > 1 && Rules.ContainsKey(words[0]) && !char.IsDigit(words[1][0])
            ? words[0] + " " + words[1]
            : words[0];
    }

    /// <summary>
    /// Why a console command line is a cheat, or null when it is not: read-only, diagnostic or normal play. An empty
    /// line is not (the command refuses it).
    /// </summary>
    internal static string? Why(string? line)
    {
        List<string> words = Split(line);
        return words.Count == 0 ? null : WhyArguments(words);
    }

    private static string? WhyArguments(List<string> words) =>
        Rules.TryGetValue(words[0], out Rule? rule)
            ? rule(words.GetRange(1, words.Count - 1))
            : $"'{words[0]}' is a command StationGod has not classified as read-only, so it counts as a cheat";

    private static Rule Cheat(string why) => _ => why;

    /// <summary>
    /// A command with subcommands: bare is the reason with none (null: free); each word is free (null) or a cheat for
    /// its reason; a word not listed is a cheat.
    /// </summary>
    private static Rule Words(string? bare, params (string Word, string? Why)[] words) => arguments =>
    {
        if (arguments.Count == 0)
        {
            return bare;
        }

        foreach ((string word, string? why) in words)
        {
            if (word == arguments[0])
            {
                return why;
            }
        }

        return Unlisted(arguments[0]);
    };

    // rocket: print, mounts, parks, debug and a relink preview read; relink apply, refresh and every creative helper
    // (refuel, recharge, instantmove, clone, abandon) change rockets.
    private static string? Rocket(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        switch (arguments[0])
        {
            case "print":
            case "mounts":
            case "parks":
            case "debug":
                return null;
            case "relink":
                return arguments.Count > 2 && arguments[2] == "apply" ? "relinks a rocket" : null;
            case "refuel":
                return "refuels a rocket";
            case "recharge":
                return "recharges a rocket";
            case "instantmove":
                return "moves a rocket at once";
            case "clone":
                return "clones a rocket";
            case "abandon":
                return "abandons a rocket";
            case "refresh":
                return "rebuilds the rocket and launch pad networks";
            default:
                return Unlisted(arguments[0]);
        }
    }

    // terraform: the status, set (the list) and set <key> (one value) read; set <key> <value>, size, reset, gas add or
    // remove and curves reload change the planet; curves export writes a file.
    private static string? Terraform(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        switch (arguments[0])
        {
            case "status":
                return null;
            case "set":
                return arguments.Count <= 2 ? null : "changes a terraforming setting of this world";
            case "size":
                return "changes the planet's size";
            case "reset":
                return "puts the whole planet back as the world ships";
            case "gas":
                return arguments.Count == 1 ? null : "puts gas into the planet or takes it out";
            case "curves":
                return arguments.Count > 1 && arguments[1] == "export" ? null : "reloads the temperature curves";
            default:
                return Unlisted(arguments[0]);
        }
    }

    private static string Unlisted(string word) =>
        $"'{word}' is a subcommand StationGod has not classified as read-only, so it counts as a cheat";

    // The words of a line as the console splits them (CmdLineParser: spaces, quotes kept together), lower case, with the
    // key's leading '-' dropped as CommandLine.Process does.
    private static List<string> Split(string? line)
    {
        List<string> words = new List<string>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return words;
        }

        bool quoted = false;
        System.Text.StringBuilder word = new System.Text.StringBuilder();
        foreach (char letter in line!)
        {
            if (letter == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(letter) && !quoted)
            {
                Add(words, word);
            }
            else
            {
                word.Append(letter);
            }
        }

        Add(words, word);
        if (words.Count > 0)
        {
            words[0] = words[0].TrimStart('-');
        }

        return words;
    }

    private static void Add(List<string> words, System.Text.StringBuilder word)
    {
        if (word.Length > 0)
        {
            words.Add(word.ToString().ToLowerInvariant());
            word.Clear();
        }
    }
}
