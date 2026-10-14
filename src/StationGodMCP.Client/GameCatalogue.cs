using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// The verb model as the client needs it, and nothing more. The mod owns every rule about options, shaping and
/// permissions; the client reads the model only to know whether a call that may have reached the game is safe to
/// send again (it only reads, or it reports and changes nothing) and how much longer than its deadline a call may
/// take (x-cost's duration). The whole document is kept for callers that build on it.
/// </summary>
public sealed class GameCatalogue
{
    private const string ResourcePrefix = "catalogue/verbs/";
    private const string SharedFile = "_shared.json";
    private const string ErrorsFile = "_errors.json";

    /// <summary>The catalogues of the protocol's own methods, which the verb model does not describe: their class is
    /// fixed here. drop_job and hold_restore write, so a call of theirs is never sent again after a broken link.</summary>
    private static readonly Dictionary<string, string> ProtocolMethods = new(StringComparer.Ordinal)
    {
        ["catalogue"] = "read",
        ["subscribe"] = "read",
        ["unsubscribe"] = "read",
        ["stream_costs"] = "read",
        ["drop_job"] = "write",
        ["hold_restore"] = "write"
    };

    // Subscriptions are kept by the client's own bookkeeping, which subscribes again only into the same world.
    private static readonly HashSet<string> NeverResent = new(StringComparer.Ordinal) { "subscribe", "unsubscribe" };

    private static readonly Lazy<GameCatalogue> Embedded = new(() => FromResources());

    private readonly Dictionary<string, VerbRules> _verbs;

    private GameCatalogue(JsonElement document, string hash, Dictionary<string, VerbRules> verbs)
    {
        Document = document;
        Hash = hash;
        _verbs = verbs;
    }

    /// <summary>The verb model this library was built with.</summary>
    public static GameCatalogue BuiltIn => Embedded.Value;

    /// <summary>
    /// The catalogue's identity: "sha256:" and the lowercase hex of SHA-256 over the verb files' raw bytes with every
    /// line ending a bare LF, concatenated whole in ordinal resource-name order (catalogue/verbs/_errors.json,
    /// _shared.json, build.json, ... write.json), with nothing between them - the hash the mod announces in
    /// welcome.catalogue.hash.
    /// </summary>
    public string Hash { get; }

    /// <summary>The whole verb model: {shared, errors, verbs}, as the mod's `catalogue` method answers it.</summary>
    public JsonElement Document { get; }

    /// <summary>The verb model the mod returned for its `catalogue` method, under the hash its welcome announced.</summary>
    public static GameCatalogue FromServer(JsonElement document, string hash) =>
        new(document.Clone(), hash, Rules(document));

    /// <summary>True when the model has the verb, or the protocol has the method (a handler or a protocol method).</summary>
    public bool Has(string method) => _verbs.ContainsKey(method) || ProtocolMethods.ContainsKey(method);

    /// <summary>True when the call only reads: a read-only verb, a report call, or a protocol method that reads.</summary>
    public bool ReadsOnly(string method, JsonElement parameters) => EffectiveClass(method, parameters) == "read";

    /// <summary>
    /// True when a call that may have reached the game can be sent again: it only reads, and it is no method whose
    /// own bookkeeping resends it. False for a method the model does not have.
    /// </summary>
    public bool ResendSafe(string method, JsonElement parameters) =>
        !NeverResent.Contains(method) && ReadsOnly(method, parameters);

    /// <summary>
    /// The read, write or cheat class of a call; null for a method the model does not have. A read-only verb reads; a
    /// cheat what is a cheat when it says "always" or when its option is set; a confirm what without confirm: true
    /// reports and changes nothing, so it reads; anything else writes.
    /// </summary>
    public string? EffectiveClass(string method, JsonElement parameters)
    {
        if (ProtocolMethods.TryGetValue(method, out string? protocol))
        {
            return protocol;
        }

        if (!_verbs.TryGetValue(method, out VerbRules? verb))
        {
            return null;
        }

        if (verb.ReadOnly)
        {
            return "read";
        }

        if (verb.What(parameters) is not WhatRules what)
        {
            return "write";
        }

        if (what.Cheat == "always" || (what.CheatOption is { } option && Flag(parameters, option)))
        {
            return "cheat";
        }

        return what.Route == "confirm" && !Flag(parameters, "confirm") ? "read" : "write";
    }

    /// <summary>The time x-cost adds to the call's deadline: the seconds the option gives, else the what's maximum.</summary>
    public TimeSpan Duration(string method, JsonElement parameters) =>
        _verbs.TryGetValue(method, out VerbRules? verb) && verb.What(parameters)?.Duration is { } duration
            ? duration.At(parameters)
            : TimeSpan.Zero;

    private static bool Flag(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;

    // The built-in model: the files this assembly embeds, hashed and assembled as the mod does.
    private static GameCatalogue FromResources()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal);
        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            string name = resource.Replace('\\', '/');
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal))
            {
                files.Add(name.Substring(ResourcePrefix.Length), Bytes(assembly, resource));
            }
        }

        if (!files.ContainsKey(SharedFile) || !files.ContainsKey(ErrorsFile))
        {
            throw new InvalidOperationException("The client was built without the verb catalogue.");
        }

        StringBuilder document = new();
        document.Append("{\"shared\":").Append(OneLine(Text(files[SharedFile])))
            .Append(",\"errors\":").Append(OneLine(Text(files[ErrorsFile]))).Append(",\"verbs\":{");
        bool first = true;
        foreach ((string file, byte[] raw) in files)
        {
            if (file == SharedFile || file == ErrorsFile)
            {
                continue;
            }

            document.Append(first ? string.Empty : ",").Append(JsonSerializer.Serialize(Verb(file))).Append(':')
                .Append(OneLine(Text(raw)));
            first = false;
        }

        document.Append("}}");
        JsonElement model = JsonDocument.Parse(document.ToString()).RootElement.Clone();
        return new GameCatalogue(model, HashOf(files), Rules(model));
    }

    /// <summary>
    /// The identity of a set of verb files: "sha256:" and the lowercase hex of SHA-256 over their raw bytes with every
    /// line ending a bare LF, concatenated whole in file-name order - the hash the mod announces in
    /// welcome.catalogue.hash for those same committed bytes, whatever line endings a checkout gave its copy of them.
    /// </summary>
    internal static string HashOf(SortedDictionary<string, byte[]> files)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (KeyValuePair<string, byte[]> entry in files)
        {
            byte[] file = Lf(entry.Value);
            sha.AppendData(file);
        }

        return "sha256:" + Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    // The file's bytes with every line ending a bare LF: a CRLF becomes one LF and a lone CR becomes one LF, so
    // nothing else about the bytes changes. Normalising never lengthens the file, so one buffer of its own length
    // holds the result.
    private static byte[] Lf(byte[] raw)
    {
        byte[] lf = new byte[raw.Length];
        int at = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\r')
            {
                lf[at++] = raw[i];
                continue;
            }

            lf[at++] = (byte)'\n';
            if (i + 1 < raw.Length && raw[i + 1] == (byte)'\n')
            {
                i++;
            }
        }

        Array.Resize(ref lf, at);
        return lf;
    }

    private static byte[] Bytes(Assembly assembly, string resource)
    {
        using Stream stream = assembly.GetManifestResourceStream(resource) ??
                              throw new InvalidOperationException($"The client was built without {resource}.");
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string Text(byte[] raw) => new UTF8Encoding(false).GetString(raw);

    private static string Verb(string file) => file.Substring(0, file.Length - ".json".Length);

    /// <summary>
    /// The file's JSON on one line, whitespace outside strings dropped: the document the mod answers is one wire line,
    /// and a file's own text is what it carries - nothing is re-serialised, so no number or word changes shape.
    /// </summary>
    private static string OneLine(string json)
    {
        StringBuilder text = new(json.Length);
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
            else if (letter is not (' ' or '\t' or '\n' or '\r'))
            {
                text.Append(letter);
            }
        }

        return text.ToString();
    }

    private static Dictionary<string, VerbRules> Rules(JsonElement document)
    {
        Dictionary<string, VerbRules> verbs = new(StringComparer.Ordinal);
        if (document.ValueKind == JsonValueKind.Object && document.TryGetProperty("verbs", out JsonElement listed) &&
            listed.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty verb in listed.EnumerateObject())
            {
                verbs[verb.Name] = VerbRules.Of(verb.Value);
            }
        }

        return verbs;
    }
}

/// <summary>One verb's rules: whether it only reads, and its whats by name ("-" for a whatless verb).</summary>
internal sealed record VerbRules(bool ReadOnly, bool HasWhats, IReadOnlyDictionary<string, WhatRules> Whats)
{
    internal static VerbRules Of(JsonElement verb)
    {
        Dictionary<string, WhatRules> whats = new(StringComparer.Ordinal);
        if (verb.ValueKind == JsonValueKind.Object && verb.TryGetProperty("whats", out JsonElement listed) &&
            listed.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty what in listed.EnumerateObject())
            {
                whats[what.Name] = WhatRules.Of(what.Value);
            }
        }

        bool hasWhats = !(whats.Count == 1 && whats.ContainsKey("-"));
        return new VerbRules(verb.ValueKind == JsonValueKind.Object && verb.TryGetProperty("read_only", out JsonElement readOnly) &&
                             readOnly.ValueKind == JsonValueKind.True, hasWhats, whats);
    }

    /// <summary>The what the call names: by name, or the one what of a whatless verb; null when there is none.</summary>
    internal WhatRules? What(JsonElement parameters)
    {
        string? name = HasWhats ? WhatName(parameters) : "-";
        return name != null && Whats.TryGetValue(name, out WhatRules? what) ? what : null;
    }

    private static string? WhatName(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("what", out JsonElement what) &&
        what.ValueKind == JsonValueKind.String
            ? what.GetString()
            : null;
}

/// <summary>
/// One what's write rules and its duration: Writes is false for a read what; Cheat is "always", or the option that
/// makes the write a cheat; Route is "acts" or "confirm"; Duration is x-cost's duration, when the what has one.
/// </summary>
internal sealed record WhatRules(bool Writes, string? Route, string? Cheat, string? CheatOption, DurationRule? Duration)
{
    internal static WhatRules Of(JsonElement what)
    {
        if (what.ValueKind != JsonValueKind.Object || what.TryGetProperty("write", out JsonElement write) is false ||
            write.ValueKind != JsonValueKind.Object)
        {
            return new WhatRules(false, null, null, null, DurationOf(what));
        }

        string? cheat = write.TryGetProperty("cheat", out JsonElement mark) && mark.ValueKind == JsonValueKind.String
            ? mark.GetString()
            : null;
        bool always = cheat == "always";
        return new WhatRules(true, write.TryGetProperty("route", out JsonElement route) ? route.GetString() : null,
            always ? "always" : null, always || cheat == null ? null : cheat.Split(':')[0].Trim(), DurationOf(what));
    }

    private static DurationRule? DurationOf(JsonElement what) =>
        what.TryGetProperty("x-cost", out JsonElement cost) && cost.TryGetProperty("duration", out JsonElement duration)
            ? DurationRule.Of(duration)
            : null;
}

/// <summary>x-cost's duration: the option naming a call's own duration in seconds, and its maximum.</summary>
internal sealed record DurationRule(string Option, double MaximumSeconds)
{
    internal static DurationRule? Of(JsonElement duration) =>
        duration.ValueKind == JsonValueKind.Object &&
        duration.TryGetProperty("option", out JsonElement option) && option.ValueKind == JsonValueKind.String &&
        duration.TryGetProperty("max_s", out JsonElement maximum) && maximum.TryGetDouble(out double seconds)
            ? new DurationRule(option.GetString()!, seconds)
            : null;

    internal TimeSpan At(JsonElement parameters) =>
        TimeSpan.FromSeconds(
            parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(Option, out JsonElement given) &&
            given.ValueKind == JsonValueKind.Number && given.TryGetDouble(out double seconds) && seconds > 0
                ? Math.Min(seconds, MaximumSeconds)
                : MaximumSeconds);
}
