using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The sidecar's own arguments on every tool that can give a large reply (the catalogue's x-shaping lists), taken out
/// before the call goes to the game. fields, omit and limits go to the mod as the call's shape.fields, shape.omit and
/// shape.limit; the mod applies them and marks the reply shaped, and the sidecar never shapes a reply the mod sent.
/// output_file never reaches the game: the reply is written on this machine.
/// </summary>
internal sealed partial record SidecarArguments(JsonElement Forwarded, JsonElement? Fields, OutputChoice Output,
    JsonElement? Omit = null, JsonElement? Limits = null)
{
    internal const string FieldsArgument = "fields";
    internal const string OmitArgument = "omit";
    internal const string LimitsArgument = "limits";
    internal const string OutputFileArgument = "output_file";
    internal const string UnmatchedKey = "fields_unmatched";
    internal const string OmitUnmatchedKey = "omit_unmatched";

    internal static readonly string[] Names = [FieldsArgument, OmitArgument, LimitsArgument, OutputFileArgument];

    /// <summary>The arguments for the game without fields, omit, limits and output_file, and what those ask for.</summary>
    internal static SidecarArguments Take(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !Names.Any(name => IsGiven(arguments, name)))
        {
            return new SidecarArguments(arguments, null, new OutputChoice.Inline());
        }

        return new SidecarArguments(
            Without(arguments, Names),
            Given(arguments, FieldsArgument),
            OutputChoice.Of(IsGiven(arguments, OutputFileArgument) ? arguments.GetProperty(OutputFileArgument) : null),
            Given(arguments, OmitArgument),
            Given(arguments, LimitsArgument));
    }

    /// <summary>
    /// The shape to send. The mod refuses a selector that does not follow the grammar (invalid_shape), where the
    /// sidecar has always reported it in fields_unmatched; so only the selectors that parse are sent, trimmed and each
    /// once, and the rest are added to the reply's fields_unmatched or omit_unmatched here (Unparsed).
    /// </summary>
    internal (JsonElement? Shape, Unparsed Unparsed) Shape()
    {
        if (Fields is null && Omit is null && Limits is null)
        {
            return (null, new Unparsed([], []));
        }

        Dictionary<string, object> shape = [];
        List<string> fieldsUnparsed = Selectors(Fields, FieldsArgument, shape);
        List<string> omitUnparsed = Selectors(Omit, OmitArgument, shape);
        if (Limits is { } limits)
        {
            shape["limit"] = limits;
        }

        return (shape.Count > 0 ? JsonSerializer.SerializeToElement(shape) : null,
            new Unparsed(fieldsUnparsed, omitUnparsed));
    }

    // The selectors that parse go into shape[key]; a value that is not an array goes as it came, for the mod to judge.
    private static List<string> Selectors(JsonElement? given, string key, Dictionary<string, object> shape)
    {
        if (given is not { } selectors)
        {
            return [];
        }

        if (selectors.ValueKind != JsonValueKind.Array)
        {
            shape[key] = selectors;
            return [];
        }

        List<string> parsed = [];
        List<string> unparsed = [];
        foreach (JsonElement selector in selectors.EnumerateArray())
        {
            string text = selector.ValueKind == JsonValueKind.String ? selector.GetString()!.Trim() : selector.GetRawText();
            List<string> into = selector.ValueKind == JsonValueKind.String && Selector().IsMatch(text) ? parsed : unparsed;
            if (!into.Contains(text, StringComparer.Ordinal))
            {
                into.Add(text);
            }
        }

        if (parsed.Count > 0)
        {
            shape[key] = parsed;
        }

        return unparsed;
    }

    /// <summary>
    /// The reply with the unparsed selectors added: fields ones to fields_unmatched when it has an object entry in a
    /// list (as the mod reports fields), omit ones to omit_unmatched always.
    /// </summary>
    internal static JsonElement WithUnmatched(JsonElement reply, Unparsed unparsed)
    {
        bool fields = unparsed.Fields.Count > 0 && reply.ValueKind == JsonValueKind.Object && HasListEntry(reply);
        bool omit = unparsed.Omit.Count > 0 && reply.ValueKind == JsonValueKind.Object;
        if (!fields && !omit)
        {
            return reply;
        }

        JsonObject shaped = JsonObject.Create(reply)!;
        if (fields)
        {
            Append(shaped, UnmatchedKey, unparsed.Fields);
        }

        if (omit)
        {
            Append(shaped, OmitUnmatchedKey, unparsed.Omit);
        }

        return JsonSerializer.SerializeToElement(shaped);
    }

    private static void Append(JsonObject reply, string key, IReadOnlyList<string> selectors)
    {
        JsonArray unmatched = reply[key] as JsonArray ?? [];
        reply.Remove(key);
        foreach (string selector in selectors)
        {
            unmatched.Add(selector);
        }

        reply[key] = unmatched;
    }

    private static JsonElement? Given(JsonElement arguments, string name) =>
        IsGiven(arguments, name) ? arguments.GetProperty(name).Clone() : null;

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

/// <summary>The selectors the sidecar could not send, by argument: each is reported as unmatched in the reply.</summary>
internal sealed record Unparsed(IReadOnlyList<string> Fields, IReadOnlyList<string> Omit);
