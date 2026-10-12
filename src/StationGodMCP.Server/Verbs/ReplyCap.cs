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

        shown["cut"] = new JsonObject { ["shown"] = kept.Count, ["total"] = entries.Count };
        shown["next"] = Next(refId, pageStart + kept.Count);
        return new CutReply(shown, key, kept.Count, entries.Count, bytes, Bytes(shown));
    }

    /// <summary>The one line that says how to get more of a cut reply.</summary>
    internal static string Next(string refId, int? nextOffset) =>
        $"whole: repeat '{refId}' with whole: true" +
        (nextOffset == null ? string.Empty : $" | page: repeat '{refId}' with offset: {nextOffset}") +
        $" | narrower: add a filter | as a target, {refId} is the shown entries";
}
