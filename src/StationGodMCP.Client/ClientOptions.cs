namespace StationGodMCP.Client;

/// <summary>Which protocol to speak: version 2 with the fall back to version 1, or version 1 from the start.</summary>
public enum ProtocolChoice
{
    Auto,
    Version1
}

/// <summary>
/// How a client connects. A key is read from an environment variable, never given as a value, so it does not show in
/// process lists; it is offered only when a client name is given and the variable is set (clients.md, As built in
/// stage 7). The legacy shared secret is for a version-1 server over TCP.
/// </summary>
public sealed record ClientOptions(GameTarget Target)
{
    public const string DefaultSecretVariable = "STATIONGODMCP_SECRET";

    /// <summary>The name in hello; the key's name when signing in. Null connects anonymously as stationgod-cs.</summary>
    public string? ClientName { get; init; }

    /// <summary>The variable holding the key; null for the target's default (STATIONGOD_KEY_&lt;PIPE&gt;).</summary>
    public string? KeyVariable { get; init; }

    public string SecretVariable { get; init; } = DefaultSecretVariable;

    public ProtocolChoice Protocol { get; init; } = ProtocolChoice.Auto;

    /// <summary>Null for the target's default: 1 second for the pipe, 3 for TCP.</summary>
    public TimeSpan? ConnectTimeout { get; init; }

    /// <summary>The mod closes a sign-in not finished within 10 seconds of connecting.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The program's own version, sent in hello as client.version.</summary>
    public string ClientVersion { get; init; } = Library.Version;

    /// <summary>The catalogue the caller was built with; replaced by the mod's when the hashes differ.</summary>
    public GameCatalogue BuiltInCatalogue { get; init; } = GameCatalogue.BuiltIn;

    /// <summary>Where keys and the secret are read from: the process environment unless a test says otherwise.</summary>
    public Func<string, string?> ReadEnvironment { get; init; } = Environment.GetEnvironmentVariable;

    internal string HelloName => string.IsNullOrWhiteSpace(ClientName) ? Library.DefaultClientName : ClientName.Trim();

    internal TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? Target.DefaultConnectTimeout;

    internal string EffectiveKeyVariable =>
        string.IsNullOrWhiteSpace(KeyVariable) ? Target.DefaultKeyVariable : KeyVariable.Trim();

    /// <summary>The key to offer: only with a client name and a non-empty variable.</summary>
    internal string? Key => string.IsNullOrWhiteSpace(ClientName) ? null : Value(EffectiveKeyVariable);

    internal string? Secret => Value(SecretVariable);

    private string? Value(string variable) =>
        ReadEnvironment(variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>What this library calls itself in hello.</summary>
public static class Library
{
    public const string DefaultClientName = "stationgod-cs";

    public static string Version { get; } =
        typeof(Library).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Name => $"stationgod-cs/{Version}";

    /// <summary>The features this library understands, sent in hello.</summary>
    public static IReadOnlyList<string> Features { get; } = ["shape", "shape.paths", "subscriptions", "cancel"];
}
