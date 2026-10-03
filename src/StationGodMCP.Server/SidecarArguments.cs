using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The sidecar's own arguments on every tool that can give a large reply (the catalogue's x-shaping lists), taken out
/// before the call goes to the game. fields goes to the mod as the call's shape.fields; the mod applies it and marks the
/// reply shaped, and the sidecar never shapes a reply the mod sent. output_file never reaches the game: the reply is
/// written on this machine.
/// </summary>
internal sealed partial record SidecarArguments(JsonElement Forwarded, JsonElement? Fields, OutputChoice Output)
{
    internal const string FieldsArgument = "fields";
    internal const string OutputFileArgument = "output_file";
    internal const string UnmatchedKey = "fields_unmatched";

    internal static readonly string[] Names = [FieldsArgument, OutputFileArgument];

    /// <summary>The arguments for the game without fields and output_file, and what those two ask for.</summary>
    internal static SidecarArguments Take(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !Names.Any(name => IsGiven(arguments, name)))
        {
            return new SidecarArguments(arguments, null, new OutputChoice.Inline());
        }

        return new SidecarArguments(
            Without(arguments, Names),
            IsGiven(arguments, FieldsArgument) ? arguments.GetProperty(FieldsArgument).Clone() : null,
            OutputChoice.Of(IsGiven(arguments, OutputFileArgument) ? arguments.GetProperty(OutputFileArgument) : null));
    }

    /// <summary>
    /// The shape to send. The mod refuses a selector that does not follow the grammar (invalid_shape), where the
    /// sidecar has always reported it in fields_unmatched; so only the selectors that parse are sent, trimmed and each
    /// once, and the rest are added to the reply's fields_unmatched here (Unparsed).
    /// </summary>
    internal (JsonElement? Shape, IReadOnlyList<string> Unparsed) Shape()
    {
        if (Fields is not { } fields)
        {
            return (null, []);
        }

        if (fields.ValueKind != JsonValueKind.Array)
        {
            return (JsonSerializer.SerializeToElement(new { fields }), []);
        }

        List<string> parsed = [];
        List<string> unparsed = [];
        foreach (JsonElement selector in fields.EnumerateArray())
        {
            string text = selector.ValueKind == JsonValueKind.String ? selector.GetString()!.Trim() : selector.GetRawText();
            List<string> into = selector.ValueKind == JsonValueKind.String && Selector().IsMatch(text) ? parsed : unparsed;
            if (!into.Contains(text, StringComparer.Ordinal))
            {
                into.Add(text);
            }
        }

        return (parsed.Count > 0 ? JsonSerializer.SerializeToElement(new { fields = parsed }) : null, unparsed);
    }

    /// <summary>The reply with the unparsed selectors added to fields_unmatched, when it has an object entry in a list.</summary>
    internal static JsonElement WithUnmatched(JsonElement reply, IReadOnlyList<string> unparsed)
    {
        if (unparsed.Count == 0 || reply.ValueKind != JsonValueKind.Object || !HasListEntry(reply))
        {
            return reply;
        }

        JsonObject shaped = JsonObject.Create(reply)!;
        JsonArray unmatched = shaped[UnmatchedKey] as JsonArray ?? [];
        shaped.Remove(UnmatchedKey);
        foreach (string selector in unparsed)
        {
            unmatched.Add(selector);
        }

        shaped[UnmatchedKey] = unmatched;
        return JsonSerializer.SerializeToElement(shaped);
    }

    private static bool HasListEntry(JsonElement reply) =>
        reply.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Array &&
                                                property.Value.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.Object));

    // Copies the object's properties except the named ones, keeping every other key as given (a repeated key included,
    // for the mod to refuse).
    private static JsonElement Without(JsonElement arguments, string[] names)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in arguments.EnumerateObject())
            {
                if (!names.Contains(property.Name, StringComparer.Ordinal))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static bool IsGiven(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null;

    // protocol.md, Field selectors: name *( "." name ), name = 1*( ALPHA / DIGIT / "_" ).
    [GeneratedRegex("^[A-Za-z0-9_]+(\\.[A-Za-z0-9_]+)*$")]
    private static partial Regex Selector();
}
