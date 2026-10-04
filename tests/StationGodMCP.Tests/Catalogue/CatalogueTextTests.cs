#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The catalogue's text rubric (catalogue.md, The text rubric), enforced: (a) short tool descriptions that end with
/// the More line and name their safety flags; (b) every tool_info node under 1.5 KB and one-line argument descriptions;
/// (c) no banned pattern anywhere (history, versions, dates, code internals, names, URLs, em dashes), except entries
/// of catalogue/text-allow.json, each with its reason and each still needed; (d) every argument described and every
/// tool with help; (e) every pointer resolves and no family topic is orphaned; (f) every error code the code can
/// return has a message and a see; (h) the size of tools/list, reported and capped. (g), the assembled file matching
/// its sources, is CatalogueConsistencyTests.CatalogueFileIsCurrent.
/// </summary>
public sealed class CatalogueTextTests
{
    /// <summary>The longest tool description, the More line included.</summary>
    internal const int DescriptionMaxChars = 440;

    /// <summary>The largest tool_info reply, in bytes of its JSON.</summary>
    internal const int NodeMaxBytes = 1536;

    internal const int PropertyDescriptionMaxChars = 200;

    internal const int SummaryMaxChars = 100;

    /// <summary>Every tool description together, in bytes.</summary>
    internal const int DescriptionsMaxBytes = 30_000;

    /// <summary>The whole tools/list result as the sidecar writes it, in bytes.</summary>
    internal const int ToolsListMaxBytes = 200_000;

    private static readonly Lazy<JsonObject> Catalogue = new(() => CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath).AsObject());

    private static readonly Lazy<ToolHelp> Help = new(() => ToolHelp.Of(JsonSerializer.SerializeToElement(Catalogue.Value)));

    private readonly ITestOutputHelper _output;

    public CatalogueTextTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---- the rules of (c), each a name, a pattern, and what it catches ----

    internal static readonly IReadOnlyList<TextRule> Rules =
    [
        new("version", @"\b[vV]?\d+\.\d+(\.\d+)?\+|\b[vV]\d+(\.\d+)+\b|\b\d+\.\d+\.\d+\b", "a version number"),
        new("history_phrase", @"(?i)\b(since|new in|added in|as of)\b", "since / new in / added in / as of"),
        new("change_word", @"(?i)\b(now|no longer|was|were|used to|previously|changed|renamed|replaces|formerly|anymore)\b",
            "a change word describing the past"),
        new("date", @"\b(19|20)\d\d-\d\d-\d\d\b|(?i)\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}\b",
            "a date"),
        new("code_member", @"\b[A-Z][A-Za-z0-9]*\.[A-Z][A-Za-z0-9]*\b", "a C# type or member name (Foo.Bar)"),
        new("code_call", @"\b[A-Za-z_][A-Za-z0-9_]*\(\)", "a method call (Foo())"),
        new("source_file", @"\b\w+\.cs\b|\b\w+\.\w+:\d+\b", "a source file or file:line citation"),
        new("code_internal", @"\b(Harmony|patch(es|ed)?|decompile[sd]?|CanConstruct|CanMountOnWall|DamageState|GridBounds|Quaternion|GameManager|MonoBehaviour|OnServer|SetCustomColor|enum)\b",
            "code internals (Harmony, patches, game method names)"),
        new("person", PersonPattern(), "a person's name, or a decision credited to one"),
        new("url", @"(?i)\bhttps?://|\bwww\.", "a URL"),
        new("em_dash", "—", "an em dash")
    ];

    // Names are not kept in this public file: STATIONGOD_BANNED_NAMES (comma separated) adds the ones to refuse;
    // decisions credited to someone ("asked by", "per ... request") are refused always.
    private static string PersonPattern()
    {
        string[] names = (Environment.GetEnvironmentVariable("STATIONGOD_BANNED_NAMES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string credited = @"(?i:\b(asked|requested|decided|approved|suggested) by\b|\bper (the )?\w+'s request\b)";
        return names.Length == 0 ? credited : credited + @"|\b(" + string.Join("|", names.Select(Regex.Escape)) + @")\b";
    }

    // ---- (a) descriptions ----

    [Fact]
    public void EveryToolDescriptionIsShortEndsWithTheMoreLineAndNamesItsSafetyFlags()
    {
        List<string> wrong = [];
        foreach (JsonObject method in Tools())
        {
            string name = Name(method);
            string description = (string)method["description"]!;
            string[] lines = description.Split('\n');
            if (description.Length > DescriptionMaxChars)
            {
                wrong.Add($"{name}: description is {description.Length} characters (max {DescriptionMaxChars})");
            }

            if (lines.Length < 2 || lines.Length > 4)
            {
                wrong.Add($"{name}: description has {lines.Length} lines (2 to 4)");
            }

            if (lines[^1] != CatalogueFiles.MoreLine(name))
            {
                wrong.Add($"{name}: the last line is not {CatalogueFiles.MoreLine(name)}");
            }

            if (Cheats(method) && description.IndexOf("cheat", StringComparison.OrdinalIgnoreCase) < 0)
            {
                wrong.Add($"{name}: a cheat class (or a class rule giving cheat) but the description does not say Cheat");
            }

            if (DryRunByDefault(method) && description.IndexOf("dry run", StringComparison.OrdinalIgnoreCase) < 0)
            {
                wrong.Add($"{name}: dry run by default but the description does not say so");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- (b) sizes ----

    [Fact]
    public void EveryToolInfoNodeIsUnderTheLimit()
    {
        List<string> wrong = [];
        foreach ((string where, int bytes) in Nodes().Where(node => node.Bytes > NodeMaxBytes))
        {
            wrong.Add($"{where}: {bytes} bytes (max {NodeMaxBytes})");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void ArgumentDescriptionsAndSummariesAreOneShortLine()
    {
        List<string> wrong = [];
        foreach (JsonObject method in Tools())
        {
            foreach ((string path, string text) in PropertyDescriptions(method))
            {
                if (text.Contains('\n') || text.Length > PropertyDescriptionMaxChars)
                {
                    wrong.Add($"{Name(method)}{path}: {text.Length} characters{(text.Contains('\n') ? ", several lines" : "")} (one line, max {PropertyDescriptionMaxChars})");
                }
            }
        }

        foreach ((string where, string text) in Texts().Where(text => text.Where.EndsWith(".summary", StringComparison.Ordinal)))
        {
            if (text.Contains('\n') || text.Length > SummaryMaxChars)
            {
                wrong.Add($"{where}: {text.Length} characters (one line, max {SummaryMaxChars})");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- (c) banned patterns ----

    [Fact]
    public void NoTextBreaksTheRubric()
    {
        List<AllowEntry> allowed = AllowList();
        HashSet<AllowEntry> used = [];
        List<string> wrong = [];
        foreach ((string where, string text) in Texts())
        {
            foreach (TextRule rule in Rules)
            {
                foreach (Match match in rule.Pattern.Matches(text))
                {
                    AllowEntry? allow = allowed.FirstOrDefault(entry => entry.Rule == rule.Name &&
                                                                        Covers(text, match, entry.Text));
                    if (allow != null)
                    {
                        used.Add(allow);
                        continue;
                    }

                    wrong.Add($"{where}: {rule.Name} ({rule.Catches}) '{match.Value}' in \"...{Around(text, match)}...\"");
                }
            }
        }

        foreach (AllowEntry stale in allowed.Where(entry => !used.Contains(entry)))
        {
            wrong.Add($"text-allow.json: the entry {stale.Rule} \"{stale.Text}\" matches nothing; remove it");
        }

        Assert.True(wrong.Count == 0, wrong.Count + " problems:\n" + string.Join("\n", wrong.Take(200)));
    }

    [Fact]
    public void EveryAllowEntryNamesARuleAndGivesItsReason()
    {
        foreach (AllowEntry entry in AllowList())
        {
            Assert.Contains(Rules, rule => rule.Name == entry.Rule);
            Assert.False(string.IsNullOrWhiteSpace(entry.Text), "an allow entry needs the text it allows");
            Assert.True(entry.Reason.Length >= 20, $"text-allow.json {entry.Rule} \"{entry.Text}\": give the reason in a sentence");
        }
    }

    // ---- (d) coverage ----

    [Fact]
    public void EveryArgumentIsDescribedAndEveryToolHasHelp()
    {
        List<string> wrong = [];
        foreach (JsonObject method in Tools())
        {
            string name = Name(method);
            foreach (string path in UndescribedProperties(method["params"]!, string.Empty))
            {
                wrong.Add($"{name}{path}: no description");
            }

            if (method["help"]?["text"] is not JsonValue)
            {
                wrong.Add($"{name}: no help (tool_info has nothing to say about it)");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- (e) links ----

    [Fact]
    public void EveryPointerResolvesAndNoFamilyTopicIsOrphaned()
    {
        ToolHelp help = Help.Value;
        HashSet<string> shared = help.SharedTopics.ToHashSet(StringComparer.Ordinal);
        HashSet<string> root = help.RootTopics.ToHashSet(StringComparer.Ordinal);
        HashSet<string> reached = [];
        List<string> wrong = [];

        void Resolve(string where, string? tool, string topic, string? subtopic)
        {
            if (help.Answer(tool, topic, subtopic) is HelpAnswer.Refused refused)
            {
                wrong.Add($"{where}: points at {tool ?? "-"}/{topic}/{subtopic ?? "-"}, which does not resolve ({refused.Message})");
                return;
            }

            if (tool == null || !help.TopicsOf(tool).Own.Contains(topic))
            {
                reached.Add(topic);
            }
        }

        foreach ((string where, string text) in Texts())
        {
            string? tool = where.StartsWith("tool ", StringComparison.Ordinal) ? where.Split(' ', '.')[1] : null;
            foreach (Match see in SeePointer.Matches(text))
            {
                Resolve(where, null, see.Groups["topic"].Value, see.Groups["sub"].Success ? see.Groups["sub"].Value : null);
                if (tool != null && !root.Contains(see.Groups["topic"].Value) &&
                    !help.TopicsOf(tool).Shared.Contains(see.Groups["topic"].Value))
                {
                    wrong.Add($"{where}: points at the family topic {see.Groups["topic"].Value}, which {tool}'s help does not list in shared");
                }
            }

            foreach (Match own in TopicPointer.Matches(text))
            {
                if (tool == null)
                {
                    wrong.Add($"{where}: (topic {own.Groups["topic"].Value}) names a tool's own topic outside a tool's text");
                    continue;
                }

                Resolve(where, tool, own.Groups["topic"].Value, own.Groups["sub"].Success ? own.Groups["sub"].Value : null);
            }

            foreach (Match call in ToolInfoPointer.Matches(text))
            {
                string? named = call.Groups["tool"].Success ? call.Groups["tool"].Value : null;
                if (call.Groups["topic"].Success)
                {
                    Resolve(where, named, call.Groups["topic"].Value, call.Groups["sub"].Success ? call.Groups["sub"].Value : null);
                }
                else if (named != null && help.Answer(named, null, null) is HelpAnswer.Refused)
                {
                    wrong.Add($"{where}: points at the tool {named}, which has no help");
                }
            }
        }

        foreach (JsonObject method in Tools())
        {
            foreach (JsonNode? topic in method["help"]?["shared"] as JsonArray ?? [])
            {
                if (!shared.Contains((string)topic!))
                {
                    wrong.Add($"tool {Name(method)}: help.shared names {topic}, which is no shared topic");
                }

                reached.Add((string)topic!);
            }
        }

        foreach ((string code, JsonNode? error) in Catalogue.Value["errors"]!.AsObject())
        {
            JsonObject see = error!["see"]!.AsObject();
            Resolve($"errors.{code}.see", (string?)see["tool"], (string)see["topic"]!, (string?)see["subtopic"]);
        }

        foreach (string family in shared.Except(root).Where(family => !reached.Contains(family)))
        {
            wrong.Add($"the family topic {family} is orphaned: no tool lists it and nothing points at it");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Distinct()));
    }

    // ---- (f) error codes ----

    [Fact]
    public void EveryErrorCodeHasAMessageAndASee()
    {
        JsonObject errors = Catalogue.Value["errors"]!.AsObject();
        IEnumerable<string> codes = ErrorSite.All().Select(site => site.Code)
            .Concat(SidecarCodes)
            .Distinct(StringComparer.Ordinal);
        List<string> wrong = [];
        foreach (string code in codes)
        {
            if (errors[code] is not JsonObject entry)
            {
                wrong.Add($"{code}: not in catalogue/errors.json");
                continue;
            }

            if (((string?)entry["description"])?.Length is not > 20)
            {
                wrong.Add($"{code}: no message saying what went wrong and what to do");
            }

            if (entry["see"]?["topic"] is not JsonValue)
            {
                wrong.Add($"{code}: no see");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void ErrorMessagesInTheCodeNameNoCodeInternals()
    {
        string[] internals = ["code_member", "code_call", "source_file", "code_internal", "em_dash", "version"];
        List<string> wrong = [];
        foreach (ErrorSite site in ErrorSite.All().Where(site => site.Message != null))
        {
            foreach (TextRule rule in Rules.Where(rule => internals.Contains(rule.Name)))
            {
                if (rule.Pattern.Match(site.Message!) is { Success: true } match)
                {
                    wrong.Add($"{site.Where} ({site.Code}): {rule.Name} '{match.Value}' in \"{site.Message}\"");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- (h) size ----

    [Fact]
    public void ToolsListStaysSmallAndItsSizeIsReported()
    {
        JsonObject catalogue = Catalogue.Value;
        string toolsList = JsonSerializer.Serialize(new { tools = ToolCatalogue.ToolsOf(catalogue) }, McpAdapter.JsonOptions);
        int total = Encoding.UTF8.GetByteCount(toolsList);
        List<(string Name, int Bytes)> descriptions = Tools()
            .Select(method => (Name(method), Encoding.UTF8.GetByteCount((string)method["description"]!))).ToList();
        int descriptionBytes = descriptions.Sum(entry => entry.Bytes);
        int propertyBytes = Tools().Sum(method => PropertyDescriptions(method).Sum(entry => Encoding.UTF8.GetByteCount(entry.Text)));

        StringBuilder report = new();
        report.AppendLine($"tools/list: {total:N0} bytes, {descriptions.Count} tools");
        report.AppendLine($"tool descriptions: {descriptionBytes:N0} bytes; argument descriptions: {propertyBytes:N0} bytes");
        report.AppendLine("largest descriptions: " + string.Join(", ",
            descriptions.OrderByDescending(entry => entry.Bytes).Take(8).Select(entry => $"{entry.Name} {entry.Bytes}")));
        report.AppendLine("largest tool_info nodes: " + string.Join(", ",
            Nodes().OrderByDescending(node => node.Bytes).Take(8).Select(node => $"{node.Where} {node.Bytes}")));
        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("STATIONGOD_SIZE_REPORT") is { Length: > 0 } file)
        {
            File.WriteAllText(file, report.ToString());
            File.WriteAllText(Path.ChangeExtension(file, ".tools.json"), toolsList);
        }

        Assert.True(descriptionBytes <= DescriptionsMaxBytes, $"tool descriptions are {descriptionBytes:N0} bytes (max {DescriptionsMaxBytes:N0})\n{report}");
        Assert.True(total <= ToolsListMaxBytes, $"tools/list is {total:N0} bytes (max {ToolsListMaxBytes:N0})\n{report}");
    }

    // ---- the corpus ----

    private static readonly string[] SidecarCodes = ["invalid_argument", "game_unavailable", "help_not_found"];

    private static readonly Regex SeePointer = new(@"\(see (?<topic>[a-z_]+)(/(?<sub>[a-z_]+))?\)", RegexOptions.Compiled);

    private static readonly Regex TopicPointer = new(@"\(topic (?<topic>[a-z_]+)(/(?<sub>[a-z_]+))?\)", RegexOptions.Compiled);

    private static readonly Regex ToolInfoPointer = new(
        @"tool_info \{(tool: ""(?<tool>[a-z_0-9]+)"")?(,? ?topic: ""(?<topic>[a-z_]+)"")?(, subtopic: ""(?<sub>[a-z_]+)"")?\}",
        RegexOptions.Compiled);

    private static IEnumerable<JsonObject> Tools() => Catalogue.Value["methods"]!.AsArray()
        .Select(method => method!.AsObject()).Where(method => (string?)method["x-mcp"] != "hidden");

    private static string Name(JsonObject method) => (string)method["name"]!;

    private static bool Cheats(JsonObject method) =>
        (string?)method["class"] == "cheat" ||
        (method["x-class-when"] as JsonArray ?? []).Any(rule => (string?)rule!["class"] == "cheat");

    private static bool DryRunByDefault(JsonObject method) =>
        method["params"]?["properties"]?["dry_run"] != null &&
        (method["x-class-when"] as JsonArray ?? []).Any(rule => rule!["when"]?["dry_run"]?["absent"] != null);

    /// <summary>Every text of the catalogue an agent can read, joined: what a rule "is documented" is held to.</summary>
    internal static string AllText() => string.Join("\n", Texts().Select(text => text.Text));

    /// <summary>
    /// A tool's whole documentation: its description, its arguments' descriptions, its help, and the shared topics its
    /// help lists, each with its subtopics: everything tool_info {tool} leads to.
    /// </summary>
    internal static string DocsOf(string tool)
    {
        JsonObject method = Tools().Single(entry => Name(entry) == tool);
        StringBuilder docs = new();
        foreach ((string where, string text) in Texts().Where(text => text.Where.StartsWith($"tool {tool}.", StringComparison.Ordinal)))
        {
            docs.Append(text).Append('\n');
        }

        foreach (JsonNode? shared in method["help"]?["shared"] as JsonArray ?? [])
        {
            foreach ((string where, string text) in Texts().Where(text =>
                         text.Where.StartsWith($"help.topics.{shared}.", StringComparison.Ordinal) ||
                         text.Where.StartsWith($"help.families.{shared}.", StringComparison.Ordinal)))
            {
                docs.Append(text).Append('\n');
            }
        }

        return docs.ToString();
    }

    /// <summary>Every text an agent reads, by where it is.</summary>
    internal static IEnumerable<(string Where, string Text)> Texts()
    {
        JsonObject catalogue = Catalogue.Value;
        yield return ("server.instructions", (string)catalogue["server"]!["instructions"]!);
        yield return ("help.intro", (string)catalogue["help"]!["intro"]!);
        foreach (string section in new[] { "topics", "families" })
        {
            foreach ((string where, string text) in TopicTexts(catalogue["help"]![section] as JsonObject, $"help.{section}"))
            {
                yield return (where, text);
            }
        }

        foreach ((string code, JsonNode? error) in catalogue["errors"]!.AsObject())
        {
            yield return ($"errors.{code}", (string)error!["description"]!);
        }

        foreach (JsonNode? node in catalogue["methods"]!.AsArray().Concat(catalogue["protocol_methods"]!.AsArray()))
        {
            JsonObject method = node!.AsObject();
            string at = $"tool {Name(method)}";
            yield return ($"{at}.description", (string)method["description"]!);
            foreach ((string path, string text) in PropertyDescriptions(method))
            {
                yield return ($"{at}.params{path}", text);
            }

            if (method["help"] is JsonObject help)
            {
                yield return ($"{at}.help.text", (string)help["text"]!);
                foreach ((string where, string text) in TopicTexts(help["topics"] as JsonObject, $"{at}.help.topics"))
                {
                    yield return (where, text);
                }
            }
        }

        foreach ((string name, string text) in new[]
                 {
                     ("output_file", ToolSet.OutputFileDescription), ("fields", ToolSet.FieldsDescription),
                     ("omit", ToolSet.OmitDescription), ("list_limits", ToolSet.LimitsDescription)
                 })
        {
            yield return ($"sidecar.{name}", text);
        }
    }

    private static IEnumerable<(string Where, string Text)> TopicTexts(JsonObject? topics, string at)
    {
        foreach ((string name, JsonNode? topic) in topics ?? [])
        {
            yield return ($"{at}.{name}.summary", (string)topic!["summary"]!);
            yield return ($"{at}.{name}.text", (string)topic["text"]!);
            foreach ((string where, string text) in TopicTexts(topic["subtopics"] as JsonObject, $"{at}.{name}.subtopics"))
            {
                yield return (where, text);
            }
        }
    }

    /// <summary>Every description inside a method's params (and its file arguments), by path.</summary>
    internal static IEnumerable<(string Path, string Text)> PropertyDescriptions(JsonObject method)
    {
        List<(string, string)> found = [];
        Collect(method["params"]!, string.Empty, found);
        foreach ((string name, JsonNode? file) in method["x-file-arguments"] as JsonObject ?? [])
        {
            found.Add(($".{name}", (string)file!["description"]!));
        }

        return found;
    }

    private static void Collect(JsonNode node, string path, List<(string, string)> into)
    {
        if (node is JsonObject obj)
        {
            if (obj["description"] is JsonValue text && path.Length > 0)
            {
                into.Add((path, (string)text!));
            }

            foreach ((string key, JsonNode? value) in obj)
            {
                if (key == "properties" && value is JsonObject properties)
                {
                    foreach ((string name, JsonNode? schema) in properties)
                    {
                        if (schema != null)
                        {
                            Collect(schema, $"{path}.{name}", into);
                        }
                    }
                }
                else if (value != null && key != "description" && key != "properties")
                {
                    Collect(value, key is "items" ? $"{path}[]" : path, into);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item != null)
                {
                    Collect(item, path, into);
                }
            }
        }
    }

    private static IEnumerable<string> UndescribedProperties(JsonNode node, string path)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (key == "properties" && value is JsonObject properties)
                {
                    foreach ((string name, JsonNode? schema) in properties)
                    {
                        if (schema is not JsonObject property || property["description"] is not JsonValue)
                        {
                            yield return $"{path}.{name}";
                        }

                        if (schema != null)
                        {
                            foreach (string inner in UndescribedProperties(schema, $"{path}.{name}"))
                            {
                                yield return inner;
                            }
                        }
                    }
                }
                else if (value != null && key != "description")
                {
                    foreach (string inner in UndescribedProperties(value, key is "items" ? $"{path}[]" : path))
                    {
                        yield return inner;
                    }
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (string inner in array.Where(item => item != null).SelectMany(item => UndescribedProperties(item!, path)))
            {
                yield return inner;
            }
        }
    }

    /// <summary>Every tool_info reply and its size: the root, each tool, each topic and subtopic.</summary>
    internal static IEnumerable<(string Where, int Bytes)> Nodes()
    {
        ToolHelp help = Help.Value;
        yield return ("{}", Size(help.Answer(null, null, null)));
        foreach (string topic in help.SharedTopics)
        {
            yield return ($"{{topic: {topic}}}", Size(help.Answer(null, topic, null)));
            foreach (string subtopic in help.SubtopicsOf(null, topic))
            {
                yield return ($"{{topic: {topic}, subtopic: {subtopic}}}", Size(help.Answer(null, topic, subtopic)));
            }
        }

        foreach (string tool in help.Tools)
        {
            yield return ($"{{tool: {tool}}}", Size(help.Answer(tool, null, null)));
            foreach (string topic in help.TopicsOf(tool).Own)
            {
                yield return ($"{{tool: {tool}, topic: {topic}}}", Size(help.Answer(tool, topic, null)));
                foreach (string subtopic in help.SubtopicsOf(tool, topic))
                {
                    yield return ($"{{tool: {tool}, topic: {topic}, subtopic: {subtopic}}}", Size(help.Answer(tool, topic, subtopic)));
                }
            }
        }
    }

    private static int Size(HelpAnswer answer) => answer is HelpAnswer.Found found
        ? Encoding.UTF8.GetByteCount(found.Reply.GetRawText())
        : int.MaxValue;

    private static List<AllowEntry> AllowList()
    {
        string path = Path.Combine(CatalogueFiles.SourceRoot, "text-allow.json");
        return CatalogueFiles.ReadJson(path).AsArray().Select(entry => new AllowEntry(
            (string)entry!["rule"]!, (string)entry["text"]!, (string?)entry["reason"] ?? string.Empty)).ToList();
    }

    // An allow entry covers a match when its text, found in the text, contains the match's span.
    private static bool Covers(string text, Match match, string allowed)
    {
        for (int at = text.IndexOf(allowed, StringComparison.Ordinal); at >= 0; at = text.IndexOf(allowed, at + 1, StringComparison.Ordinal))
        {
            if (at <= match.Index && match.Index + match.Length <= at + allowed.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static string Around(string text, Match match)
    {
        int start = Math.Max(0, match.Index - 40);
        int end = Math.Min(text.Length, match.Index + match.Length + 40);
        return text[start..end].Replace('\n', ' ');
    }

    internal sealed record TextRule(string Name, Regex Pattern, string Catches)
    {
        internal TextRule(string name, string pattern, string catches) : this(name, new Regex(pattern, RegexOptions.Compiled), catches)
        {
        }
    }

    private sealed record AllowEntry(string Rule, string Text, string Reason);
}
