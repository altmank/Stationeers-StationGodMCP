#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// One version-2 message from a client, parsed on its connection's reader thread: hello, call, cancel or bye, or
/// a Refused line the protocol does not allow (what to answer is in it). The keys each type may carry are fixed; an
/// unknown one is a protocol error, so a client cannot believe the server read something it ignored.
/// </summary>
internal abstract class ClientMessage
{
    internal const int MaximumIdLength = 64;
    internal const int MaximumClientNameLength = 64;
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

    /// <summary>hello: the protocols the client speaks, who it is, and what it understands.</summary>
    internal sealed class Hello : ClientMessage
    {
        private Hello(List<int> protocols, string clientName, string? clientVersion, string? library, List<string> features)
        {
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

        internal static ClientMessage From(JObject message, string line)
        {
            Refused? unknown = OnlyKeys(message, line, "type", "protocol", "client", "features");
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

            return new Hello(protocols, name, Text(client["version"]), Text(client["library"]), features);
        }

        private static string? Text(JToken? token) => token?.Type == JTokenType.String ? (string)token! : null;
    }

    /// <summary>call: one method call, by id, with its params, shape and deadline.</summary>
    internal sealed class Call : ClientMessage
    {
        private Call(string id, string method, JObject? parameters, JToken? shape, int deadlineMs)
        {
            Id = id;
            Method = method;
            Params = parameters;
            Shape = shape;
            DeadlineMs = deadlineMs;
        }

        internal string Id { get; }

        internal string Method { get; }

        /// <summary>Null when the call gave none (read as {}).</summary>
        internal JObject? Params { get; }

        /// <summary>The shape as given, read by the shaping rules of the connection's protocol; null when absent.</summary>
        internal JToken? Shape { get; }

        internal int DeadlineMs { get; }

        internal static ClientMessage From(JObject message, string line)
        {
            Refused? unknown = OnlyKeys(message, line, "type", "id", "method", "params", "shape", "deadline_ms");
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

            JToken? shape = message["shape"];
            return new Call(id, method, parameters, shape?.Type == JTokenType.Null ? null : shape, deadlineMs);
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

/// <summary>
/// Which protocol a connection speaks, from its first line: an object whose type is "hello" starts version 2; anything
/// else (a request object, an empty object, a line that is not JSON) is a version-1 request and is answered as today.
/// </summary>
internal static class FirstLine
{
    internal static bool StartsVersion2(string line)
    {
        if (line.IndexOf("hello", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        try
        {
            using JsonTextReader reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            JToken token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Ignore });
            return token is JObject message && message["type"]?.Type == JTokenType.String && (string)message["type"]! == "hello";
        }
        catch (JsonReaderException)
        {
            // Not JSON: a version-1 line, which version 1 answers as it always has.
            return false;
        }
    }
}
