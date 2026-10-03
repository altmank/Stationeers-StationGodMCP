using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// The method catalogue as the client needs it, and nothing more. The mod owns every rule about arguments, shaping and
/// permissions; the client reads the catalogue only to know whether a call that may have reached the game is safe to
/// send again (its effective class is read and it has no x-effects) and how much longer than its deadline a call may
/// take (x-duration). The whole document is kept for callers that build on it (the sidecar's tool list).
/// </summary>
public sealed class GameCatalogue
{
    private const string Resource = "StationGodMCP.catalogue.json";

    // Methods of the protocol itself, read class, for a catalogue that does not list them yet.
    private static readonly string[] ProtocolMethods = ["catalogue", "subscribe", "unsubscribe"];

    // Subscriptions are kept by the client's own bookkeeping, which subscribes again only into the same world.
    private static readonly HashSet<string> NeverResent = new(StringComparer.Ordinal) { "subscribe", "unsubscribe" };

    private static readonly Lazy<GameCatalogue> Embedded = new(() => FromBytes(ReadResource()));

    private readonly Dictionary<string, MethodRules> _methods;

    private GameCatalogue(JsonElement document, string hash)
    {
        Document = document;
        Hash = hash;
        _methods = Rules(document);
    }

    /// <summary>The catalogue this library was built with.</summary>
    public static GameCatalogue BuiltIn => Embedded.Value;

    /// <summary>The catalogue's identity: "sha256:" and the lowercase hex SHA-256 of the exact bytes of catalogue.json.</summary>
    public string Hash { get; }

    /// <summary>The whole catalogue.</summary>
    public JsonElement Document { get; }

    /// <summary>A catalogue from the exact bytes of a catalogue.json; the hash is taken over those bytes.</summary>
    public static GameCatalogue FromBytes(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        return new GameCatalogue(document.RootElement.Clone(), "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>The catalogue the mod returned for its `catalogue` method, under the hash its welcome announced.</summary>
    public static GameCatalogue FromServer(JsonElement document, string hash) => new(document.Clone(), hash);

    /// <summary>The method's catalogue entry, or null when the catalogue has no such method.</summary>
    public JsonElement? Method(string name)
    {
        foreach (string section in (string[])["methods", "protocol_methods"])
        {
            if (Document.TryGetProperty(section, out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty("name", out JsonElement method) && method.ValueEquals(name))
                    {
                        return entry;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when a call that may have reached the game can be sent again: read class at these arguments and no
    /// x-effects. False for a method the catalogue does not have.
    /// </summary>
    public bool ResendSafe(string method, JsonElement parameters) =>
        !NeverResent.Contains(method) && _methods.TryGetValue(method, out MethodRules? rules) && !rules.HasEffects &&
        rules.EffectiveClass(parameters) == "read";

    /// <summary>The read, write or cheat class of a call at these arguments; null for an unknown method.</summary>
    public string? EffectiveClass(string method, JsonElement parameters) =>
        _methods.TryGetValue(method, out MethodRules? rules) ? rules.EffectiveClass(parameters) : null;

    /// <summary>The time x-duration adds to the call's deadline: the argument given, else the method's maximum.</summary>
    public TimeSpan Duration(string method, JsonElement parameters) =>
        _methods.TryGetValue(method, out MethodRules? rules) && rules.Duration is { } duration
            ? duration.At(parameters)
            : TimeSpan.Zero;

    private static byte[] ReadResource()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource) ??
                              throw new InvalidOperationException($"The client was built without {Resource}.");
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static Dictionary<string, MethodRules> Rules(JsonElement document)
    {
        Dictionary<string, MethodRules> rules = new(StringComparer.Ordinal);
        foreach (string section in (string[])["protocol_methods", "methods"])
        {
            if (!document.TryGetProperty(section, out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                {
                    rules[name.GetString()!] = MethodRules.Of(entry);
                }
            }
        }

        foreach (string method in ProtocolMethods)
        {
            rules.TryAdd(method, MethodRules.ProtocolMethod);
        }

        return rules;
    }
}

/// <summary>One method's class, its class rules (x-class-when), whether it has x-effects, and its x-duration.</summary>
internal sealed record MethodRules(string Class, IReadOnlyList<ClassRule> ClassWhen, bool HasEffects, DurationRule? Duration)
{
    internal static readonly MethodRules ProtocolMethod = new("read", [], false, null);

    internal static MethodRules Of(JsonElement entry)
    {
        string type = entry.TryGetProperty("class", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "write";
        List<ClassRule> rules = [];
        if (entry.TryGetProperty("x-class-when", out JsonElement when) && when.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement rule in when.EnumerateArray())
            {
                if (ClassRule.Of(rule) is { } parsed)
                {
                    rules.Add(parsed);
                }
            }
        }

        bool effects = entry.TryGetProperty("x-effects", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array &&
                       listed.GetArrayLength() > 0;
        return new MethodRules(type, rules, effects, DurationRule.Of(entry));
    }

    /// <summary>The class of the first rule that matches the arguments, else the method's own.</summary>
    internal string EffectiveClass(JsonElement parameters)
    {
        foreach (ClassRule rule in ClassWhen)
        {
            if (rule.Matches(parameters))
            {
                return rule.Class;
            }
        }

        return Class;
    }
}

/// <summary>
/// One x-class-when rule: every condition in when holds (and one of any_of, when given). A condition is present, absent,
/// equals or in; JSON null is an omitted argument; words compare trimmed and ignoring case, numbers by value.
/// </summary>
internal sealed record ClassRule(string Class, JsonElement When, JsonElement? AnyOf)
{
    internal static ClassRule? Of(JsonElement rule) =>
        rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("class", out JsonElement type) &&
        type.ValueKind == JsonValueKind.String && rule.TryGetProperty("when", out JsonElement when) &&
        when.ValueKind == JsonValueKind.Object
            ? new ClassRule(type.GetString()!, when.Clone(),
                rule.TryGetProperty("any_of", out JsonElement anyOf) && anyOf.ValueKind == JsonValueKind.Array ? anyOf.Clone() : null)
            : null;

    internal bool Matches(JsonElement parameters) =>
        Holds(When, parameters) && (AnyOf is not { } alternatives || alternatives.EnumerateArray().Any(when => Holds(when, parameters)));

    private static bool Holds(JsonElement when, JsonElement parameters) =>
        when.ValueKind == JsonValueKind.Object &&
        when.EnumerateObject().All(condition => Satisfies(condition.Value, Argument(parameters, condition.Name)));

    private static JsonElement? Argument(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    private static bool Satisfies(JsonElement matcher, JsonElement? value)
    {
        if (matcher.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (matcher.TryGetProperty("present", out _))
        {
            return value != null;
        }

        if (matcher.TryGetProperty("absent", out _))
        {
            return value == null;
        }

        if (matcher.TryGetProperty("equals", out JsonElement expected))
        {
            return value is { } given && Same(expected, given);
        }

        return matcher.TryGetProperty("in", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array &&
               value is { } candidate && listed.EnumerateArray().Any(allowed => Same(allowed, candidate));
    }

    private static bool Same(JsonElement expected, JsonElement value) => (expected.ValueKind, value.ValueKind) switch
    {
        (JsonValueKind.String, JsonValueKind.String) =>
            string.Equals(expected.GetString()!.Trim(), value.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase),
        (JsonValueKind.Number, JsonValueKind.Number) =>
            expected.TryGetDouble(out double first) && value.TryGetDouble(out double second) && first == second,
        _ => expected.ValueKind == value.ValueKind && expected.GetRawText() == value.GetRawText()
    };
}

/// <summary>x-duration: the argument naming a call's own duration in seconds, and its maximum.</summary>
internal sealed record DurationRule(string Parameter, double MaximumSeconds)
{
    internal static DurationRule? Of(JsonElement entry) =>
        entry.TryGetProperty("x-duration", out JsonElement duration) && duration.ValueKind == JsonValueKind.Object &&
        duration.TryGetProperty("param", out JsonElement parameter) && parameter.ValueKind == JsonValueKind.String &&
        duration.TryGetProperty("max_s", out JsonElement maximum) && maximum.TryGetDouble(out double seconds)
            ? new DurationRule(parameter.GetString()!, seconds)
            : null;

    internal TimeSpan At(JsonElement parameters) =>
        TimeSpan.FromSeconds(
            parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(Parameter, out JsonElement given) &&
            given.ValueKind == JsonValueKind.Number && given.TryGetDouble(out double seconds) && seconds > 0
                ? Math.Min(seconds, MaximumSeconds)
                : MaximumSeconds);
}
