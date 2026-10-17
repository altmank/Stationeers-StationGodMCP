using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// One ref (rN): the call that made it (verb, what, options, for repeat), its whole reply, the reply as shown (after
/// the cut), and for a report the targets it listed.
/// </summary>
internal sealed class StoredRef
{
    internal StoredRef(string id, string verb, string? what, JsonObject options, JsonNode? whole, JsonNode? shown,
        string? mainList, bool report)
    {
        Id = id;
        Verb = verb;
        What = what;
        Options = options;
        Whole = whole;
        Shown = shown;
        MainList = mainList;
        Report = report;
    }

    internal string Id { get; }

    internal string Verb { get; }

    internal string? What { get; }

    internal JsonObject Options { get; }

    internal JsonNode? Whole { get; }

    /// <summary>The reply as shown; the whole reply once it was asked whole (then all of it was shown).</summary>
    internal JsonNode? Shown { get; set; }

    /// <summary>For a report: the options that act on exactly what it listed (refs and expanded spreads as ids).</summary>
    internal JsonObject? Replay { get; init; }

    /// <summary>Said when a repeat with confirm acts on the call again (a spread: the selection may differ).</summary>
    internal string? ReplayNote { get; init; }

    /// <summary>For a plan route or plan spot: the reply with its build_arguments, whatever the call's shaping showed.</summary>
    internal JsonNode? Planned { get; init; }

    internal string? MainList { get; }

    internal bool Report { get; }
}

/// <summary>A ref used as a target: the option path it stood in, the ref, and the ids it gave.</summary>
internal sealed class RefUse(string path, string reference, List<string> ids)
{
    internal string Path { get; } = path;

    internal string Ref { get; } = reference;

    internal List<string> Ids { get; } = ids;
}

/// <summary>
/// The refs of one hub (refs topic): minted rN with a session-wide counter, kept until the world changes. A ref as a
/// target is the ids its reply showed: "r3" its main list's shown entries (a report's targets), "r3.members" or
/// "r4.rockets.parts" a path through lists, "r9.spots.2" one entry.
/// </summary>
internal sealed class RefStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredRef> _refs = new(StringComparer.Ordinal);
    private int _next;

    internal string NextId()
    {
        lock (_gate)
        {
            _next++;
            return "r" + _next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal void Keep(StoredRef stored)
    {
        lock (_gate)
        {
            _refs[stored.Id] = stored;
        }
    }

    internal StoredRef? Get(string id)
    {
        lock (_gate)
        {
            return _refs.TryGetValue(id, out StoredRef? stored) ? stored : null;
        }
    }

    /// <summary>A world change: every id a ref holds may name something else now.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _refs.Clear();
        }
    }

    /// <summary>The ref a value names ("r7", "r7.things.2"): its id and path, or null for anything else.</summary>
    internal static (string Id, string Path)? Parse(string? text)
    {
        text = text?.Trim();
        if (text is not { Length: > 1 } || text[0] != 'r' || !char.IsAsciiDigit(text[1]))
        {
            return null;
        }

        int index = 1;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        if (index == text.Length)
        {
            return (text, string.Empty);
        }

        return text[index] == '.' && index + 1 < text.Length ? (text[..index], text[(index + 1)..]) : null;
    }

    /// <summary>The ids a ref (with its path) shows; empty when the path finds none.</summary>
    internal static List<string> IdsOf(StoredRef stored, string path)
    {
        List<string> ids = [];
        JsonNode? shown = stored.Shown;
        if (path.Length == 0)
        {
            JsonNode? main = stored.Report ? shown?["targets"] : stored.MainList != null ? shown?[stored.MainList] : null;
            Collect(main ?? shown, ids, 0);
            return ids;
        }

        List<JsonNode?> nodes = [shown];
        foreach (string step in path.Split('.'))
        {
            List<JsonNode?> next = [];
            bool index = int.TryParse(step, out int at);
            foreach (JsonNode? node in nodes)
            {
                switch (node)
                {
                    case JsonArray list when index:
                        if (at >= 0 && at < list.Count)
                        {
                            next.Add(list[at]);
                        }

                        break;
                    case JsonArray list:
                        foreach (JsonNode? entry in list)
                        {
                            if (entry is JsonObject inner && inner[step] is { } child)
                            {
                                next.Add(child);
                            }
                        }

                        break;
                    case JsonObject inner when inner[step] is { } child:
                        next.Add(child);
                        break;
                }
            }

            nodes = next;
        }

        foreach (JsonNode? node in nodes)
        {
            Collect(node, ids, 0);
        }

        return ids;
    }

    // An entry's id is its reference_id (or id); a plain string or number is an id itself.
    private static void Collect(JsonNode? node, List<string> ids, int depth)
    {
        switch (node)
        {
            case JsonArray list when depth < 2:
                foreach (JsonNode? entry in list)
                {
                    Collect(entry, ids, depth + 1);
                }

                break;
            case JsonObject entry:
                JsonNode? id = entry["reference_id"] ?? entry["id"];
                if (JsonNodes.Text(id) is { } text)
                {
                    ids.Add(text);
                }
                else if (JsonNodes.Number(id) is { } number)
                {
                    ids.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                break;
            case JsonValue value when JsonNodes.Text(value) is { } word:
                ids.Add(word);
                break;
            case JsonValue value when JsonNodes.Number(value) is { } whole:
                ids.Add(whole.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }
}
