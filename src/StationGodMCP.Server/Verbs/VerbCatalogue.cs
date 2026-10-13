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
        foreach (VerbEntry verb in verbs)
        {
            _verbs.Add(verb.Name, verb);
            VerbNames.Add(verb.Name);
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
/// One what: its options (resolved), the write rules of a write what, the list option an id list goes into (id_list),
/// the reply's main list and the example.
/// </summary>
internal sealed class WhatEntry
{
    private WhatEntry(string verb, string name, Dictionary<string, OptionEntry> options, List<string> optionNames,
        WriteRules? write, string? idList, string? mainList, JsonObject? example)
    {
        Verb = verb;
        Name = name;
        Options = options;
        OptionNames = optionNames;
        Write = write;
        IdList = idList;
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

    internal WriteRules? Write { get; }

    /// <summary>The list option whose entries an id list becomes (move item's moves); null where it answers rows.</summary>
    internal string? IdList { get; }

    internal string? MainList { get; }

    internal JsonObject? Example { get; }

    /// <summary>True when the id option takes a list of ids.</summary>
    internal bool IdTakesList => Options.TryGetValue("id", out OptionEntry? id) && id.Takes("array");

    /// <summary>True when an id list answers one row per id (D13): the id takes a list and no id_list folds it.</summary>
    internal bool TakesIdList => IdTakesList && IdList == null;

    internal static WhatEntry Of(string verb, string name, JsonObject source, JsonObject sharedOptions)
    {
        Dictionary<string, OptionEntry> options = new(StringComparer.Ordinal);
        List<string> optionNames = [];
        foreach ((string optionName, JsonNode? node) in source["options"] as JsonObject ?? [])
        {
            JsonObject resolved = Resolve(node!.AsObject(), sharedOptions);
            if ((bool?)resolved["x-wire"] == true)
            {
                // A wire option (x-wire) is for the hub and sg to send to the game; an agent never gives it.
                continue;
            }

            options.Add(optionName, OptionEntry.Of(optionName, resolved));
            optionNames.Add(optionName);
        }

        WriteRules? write = source["write"] is JsonObject rules ? WriteRules.Of(rules) : null;
        return new WhatEntry(verb, name, options, optionNames, write, (string?)source["id_list"],
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

    /// <summary>True when the schema's type is this one or lists it.</summary>
    internal bool Takes(string type) => Schema["type"] switch
    {
        JsonValue one => (string?)one == type,
        JsonArray several => several.Any(listed => (string?)listed == type),
        _ => false
    };

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

/// <summary>A write what's rules: route, undo class and the target paths.</summary>
internal sealed class WriteRules
{
    private WriteRules(string route, string undo, List<TargetPath> targets, string? undoMark, string? cheat)
    {
        Route = route;
        Undo = undo;
        Targets = targets;
        UndoMark = undoMark ?? (undo == "undo" ? "[undo]" : "[cannot undo]");
        Cheat = cheat;
    }

    /// <summary>The mark a report row carries (x-undo-mark).</summary>
    internal string UndoMark { get; }

    /// <summary>"always", or the option that makes the write a cheat ("free: true", "force: true"); null for none.</summary>
    internal string? Cheat { get; }

    internal string Route { get; }

    internal string Undo { get; }

    internal List<TargetPath> Targets { get; }

    internal static WriteRules Of(JsonObject source)
    {
        List<TargetPath> targets = [];
        foreach (JsonNode? target in source["x-target"] as JsonArray ?? [])
        {
            targets.Add(new TargetPath((string)target!["path"]!, (string)target["role"]!));
        }

        return new WriteRules((string)source["route"]!, (string)source["x-undo"]!, targets,
            (string?)source["x-undo-mark"], (string?)source["cheat"]);
    }
}

/// <summary>An x-target path ("id", "writes[].id") and its role (acts-on or pays-from).</summary>
internal sealed class TargetPath(string path, string role)
{
    internal string Path { get; } = path;

    /// <summary>acts-on, pays-from, or spread (a selector: the values choose what the write acts on).</summary>
    internal string Role { get; } = role;
}
