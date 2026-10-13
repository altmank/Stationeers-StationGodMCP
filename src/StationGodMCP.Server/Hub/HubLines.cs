using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The lines a hub session sends (protocol.md, Messages), as compact JSON. Replies and events are written per message,
/// so they go through one Utf8JsonWriter each with no closures.
/// </summary>
internal static class HubLines
{
    internal const string Supported = "{\"supported\":[2]}";

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>
    /// A game reply passed back under the caller's id: the result with its frame, or the mod's error.
    /// </summary>
    internal static string Reply(string id, GameReply reply)
    {
        switch (reply)
        {
            case GameReply.Ok ok:
                return Ok(id, ok.Result, ok);
            case GameReply.Refused refused:
                return Error(id, refused.Code, refused.Message, refused.Data, refused.MaybeRan);
            case GameReply.Unreachable unreachable:
                return Error(id, HubSession.GameUnreachable, unreachable.Message, null, unreachable.MaybeRan);
            default:
                throw new InvalidOperationException("A game reply is ok, refused or unreachable.");
        }
    }

    /// <summary>A verb's (or a pipeline method's) reply: the result, or the hub error object.</summary>
    internal static string Reply(string id, VerbReply reply)
    {
        switch (reply)
        {
            case VerbReply.Done done:
                return Ok(id, done.Reply);
            case VerbReply.Failed failed:
                return Error(id, failed.ToJson());
            default:
                throw new InvalidOperationException("A verb reply is done or failed.");
        }
    }

    internal static string Ok(string id, JsonElement result, GameReply.Ok? meta)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = Start(buffer, id, ok: true))
        {
            writer.WritePropertyName("result");
            result.WriteTo(writer);
            if (meta != null)
            {
                if (meta.Shaped)
                {
                    writer.WriteBoolean("shaped", true);
                }

                writer.WriteNumber("elapsed_ms", meta.ElapsedMs);
                if (meta.QueueMs is { } queue)
                {
                    writer.WriteNumber("queue_ms", queue);
                }

                writer.WriteNumber("frame", meta.Frame);
                if (meta.BudgetHit)
                {
                    writer.WriteBoolean("budget_hit", true);
                }
            }

            writer.WriteEndObject();
        }

        return Text(buffer);
    }

    internal static string Ok(string id, JsonNode? result)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = Start(buffer, id, ok: true))
        {
            writer.WritePropertyName("result");
            if (result == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                result.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Text(buffer);
    }

    /// <summary>An error reply; id null for a message that has none (hello, stream_end, a broken line).</summary>
    internal static string Error(string? id, string code, string message, JsonElement? data = null, bool maybeRan = false)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = Start(buffer, id, ok: false))
        {
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            if (maybeRan)
            {
                writer.WriteBoolean("maybe_ran", true);
            }

            if (data is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given)
            {
                writer.WritePropertyName("data");
                given.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Text(buffer);
    }

    internal static string Error(string? id, JsonObject error)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = Start(buffer, id, ok: false))
        {
            writer.WritePropertyName("error");
            error.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Text(buffer);
    }

    /// <summary>The message with its subscription key's value replaced (an event or a subscribe result).</summary>
    internal static string WithSubscription(JsonElement message, string subscription)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Compact))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in message.EnumerateObject())
            {
                if (property.NameEquals("subscription"))
                {
                    writer.WriteString("subscription", subscription);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Text(buffer);
    }

    /// <summary>
    /// The world_changed connection event (protocol.md, Connection events) for a world the link learned of, from the
    /// mod's event or from a welcome after a reconnect: world as the link has it, else {id} ("" for no world).
    /// </summary>
    internal static string WorldChanged(JsonElement? world, string? id)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "event");
            writer.WriteString("event", "world_changed");
            writer.WritePropertyName("world");
            if (world is { ValueKind: JsonValueKind.Object } given)
            {
                given.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("id", id ?? "");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Text(buffer);
    }

    /// <summary>An object of string keys and values, as a JsonElement to send as a call's params.</summary>
    internal static JsonElement Parameters(string key, string value)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    internal static JsonElement Element(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static string Line(JsonNode message)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Compact))
        {
            message.WriteTo(writer);
        }

        return Text(buffer);
    }

    private static Utf8JsonWriter Start(MemoryStream buffer, string? id, bool ok)
    {
        Utf8JsonWriter writer = new(buffer, Compact);
        writer.WriteStartObject();
        writer.WriteString("type", "reply");
        if (id == null)
        {
            writer.WriteNull("id");
        }
        else
        {
            writer.WriteString("id", id);
        }

        writer.WriteBoolean("ok", ok);
        return writer;
    }

    private static string Text(MemoryStream buffer) =>
        Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
}
