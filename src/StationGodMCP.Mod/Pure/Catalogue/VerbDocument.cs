#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>
/// The verb model document (catalogue/verbs): the files an assembly embeds, assembled once and identified by one
/// hash. The hash is "sha256:" and the lowercase hex of SHA-256 over the files' raw bytes, concatenated whole in
/// ordinal resource-name order (catalogue/verbs/_errors.json, _shared.json, build.json, ... write.json), with nothing
/// between them; every assembly that embeds catalogue/verbs/*.json computes the same hash from its own copy, and only
/// raw bytes are hashed, never re-serialised JSON. welcome.catalogue.hash is that hash.
/// <para>
/// The assembled document - {"shared": &lt;_shared.json&gt;, "errors": &lt;_errors.json&gt;,
/// "verbs": {"&lt;verb&gt;": &lt;file&gt;}} - carries the files' own text, so what the `catalogue` method answers is
/// exactly what the mod carries, and a client parsing it sees the same model the mod runs. Loading checks the files:
/// a missing _shared.json or _errors.json, one that is not JSON, or a see that names neither a verb, a what, a topic
/// nor a subtopic is a CatalogueException naming the problem.
/// </para>
/// </summary>
internal sealed class VerbDocument
{
    internal const string ResourcePrefix = "catalogue/verbs/";
    internal const string SharedFile = "_shared.json";
    internal const string ErrorsFile = "_errors.json";

    private static readonly Lazy<VerbDocument> Loaded = new Lazy<VerbDocument>(Load);

    private VerbDocument(string hash, string text, Dictionary<string, ErrorSee> errors, int verbs, int methods)
    {
        Hash = hash;
        Text = text;
        Errors = errors;
        VerbCount = verbs;
        MethodCount = methods;
    }

    /// <summary>"sha256:" and the lowercase hex of the verb files' SHA-256.</summary>
    internal string Hash { get; }

    /// <summary>The assembled document, as the `catalogue` method answers it.</summary>
    internal string Text { get; }

    /// <summary>Each error code's help pointer (its see), by code.</summary>
    internal Dictionary<string, ErrorSee> Errors { get; }

    /// <summary>The verb files the document holds.</summary>
    internal int VerbCount { get; }

    /// <summary>The whats over every verb: the calls the document describes.</summary>
    internal int MethodCount { get; }

    /// <summary>The document of the assembly this code runs in: the mod's DLL, or the tests' assembly in a test build.</summary>
    internal static VerbDocument Embedded => Loaded.Value;

    private static VerbDocument Load() => Of(Assembly.GetExecutingAssembly());

    /// <summary>The document an assembly carries in its catalogue/verbs resources.</summary>
    internal static VerbDocument Of(Assembly assembly)
    {
        List<string> names = new List<string>();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            string name = resource.Replace('\\', '/');
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            throw new CatalogueException($"The assembly carries no {ResourcePrefix}*.json.");
        }

        names.Sort(StringComparer.Ordinal);
        Dictionary<string, string> texts = new Dictionary<string, string>(names.Count, StringComparer.Ordinal);
        Dictionary<string, byte[]> bytes = new Dictionary<string, byte[]>(names.Count, StringComparer.Ordinal);
        foreach (string name in names)
        {
            byte[] raw = Raw(assembly, name);
            texts.Add(FileName(name), new UTF8Encoding(false).GetString(raw));
            bytes.Add(FileName(name), raw);
        }

        if (!texts.TryGetValue(SharedFile, out string? shared) || shared == null)
        {
            throw new CatalogueException($"The verb catalogue has no {ResourcePrefix}{SharedFile}.");
        }

        if (!texts.TryGetValue(ErrorsFile, out string? errors) || errors == null)
        {
            throw new CatalogueException($"The verb catalogue has no {ResourcePrefix}{ErrorsFile}.");
        }

        int verbs = 0;
        int methods = 0;
        StringBuilder document = new StringBuilder(shared.Length + errors.Length + 64 * 1024);
        document.Append("{\"shared\":").Append(OneLine(shared)).Append(",\"errors\":").Append(OneLine(errors))
            .Append(",\"verbs\":{");
        bool first = true;
        foreach (string name in names)
        {
            string file = FileName(name);
            if (file == SharedFile || file == ErrorsFile)
            {
                continue;
            }

            string verb = file.Substring(0, file.Length - ".json".Length);
            document.Append(first ? string.Empty : ",").Append(JsonQuote(verb)).Append(':').Append(OneLine(texts[file]));
            first = false;
            verbs++;
            methods += WhatsIn(texts[file], file);
        }

        document.Append("}}");
        return new VerbDocument(HashOf(names, bytes), document.ToString(), PointersOf(errors), verbs, methods);
    }

    private static byte[] Raw(Assembly assembly, string name)
    {
        using Stream? stream = assembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            throw new CatalogueException($"The assembly carries no {name}.");
        }

        using MemoryStream bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string FileName(string resource) => resource.Substring(ResourcePrefix.Length);

    // The hash of every file's raw bytes, concatenated whole in the order names gives them.
    private static string HashOf(List<string> names, Dictionary<string, byte[]> bytes)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in names)
        {
            byte[] file = bytes[FileName(name)];
            sha.AppendData(file, 0, file.Length);
        }

        byte[] digest = sha.GetHashAndReset();
        StringBuilder hex = new StringBuilder("sha256:", 7 + digest.Length * 2);
        foreach (byte value in digest)
        {
            hex.Append(value.ToString("x2"));
        }

        return hex.ToString();
    }

    private static string JsonQuote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// The file's JSON on one line, whitespace outside strings dropped: the reply is one wire line, and a file's own
    /// text is what the document carries - nothing is re-serialised, so no number or word changes shape.
    /// </summary>
    private static string OneLine(string json)
    {
        StringBuilder text = new StringBuilder(json.Length);
        bool quoted = false;
        bool escaped = false;
        foreach (char letter in json)
        {
            if (quoted)
            {
                text.Append(letter);
                if (escaped)
                {
                    escaped = false;
                }
                else if (letter == '\\')
                {
                    escaped = true;
                }
                else if (letter == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (letter == '"')
            {
                quoted = true;
                text.Append(letter);
            }
            else if (letter != ' ' && letter != '\t' && letter != '\n' && letter != '\r')
            {
                text.Append(letter);
            }
        }

        return text.ToString();
    }

    private static int WhatsIn(string json, string file)
    {
        JObject verb = Parse(json, file);
        return verb["whats"] is JObject whats && verb["verb"] is JToken name && name.Type == JTokenType.String
            ? whats.Count
            : throw new CatalogueException($"{ResourcePrefix}{file} is not a verb: it needs a verb name and its whats.");
    }

    private static Dictionary<string, ErrorSee> PointersOf(string json)
    {
        JObject errors = Parse(json, ErrorsFile);
        Dictionary<string, ErrorSee> pointers = new Dictionary<string, ErrorSee>(errors.Count, StringComparer.Ordinal);
        foreach (JProperty error in errors.Properties())
        {
            if (error.Value is not JObject entry || entry["see"] is not JObject see)
            {
                continue;
            }

            pointers.Add(error.Name, SeeOf(see, $"{ErrorsFile}: {error.Name}.see"));
        }

        return pointers;
    }

    // A see names a verb (with a what, or alone), a topic (with a subtopic, or alone); it names at least one of them.
    private static ErrorSee SeeOf(JObject see, string at)
    {
        string? Text(string key)
        {
            JToken? given = see[key];
            if (given == null)
            {
                return null;
            }

            return given.Type == JTokenType.String
                ? (string)given!
                : throw new CatalogueException($"{at}.{key} must be a string.");
        }

        ErrorSee pointer = new ErrorSee(Text("verb"), Text("what"), Text("topic"), Text("subtopic"));
        if (pointer.Verb == null && pointer.What == null && pointer.Topic == null && pointer.Subtopic == null)
        {
            throw new CatalogueException($"{at} names no verb, what, topic or subtopic.");
        }

        foreach (JProperty key in see.Properties())
        {
            if (key.Name != "verb" && key.Name != "what" && key.Name != "topic" && key.Name != "subtopic")
            {
                throw new CatalogueException($"{at} has no place '{key.Name}'; a see is verb, what, topic or subtopic.");
            }
        }

        return pointer;
    }

    private static JObject Parse(string json, string file)
    {
        try
        {
            return JObject.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
        }
        catch (Exception exception)
        {
            throw new CatalogueException($"{ResourcePrefix}{file} is not JSON the mod reads: {exception.Message}");
        }
    }
}

/// <summary>The verb catalogue did not load; the message names the problem.</summary>
internal sealed class CatalogueException : Exception
{
    internal CatalogueException(string message)
        : base(message)
    {
    }
}
