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

    private Catalogue(string modVersion, Dictionary<string, CatalogueMethod> methods, HashSet<string> errorCodes)
    {
        ModVersion = modVersion;
        _methods = methods;
        ErrorCodes = errorCodes;
    }

    internal string ModVersion { get; }

    /// <summary>Every error code the catalogue registers.</summary>
    internal HashSet<string> ErrorCodes { get; }

    internal IEnumerable<CatalogueMethod> Methods => _methods.Values;

    internal int MethodCount => _methods.Count;

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

        foreach (JProperty error in errors.Properties())
        {
            errorCodes.Add(error.Name);
        }

        Dictionary<string, CatalogueMethod> methods = new Dictionary<string, CatalogueMethod>(StringComparer.Ordinal);
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
            }
        }

        return new Catalogue(modVersion, methods, errorCodes);
    }
}

/// <summary>One method of the catalogue, compiled.</summary>
internal sealed class CatalogueMethod
{
    private readonly List<CatalogueRule> _classRules;
    private readonly List<CatalogueRule> _costRules;

    private CatalogueMethod(string name, MethodClass defaultClass, List<CatalogueRule> classRules, CostClass defaultCost,
        List<CatalogueRule> costRules, SchemaNode parameters, bool listsShaped, bool hidden, bool runsInSidecar,
        List<string> effects)
    {
        Name = name;
        DefaultClass = defaultClass;
        _classRules = classRules;
        DefaultCost = defaultCost;
        _costRules = costRules;
        Parameters = parameters;
        ListsShaped = listsShaped;
        Hidden = hidden;
        RunsInSidecar = runsInSidecar;
        Effects = effects;
        ArgumentNames = new ArgumentNames(name, parameters.PropertyNames);
    }

    internal string Name { get; }

    internal MethodClass DefaultClass { get; }

    /// <summary>Whether x-class-when gives this method another class at some arguments.</summary>
    internal bool HasClassRules => _classRules.Count > 0;

    internal CostClass DefaultCost { get; }

    /// <summary>The argument schema, compiled: what version 2 checks a call against.</summary>
    internal SchemaNode Parameters { get; }

    /// <summary>The top-level argument names params declares.</summary>
    internal ArgumentNames ArgumentNames { get; }

    /// <summary>x-shaping lists: the reply can be large and takes fields and output_file.</summary>
    internal bool ListsShaped { get; }

    /// <summary>x-mcp hidden: not an MCP tool (a protocol method).</summary>
    internal bool Hidden { get; }

    /// <summary>x-runs-in sidecar: answered by the sidecar, not the mod (sample_logic).</summary>
    internal bool RunsInSidecar { get; }

    /// <summary>x-effects: display, server_state, files; a method with any is never resent automatically.</summary>
    internal List<string> Effects { get; }

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

        return new CatalogueMethod(name,
            CatalogueWords.ClassOf(method["class"] ?? JValue.CreateNull(), $"{at}.class"),
            CatalogueRule.CompileAll(method["x-class-when"], $"{at}.x-class-when"),
            CatalogueWords.CostOf(method["cost"] ?? JValue.CreateNull(), $"{at}.cost"),
            CatalogueRule.CompileAll(method["x-cost-when"], $"{at}.x-cost-when"),
            SchemaNode.Compile(parameters, $"{at}.params"),
            (string?)method["x-shaping"] == "lists",
            (string?)method["x-mcp"] == "hidden",
            (string?)method["x-runs-in"] == "sidecar",
            effects);
    }

    /// <summary>The problems with a call's arguments against params; empty when it passes.</summary>
    internal List<SchemaProblem> Check(JObject? arguments)
    {
        List<SchemaProblem> problems = new List<SchemaProblem>();
        Parameters.Validate(arguments ?? new JObject(), string.Empty, problems);
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
