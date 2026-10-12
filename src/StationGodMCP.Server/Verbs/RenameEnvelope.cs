using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The rename envelope (protocol.md, Rename) sent on every handler call so the mod prints verb names: args maps each
/// handler argument the call's handler declares (nested keys too) to the option phrase a caller uses, from the same
/// argument tables LegacyVerbMap translates with; tools maps every handler to its verb phrase (verb {what: "x"}), the
/// what being called for its own handlers. Built once per what and handler.
/// </summary>
internal sealed class RenameEnvelope
{
    internal const int MostEntries = 256;

    private const int LongestPhrase = 128;

    private readonly VerbCatalogue _catalogue;
    private readonly HandlerSchemas _schemas;
    private readonly ConcurrentDictionary<string, JsonElement> _built = new(StringComparer.Ordinal);

    internal RenameEnvelope(VerbCatalogue catalogue, HandlerSchemas schemas)
    {
        _catalogue = catalogue;
        _schemas = schemas;
    }

    /// <summary>The envelope for one handler called for one what.</summary>
    internal JsonElement For(WhatEntry what, string handler) =>
        _built.GetOrAdd(what.Key + " " + handler, _ => JsonNodes.ToElement(Build(what, handler)));

    internal JsonObject Build(WhatEntry what, string handler)
    {
        JsonObject args = new();
        HashSet<string> names = _schemas.AllNames(handler);
        HandlerEntry? own = what.Handler(handler);
        foreach (string name in names)
        {
            string? phrase = PhraseIn(own, name) ?? PhraseInWhat(what, name) ?? SharedPhrase(name);
            if (phrase != null && phrase != name && Usable(phrase))
            {
                args[name] = phrase;
            }
        }

        // A job poll goes to another what's handler: its job_id is this what's id.
        if (names.Contains("job_id") && args["job_id"] == null && what.Options.ContainsKey("id"))
        {
            args["job_id"] = "id";
        }

        JsonObject tools = new();
        int room = MostEntries - args.Count;
        foreach (HandlerEntry mine in what.Handlers)
        {
            if (!mine.IsHub && tools.Count < room && mine.Name != what.Phrase)
            {
                tools[mine.Name] = what.Phrase;
            }
        }

        foreach ((string tool, string phrase) in _catalogue.HandlerPhrases)
        {
            if (tools.Count >= room)
            {
                break;
            }

            if (!tool.StartsWith(HandlerEntry.HubPrefix, StringComparison.Ordinal) && tools[tool] == null && tool != phrase)
            {
                tools[tool] = phrase;
            }
        }

        return new JsonObject { ["args"] = args, ["tools"] = tools };
    }

    private static string? PhraseIn(HandlerEntry? handler, string arg)
    {
        if (handler == null)
        {
            return null;
        }

        foreach (ArgEntry entry in handler.Args)
        {
            if (entry.Arg == arg && entry.Option.Length > 0)
            {
                return Phrase(entry);
            }
        }

        return null;
    }

    private static string? PhraseInWhat(WhatEntry what, string arg)
    {
        foreach (HandlerEntry handler in what.Handlers)
        {
            if (PhraseIn(handler, arg) is { } phrase)
            {
                return phrase;
            }
        }

        return null;
    }

    // The keys inside objects and list entries the verbs name alike everywhere.
    private static string? SharedPhrase(string arg) => arg switch
    {
        "reference_id" or "reference_ids" => "id",
        "network_id" => "network",
        "prefab_name" or "prefab_hash" => "prefab",
        "to_id" => "to",
        "to_slot" => "slot",
        "logic_type" => "logic",
        _ => null
    };

    /// <summary>
    /// How a caller says the option an argument comes from: the note's own wording when it starts with the option
    /// (include: ["inner"], allow: ["burst"], what_if: {fuel_mol}), near: "player" with radius for near_player_m, else
    /// the option's name.
    /// </summary>
    internal static string Phrase(ArgEntry entry)
    {
        if (entry.Option == "near+radius")
        {
            return "near: \"player\" with radius";
        }

        if (entry.Change is "merged" or "moved" && entry.Note is { } note &&
            note.StartsWith(entry.Option + ":", StringComparison.Ordinal))
        {
            int end = note.Length;
            int semicolon = note.IndexOf(';');
            int see = note.IndexOf(" (see", StringComparison.Ordinal);
            if (semicolon >= 0)
            {
                end = semicolon;
            }

            if (see >= 0 && see < end)
            {
                end = see;
            }

            return note[..end].Trim();
        }

        return entry.Option;
    }

    private static bool Usable(string phrase)
    {
        if (phrase.Length is 0 or > LongestPhrase)
        {
            return false;
        }

        foreach (char character in phrase)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}
