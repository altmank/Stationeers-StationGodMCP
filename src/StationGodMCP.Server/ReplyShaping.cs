using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The shaping arguments of every tool that can give a large reply (the catalogue's x-shaping lists): fields keeps only
/// the named keys in each entry of the reply's top-level lists, and output_file writes the whole reply to a JSON file
/// and answers a small pointer instead. Take strips both from the tool's arguments. fields goes to the mod as the
/// request's shape.fields (ModShape); the mod marks a reply it shaped, and only a reply not so marked (an older mod) is
/// shaped here. output_file is always written here, on the caller's machine.
/// </summary>
internal sealed record ReplyShaping(FieldSelection? Fields, OutputTarget? Output, JsonElement? ModShape)
{
    internal const string FieldsArgument = "fields";
    internal const string OutputFileArgument = "output_file";

    internal static readonly string[] Arguments = [FieldsArgument, OutputFileArgument];

    private static readonly ReplyShaping None = new(null, null, null);

    /// <summary>The shaping asked for, and the arguments without it, for the game. A bad file name is invalid_argument.</summary>
    internal static (ReplyShaping Shaping, JsonElement Forwarded) Take(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !Arguments.Any(name => IsGiven(arguments, name)))
        {
            return (None, arguments);
        }

        FieldSelection? fields = IsGiven(arguments, FieldsArgument)
            ? FieldSelection.Of(arguments.GetProperty(FieldsArgument))
            : null;
        JsonElement? modShape = fields != null
            ? JsonSerializer.SerializeToElement(new { fields = arguments.GetProperty(FieldsArgument) })
            : null;
        OutputTarget? output = IsGiven(arguments, OutputFileArgument)
            ? OutputTarget.Of(arguments.GetProperty(OutputFileArgument))
            : null;
        JsonObject forwarded = JsonObject.Create(arguments)!;
        foreach (string name in Arguments)
        {
            forwarded.Remove(name);
        }

        return (new ReplyShaping(fields, output, modShape), JsonSerializer.SerializeToElement(forwarded));
    }

    /// <summary>
    /// The reply as the caller asked for it: fields applied unless the mod already shaped it, then written to a file when
    /// output_file is given.
    /// </summary>
    internal JsonElement Apply(string tool, JsonElement reply, OutputFolder folder, bool shapedByMod = false)
    {
        JsonElement selected = shapedByMod ? reply : Fields?.Apply(reply) ?? reply;
        return Output == null ? selected : folder.Write(tool, Output, selected);
    }

    private static bool IsGiven(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null;
}

/// <summary>
/// fields: the keys to keep in every object entry of the reply's top-level lists (things, members, items...); the other
/// top-level keys stay as they are. A name no entry has is listed in fields_unmatched, so a misspelt field is seen
/// rather than silently giving empty entries.
/// </summary>
internal sealed record FieldSelection(IReadOnlyList<string> Names)
{
    internal const string UnmatchedKey = "fields_unmatched";

    internal static FieldSelection Of(JsonElement value) =>
        new(value.EnumerateArray().Select(name => name.GetString()!.Trim()).Distinct(StringComparer.Ordinal).ToArray());

    internal JsonElement Apply(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object)
        {
            return reply;
        }

        JsonObject shaped = JsonObject.Create(reply)!;
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool anyEntry = false;
        foreach (JsonArray list in shaped.Select(property => property.Value).OfType<JsonArray>())
        {
            foreach (JsonObject entry in list.OfType<JsonObject>())
            {
                anyEntry = true;
                seen.UnionWith(entry.Select(property => property.Key));
                foreach (string key in entry.Select(property => property.Key).Where(key => !Names.Contains(key)).ToArray())
                {
                    entry.Remove(key);
                }
            }
        }

        string[] unmatched = Names.Where(name => !seen.Contains(name)).ToArray();
        if (anyEntry && unmatched.Length > 0)
        {
            shaped[UnmatchedKey] = new JsonArray(unmatched.Select(name => (JsonNode)JsonValue.Create(name)).ToArray());
        }

        return JsonSerializer.SerializeToElement(shaped);
    }
}

/// <summary>output_file: true names the file after the tool and the time; a string is the file name to use.</summary>
internal abstract record OutputTarget
{
    private OutputTarget()
    {
    }

    /// <summary>The target output_file names; null for false, which answers in the reply as leaving it out does.</summary>
    internal static OutputTarget? Of(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => new Auto(),
        JsonValueKind.False => null,
        JsonValueKind.String => new Named(OutputFileName.Parse(value.GetString()!)),
        _ => throw ToolFailure.Argument("Argument 'output_file' must be true, false or a file name.")
    };

    internal sealed record Auto : OutputTarget;

    internal sealed record Named(OutputFileName Name) : OutputTarget;
}

/// <summary>
/// A file name the caller chose for output_file: letters, digits, '-', '_' and '.', at most 80 characters, no path; .json
/// is added when it does not end so. The file is always in the output folder.
/// </summary>
internal sealed record OutputFileName
{
    private const int MaximumLength = 80;
    private const string Extension = ".json";

    private OutputFileName(string value) => Value = value;

    internal string Value { get; }

    internal static OutputFileName Parse(string given)
    {
        string name = given.Trim();
        bool allowed = name.Length is > 0 and <= MaximumLength && name.All(IsAllowed) && name[0] != '.' &&
                       !name.Contains("..", StringComparison.Ordinal);
        if (!allowed)
        {
            throw ToolFailure.Argument(
                $"Argument 'output_file' must be true or a plain file name (letters, digits, '-', '_' and '.', at most " +
                $"{MaximumLength} characters, not starting with '.', no folders); '{given}' is not.");
        }

        return new OutputFileName(name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Extension);
    }

    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';
}
