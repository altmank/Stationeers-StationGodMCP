using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>The client's messages as compact JSON lines, and reading the keys of the server's.</summary>
internal static class Wire
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Hello(ClientOptions options, bool offersKey) => Write(writer =>
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
        foreach (string feature in Library.Features)
        {
            writer.WriteStringValue(feature);
        }

        writer.WriteEndArray();
        if (offersKey)
        {
            writer.WriteString("auth", "key");
        }
    });

    internal static string Auth(string client, string proof) => Write(writer =>
    {
        writer.WriteString("type", "auth");
        writer.WriteString("client", client);
        writer.WriteString("proof", proof);
    });

    // Today's TCP sign-in: the shared secret in plain text, version 1 only.
    internal static string LegacyAuth(string secret) => Write(writer =>
    {
        writer.WriteString("type", "auth");
        writer.WriteString("secret", secret);
    });

    /// <summary>
    /// A call. Version 1: {id, method, params, shape?}, shape always sent (an old mod ignores it). Version 2: {type,
    /// id, method, params, shape?, deadline_ms?}, shape only when the server lists the feature, deadline_ms only when
    /// the caller gave one.
    /// </summary>
    internal static string Call(ProtocolVersion version, IReadOnlySet<string> features, string id, string method,
        JsonElement parameters, JsonElement? shape, int? deadlineMs) => Write(writer =>
    {
        if (version == ProtocolVersion.Version2)
        {
            writer.WriteString("type", "call");
        }

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

        if (shape is { ValueKind: JsonValueKind.Object } given &&
            (version == ProtocolVersion.Version1 || features.Contains("shape")))
        {
            writer.WritePropertyName("shape");
            given.WriteTo(writer);
        }

        if (version == ProtocolVersion.Version2 && deadlineMs is { } deadline)
        {
            writer.WriteNumber("deadline_ms", deadline);
        }
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
