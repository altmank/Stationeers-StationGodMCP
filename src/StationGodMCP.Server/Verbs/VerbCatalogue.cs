using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The verb catalogue at run time: catalogue/verbs (one file per verb plus _shared.json) and generated/verbs (the tools
/// array with instructions, the help pages and the Python stubs), embedded in the server and read once. Options that
/// name a shared definition with "use" are resolved here, so every what carries its options whole.
/// </summary>
internal sealed class VerbCatalogue
{
    internal const string WhatlessKey = "-";

    /// <summary>The shaping options every verb but help and run_script takes (the tool list adds them).</summary>
    internal static readonly string[] Shaping = ["fields", "omit", "list_limits", "output_file", "whole", "repeat"];

    private const string SourcePrefix = "catalogue/verbs/";
    private const string GeneratedPrefix = "generated/verbs/";

    private static readonly Lazy<VerbCatalogue> EmbeddedCatalogue = new(Load);

    private readonly Dictionary<string, VerbEntry> _verbs;
    private readonly Dictionary<string, string> _help;
    private readonly Dictionary<string, string> _stubs;
    private readonly Dictionary<string, OptionEntry> _shaping;
    private readonly Dictionary<string, string> _handlerPhrases;
    private readonly Dictionary<string, List<ArgEntry>> _handlerArgs;

    private VerbCatalogue(List<VerbEntry> verbs, JsonElement tools, string instructions, Dictionary<string, string> help,
        Dictionary<string, string> stubs, Dictionary<string, OptionEntry> shaping, List<string> topics)
    {
        Verbs = verbs;
        Tools = tools;
        Instructions = instructions;
        Topics = topics;
        _help = help;
        _stubs = stubs;
        _shaping = shaping;
        _verbs = new Dictionary<string, VerbEntry>(StringComparer.Ordinal);
        VerbNames = [];
        _handlerPhrases = new Dictionary<string, string>(StringComparer.Ordinal);
        _handlerArgs = new Dictionary<string, List<ArgEntry>>(StringComparer.Ordinal);
        foreach (VerbEntry verb in verbs)
        {
            _verbs.Add(verb.Name, verb);
            VerbNames.Add(verb.Name);
            foreach (string whatName in verb.WhatNames)
            {
                WhatEntry what = verb.Whats[whatName];
                foreach (HandlerEntry handler in what.Handlers)
                {
                    // A handler several whats call is named by the first of them in catalogue order.
                    _handlerPhrases.TryAdd(handler.Name, what.Phrase);
                    if (!_handlerArgs.TryGetValue(handler.Name, out List<ArgEntry>? args))
                    {
                        args = [];
                        _handlerArgs.Add(handler.Name, args);
                    }

                    args.AddRange(handler.Args);
                }
            }
        }
    }

    /// <summary>The catalogue the server was built with.</summary>
    internal static VerbCatalogue Embedded => EmbeddedCatalogue.Value;

    internal IReadOnlyList<VerbEntry> Verbs { get; }

    internal List<string> VerbNames { get; }

    /// <summary>The MCP tools array, as generated/verbs/tools.json holds it.</summary>
    internal JsonElement Tools { get; }

    /// <summary>The initialize result's instructions.</summary>
    internal string Instructions { get; }

    /// <summary>The help topics (types.BuildJob and the like included), in catalogue order.</summary>
    internal List<string> Topics { get; }

    internal VerbEntry? Verb(string name) => _verbs.TryGetValue(name, out VerbEntry? verb) ? verb : null;

    /// <summary>A shaping option's definition, or null for a name that is not one.</summary>
    internal OptionEntry? ShapingOption(string name) => _shaping.TryGetValue(name, out OptionEntry? option) ? option : null;

    /// <summary>A generated help page by its file name without .txt ("find", "find.thing", "topic.paging").</summary>
    internal string? HelpPage(string name) => _help.TryGetValue(name, out string? text) ? text : null;

    /// <summary>A generated Python stub by its file name without .py ("write.logic", "undo").</summary>
    internal string? Stub(string name) => _stubs.TryGetValue(name, out string? text) ? text : null;

    /// <summary>How a caller says a handler: verb {what: "x"}, or the bare verb for a whatless one.</summary>
    internal string? HandlerPhrase(string handler) =>
        _handlerPhrases.TryGetValue(handler, out string? phrase) ? phrase : null;

    /// <summary>Every handler with its phrase, for the rename envelope's tools.</summary>
    internal IReadOnlyDictionary<string, string> HandlerPhrases => _handlerPhrases;

    /// <summary>Every argument entry any what gives the handler, for options a what's own table leaves out.</summary>
    internal IReadOnlyList<ArgEntry> HandlerArgs(string handler) =>
        _handlerArgs.TryGetValue(handler, out List<ArgEntry>? args) ? args : [];

    private static VerbCatalogue Load()
    {
        Dictionary<string, string> sources = new(StringComparer.Ordinal);
        Dictionary<string, string> help = new(StringComparer.Ordinal);
        Dictionary<string, string> stubs = new(StringComparer.Ordinal);
        JsonElement? toolsFile = null;
        Assembly assembly = typeof(VerbCatalogue).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            string name = resource.Replace('\\', '/');
            if (name.StartsWith(SourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            {
                sources[name[SourcePrefix.Length..^".json".Length]] = Text(assembly, resource);
            }
            else if (name.StartsWith(GeneratedPrefix + "help/", StringComparison.Ordinal))
            {
                help[name[(GeneratedPrefix.Length + 5)..^".txt".Length]] = Text(assembly, resource);
            }
            else if (name.StartsWith(GeneratedPrefix + "stubs/", StringComparison.Ordinal))
            {
                stubs[name[(GeneratedPrefix.Length + 6)..^".py".Length]] = Text(assembly, resource);
            }
            else if (name == GeneratedPrefix + "tools.json")
            {
                using JsonDocument document = JsonDocument.Parse(Text(assembly, resource));
                toolsFile = document.RootElement.Clone();
            }
        }

        if (toolsFile is not { } generated || !sources.TryGetValue("_shared", out string? sharedText))
        {
            throw new InvalidOperationException("The verb catalogue is not embedded in the server.");
        }

        JsonObject shared = JsonNode.Parse(sharedText)!.AsObject();
        JsonObject sharedOptions = shared["options"]!.AsObject();
        Dictionary<string, OptionEntry> shaping = new(StringComparer.Ordinal);
        foreach (string name in Shaping)
        {
            shaping.Add(name, OptionEntry.Of(name, sharedOptions[name]!.AsObject()));
        }

        List<string> topics = [];
        foreach ((string topic, JsonNode? _) in shared["topics"]!.AsObject())
        {
            topics.Add(topic);
        }

        List<VerbEntry> verbs = [];
        foreach (JsonNode? verbName in shared["verbs"]!.AsArray())
        {
            JsonObject source = JsonNode.Parse(sources[(string)verbName!])!.AsObject();
            verbs.Add(VerbEntry.Of(source, sharedOptions));
        }

        return new VerbCatalogue(verbs, generated.GetProperty("tools").Clone(),
            generated.GetProperty("instructions").GetString()!, help, stubs, shaping, topics);
    }

    // Line ends as the source files have them (git may check generated text out with CRLF).
    private static string Text(Assembly assembly, string resource)
    {
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream, new UTF8Encoding(false));
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}

/// <summary>One verb: its whats in catalogue order (a whatless verb has the one what "-").</summary>
internal sealed class VerbEntry
{
    private VerbEntry(string name, bool readOnly, Dictionary<string, WhatEntry> whats, List<string> whatNames)
    {
        Name = name;
        ReadOnly = readOnly;
        Whats = whats;
        WhatNames = whatNames;
        HasWhats = !(whatNames.Count == 1 && whatNames[0] == VerbCatalogue.WhatlessKey);
    }

    internal string Name { get; }

    internal bool ReadOnly { get; }

    internal bool HasWhats { get; }

    internal Dictionary<string, WhatEntry> Whats { get; }

    internal List<string> WhatNames { get; }

    /// <summary>Help and run_script take no shaping options.</summary>
    internal bool TakesShaping => Name != "help" && Name != "run_script";

    internal WhatEntry? What(string? name) =>
        Whats.TryGetValue(HasWhats ? name ?? string.Empty : VerbCatalogue.WhatlessKey, out WhatEntry? what) ? what : null;

    internal static VerbEntry Of(JsonObject source, JsonObject sharedOptions)
    {
        string name = (string)source["verb"]!;
        Dictionary<string, WhatEntry> whats = new(StringComparer.Ordinal);
        List<string> names = [];
        foreach ((string whatName, JsonNode? what) in source["whats"]!.AsObject())
        {
            whats.Add(whatName, WhatEntry.Of(name, whatName, what!.AsObject(), sharedOptions));
            names.Add(whatName);
        }

        return new VerbEntry(name, (bool?)source["read_only"] ?? true, whats, names);
    }
}

/// <summary>
/// One what: its options (resolved), its handlers with their argument tables, the write rules of a write what, how a
/// list of ids is taken (x-list), the reply's main list and the example.
/// </summary>
internal sealed class WhatEntry
{
    private WhatEntry(string verb, string name, Dictionary<string, OptionEntry> options, List<string> optionNames,
        List<HandlerEntry> handlers, WriteRules? write, string? listForm, string? mainList, JsonObject? example)
    {
        Verb = verb;
        Name = name;
        Options = options;
        OptionNames = optionNames;
        Handlers = handlers;
        Write = write;
        ListForm = listForm;
        MainList = mainList;
        Example = example;
        Phrase = name == VerbCatalogue.WhatlessKey ? verb : $"{verb} {{what: \"{name}\"}}";
        Key = name == VerbCatalogue.WhatlessKey ? verb : verb + "." + name;
        Label = name == VerbCatalogue.WhatlessKey ? verb : verb + " " + name;
    }

    internal string Verb { get; }

    /// <summary>The what's name; "-" for a whatless verb.</summary>
    internal string Name { get; }

    /// <summary>How a caller writes it: write {what: "logic"}, or undo.</summary>
    internal string Phrase { get; }

    /// <summary>Its help page and stub name: write.logic, or undo.</summary>
    internal string Key { get; }

    /// <summary>Its name in messages: write logic, or undo.</summary>
    internal string Label { get; }

    internal Dictionary<string, OptionEntry> Options { get; }

    internal List<string> OptionNames { get; }

    internal List<HandlerEntry> Handlers { get; }

    internal WriteRules? Write { get; }

    /// <summary>native, split, native-shape or one; null when the what takes no id list.</summary>
    internal string? ListForm { get; }

    internal string? MainList { get; }

    internal JsonObject? Example { get; }

    /// <summary>True when id takes a list here (x-list native, split or native-shape).</summary>
    internal bool TakesIdList =>
        Options.ContainsKey("id") && ListForm is "native" or "split" or "native-shape";

    internal HandlerEntry? Handler(string name)
    {
        foreach (HandlerEntry handler in Handlers)
        {
            if (handler.Name == name)
            {
                return handler;
            }
        }

        return null;
    }

    internal static WhatEntry Of(string verb, string name, JsonObject source, JsonObject sharedOptions)
    {
        Dictionary<string, OptionEntry> options = new(StringComparer.Ordinal);
        List<string> optionNames = [];
        foreach ((string optionName, JsonNode? node) in source["options"] as JsonObject ?? [])
        {
            options.Add(optionName, OptionEntry.Of(optionName, Resolve(node!.AsObject(), sharedOptions)));
            optionNames.Add(optionName);
        }

        List<HandlerEntry> handlers = [];
        foreach (JsonNode? handler in source["handlers"] as JsonArray ?? [])
        {
            handlers.Add(HandlerEntry.Of(handler!.AsObject()));
        }

        WriteRules? write = source["write"] is JsonObject rules ? WriteRules.Of(rules) : null;
        string? listForm = write?.ListForm ?? (string?)source["x-list"];
        return new WhatEntry(verb, name, options, optionNames, handlers, write, listForm,
            (string?)source["reply"]?["list"], source["example"] as JsonObject);
    }

    // A shared definition with the what's own keys replacing its keys.
    private static JsonObject Resolve(JsonObject option, JsonObject sharedOptions)
    {
        if ((string?)option["use"] is not { } use)
        {
            return option;
        }

        JsonObject resolved = sharedOptions[use]!.DeepClone().AsObject();
        foreach ((string key, JsonNode? value) in option)
        {
            if (key != "use")
            {
                resolved[key] = value?.DeepClone();
            }
        }

        return resolved;
    }
}

/// <summary>One option: its schema, how a script takes it (x-script: drop, same, or a text) and whether it is required.</summary>
internal sealed class OptionEntry
{
    private OptionEntry(string name, JsonObject schema, string xScript, bool required)
    {
        Name = name;
        Schema = schema;
        XScript = xScript;
        Required = required;
    }

    internal string Name { get; }

    internal JsonObject Schema { get; }

    internal string XScript { get; }

    internal bool Required { get; }

    /// <summary>Refused on the script face.</summary>
    internal bool DroppedInScripts => XScript == "drop";

    /// <summary>The enum values of the option or of its list's items; empty when it lists none.</summary>
    internal List<string> EnumValues()
    {
        List<string> values = [];
        JsonArray? listed = Schema["enum"] as JsonArray ?? Schema["items"]?["enum"] as JsonArray;
        foreach (JsonNode? value in listed ?? [])
        {
            if (value is JsonValue text && text.TryGetValue(out string? word))
            {
                values.Add(word);
            }
        }

        return values;
    }

    internal static OptionEntry Of(string name, JsonObject definition) =>
        new(name, definition["schema"] as JsonObject ?? new JsonObject(), (string?)definition["x-script"] ?? "same",
            (bool?)definition["required"] ?? false);
}

/// <summary>One handler a what calls: today's method, when it is the one, and how options become its arguments.</summary>
internal sealed class HandlerEntry
{
    internal const string HubPrefix = "hub:";

    private HandlerEntry(string name, string? when, string? pending, List<ArgEntry> args)
    {
        Name = name;
        When = when;
        Pending = pending;
        Args = args;
    }

    internal string Name { get; }

    internal string? When { get; }

    /// <summary>Set when the handler is not in the mod yet.</summary>
    internal string? Pending { get; }

    internal List<ArgEntry> Args { get; }

    /// <summary>A hub:... handler, answered by the hub rather than the mod.</summary>
    internal bool IsHub => Name.StartsWith(HubPrefix, StringComparison.Ordinal);

    internal static HandlerEntry Of(JsonObject source)
    {
        List<ArgEntry> args = [];
        foreach (JsonNode? arg in source["args"] as JsonArray ?? [])
        {
            args.Add(ArgEntry.Of(arg!.AsObject()));
        }

        return new HandlerEntry((string)source["name"]!, (string?)source["when"], (string?)source["pending"], args);
    }
}

/// <summary>
/// One row of a handler's argument table: the handler argument, the option it comes from ("near+radius" for two), and
/// the change: kept, renamed, merged, moved, default-changed or dropped, with its note and the default before and now.
/// </summary>
internal sealed class ArgEntry
{
    private ArgEntry(string arg, string option, string change, string? note, string? defaultNow)
    {
        Arg = arg;
        Option = option;
        Change = change;
        Note = note;
        DefaultNow = defaultNow;
    }

    internal string Arg { get; }

    internal string Option { get; }

    internal string Change { get; }

    internal string? Note { get; }

    /// <summary>The default the verb states now (default-changed only).</summary>
    internal string? DefaultNow { get; }

    internal static ArgEntry Of(JsonObject source) =>
        new((string)source["arg"]!, (string?)source["option"] ?? string.Empty, (string)source["change"]!,
            (string?)source["note"], source["default"] is JsonArray { Count: 2 } given ? (string?)given[1] : null);
}

/// <summary>A write what's rules: report class, route, undo class, list form and the target paths.</summary>
internal sealed class WriteRules
{
    private WriteRules(string report, string route, string undo, string listForm, List<TargetPath> targets)
    {
        Report = report;
        Route = route;
        Undo = undo;
        ListForm = listForm;
        Targets = targets;
    }

    internal string Report { get; }

    internal string Route { get; }

    internal string Undo { get; }

    internal string ListForm { get; }

    internal List<TargetPath> Targets { get; }

    internal static WriteRules Of(JsonObject source)
    {
        List<TargetPath> targets = [];
        foreach (JsonNode? target in source["x-target"] as JsonArray ?? [])
        {
            targets.Add(new TargetPath((string)target!["path"]!, (string)target["role"]!));
        }

        return new WriteRules((string)source["report"]!, (string)source["route"]!, (string)source["x-undo"]!,
            (string)source["x-list"]!, targets);
    }
}

/// <summary>An x-target path ("id", "writes[].id") and its role (acts-on or pays-from).</summary>
internal sealed class TargetPath(string path, string role)
{
    internal string Path { get; } = path;

    internal string Role { get; } = role;
}
