using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Client;

/// <summary>
/// tool_info's answers, read from a catalogue's help: the shared topics (the catalogue's help section: topics, listed
/// at the root, and families, shared by a family of tools and listed by those tools) and each tool's own help (its
/// catalogue entry's help). Three levels at most: a tool or a shared topic, a topic, a subtopic. Answered without the
/// game: the catalogue holds everything.
/// </summary>
public sealed class ToolHelp
{
    /// <summary>The method name tool_info has in the catalogue.</summary>
    public const string Method = "tool_info";

    private static readonly JsonSerializerOptions ReplyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HelpPointer Levels = new(Method, "levels", null);

    private readonly string _intro;
    private readonly IReadOnlyList<HelpNode> _root;
    private readonly IReadOnlyList<HelpNode> _shared;
    private readonly IReadOnlyDictionary<string, ToolEntry> _tools;

    private ToolHelp(string intro, IReadOnlyList<HelpNode> root, IReadOnlyList<HelpNode> families,
        IReadOnlyDictionary<string, ToolEntry> tools)
    {
        _intro = intro;
        _root = root;
        _shared = root.Concat(families).ToList();
        _tools = tools;
    }

    /// <summary>The help of a catalogue document; a catalogue without help answers an empty intro and no topics.</summary>
    public static ToolHelp Of(JsonElement catalogue)
    {
        JsonObject document = JsonObject.Create(catalogue) ?? [];
        JsonObject help = document["help"] as JsonObject ?? [];
        Dictionary<string, ToolEntry> tools = new(StringComparer.Ordinal);
        foreach (JsonNode? method in document["methods"] as JsonArray ?? [])
        {
            if (method?["help"] is JsonObject toolHelp && (string?)method["x-mcp"] != "hidden")
            {
                string name = (string)method["name"]!;
                tools[name] = new ToolEntry(name, (string?)toolHelp["text"] ?? string.Empty, NodesOf(toolHelp["topics"]),
                    (toolHelp["shared"] as JsonArray ?? []).Select(topic => (string)topic!).ToList());
            }
        }

        return new ToolHelp((string?)help["intro"] ?? string.Empty, NodesOf(help["topics"]), NodesOf(help["families"]), tools);
    }

    /// <summary>The tools that have help, in name order.</summary>
    public IEnumerable<string> Tools => _tools.Keys.Order(StringComparer.Ordinal);

    /// <summary>The shared topics' names, root topics first, then families, in catalogue order.</summary>
    public IEnumerable<string> SharedTopics => _shared.Select(topic => topic.Name);

    /// <summary>The topics {} lists.</summary>
    public IEnumerable<string> RootTopics => _root.Select(topic => topic.Name);

    /// <summary>A tool's own topic names, and the shared topics it lists; empty for a tool without help.</summary>
    public (IReadOnlyList<string> Own, IReadOnlyList<string> Shared) TopicsOf(string tool) =>
        _tools.TryGetValue(tool, out ToolEntry? entry)
            ? (entry.Topics.Select(node => node.Name).ToList(), entry.Shared)
            : ([], []);

    /// <summary>A node's subtopic names; empty when the node has none or does not exist.</summary>
    public IReadOnlyList<string> SubtopicsOf(string? tool, string topic)
    {
        HelpNode? node = (tool != null && _tools.TryGetValue(tool, out ToolEntry? entry)
            ? entry.Topics.FirstOrDefault(own => own.Name == topic)
            : null) ?? _shared.FirstOrDefault(shared => shared.Name == topic);
        return node?.Children.Select(child => child.Name).ToList() ?? [];
    }

    /// <summary>
    /// The answer at one node: {} the intro and the shared topics; {tool} a tool's text and topics; {tool, topic} or
    /// {topic} a topic and its subtopics; with subtopic the subtopic's text. A tool's topic may also name a shared topic.
    /// </summary>
    public HelpAnswer Answer(string? tool, string? topic, string? subtopic)
    {
        tool = Blank(tool);
        topic = Blank(topic);
        subtopic = Blank(subtopic);
        if (subtopic != null && topic == null)
        {
            return new HelpAnswer.Refused("invalid_argument", "subtopic needs topic: give the topic the subtopic belongs to.", Levels);
        }

        if (tool == null)
        {
            return topic == null ? Found(Root()) : SharedNode(topic, subtopic);
        }

        if (!_tools.TryGetValue(tool, out ToolEntry? entry))
        {
            return NotFound($"No tool '{tool}' has help.", tool, _tools.Keys);
        }

        if (topic == null)
        {
            return Found(ToolNode(entry));
        }

        HelpNode? own = entry.Topics.FirstOrDefault(node => node.Name == topic);
        if (own == null)
        {
            return _shared.Any(node => node.Name == topic)
                ? SharedNode(topic, subtopic)
                : NotFound($"{tool} has no topic '{topic}'.", topic, entry.Topics.Select(node => node.Name).Concat(entry.Shared));
        }

        return NodeAnswer(own, subtopic, reply => reply["tool"] = tool);
    }

    private HelpAnswer SharedNode(string topic, string? subtopic)
    {
        HelpNode? node = _shared.FirstOrDefault(shared => shared.Name == topic);
        return node == null
            ? NotFound($"No shared topic '{topic}'.", topic, _shared.Select(shared => shared.Name))
            : NodeAnswer(node, subtopic, _ => { });
    }

    private static HelpAnswer NodeAnswer(HelpNode node, string? subtopic, Action<JsonObject> owner)
    {
        JsonObject reply = [];
        owner(reply);
        reply["topic"] = node.Name;
        if (subtopic == null)
        {
            reply["text"] = node.Text;
            if (node.Children.Count > 0)
            {
                reply["subtopics"] = Listing(node.Children, "subtopic");
            }

            return Found(reply);
        }

        HelpNode? child = node.Children.FirstOrDefault(entry => entry.Name == subtopic);
        if (child == null)
        {
            return NotFound($"Topic '{node.Name}' has no subtopic '{subtopic}'.", subtopic, node.Children.Select(entry => entry.Name));
        }

        reply["subtopic"] = child.Name;
        reply["text"] = child.Text;
        return Found(reply);
    }

    private JsonObject Root() => new()
    {
        ["text"] = _intro,
        ["topics"] = Listing(_root, "topic")
    };

    private JsonObject ToolNode(ToolEntry entry)
    {
        JsonObject reply = new() { ["tool"] = entry.Name, ["text"] = entry.Text };
        if (entry.Topics.Count > 0)
        {
            reply["topics"] = Listing(entry.Topics, "topic");
        }

        List<HelpNode> shared = entry.Shared.Select(name => _shared.FirstOrDefault(node => node.Name == name))
            .OfType<HelpNode>().ToList();
        if (shared.Count > 0)
        {
            reply["shared_topics"] = Listing(shared, "topic");
        }

        return reply;
    }

    private static JsonArray Listing(IEnumerable<HelpNode> nodes, string key) =>
        new(nodes.Select(node => (JsonNode)new JsonObject { [key] = node.Name, ["summary"] = node.Summary }).ToArray());

    private static HelpAnswer Found(JsonObject reply) =>
        new HelpAnswer.Found(JsonSerializer.SerializeToElement(reply, ReplyOptions));

    private static HelpAnswer NotFound(string what, string asked, IEnumerable<string> names)
    {
        List<string> known = names.Distinct(StringComparer.Ordinal).ToList();
        List<string> near = known.OrderBy(name => Distance(asked, name)).ThenBy(name => name, StringComparer.Ordinal)
            .Take(3).ToList();
        string hint = known.Count == 0 ? " It has none."
            : known.Count <= 12 ? $" Known: {string.Join(", ", known)}."
            : $" Nearest: {string.Join(", ", near)}.";
        return new HelpAnswer.Refused("help_not_found", what + hint, Levels);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<HelpNode> NodesOf(JsonNode? topics) =>
        (topics as JsonObject ?? []).Select(pair => new HelpNode(pair.Key, (string?)pair.Value?["summary"] ?? string.Empty,
            (string?)pair.Value?["text"] ?? string.Empty, NodesOf(pair.Value?["subtopics"]))).ToList();

    // Levenshtein distance, for naming the nearest names on a miss.
    private static int Distance(string a, string b)
    {
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int[] current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }

    private sealed record HelpNode(string Name, string Summary, string Text, IReadOnlyList<HelpNode> Children);

    private sealed record ToolEntry(string Name, string Text, IReadOnlyList<HelpNode> Topics, IReadOnlyList<string> Shared);
}

/// <summary>A help node to read: tool (absent for a shared topic), topic, and subtopic when it is one.</summary>
public sealed record HelpPointer(string? Tool, string Topic, string? Subtopic);

/// <summary>What tool_info answers: the node's reply, or a refusal with the node that explains it.</summary>
public abstract record HelpAnswer
{
    private HelpAnswer()
    {
    }

    /// <summary>The node, as tool_info's reply object.</summary>
    public sealed record Found(JsonElement Reply) : HelpAnswer;

    /// <summary>No such node, or arguments that name none: code, message, and where tool_info's own help explains it.</summary>
    public sealed record Refused(string Code, string Message, HelpPointer See) : HelpAnswer;
}
