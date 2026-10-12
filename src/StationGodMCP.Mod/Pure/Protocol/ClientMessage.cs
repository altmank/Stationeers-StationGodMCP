#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// One message from a client, parsed on its connection's reader thread: hello, call, cancel, stream_end, cursor_close or
/// bye, or a Refused line the protocol does not allow (what to answer is in it). The keys each type may carry are
/// fixed; an unknown one is a protocol error, so a client cannot believe the server read something it ignored.
/// </summary>
internal abstract class ClientMessage
{
    internal const int MaximumIdLength = 64;
    internal const int MaximumClientNameLength = 64;
    internal const int MinimumResumeTokenLength = 16;
    internal const int MaximumResumeTokenLength = 128;
    internal const int MinimumDeadlineMs = 100;
    internal const int MaximumDeadlineMs = 600000;
    internal const int DefaultDeadlineMs = 30000;

    private static readonly JsonLoadSettings Strict = new JsonLoadSettings
    {
        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
    };

    private ClientMessage()
    {
    }

    /// <summary>The message a line holds, or the refusal it earns.</summary>
    internal static ClientMessage Parse(string line)
    {
        JObject message;
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(line))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double
            };
            JToken token = JToken.ReadFrom(reader, Strict);
            if (reader.Read())
            {
                return Refused.Protocol("The line holds more than one JSON value.", line);
            }

            if (!(token is JObject parsed))
            {
                return Refused.Protocol("A message must be a JSON object.", line);
            }

            message = parsed;
        }
        catch (JsonReaderException exception) when (exception.Message.Contains("already exists"))
        {
            return new Refused("invalid_argument", $"A key is given twice: {exception.Message}", LenientId(line), false, null);
        }
        catch (JsonReaderException exception)
        {
            return Refused.Protocol($"The line is not JSON: {exception.Message}", line);
        }

        string? type = message["type"]?.Type == JTokenType.String ? (string)message["type"]! : null;
        return type switch
        {
            "hello" => Hello.From(message, line),
            "call" => Call.From(message, line),
            "cancel" => Cancel.From(message, line),
            "stream_end" => EndOfStream.From(message, line),
            "cursor_close" => CursorClose.From(message, line),
            "bye" => (ClientMessage?)OnlyKeys(message, line, "type") ?? new Bye(),
            null => Refused.Protocol("A message needs a type.", line),
            _ => Refused.Protocol($"Unknown message type '{type}'.", line)
        };
    }

    /// <summary>A refusal when the message has a key outside the allowed ones; null when it does not.</summary>
    private static Refused? OnlyKeys(JObject message, string line, params string[] allowed)
    {
        foreach (JProperty property in message.Properties())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                return Refused.Protocol($"Unknown key '{property.Name}' in a {message["type"]} message.", line);
            }
        }

        return null;
    }

    private static string? LenientId(string line)
    {
        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            JToken id = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Ignore })["id"]!;
            return id?.Type == JTokenType.String ? (string)id! : null;
        }
        catch (JsonReaderException)
        {
            // Not even leniently JSON: the refusal goes out without an id.
            return null;
        }
    }

    /// <summary>
    /// A stream number from minimum to StreamEnd.MaximumStream: an integer, or a number with no fraction; null for
    /// anything else.
    /// </summary>
    private static long? StreamNumber(JToken? token, long minimum)
    {
        double value;
        switch (token?.Type)
        {
            case JTokenType.Integer when token is JValue { Value: long whole }:
                return whole >= minimum && whole <= StreamEnd.MaximumStream ? whole : null;
            case JTokenType.Float:
                value = (double)token;
                return Math.Floor(value) == value && value >= minimum && value <= StreamEnd.MaximumStream
                    ? (long)value
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Whether text is a resume token: 16 to 128 letters, digits, '-' or '_'.</summary>
    internal static bool IsResumeToken(string? text)
    {
        if (text == null || text.Length < MinimumResumeTokenLength || text.Length > MaximumResumeTokenLength)
        {
            return false;
        }

        foreach (char character in text)
        {
            bool allowed = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsClientName(string name)
    {
        if (name.Length == 0 || name.Length > MaximumClientNameLength)
        {
            return false;
        }

        foreach (char character in name)
        {
            bool allowed = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>hello: the protocols the client speaks, who it is, what it understands, and its resume token if any.</summary>
    internal sealed class Hello : ClientMessage
    {
        private Hello(List<int> protocols, string clientName, string? clientVersion, string? library, List<string> features,
            string? resume)
        {
            Resume = resume;
            Protocols = protocols;
            ClientName = clientName;
            ClientVersion = clientVersion;
            Library = library;
            Features = features;
        }

        internal List<int> Protocols { get; }

        internal string ClientName { get; }

        internal string? ClientVersion { get; }

        internal string? Library { get; }

        internal List<string> Features { get; }

        /// <summary>
        /// The client's resume token (protocol.md, Resuming): 16 to 128 letters, digits, '-' or '_', chosen by the
        /// client; null when not given.
        /// </summary>
        internal string? Resume { get; }

        internal static ClientMessage From(JObject message, string line)
        {
            Refused? unknown = OnlyKeys(message, line, "type", "protocol", "client", "features", "resume");
            if (unknown != null)
            {
                return unknown;
            }

            List<int> protocols = new List<int>();
            if (!(message["protocol"] is JArray listed) || listed.Count == 0)
            {
                return Refused.Protocol("hello needs protocol, an array of the major versions the client speaks.", line);
            }

            foreach (JToken version in listed)
            {
                if (version.Type != JTokenType.Integer)
                {
                    return Refused.Protocol("hello.protocol must hold integers.", line);
                }

                protocols.Add((int)version);
            }

            if (!(message["client"] is JObject client))
            {
                return Refused.Protocol("hello needs client, an object with name and version.", line);
            }

            string? name = client["name"]?.Type == JTokenType.String ? (string)client["name"]! : null;
            if (name == null || !IsClientName(name))
            {
                return Refused.Protocol(
                    "hello.client.name must be 1 to 64 letters, digits, '-', '_' or '.'.", line);
            }

            List<string> features = new List<string>();
            if (message["features"] is JArray wanted)
            {
                foreach (JToken feature in wanted)
                {
                    if (feature.Type == JTokenType.String)
                    {
                        features.Add((string)feature!);
                    }
                }
            }
            else if (message["features"] != null && message["features"]!.Type != JTokenType.Null)
            {
                return Refused.Protocol("hello.features must be an array of strings.", line);
            }

            JToken? resume = message["resume"];
            string? token = resume?.Type == JTokenType.String ? (string)resume! : null;
            if (resume != null && resume.Type != JTokenType.Null && !IsResumeToken(token))
            {
                return Refused.Protocol(
                    "hello.resume must be 16 to 128 letters, digits, '-' or '_', a token the client chose.", line);
            }

            return new Hello(protocols, name, Text(client["version"]), Text(client["library"]), features, token);
        }

        private static string? Text(JToken? token) => token?.Type == JTokenType.String ? (string)token! : null;
    }

    /// <summary>call: one method call, by id, with its params, shape, deadline and stream.</summary>
    internal sealed class Call : ClientMessage
    {
        private Call(string id, string method, JObject? parameters, JToken? shape, int deadlineMs, long stream)
        {
            Id = id;
            Method = method;
            Params = parameters;
            Shape = shape;
            DeadlineMs = deadlineMs;
            Stream = stream;
        }

        internal string Id { get; }

        internal string Method { get; }

        /// <summary>Null when the call gave none (read as {}).</summary>
        internal JObject? Params { get; }

        /// <summary>The shape as given, read by the shaping rules of the connection's protocol; null when absent.</summary>
        internal JToken? Shape { get; }

        internal int DeadlineMs { get; }

        /// <summary>The stream it belongs to; 0, the connection's default stream, when absent.</summary>
        internal long Stream { get; }

        internal static ClientMessage From(JObject message, string line)
        {
            Refused? unknown = OnlyKeys(message, line, "type", "id", "method", "params", "shape", "deadline_ms", "stream");
            if (unknown != null)
            {
                return unknown;
            }

            string? id = message["id"]?.Type == JTokenType.String ? (string)message["id"]! : null;
            if (id == null || id.Length == 0 || id.Length > MaximumIdLength)
            {
                return Refused.Protocol("A call needs id, a string of 1 to 64 characters.", line);
            }

            string? method = message["method"]?.Type == JTokenType.String ? (string)message["method"]! : null;
            if (method == null)
            {
                return new Refused("method_not_found", "A call needs method, the name of a method.", id, false, null);
            }

            JToken? given = message["params"];
            JObject? parameters = given as JObject;
            if (given != null && given.Type != JTokenType.Null && parameters == null)
            {
                return new Refused("invalid_argument", "params must be an object.", id, false, null);
            }

            JToken? deadline = message["deadline_ms"];
            int deadlineMs = DefaultDeadlineMs;
            if (deadline != null && deadline.Type != JTokenType.Null)
            {
                int? read = Shaping.ShapeRequest.WholeNumber(deadline, MinimumDeadlineMs, MaximumDeadlineMs);
                if (read == null)
                {
                    return new Refused("invalid_argument",
                        $"deadline_ms must be an integer from {MinimumDeadlineMs} to {MaximumDeadlineMs}.", id, false, null);
                }

                deadlineMs = read.Value;
            }

            JToken? givenStream = message["stream"];
            long stream = 0;
            if (givenStream != null && givenStream.Type != JTokenType.Null)
            {
                long? read = StreamNumber(givenStream, 0);
                if (read == null)
                {
                    return new Refused("invalid_argument",
                        $"stream must be an integer from 0 to {StreamEnd.MaximumStream}.", id, false, null);
                }

                stream = read.Value;
            }

            JToken? shape = message["shape"];
            return new Call(id, method, parameters, shape?.Type == JTokenType.Null ? null : shape, deadlineMs, stream);
        }
    }

    /// <summary>cancel: drop the call of that id if it has not started.</summary>
    internal sealed class Cancel : ClientMessage
    {
        private Cancel(string id) => Id = id;

        internal string Id { get; }

        internal static ClientMessage From(JObject message, string line)
        {
            Refused? unknown = OnlyKeys(message, line, "type", "id");
            if (unknown != null)
            {
                return unknown;
            }

            return message["id"]?.Type == JTokenType.String
                ? new Cancel((string)message["id"]!)
                : Refused.Protocol("cancel needs id, a string.", line);
        }
    }

    /// <summary>
    /// stream_end: the client is done with a stream (a script run ended); its unstarted calls are cancelled and it is
    /// never used again on this connection. Stream 0, the default stream, never ends: ending it, or giving no stream
    /// from 1 to StreamEnd.MaximumStream, is invalid_argument with a null id (the message has none), and the connection
    /// stays open.
    /// </summary>
    internal sealed class EndOfStream : ClientMessage
    {
        private EndOfStream(long stream) => Stream = stream;

        internal long Stream { get; }

        internal static ClientMessage From(JObject message, string line)
        {
            Refused? unknown = OnlyKeys(message, line, "type", "stream");
            if (unknown != null)
            {
                return unknown;
            }

            long? stream = StreamNumber(message["stream"], 1);
            return stream != null
                ? new EndOfStream(stream.Value)
                : new Refused("invalid_argument",
                    $"stream_end needs stream, an integer from 1 to {StreamEnd.MaximumStream}; stream 0 never ends.",
                    null, false, null);
        }
    }

    /// <summary>
    /// cursor_close: the client is done with a walk before its end (protocol.md, Walks); the cursor is freed at the next
    /// frame if this connection owns it. No reply, like cancel: an unknown or already freed id does nothing.
    /// </summary>
    internal sealed class CursorClose : ClientMessage
    {
        private CursorClose(string cursorId) => CursorId = cursorId;

        internal string CursorId { get; }

        internal static ClientMessage From(JObject message, string line)
        {
            Refused? unknown = OnlyKeys(message, line, "type", "cursor_id");
            if (unknown != null)
            {
                return unknown;
            }

            return message["cursor_id"]?.Type == JTokenType.String
                ? new CursorClose((string)message["cursor_id"]!)
                : Refused.Protocol("cursor_close needs cursor_id, a string.", line);
        }
    }

    /// <summary>bye: the client is closing.</summary>
    internal sealed class Bye : ClientMessage
    {
    }

    /// <summary>
    /// A line answered with an error instead of being acted on: its code, message, the call id to answer (null when
    /// none could be read), whether the connection then closes, and the error's data.
    /// </summary>
    internal sealed class Refused : ClientMessage
    {
        internal const int LineStartLength = 80;

        internal Refused(string code, string message, string? id, bool closes, object? data)
        {
            Code = code;
            Message = message;
            Id = id;
            Closes = closes;
            Data = data;
        }

        internal string Code { get; }

        internal string Message { get; }

        internal string? Id { get; }

        internal bool Closes { get; }

        internal object? Data { get; }

        /// <summary>protocol_error: answered with the line's start, then the connection closes.</summary>
        internal static Refused Protocol(string message, string line) =>
            new Refused("protocol_error", message, null, true,
                new Dictionary<string, string> { ["line_start"] = line.Length <= LineStartLength ? line : line.Substring(0, LineStartLength) });
    }
}
