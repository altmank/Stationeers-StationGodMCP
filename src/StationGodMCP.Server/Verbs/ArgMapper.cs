using System.Text;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>A handler's arguments made from a verb call's options, and what could not be made.</summary>
internal sealed class MappedArgs
{
    internal MappedArgs(JsonObject args, List<OptionProblem> problems, List<string> unavailable, bool takesShaping)
    {
        Args = args;
        Problems = problems;
        Unavailable = unavailable;
        TakesShaping = takesShaping;
    }

    internal JsonObject Args { get; }

    /// <summary>Options the call gave that do not go with this handler or this form.</summary>
    internal List<OptionProblem> Problems { get; }

    /// <summary>Options no handler argument takes yet (the verb layer brings them).</summary>
    internal List<string> Unavailable { get; }

    /// <summary>The handler takes fields, omit, list_limits and output_file (its reply can be large).</summary>
    internal bool TakesShaping { get; }
}

/// <summary>
/// Maps a verb call's options onto one handler's arguments by the what's argument table (catalogue/verbs, handlers[].args):
/// kept and renamed options are copied (one id or a list to the argument of that kind), merged and moved ones
/// transformed as each note says, defaults the verb changed filled in, and keys inside objects and list entries renamed
/// to the handler's (id to reference_id, logic to logic_type). The options the table leaves out are taken by the same
/// handler's row in another what, or by an argument of the same name, or reported.
/// </summary>
internal sealed class ArgMapper
{
    internal static readonly string[] ShapingArgs = ["fields", "omit", "list_limits", "output_file"];

    // Options the pipeline takes itself: never sent, never reported unmapped.
    private static readonly string[] HubOptions =
        ["fields", "omit", "list_limits", "output_file", "whole", "repeat", "max_wait", "confirm"];

    private static readonly string[] TokenOptions = ["include", "allow", "avoid", "parts"];

    private static readonly string[] TokenPrefixes = ["include_", "allow_", "avoid_"];

    private readonly VerbCatalogue _catalogue;
    private readonly HandlerSchemas _schemas;

    internal ArgMapper(VerbCatalogue catalogue, HandlerSchemas schemas)
    {
        _catalogue = catalogue;
        _schemas = schemas;
    }

    internal MappedArgs Map(WhatEntry what, HandlerEntry handler, JsonObject options)
    {
        JsonObject args = new();
        List<OptionProblem> problems = [];
        List<string> unavailable = [];
        HashSet<string> used = new(StringComparer.Ordinal);
        Dictionary<string, List<ArgEntry>> plain = new(StringComparer.Ordinal);
        List<string> prefabArgs = [];
        bool takesShaping = false;
        bool hasNearPlayer = false;
        bool hasPlainNear = false;
        bool nearPlayer = JsonNodes.Text(options["near"]) is { } centre &&
                          string.Equals(centre.Trim(), "player", StringComparison.OrdinalIgnoreCase) && options["radius"] != null;

        foreach (ArgEntry entry in handler.Args)
        {
            if (entry.Change == "dropped")
            {
                continue;
            }

            if (Array.IndexOf(ShapingArgs, entry.Option) >= 0)
            {
                takesShaping = true;
                continue;
            }

            switch (entry.Option)
            {
                case "near+radius":
                    hasNearPlayer = true;
                    used.Add("near");
                    used.Add("radius");
                    if (nearPlayer)
                    {
                        args[entry.Arg] = options["radius"]!.DeepClone();
                    }

                    continue;
                case "confirm" when entry.Arg == "dry_run":
                    // dry_run and confirm are one confirm: false or left out reports only.
                    used.Add("confirm");
                    args["dry_run"] = !JsonNodes.IsTrue(options["confirm"]);
                    continue;
                case "box" when entry.Arg is "min" or "max":
                    used.Add("box");
                    if (options["box"] is JsonArray { Count: 2 } corners)
                    {
                        args[entry.Arg] = corners[entry.Arg == "min" ? 0 : 1]?.DeepClone();
                    }

                    continue;
                case "prefab":
                    used.Add("prefab");
                    prefabArgs.Add(entry.Arg);
                    continue;
                case "what_if":
                    used.Add("what_if");
                    if (options["what_if"] is JsonObject asked && asked[entry.Arg] is { } part)
                    {
                        args[entry.Arg] = part.DeepClone();
                    }

                    continue;
                case "refund_to" when entry.Arg == "refund":
                    // refund: false is refund_to: "none", which refund_to itself carries.
                    continue;
            }

            if (entry.Change is "merged" or "moved" && Array.IndexOf(TokenOptions, entry.Option) >= 0)
            {
                used.Add(entry.Option);
                if (JsonNodes.Holds(options[entry.Option], Token(what, entry)))
                {
                    args[entry.Arg] = true;
                }

                continue;
            }

            if (entry.Option == "near")
            {
                hasPlainNear = true;
            }

            if (!plain.TryGetValue(entry.Option, out List<ArgEntry>? entries))
            {
                entries = [];
                plain.Add(entry.Option, entries);
            }

            entries.Add(entry);
        }

        if (options["prefab"] is { } prefab && prefabArgs.Count > 0)
        {
            Prefab(prefab, prefabArgs, args, problems, PrefabIsAPart(handler));
        }

        List<(string Arg, JsonNode Value)> fills = [];
        foreach ((string option, List<ArgEntry> entries) in plain)
        {
            used.Add(option);
            if (nearPlayer && option is "near" or "radius")
            {
                continue;
            }

            Place(handler.Name, option, options[option], entries, args, problems, what, fills);
        }

        // A default the verb now states is filled in, unless the call takes the list form whose entries carry it
        // (move item's moves: each move has its own slot).
        foreach ((string arg, JsonNode filled) in fills)
        {
            if (!CarriedByEntries(handler.Name, arg, args))
            {
                args[arg] = filled;
            }
        }

        // radius alone would widen the call to the whole world: it needs its centre.
        if (options["radius"] != null && options["near"] == null && (hasNearPlayer || hasPlainNear))
        {
            problems.Add(new OptionProblem("radius", hasPlainNear ? "needs near" : "needs near: \"player\" here"));
        }

        if (options["what_if"] is JsonObject whatIf)
        {
            foreach ((string key, JsonNode? _) in whatIf)
            {
                if (!MapsFrom(handler, "what_if", key))
                {
                    problems.Add(new OptionProblem("what_if." + key, "not a what_if of " + what.Label,
                        Nearest.Of(key, ArgsFrom(handler, "what_if"))));
                }
            }
        }

        if (hasNearPlayer && !hasPlainNear && options["near"] != null && !nearPlayer)
        {
            problems.Add(JsonNodes.Text(options["near"]) is { } given &&
                         string.Equals(given.Trim(), "player", StringComparison.OrdinalIgnoreCase)
                ? new OptionProblem("radius", "required with near: \"player\"")
                : new OptionProblem("near", "takes only \"player\" (with radius) here"));
        }

        Unmapped(what, handler, options, used, args, problems, unavailable);
        return new MappedArgs(args, problems, unavailable, takesShaping);
    }

    // Options the table leaves out: another handler's (a problem), the handler's row in another what, a same-named
    // argument, or none yet.
    private void Unmapped(WhatEntry what, HandlerEntry handler, JsonObject options, HashSet<string> used, JsonObject args,
        List<OptionProblem> problems, List<string> unavailable)
    {
        foreach ((string name, JsonNode? value) in options)
        {
            if (value == null || used.Contains(name) || Array.IndexOf(HubOptions, name) >= 0)
            {
                continue;
            }

            if (MappedElsewhere(what, handler, name) is { } other)
            {
                problems.Add(new OptionProblem(name, $"not taken with {handler.When ?? "this form"} (it goes with {other})"));
                continue;
            }

            List<ArgEntry> shared = [];
            bool switched = false;
            foreach (ArgEntry entry in _catalogue.HandlerArgs(handler.Name))
            {
                if (entry.Option != name)
                {
                    continue;
                }

                if (entry.Change is "merged" or "moved" && Array.IndexOf(TokenOptions, name) >= 0)
                {
                    switched = true;
                    if (JsonNodes.Holds(value, Token(what, entry)))
                    {
                        args[entry.Arg] = true;
                    }
                }
                else if (entry.Change is "kept" or "renamed" or "default-changed" or "moved")
                {
                    shared.Add(entry);
                }
            }

            if (shared.Count > 0)
            {
                Place(handler.Name, name, value, shared, args, problems, what);
                continue;
            }

            if (switched)
            {
                continue;
            }

            if (_schemas.Arg(handler.Name, name) is { } same)
            {
                args[name] = Convert(value, same, what);
                continue;
            }

            if (Switches(handler.Name, name, value, args))
            {
                continue;
            }

            unavailable.Add(name);
        }
    }

    // An include or allow word the tables leave out that the handler takes as its own switch (allow: ["lint"] is
    // write logic's allow_lint): every word must be one.
    private bool Switches(string handler, string option, JsonNode value, JsonObject args)
    {
        if (Array.IndexOf(TokenOptions, option) < 0 || value is not JsonArray words || words.Count == 0)
        {
            return false;
        }

        List<string> found = [];
        foreach (JsonNode? word in words)
        {
            string? arg = JsonNodes.Text(word) is { } text ? option + "_" + text.Trim().ToLowerInvariant() : null;
            if (arg == null || !HandlerSchemas.HasType(_schemas.Arg(handler, arg), "boolean"))
            {
                return false;
            }

            found.Add(arg);
        }

        foreach (string arg in found)
        {
            args[arg] = true;
        }

        return true;
    }

    // The when of another handler of the what whose table takes the option; null when none does.
    private static string? MappedElsewhere(WhatEntry what, HandlerEntry handler, string option)
    {
        foreach (HandlerEntry other in what.Handlers)
        {
            if (other == handler)
            {
                continue;
            }

            foreach (ArgEntry entry in other.Args)
            {
                if (entry.Option == option || entry.Option.Contains(option + "+", StringComparison.Ordinal))
                {
                    return other.When ?? "another form";
                }
            }
        }

        return null;
    }

    // One option onto the argument of its kind: a list to a list argument, one value to a one-value argument.
    private void Place(string handler, string option, JsonNode? value, List<ArgEntry> entries, JsonObject args,
        List<OptionProblem> problems, WhatEntry what, List<(string Arg, JsonNode Value)>? fills = null)
    {
        fills ??= [];
        if (value == null)
        {
            foreach (ArgEntry entry in entries)
            {
                if (entry.Change == "default-changed" && Literal(entry.DefaultNow) is { } filled &&
                    _schemas.Arg(handler, entry.Arg) != null)
                {
                    fills.Add((entry.Arg, filled));
                }
            }

            return;
        }

        ArgEntry chosen = entries[0];
        bool isList = value is JsonArray;
        foreach (ArgEntry entry in entries)
        {
            if (HandlerSchemas.IsList(_schemas.Arg(handler, entry.Arg)) == isList)
            {
                chosen = entry;
                break;
            }
        }

        JsonObject? schema = _schemas.Arg(handler, chosen.Arg);
        bool takesList = HandlerSchemas.IsList(schema);
        if (takesList && !isList && !HandlerSchemas.HasType(schema, "string") && !HandlerSchemas.HasType(schema, "object"))
        {
            value = new JsonArray(value.DeepClone());
        }
        else if (!takesList && isList && Typed(schema))
        {
            JsonArray list = value.AsArray();
            if (list.Count != 1)
            {
                problems.Add(new OptionProblem(option, $"takes one value with {what.Label} here; a list of {list.Count} was given"));
                return;
            }

            value = list[0];
        }

        if (value != null)
        {
            args[chosen.Arg] = Convert(value, schema, what);
        }
    }

    private bool CarriedByEntries(string handler, string arg, JsonObject args)
    {
        foreach ((string name, JsonNode? value) in args)
        {
            if (value is JsonArray && HandlerSchemas.Properties(_schemas.Arg(handler, name)?["items"] as JsonObject) is { } item &&
                item.ContainsKey(arg))
            {
                return true;
            }
        }

        return false;
    }

    // A schema that says what it takes (a type or forms); one that only describes passes any value on.
    private static bool Typed(JsonObject? schema) =>
        schema != null && (OptionCheck.TypesOf(schema).Count > 0 || schema["oneOf"] is JsonArray);

    // A default the verb states as a literal is filled in where the handler's own default differs.
    private static JsonNode? Literal(string? stated) => stated switch
    {
        "true" => JsonValue.Create(true),
        "false" => JsonValue.Create(false),
        "auto" => JsonValue.Create("auto"),
        _ => null
    };

    private static bool MapsFrom(HandlerEntry handler, string option, string arg)
    {
        foreach (ArgEntry entry in handler.Args)
        {
            if (entry.Option == option && entry.Arg == arg)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ArgsFrom(HandlerEntry handler, string option)
    {
        List<string> args = [];
        foreach (ArgEntry entry in handler.Args)
        {
            if (entry.Option == option)
            {
                args.Add(entry.Arg);
            }
        }

        return args;
    }

    // Where prefab moved onto a part argument (survey network: "a part with *"), a name without * is that part.
    private static bool PrefabIsAPart(HandlerEntry handler)
    {
        foreach (ArgEntry entry in handler.Args)
        {
            if (entry.Option == "prefab" && entry.Arg == "prefab_contains" && entry.Change == "moved")
            {
                return true;
            }
        }

        return false;
    }

    // prefab: a whole name, a hash, a part written with *, or a list of names, each onto the argument that takes it.
    private static void Prefab(JsonNode value, List<string> available, JsonObject args, List<OptionProblem> problems,
        bool asPart = false)
    {
        bool Has(string arg) => available.Contains(arg);

        if (value is JsonArray list)
        {
            if (list.Count == 1 && list[0] is { } only)
            {
                Prefab(only, available, args, problems, asPart);
                return;
            }

            if (!Has("prefabs"))
            {
                problems.Add(new OptionProblem("prefab", "takes one value here"));
                return;
            }

            JsonArray names = [];
            foreach (JsonNode? entry in list)
            {
                if (JsonNodes.Text(entry) is not { } name || name.Contains('*'))
                {
                    problems.Add(new OptionProblem("prefab", "a list takes whole prefab names here"));
                    return;
                }

                names.Add(name);
            }

            args["prefabs"] = names;
            return;
        }

        if (JsonNodes.Text(value) is { } text)
        {
            if (text.Contains('*'))
            {
                List<string> parts = PrefabParts(text);
                if (!Has("prefab_contains"))
                {
                    problems.Add(new OptionProblem("prefab", "parts with * are not taken here; give the whole name"));
                }
                else if (parts.Count != 1)
                {
                    problems.Add(new OptionProblem("prefab", "takes one part with * here ('*Battery*')"));
                }
                else
                {
                    args["prefab_contains"] = parts[0];
                }

                return;
            }

            if (Has("prefab"))
            {
                args["prefab"] = text;
            }
            else if (Has("prefab_name"))
            {
                args["prefab_name"] = text;
            }
            else if (Has("prefab_hash"))
            {
                // A name is resolved to its hash, as the game's HASH() does.
                args["prefab_hash"] = PrefabHash(text);
            }
            else if (Has("prefabs"))
            {
                args["prefabs"] = new JsonArray(JsonValue.Create(text));
            }
            else if (asPart)
            {
                args["prefab_contains"] = text;
            }
            else
            {
                // Only a part is matched here: a whole name would match every prefab that holds it.
                problems.Add(new OptionProblem("prefab", "a whole name is not matched exactly here; give a part",
                    ["*" + text + "*"]));
            }

            return;
        }

        if (JsonNodes.Number(value) is { } hash)
        {
            if (Has("prefab_hash"))
            {
                args["prefab_hash"] = (long)hash;
            }
            else
            {
                problems.Add(new OptionProblem("prefab", "a hash is not taken here; give the prefab name"));
            }
        }
    }

    /// <summary>The parts of a prefab written with * ('*Tank*Insulated*': Tank, Insulated).</summary>
    internal static List<string> PrefabParts(string text)
    {
        List<string> parts = [];
        foreach (string part in text.Split('*'))
        {
            if (part.Trim().Length > 0)
            {
                parts.Add(part.Trim());
            }
        }

        return parts;
    }

    /// <summary>The game's HASH(): Unity's Animator.StringToHash, a CRC-32 of the UTF-8 bytes, as a signed int.</summary>
    internal static int PrefabHash(string text)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in Encoding.UTF8.GetBytes(text))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return unchecked((int)~crc);
    }

    /// <summary>
    /// The word in an include, allow, avoid or parts list that sets a handler's switch: the argument's own name less its
    /// prefix when the option lists it (allow_in_use: in_use), else the word the note quotes (force: allow: ["burst"]).
    /// </summary>
    internal static string Token(WhatEntry what, ArgEntry entry)
    {
        List<string> listed = what.Options.TryGetValue(entry.Option, out OptionEntry? option) ? option.EnumValues() : [];
        string stripped = entry.Arg;
        foreach (string prefix in TokenPrefixes)
        {
            if (stripped.StartsWith(prefix, StringComparison.Ordinal))
            {
                stripped = stripped[prefix.Length..];
                break;
            }
        }

        if (listed.Contains(stripped))
        {
            return stripped;
        }

        if (listed.Contains(entry.Arg))
        {
            return entry.Arg;
        }

        List<string> quoted = Quoted(entry.Note);
        foreach (string word in quoted)
        {
            if (listed.Contains(word))
            {
                return word;
            }
        }

        return quoted.Count > 0 ? quoted[0] : stripped;
    }

    private static List<string> Quoted(string? note)
    {
        List<string> words = [];
        if (note == null)
        {
            return words;
        }

        int at = 0;
        while ((at = note.IndexOf('"', at)) >= 0)
        {
            int end = note.IndexOf('"', at + 1);
            if (end < 0)
            {
                break;
            }

            words.Add(note[(at + 1)..end]);
            at = end + 1;
        }

        return words;
    }

    /// <summary>
    /// A value with the keys of its objects (and of its lists' objects) in the handler's names: a key the schema
    /// declares stays; another becomes the argument the what's tables map it to, or a shared one (id to reference_id or
    /// reference_ids by kind, network to network_id, prefab to prefab_name or prefab_hash, to to to_id, slot to to_slot,
    /// logic to logic_type). An object whose schema declares no keys gets only id, ids and network renamed.
    /// </summary>
    internal JsonNode Convert(JsonNode value, JsonObject? schema, WhatEntry what)
    {
        switch (value)
        {
            case JsonArray list:
            {
                JsonObject? items = schema?["items"] as JsonObject;
                JsonArray converted = [];
                foreach (JsonNode? item in list)
                {
                    converted.Add(item == null ? null : Convert(item, items, what));
                }

                return converted;
            }
            case JsonObject entry:
            {
                JsonObject? properties = HandlerSchemas.Properties(schema);
                if (properties == null && schema?["additionalProperties"] is JsonObject)
                {
                    // A map of free keys (genes, pins): keys are data, not names.
                    return entry.DeepClone();
                }

                JsonObject converted = new();
                foreach ((string key, JsonNode? inner) in entry)
                {
                    if (inner == null || properties?.ContainsKey(key) == true)
                    {
                        converted[key] = inner == null ? null : Convert(inner, properties?[key] as JsonObject, what);
                        continue;
                    }

                    if (NestedToken(what, key, properties) is { } token)
                    {
                        if (JsonNodes.Holds(inner, token.Word))
                        {
                            converted[token.Arg] = true;
                        }

                        continue;
                    }

                    string name = NestedName(what, key, inner, properties) ?? key;
                    converted[name] = Convert(inner, properties?[name] as JsonObject, what);
                }

                return converted;
            }
            default:
                return value.DeepClone();
        }
    }

    private static (string Arg, string Word)? NestedToken(WhatEntry what, string key, JsonObject? properties)
    {
        if (properties == null || Array.IndexOf(TokenOptions, key) < 0)
        {
            return null;
        }

        foreach (HandlerEntry handler in what.Handlers)
        {
            foreach (ArgEntry entry in handler.Args)
            {
                if (entry.Option == key && entry.Change == "merged" && properties.ContainsKey(entry.Arg))
                {
                    return (entry.Arg, Token(what, entry));
                }
            }
        }

        return null;
    }

    private static string? NestedName(WhatEntry what, string key, JsonNode value, JsonObject? properties)
    {
        bool isList = value is JsonArray;
        if (properties != null)
        {
            string? fallback = null;
            foreach (HandlerEntry handler in what.Handlers)
            {
                foreach (ArgEntry entry in handler.Args)
                {
                    if (entry.Option != key || entry.Arg == key || !properties.ContainsKey(entry.Arg))
                    {
                        continue;
                    }

                    if (HandlerSchemas.IsList(properties[entry.Arg] as JsonObject) == isList)
                    {
                        return entry.Arg;
                    }

                    fallback ??= entry.Arg;
                }
            }

            if (fallback != null)
            {
                return fallback;
            }
        }

        string kind = OptionCheck.KindOf(value);
        string? shared = key switch
        {
            "id" => isList ? "reference_ids" : "reference_id",
            "ids" => "reference_ids",
            "network" => "network_id",
            "prefab" when properties != null => kind == "integer" ? "prefab_hash" : "prefab_name",
            "to" when properties != null => "to_id",
            "slot" when properties != null => "to_slot",
            "logic" when properties != null => "logic_type",
            _ => null
        };
        return shared != null && (properties == null || properties.ContainsKey(shared)) ? shared : null;
    }
}
