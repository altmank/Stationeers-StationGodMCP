using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>A reply the cap cut: what is shown, how much of its main list, and its size before and after.</summary>
internal sealed class CutReply(JsonObject shown, string list, int shownCount, int total, int bytes, int sent)
{
    internal JsonObject Shown { get; } = shown;

    internal string List { get; } = list;

    internal int ShownCount { get; } = shownCount;

    internal int Total { get; } = total;

    internal int Bytes { get; } = bytes;

    internal int Sent { get; } = sent;
}

/// <summary>
/// The direct face's reply cap (shaping topic): a reply over 12 KB of JSON is cut to about 4 KB by keeping the first
/// whole entries of its main list that fit, with ref, cut {shown, total} and next (one line: how to get more) added.
/// </summary>
internal static class ReplyCap
{
    internal const int Threshold = 12 * 1024;

    internal const int Target = 4 * 1024;

    // The text the MCP reply carries is written with plain escaping: measure it the same way.
    internal static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    internal static int Bytes(JsonNode? node) => Encoding.UTF8.GetByteCount(node?.ToJsonString(TextOptions) ?? "null");

    /// <summary>The reply's main list: the declared one when it is a list here, else its largest list; null for none.</summary>
    internal static string? MainListOf(JsonObject reply, string? declared)
    {
        if (declared != null && reply[declared] is JsonArray)
        {
            return declared;
        }

        string? largest = null;
        int most = -1;
        foreach ((string key, JsonNode? value) in reply)
        {
            if (value is JsonArray list && Bytes(list) > most)
            {
                most = Bytes(list);
                largest = key;
            }
        }

        return largest;
    }

    /// <summary>
    /// The reply cut to about 4 KB, or null when it is not over 12 KB or has no list to cut. pageStart is the offset the
    /// page began at when the what pages by offset (next then names the offset after the shown entries).
    /// </summary>
    internal static CutReply? Cut(JsonObject reply, string? declared, string refId, int? pageStart)
    {
        int bytes = Bytes(reply);
        if (bytes <= Threshold || MainListOf(reply, declared) is not { } key)
        {
            return null;
        }

        JsonArray entries = reply[key]!.AsArray();
        JsonObject shown = new();
        foreach ((string name, JsonNode? value) in reply)
        {
            shown[name] = name == key ? new JsonArray() : value?.DeepClone();
        }

        shown["ref"] = refId;
        shown["cut"] = new JsonObject { ["shown"] = entries.Count, ["total"] = entries.Count };
        shown["next"] = Next(refId, pageStart + entries.Count);

        // The rest of the reply leaves the main list at least half the room: its other lists are cut first.
        JsonObject others = new();
        Shrink(shown, key, Target / 2, others);
        int used = Bytes(shown);
        JsonArray kept = shown[key]!.AsArray();
        foreach (JsonNode? entry in entries)
        {
            int size = Bytes(entry) + (kept.Count > 0 ? 1 : 0);
            if (used + size > Target)
            {
                break;
            }

            used += size;
            kept.Add(entry?.DeepClone());
        }

        // Never 0 entries in silence: a first entry too large for the room is shown shallow, else as its id.
        bool shallow = false;
        if (kept.Count == 0 && entries.Count > 0)
        {
            shallow = true;
            JsonNode first = Shallow(entries[0]);
            kept.Add(used + Bytes(first) <= Target ? first : new JsonObject
            {
                ["reference_id"] = entries[0]?["reference_id"]?.DeepClone(), ["truncated"] = true
            });
        }

        JsonObject cut = new() { ["shown"] = kept.Count, ["total"] = entries.Count };
        if (shallow)
        {
            cut["shallow"] = true;
        }

        if (others.Count > 0)
        {
            cut["lists"] = others;
        }

        shown["cut"] = cut;
        shown["next"] = Next(refId, pageStart + kept.Count);
        Shrink(shown, key, Target, others);
        return new CutReply(shown, key, kept.Count, entries.Count, bytes, Bytes(shown));
    }

    /// <summary>A report's largest size: a report is never cut, and one larger answers report_too_large.</summary>
    internal const int ReportLimit = 256 * 1024;

    // Cuts the reply's other lists, largest first and wherever they are, until the reply is within room bytes; each one
    // cut is named in others with what it shows of its length.
    private static void Shrink(JsonObject reply, string main, int room, JsonObject others)
    {
        List<(string Path, JsonArray List)> lists = [];
        Lists(reply, string.Empty, main, lists, 0);
        lists.Sort((a, b) => Bytes(b.List).CompareTo(Bytes(a.List)));
        foreach ((string path, JsonArray list) in lists)
        {
            int excess = Bytes(reply) - room;
            if (excess <= 0)
            {
                return;
            }

            int total = others[path] is JsonObject seen ? (int)JsonNodes.Number(seen["total"])! : list.Count;
            int removed = 0;
            while (list.Count > 0 && removed < excess)
            {
                removed += Bytes(list[^1]) + 1;
                list.RemoveAt(list.Count - 1);
            }

            others[path] = new JsonObject { ["shown"] = list.Count, ["total"] = total };
        }
    }

    private static void Lists(JsonNode? node, string path, string main, List<(string, JsonArray)> found, int depth)
    {
        if (depth > 6 || node is not JsonObject entry)
        {
            return;
        }

        foreach ((string name, JsonNode? value) in entry)
        {
            string at = path.Length == 0 ? name : path + "." + name;
            if (depth == 0 && name is "cut" or "next" or "ref")
            {
                continue;
            }

            if (value is JsonArray list)
            {
                if (at != main)
                {
                    found.Add((at, list));
                }
            }
            else
            {
                Lists(value, at, main, found, depth + 1);
            }
        }
    }

    /// <summary>An entry in shallow form: plain values kept, nested lists as their length, objects as {...}.</summary>
    internal static JsonNode Shallow(JsonNode? entry)
    {
        if (entry is not JsonObject whole)
        {
            return entry?.DeepClone() ?? JsonValue.Create("null")!;
        }

        JsonObject shallow = new();
        foreach ((string name, JsonNode? value) in whole)
        {
            shallow[name] = value switch
            {
                JsonArray list => list.Count,
                JsonObject => "{...}",
                _ => value?.DeepClone()
            };
        }

        return shallow;
    }

    /// <summary>The one line that says how to get more of a cut reply.</summary>
    internal static string Next(string refId, int? nextOffset) =>
        $"whole: repeat '{refId}' with whole: true" +
        (nextOffset == null ? string.Empty : $" | page: repeat '{refId}' with offset: {nextOffset}") +
        $" | narrower: add a filter | as a target, {refId} is the shown entries";
}
