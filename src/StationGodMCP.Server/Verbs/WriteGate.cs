using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// Asked before every write, with the call's resolved targets (docs/architecture/hub.md, C# seams). The direct face's
/// gate is the direct write rule; a run's gate is its route and the seen set.
/// </summary>
public interface IWriteGate
{
    WriteDecision Decide(VerbCall call, WriteTargets targets);
}

/// <summary>The cap calls it with the text actually sent, so the seen set learns the ids shown.</summary>
public interface IShownText
{
    void Shown(string text);
}

/// <summary>What the gate decided: act, report instead, refuse with a row, or stop the run with rows and a fix.</summary>
public abstract class WriteDecision
{
    private WriteDecision()
    {
    }

    public sealed class Act : WriteDecision
    {
    }

    public sealed class Report : WriteDecision
    {
    }

    public sealed class Refuse : WriteDecision
    {
        public Refuse(JsonNode row)
        {
            Row = row;
        }

        public JsonNode Row { get; }
    }

    public sealed class Stop : WriteDecision
    {
        public Stop(JsonArray rows, string fix)
        {
            Rows = rows;
            Fix = fix;
        }

        public JsonArray Rows { get; }

        public string Fix { get; }
    }
}

/// <summary>A write call's targets: each x-target path given, with the ids or selector found there.</summary>
public sealed class WriteTargets
{
    public WriteTargets(IReadOnlyList<WriteTarget> targets)
    {
        Targets = targets;
    }

    public IReadOnlyList<WriteTarget> Targets { get; }
}

/// <summary>
/// One target: its x-target path and role (acts-on, pays-from), the ids given there (strings; a number id as its text,
/// words such as "player" as written), and the selector value when the path is a selector (network, room, near, box)
/// or holds an object.
/// </summary>
public sealed class WriteTarget
{
    public WriteTarget(string path, string role, IReadOnlyList<string> ids, JsonNode? selector)
    {
        Path = path;
        Role = role;
        Ids = ids;
        Selector = selector;
    }

    public string Path { get; }

    public string Role { get; }

    public IReadOnlyList<string> Ids { get; }

    public JsonNode? Selector { get; }
}

/// <summary>A gate that lets every write act as sent.</summary>
internal sealed class PassThroughGate : IWriteGate
{
    internal static readonly PassThroughGate Instance = new();

    private static readonly WriteDecision.Act Acts = new();

    public WriteDecision Decide(VerbCall call, WriteTargets targets) => Acts;
}

/// <summary>Reads a write what's x-target paths out of the options.</summary>
internal static class WriteTargetPaths
{
    private static readonly string[] Selectors = ["network", "room", "near", "box"];

    internal static WriteTargets Resolve(WriteRules rules, JsonObject options)
    {
        List<WriteTarget> targets = [];
        foreach (TargetPath path in rules.Targets)
        {
            List<string> ids = [];
            List<JsonNode> selectors = [];
            Collect(options, path.Path, ids, selectors);
            if (ids.Count == 0 && selectors.Count == 0)
            {
                continue;
            }

            JsonNode? selector = selectors.Count switch
            {
                0 => null,
                1 => selectors[0],
                _ => new JsonArray(selectors.ToArray())
            };
            targets.Add(new WriteTarget(path.Path, path.Role, ids, selector));
        }

        return new WriteTargets(targets);
    }

    // A path is dotted names, where name[] walks every entry of a list ("writes[].id").
    private static void Collect(JsonNode? node, string path, List<string> ids, List<JsonNode> selectors)
    {
        if (node == null)
        {
            return;
        }

        if (path.Length == 0)
        {
            Leaf(node, ids, selectors);
            return;
        }

        int dot = path.IndexOf('.');
        string step = dot < 0 ? path : path[..dot];
        string rest = dot < 0 ? string.Empty : path[(dot + 1)..];
        bool each = step.EndsWith("[]", StringComparison.Ordinal);
        string name = each ? step[..^2] : step;
        if (node is not JsonObject container || container[name] is not { } child)
        {
            return;
        }

        if (rest.Length == 0 && Array.IndexOf(Selectors, name) >= 0)
        {
            selectors.Add(child.DeepClone());
            return;
        }

        if (each && child is JsonArray entries)
        {
            foreach (JsonNode? entry in entries)
            {
                Collect(entry, rest, ids, selectors);
            }

            return;
        }

        Collect(child, rest, ids, selectors);
    }

    private static void Leaf(JsonNode node, List<string> ids, List<JsonNode> selectors)
    {
        switch (node)
        {
            case JsonArray list:
                foreach (JsonNode? item in list)
                {
                    if (item != null)
                    {
                        Leaf(item, ids, selectors);
                    }
                }

                break;
            case JsonObject:
                selectors.Add(node.DeepClone());
                break;
            case JsonValue value when value.TryGetValue(out string? text):
                ids.Add(text);
                break;
            default:
                ids.Add(node.ToJsonString());
                break;
        }
    }
}
