using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>The client's messages as compact JSON lines, and reading the keys of the server's.</summary>
internal static class Wire
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Hello(ClientOptions options) => Hello(options, Library.Features, resume: null);

    /// <summary>hello with the features offered and, when given, the resume token (protocol.md, Resuming).</summary>
    internal static string Hello(ClientOptions options, IReadOnlyList<string> features, string? resume) => Write(writer =>
    {
        writer.WriteString("type", "hello");
        writer.WriteStartArray("protocol");
        writer.WriteNumberValue(2);
        writer.WriteEndArray();
        writer.WriteStartObject("client");
        writer.WriteString("name", options.HelloName);
        writer.WriteString("version", options.ClientVersion);
        writer.WriteString("library", Library.Name);
        writer.WriteEndObject();
        writer.WriteStartArray("features");
        foreach (string feature in features)
        {
            writer.WriteStringValue(feature);
        }

        writer.WriteEndArray();
        if (resume != null)
        {
            writer.WriteString("resume", resume);
        }
    });

    // The TCP sign-in: the shared secret in plain text, sent before anything else.
    internal static string SecretAuth(string secret) => Write(writer =>
    {
        writer.WriteString("type", "auth");
        writer.WriteString("secret", secret);
    });

    /// <summary>
    /// A call: {type, id, method, params, shape?, deadline_ms?}, shape only when the server lists the feature,
    /// deadline_ms only when the caller gave one.
    /// </summary>
    internal static string Call(IReadOnlySet<string> features, string id, string method, JsonElement parameters,
        JsonElement? shape, int? deadlineMs, JsonElement? rename = null) => Write(writer =>
    {
        writer.WriteString("type", "call");
        writer.WriteString("id", id);
        writer.WriteString("method", method);
        writer.WritePropertyName("params");
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            parameters.WriteTo(writer);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }

        if (shape is { ValueKind: JsonValueKind.Object } given && features.Contains("shape"))
        {
            writer.WritePropertyName("shape");
            given.WriteTo(writer);
        }

        if (deadlineMs is { } deadline)
        {
            writer.WriteNumber("deadline_ms", deadline);
        }

        // An older server refuses the key as an unknown one, so it goes only where welcome lists the feature.
        if (rename is { ValueKind: JsonValueKind.Object } names && features.Contains("rename"))
        {
            writer.WritePropertyName("rename");
            names.WriteTo(writer);
        }
    });

    /// <summary>
    /// A link call: {type, id, method, params, stream?, shape?, rename?, deadline_ms?, world?}. stream only when it is
    /// not 0 and the server lists streams, shape and rename only when listed, world whenever the link knows one.
    /// Written without a closure, since every call of the hub passes here.
    /// </summary>
    internal static string LinkCall(IReadOnlySet<string> features, string id, GameCall call, JsonElement parameters,
        string? world)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "call");
            writer.WriteString("id", id);
            writer.WriteString("method", call.Method);
            writer.WritePropertyName("params");
            parameters.WriteTo(writer);
            if (call.Stream != 0 && features.Contains("streams"))
            {
                writer.WriteNumber("stream", call.Stream);
            }

            if (call.Shape is { ValueKind: JsonValueKind.Object } shape && features.Contains("shape"))
            {
                writer.WritePropertyName("shape");
                shape.WriteTo(writer);
            }

            if (call.Rename is { ValueKind: JsonValueKind.Object } rename && features.Contains("rename"))
            {
                writer.WritePropertyName("rename");
                rename.WriteTo(writer);
            }

            if (call.DeadlineMs is { } deadline)
            {
                writer.WriteNumber("deadline_ms", deadline);
            }

            if (world != null)
            {
                writer.WriteString("world", world);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static string StreamEnd(long stream) => Write(writer =>
    {
        writer.WriteString("type", "stream_end");
        writer.WriteNumber("stream", stream);
    });

    internal static string SessionEnd() => Write(writer => writer.WriteString("type", "session_end"));

    // protocol.md, Job pins: the stream's job pins go. Sent only when the mod lists the feature release.
    internal static string Release(long stream) => Write(writer =>
    {
        writer.WriteString("type", "release");
        writer.WriteNumber("stream", stream);
    });

    internal static string Cancel(string id) => Write(writer =>
    {
        writer.WriteString("type", "cancel");
        writer.WriteString("id", id);
    });

    internal static string Bye() => Write(writer => writer.WriteString("type", "bye"));

    internal static string? Text(JsonElement message, string key) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static JsonElement? Child(JsonElement message, string key) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    /// <summary>The message a line holds, or null when it is not a JSON object.</summary>
    internal static JsonElement? Parse(byte[] line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Start(byte[] line) => Encoding.UTF8.GetString(line, 0, Math.Min(line.Length, 120));

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Compact))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
