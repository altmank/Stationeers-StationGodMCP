using System.Text.Json;

namespace StationGodMCP.Server;

/// <summary>
/// The ids this hub has shown its agent (docs/architecture/hub.md, The seen set): one set per hub, shared by the runs
/// and the direct face (a documented limit). A confirmed run's write may act only on targets in it, or on the run's own
/// literals, builds and "player" (RunGate). The cap feeds it every reply text actually sent (IShownText); a run feeds
/// it its printed and returned text, report rows and stop rows when its reply goes out.
/// </summary>
public sealed class SeenSet : IShownText
{
    private const int LongestId = 19;

    // The keys whose values name things: an allow-list, since a key ending _id may hold anything (a network, a job).
    private static readonly string[] IdListKeys =
        ["id", "ids", "reference_id", "reference_ids", "created_ids", "placed", "removed", "targets", "thing_id", "device_id"];

    private readonly object _gate = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public bool Contains(string id)
    {
        lock (_gate)
        {
            return _ids.Contains(id);
        }
    }

    /// <summary>A world change: ids are reused across saves, so nothing shown before names anything now.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _ids.Clear();
        }
    }

    public void Add(string id)
    {
        lock (_gate)
        {
            _ids.Add(id);
        }
    }

    public void AddAll(List<string> ids)
    {
        lock (_gate)
        {
            foreach (string id in ids)
            {
                _ids.Add(id);
            }
        }
    }

    /// <summary>A JSON reply as sent to the MCP client: the ids under id-like keys. Text that is not JSON adds nothing.</summary>
    public void Shown(string text) => AddAll(IdsInJson(text));

    /// <summary>A finished run's printed or returned text within its cut: every free-standing run of digits.</summary>
    public void ShownPlain(string text) => AddAll(IdsInPlainText(text));

    /// <summary>
    /// Every string of 1 to 19 digits, or integer, that is the value or an element of the value of a key named id, ids,
    /// reference_id, reference_ids, created_ids, placed, removed, targets, thing_id, device_id, or ending _reference_id;
    /// at any depth.
    /// </summary>
    internal static List<string> IdsInJson(string text)
    {
        List<string> ids = [];
        if (text.Length == 0 || (text[0] != '{' && text[0] != '['))
        {
            return ids;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            Walk(document.RootElement, ids);
        }
        catch (JsonException)
        {
            // Not JSON: plain text carries no keys to read ids under.
        }

        return ids;
    }

    /// <summary>Every run of 1 to 19 digits that touches no letter, digit, '.' or '-'.</summary>
    internal static List<string> IdsInPlainText(string text)
    {
        List<string> ids = [];
        int index = 0;
        while (index < text.Length)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            int length = index - start;
            bool freeBefore = start == 0 || IsFree(text[start - 1]);
            bool freeAfter = index == text.Length || IsFree(text[index]);
            if (length <= LongestId && freeBefore && freeAfter)
            {
                ids.Add(text.Substring(start, length));
            }
        }

        return ids;
    }

    // A digit run must not touch these: a letter or digit makes it part of a word, '.' a decimal, '-' a sign or range.
    private static bool IsFree(char neighbour) => !char.IsLetterOrDigit(neighbour) && neighbour != '.' && neighbour != '-';

    private static void Walk(JsonElement element, List<string> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (IsIdKey(property.Name))
                    {
                        Take(property.Value, ids);
                    }

                    Walk(property.Value, ids);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, ids);
                }

                break;
        }
    }

    private static bool IsIdKey(string key) =>
        Array.IndexOf(IdListKeys, key) >= 0 || key.EndsWith("_reference_id", StringComparison.Ordinal);

    // The value itself, or each element of a list value.
    private static void Take(JsonElement value, List<string> ids)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                TakeOne(item, ids);
            }

            return;
        }

        TakeOne(value, ids);
    }

    private static void TakeOne(JsonElement value, List<string> ids)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && IsDigits(text))
        {
            ids.Add(text);
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long whole) && whole >= 0)
        {
            ids.Add(whole.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    internal static bool IsDigits(string text)
    {
        if (text.Length is 0 or > LongestId)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
