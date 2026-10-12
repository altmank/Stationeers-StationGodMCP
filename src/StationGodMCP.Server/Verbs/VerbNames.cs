using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StationGodMCP.Server;

/// <summary>
/// The names table of the rename envelope, applied by the hub itself to texts the mod's rename map does not reach: the
/// replies and refusals of protocol methods (inspect_controllers, sample_logic's sampler, subscribe, hold_restore,
/// drop_job, stream_costs, catalogue) and anything else a handler name slips through in. Each handler tool becomes its
/// verb phrase and each handler argument no option is named after becomes the option phrase, as whole words. Only
/// names with an underscore are mapped: one-word names (label, connections) are ordinary words and what names too.
/// </summary>
internal sealed class VerbNames
{
    private static readonly Lazy<VerbNames> Built = new(() => new VerbNames(VerbCatalogue.Embedded));

    // Keys whose string values are texts for a reader: messages, warnings, notes and fixes, at any depth.
    private static readonly string[] TextKeys = ["message", "messages", "warning", "warnings", "note", "notes", "fix",
        "problem", "problems", "hint", "error", "errors", "reason"];

    // The protocol methods a what goes to, by their verb phrase.
    private static readonly (string Method, string Phrase)[] ProtocolMethods =
        [("inspect_controllers", "inspect {what: \"controllers\"}")];

    private readonly Dictionary<string, string> _phrases = new(StringComparer.Ordinal);
    private readonly Regex? _names;

    internal VerbNames(VerbCatalogue catalogue)
    {
        HashSet<string> options = new(StringComparer.Ordinal);
        foreach (VerbEntry verb in catalogue.Verbs)
        {
            foreach (string whatName in verb.WhatNames)
            {
                WhatEntry what = verb.Whats[whatName];
                options.UnionWith(what.OptionNames);
                options.Add(what.Name);
            }
        }

        options.UnionWith(VerbCatalogue.Shaping);
        foreach ((string handler, string phrase) in catalogue.HandlerPhrases)
        {
            if (handler.Contains('_') && !handler.StartsWith(HandlerEntry.HubPrefix, StringComparison.Ordinal) &&
                !options.Contains(handler))
            {
                _phrases.TryAdd(handler, phrase);
            }
        }

        foreach ((string method, string phrase) in ProtocolMethods)
        {
            _phrases.TryAdd(method, phrase);
        }

        foreach (VerbEntry verb in catalogue.Verbs)
        {
            foreach (string whatName in verb.WhatNames)
            {
                foreach (HandlerEntry handler in verb.Whats[whatName].Handlers)
                {
                    foreach (ArgEntry entry in handler.Args)
                    {
                        if (entry.Arg.Contains('_') && !options.Contains(entry.Arg) && entry.Option.Length > 0)
                        {
                            _phrases.TryAdd(entry.Arg, RenameEnvelope.Phrase(entry));
                        }
                    }
                }
            }
        }

        foreach ((string arg, string phrase) in Shared)
        {
            _phrases.TryAdd(arg, phrase);
        }

        List<string> names = new(_phrases.Keys);
        names.Sort(static (a, b) => b.Length.CompareTo(a.Length));
        StringBuilder pattern = new(@"(?<![A-Za-z0-9_])(");
        for (int index = 0; index < names.Count; index++)
        {
            pattern.Append(index == 0 ? string.Empty : "|").Append(Regex.Escape(names[index]));
        }

        pattern.Append(@")(?![A-Za-z0-9_])");
        _names = names.Count == 0 ? null : new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
    }

    // The keys inside entries the verbs name alike everywhere.
    private static readonly (string Arg, string Phrase)[] Shared =
    [
        ("reference_id", "id"), ("reference_ids", "id"), ("network_id", "network"), ("prefab_name", "prefab"),
        ("prefab_hash", "prefab"), ("to_id", "to"), ("to_slot", "slot"), ("logic_type", "logic")
    ];

    internal static VerbNames Default => Built.Value;

    /// <summary>The text with every handler name in it said in verb names.</summary>
    internal string Say(string text) =>
        _names == null || text.Length == 0 ? text : _names.Replace(text, match => _phrases[match.Value]);

    /// <summary>The texts of a reply or error object (messages, warnings, notes, fixes), said in verb names in place.</summary>
    internal JsonNode? SayAll(JsonNode? node)
    {
        Walk(node, false);
        return node;
    }

    private void Walk(JsonNode? node, bool text)
    {
        switch (node)
        {
            case JsonObject entry:
                List<(string Key, string Value)> said = [];
                foreach ((string key, JsonNode? value) in entry)
                {
                    bool isText = text || Array.IndexOf(TextKeys, key) >= 0;
                    if (isText && JsonNodes.Text(value) is { } words)
                    {
                        string mapped = Say(words);
                        if (!ReferenceEquals(mapped, words) && mapped != words)
                        {
                            said.Add((key, mapped));
                        }
                    }
                    else
                    {
                        Walk(value, isText);
                    }
                }

                foreach ((string key, string value) in said)
                {
                    entry[key] = value;
                }

                break;
            case JsonArray list:
                for (int index = 0; index < list.Count; index++)
                {
                    if (text && JsonNodes.Text(list[index]) is { } words)
                    {
                        list[index] = Say(words);
                    }
                    else
                    {
                        Walk(list[index], text);
                    }
                }

                break;
        }
    }
}
