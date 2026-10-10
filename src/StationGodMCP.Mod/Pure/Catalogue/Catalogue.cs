#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>
/// The method catalogue (catalogue.json, assembled from catalogue/ and embedded in the mod and the sidecar), loaded
/// once into what the mod uses per call: each method's compiled argument schema and declared names, its class and
/// cost rules, and its shaping and paging data. Loading checks the whole file; a catalogue that does not load is a
/// CatalogueException naming the problem.
/// </summary>
internal sealed class Catalogue
{
    internal const int SupportedVersion = 1;

    private readonly Dictionary<string, CatalogueMethod> _methods;

    private Catalogue(string modVersion, Dictionary<string, CatalogueMethod> methods, HashSet<string> errorCodes,
        Dictionary<string, ErrorSee> errorPointers, int protocolMethodCount)
    {
        ModVersion = modVersion;
        _methods = methods;
        ErrorCodes = errorCodes;
        ErrorPointers = errorPointers;
        ProtocolMethodCount = protocolMethodCount;
    }

    internal string ModVersion { get; }

    /// <summary>Every error code the catalogue registers.</summary>
    internal HashSet<string> ErrorCodes { get; }

    /// <summary>Each error code's help pointer (its see), by code.</summary>
    internal Dictionary<string, ErrorSee> ErrorPointers { get; }

    internal IEnumerable<CatalogueMethod> Methods => _methods.Values;

    /// <summary>The methods of the methods section (the tools), without the protocol's own.</summary>
    internal int MethodCount => _methods.Count - ProtocolMethodCount;

    /// <summary>The methods of the protocol_methods section (catalogue, subscribe, ...).</summary>
    internal int ProtocolMethodCount { get; }

    internal bool TryGet(string method, [NotNullWhen(true)] out CatalogueMethod? found) =>
        _methods.TryGetValue(method, out found);

    /// <summary>The catalogue from its JSON text; a key given twice anywhere is refused, as in requests.</summary>
    internal static Catalogue Load(string json)
    {
        JObject root;
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonReaderException exception)
        {
            throw new CatalogueException($"catalogue.json is not JSON the mod reads: {exception.Message}");
        }

        int version = root["catalogue_version"]?.Type == JTokenType.Integer ? root.Value<int>("catalogue_version") : -1;
        if (version != SupportedVersion)
        {
            throw new CatalogueException($"catalogue_version {root["catalogue_version"]} is not {SupportedVersion}.");
        }

        string modVersion = root["mod_version"]?.Type == JTokenType.String
            ? (string)root["mod_version"]!
            : throw new CatalogueException("mod_version is missing.");

        HashSet<string> errorCodes = new HashSet<string>(StringComparer.Ordinal);
        if (!(root["errors"] is JObject errors))
        {
            throw new CatalogueException("errors is missing.");
        }

        Dictionary<string, ErrorSee> errorPointers = new Dictionary<string, ErrorSee>(StringComparer.Ordinal);
        foreach (JProperty error in errors.Properties())
        {
            errorCodes.Add(error.Name);
            if (error.Value["see"] is JObject see)
            {
                errorPointers.Add(error.Name, SeeOf(see, $"errors.{error.Name}.see"));
            }
        }

        Dictionary<string, CatalogueMethod> methods = new Dictionary<string, CatalogueMethod>(StringComparer.Ordinal);
        int protocolMethods = 0;
        foreach (string section in new[] { "methods", "protocol_methods" })
        {
            if (!(root[section] is JArray list))
            {
                throw new CatalogueException($"{section} is missing.");
            }

            for (int index = 0; index < list.Count; index++)
            {
                CatalogueMethod method = CatalogueMethod.Compile(list[index], $"{section}[{index}]");
                if (methods.ContainsKey(method.Name))
                {
                    throw new CatalogueException($"{section}[{index}]: the method '{method.Name}' is listed twice.");
                }

                methods.Add(method.Name, method);
                if (section == "protocol_methods")
                {
                    protocolMethods++;
                }
            }
        }

        return new Catalogue(modVersion, methods, errorCodes, errorPointers, protocolMethods);
    }

    private static ErrorSee SeeOf(JObject see, string at)
    {
        string? Text(string key) => see[key] == null ? null
            : see[key]!.Type == JTokenType.String ? (string)see[key]! : throw new CatalogueException($"{at}.{key}: must be a string.");

        return new ErrorSee(Text("tool"), Text("topic") ?? throw new CatalogueException($"{at}.topic is missing."),
            Text("subtopic"));
    }
}

/// <summary>One method of the catalogue, compiled.</summary>
internal sealed class CatalogueMethod
{
    private readonly List<CatalogueRule> _classRules;
    private readonly List<CatalogueRule> _costRules;

    private CatalogueMethod(string name, MethodClass defaultClass, List<CatalogueRule> classRules, CostClass defaultCost,
        List<CatalogueRule> costRules, SchemaNode parameters, bool listsShaped, bool hidden,
        List<string> effects, HashSet<string> replyLists)
    {
        ReplyLists = replyLists;
        Name = name;
        DefaultClass = defaultClass;
        _classRules = classRules;
        DefaultCost = defaultCost;
        _costRules = costRules;
        Parameters = parameters;
        ListsShaped = listsShaped;
        Hidden = hidden;
        Effects = effects;
        ArgumentNames = new ArgumentNames(name, parameters.PropertyNames);
    }

    internal string Name { get; }

    internal MethodClass DefaultClass { get; }

    /// <summary>Whether x-class-when gives this method another class at some arguments.</summary>
    internal bool HasClassRules => _classRules.Count > 0;

    internal CostClass DefaultCost { get; }

    /// <summary>The argument schema, compiled: what a call is checked against.</summary>
    internal SchemaNode Parameters { get; }

    /// <summary>The top-level argument names params declares.</summary>
    internal ArgumentNames ArgumentNames { get; }

    /// <summary>Whether Check refuses a top-level argument name params does not declare (params is closed).</summary>
    internal bool RefusesUnknownArguments => Parameters.Closed;

    /// <summary>x-shaping lists: the reply can be large and takes fields and output_file.</summary>
    internal bool ListsShaped { get; }

    /// <summary>x-mcp hidden: not an MCP tool (a protocol method).</summary>
    internal bool Hidden { get; }

    /// <summary>x-runs-in sidecar: the MCP server answers it from the catalogue; the mod has no handler for it.</summary>
    internal bool RunsInSidecar { get; private set; }

    // x-file-arguments: arguments only the MCP server reads (a file it sends as another argument), by name: the
    // argument it fills.
    private readonly Dictionary<string, string> _fileArguments = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// What to tell a direct client that sends an argument only the MCP server reads (set_ic_source's source_file);
    /// null for any other name.
    /// </summary>
    internal string? ServerOnlyArgument(string name) => _fileArguments.TryGetValue(name, out string into)
        ? $"'{name}' is read by the MCP server, which sends that file's text as '{into}'; a direct client sends '{into}' itself."
        : null;

    /// <summary>x-duration's argument: the seconds the call itself runs for; null without x-duration.</summary>
    internal string? DurationParameter { get; private set; }

    /// <summary>x-duration's max_s.</summary>
    internal double MaxDurationSeconds { get; private set; }

    /// <summary>
    /// The milliseconds a call runs for at these arguments, which its deadline and every wait for it add: the
    /// argument given (at most max_s), else max_s; 0 for a method without x-duration.
    /// </summary>
    internal int DurationMs(JObject? arguments)
    {
        if (DurationParameter == null)
        {
            return 0;
        }

        JToken? given = arguments?[DurationParameter];
        double seconds = given != null && (given.Type == JTokenType.Integer || given.Type == JTokenType.Float)
            ? Math.Max(0.0, Math.Min((double)given, MaxDurationSeconds))
            : MaxDurationSeconds;
        return (int)Math.Ceiling(seconds * 1000.0);
    }

    /// <summary>x-effects: display, server_state, files; a method with any is never resent automatically.</summary>
    internal List<string> Effects { get; }

    /// <summary>The reply's top-level keys that are lists (reply properties whose type includes array): what limit may name.</summary>
    internal HashSet<string> ReplyLists { get; }

    /// <summary>
    /// x-default-limits: how many entries of a top-level list the reply keeps when the call's shape sets no limit for
    /// it; the writer cuts the rest and says so in truncated. Empty for most methods.
    /// </summary>
    internal IReadOnlyDictionary<string, int> DefaultLimits { get; private set; } = NoLimits;

    private static readonly Dictionary<string, int> NoLimits = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The class at these arguments: the first class rule that matches, else the method's class.</summary>
    internal MethodClass ClassAt(JObject? arguments)
    {
        foreach (CatalogueRule rule in _classRules)
        {
            if (rule.Class.HasValue && rule.Matches(arguments))
            {
                return rule.Class.Value;
            }
        }

        return DefaultClass;
    }

    /// <summary>The cost at these arguments: the first cost rule that matches (with its per_item count), else the method's.</summary>
    internal CallCost CostAt(JObject? arguments)
    {
        foreach (CatalogueRule rule in _costRules)
        {
            if (rule.Cost.HasValue && rule.Matches(arguments))
            {
                int items = rule.PerItem != null && arguments?[rule.PerItem] is JArray array ? array.Count : 1;
                return new CallCost(rule.Cost.Value, items);
            }
        }

        return new CallCost(DefaultCost, 1);
    }

    internal static CatalogueMethod Compile(JToken token, string at)
    {
        if (!(token is JObject method))
        {
            throw new CatalogueException($"{at}: a method must be an object.");
        }

        string name = method["name"]?.Type == JTokenType.String
            ? (string)method["name"]!
            : throw new CatalogueException($"{at}: name is missing.");
        at = $"{at} ({name})";
        if (!(method["params"] is JObject parameters) || (string?)parameters["type"] != "object")
        {
            throw new CatalogueException($"{at}.params: must be an object schema.");
        }

        List<string> effects = new List<string>();
        if (method["x-effects"] is JArray listed)
        {
            foreach (JToken effect in listed)
            {
                effects.Add((string)effect!);
            }
        }

        CatalogueMethod compiled = new CatalogueMethod(name,
            CatalogueWords.ClassOf(method["class"] ?? JValue.CreateNull(), $"{at}.class"),
            CatalogueRule.CompileAll(method["x-class-when"], $"{at}.x-class-when"),
            CatalogueWords.CostOf(method["cost"] ?? JValue.CreateNull(), $"{at}.cost"),
            CatalogueRule.CompileAll(method["x-cost-when"], $"{at}.x-cost-when"),
            SchemaNode.Compile(parameters, $"{at}.params"),
            (string?)method["x-shaping"] == "lists",
            (string?)method["x-mcp"] == "hidden",
            effects,
            ReplyListsOf(method["reply"]));
        if (method["x-default-limits"] is JObject limits)
        {
            Dictionary<string, int> defaults = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (JProperty limit in limits.Properties())
            {
                if (!compiled.ReplyLists.Contains(limit.Name) || limit.Value.Type != JTokenType.Integer ||
                    (int)limit.Value < 0)
                {
                    throw new CatalogueException(
                        $"{at}.x-default-limits.{limit.Name}: must name a list of the reply and be a count from 0.");
                }

                defaults[limit.Name] = (int)limit.Value;
            }

            compiled.DefaultLimits = defaults;
        }

        compiled.RunsInSidecar = (string?)method["x-runs-in"] == "sidecar";
        if (method["x-file-arguments"] is JObject files)
        {
            foreach (JProperty file in files.Properties())
            {
                compiled._fileArguments[file.Name] = (string?)file.Value["into"] ?? string.Empty;
            }
        }

        if (method["x-duration"] is JObject duration)
        {
            compiled.DurationParameter = duration["param"]?.Type == JTokenType.String
                ? (string)duration["param"]!
                : throw new CatalogueException($"{at}.x-duration.param: must be a string.");
            compiled.MaxDurationSeconds = duration["max_s"]?.Type == JTokenType.Integer || duration["max_s"]?.Type == JTokenType.Float
                ? (double)duration["max_s"]!
                : throw new CatalogueException($"{at}.x-duration.max_s: must be a number.");
        }

        return compiled;
    }

    private static HashSet<string> ReplyListsOf(JToken? reply)
    {
        HashSet<string> lists = new HashSet<string>(StringComparer.Ordinal);
        if (reply?["properties"] is JObject properties)
        {
            foreach (JProperty property in properties.Properties())
            {
                if (NamesArray(property.Value["type"]))
                {
                    lists.Add(property.Name);
                }
            }
        }

        return lists;
    }

    private static bool NamesArray(JToken? type)
    {
        if (type?.Type == JTokenType.String)
        {
            return (string)type! == "array";
        }

        if (type is JArray types)
        {
            foreach (JToken name in types)
            {
                if (name.Type == JTokenType.String && (string)name! == "array")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The problems with a call's arguments against params; empty when it passes.</summary>
    internal List<SchemaProblem> Check(JObject? arguments)
    {
        List<SchemaProblem> problems = new List<SchemaProblem>();
        Parameters.Validate(arguments ?? new JObject(), string.Empty, problems);
        for (int index = 0; index < problems.Count; index++)
        {
            if (ServerOnlyArgument(problems[index].Path) is string note)
            {
                problems[index] = new SchemaProblem(problems[index].Path, note);
            }
        }

        return problems;
    }

    /// <summary>
    /// The problems with arguments given as JSON text: a key given twice anywhere is one (a parser would keep the
    /// last silently), JSON that is not an object or null another; then the schema's.
    /// </summary>
    internal List<SchemaProblem> Check(string json)
    {
        JToken parsed;
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            parsed = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonReaderException exception)
        {
            bool repeated = exception.Message.IndexOf("already exists", StringComparison.Ordinal) >= 0;
            string path = exception.Path ?? string.Empty;
            return new List<SchemaProblem>
            {
                new SchemaProblem(path, repeated
                    ? $"Argument '{path}' is given twice; name each key once."
                    : $"The arguments are not JSON: {exception.Message}")
            };
        }

        if (parsed.Type == JTokenType.Null)
        {
            return Check((JObject?)null);
        }

        return parsed is JObject arguments
            ? Check(arguments)
            : new List<SchemaProblem> { new SchemaProblem(string.Empty, "The arguments must be a JSON object.") };
    }
}

/// <summary>
/// A method's declared top-level argument names, as a set for the run-time drift check (one lookup per name read)
/// and as a sorted list for naming the nearest one in a refusal.
/// </summary>
internal sealed class ArgumentNames
{
    private readonly HashSet<string> _set;

    internal ArgumentNames(string method, IReadOnlyList<string> names)
    {
        Method = method;
        _set = new HashSet<string>(StringComparer.Ordinal);
        Sorted = new List<string>(names.Count);
        foreach (string name in names)
        {
            _set.Add(name);
            Sorted.Add(name);
        }

        Sorted.Sort(StringComparer.Ordinal);
    }

    internal string Method { get; }

    internal List<string> Sorted { get; }

    internal bool Contains(string name) => _set.Contains(name);
}
