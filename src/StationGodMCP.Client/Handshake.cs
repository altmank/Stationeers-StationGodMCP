using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>Connecting: a signed-in connection, or the outcome every call waiting for it gets instead.</summary>
internal abstract record Connecting
{
    private Connecting()
    {
    }

    internal sealed record Connected(GameConnection Connection) : Connecting;

    internal sealed record Failed(CallOutcome Outcome) : Connecting;
}

/// <summary>
/// Opening a connection (protocol.md, Versions and negotiation; Old clients). Over TCP the shared secret goes first;
/// after it TCP talks as the pipe does. With ProtocolChoice.Auto the client sends hello offering version 2; any first
/// answer without a type key is an old mod, which read hello as a version-1 request, and the same connection goes on in
/// version 1.
/// </summary>
internal static class Handshake
{
    internal static async Task<Connecting> OpenAsync(ClientOptions options, CancellationToken cancellation)
    {
        GameTarget target = options.Target;
        Opened opened = await target.OpenAsync(options.EffectiveConnectTimeout, cancellation).ConfigureAwait(false);
        if (opened is not Opened.Stream stream)
        {
            return Unreachable(((Opened.Nothing)opened).Message);
        }

        LineChannel channel = new(stream.Value);
        Connecting outcome;
        try
        {
            Connecting? signIn = target is GameTarget.Tcp
                ? await SecretAsync(options, channel, cancellation).ConfigureAwait(false)
                : null;
            outcome = signIn ?? (options.Protocol == ProtocolChoice.Version1
                ? new Connecting.Connected(new GameConnection(target, channel, ProtocolVersion.Version1, null))
                : await HelloAsync(options, channel, cancellation).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException ||
                                          exception is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            outcome = Unreachable($"The connection to {target.Description} broke while connecting ({exception.Message}).");
        }

        if (outcome is not Connecting.Connected)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    private static async Task<Connecting> HelloAsync(ClientOptions options, LineChannel channel, CancellationToken cancellation)
    {
        GameTarget target = options.Target;
        await channel.WriteLineAsync(Wire.Hello(options), cancellation).ConfigureAwait(false);
        Answer answer = await NextAsync(options, channel, "hello", cancellation).ConfigureAwait(false);
        if (answer is not Answer.Message { Value: var message })
        {
            return Unreachable(((Answer.Missing)answer).Why);
        }

        if (!message.TryGetProperty("type", out _))
        {
            return new Connecting.Connected(new GameConnection(target, channel, ProtocolVersion.Version1, null));
        }

        return Wire.Text(message, "type") switch
        {
            "welcome" => new Connecting.Connected(new GameConnection(target, channel, ProtocolVersion.Version2, message)),
            "reply" when Wire.Child(message, "error") is { } error => new Connecting.Failed(new CallOutcome.Refused(error)),
            _ => Unreachable($"{target.Description} answered hello unexpectedly: {Shorten(message)}")
        };
    }

    // TCP: the shared secret first; null when the server accepted it, else why not.
    private static async Task<Connecting?> SecretAsync(ClientOptions options, LineChannel channel, CancellationToken cancellation)
    {
        GameTarget target = options.Target;
        if (options.Secret is not { } secret)
        {
            return Refused("unauthorized",
                $"TCP needs the shared secret in the environment variable {options.SecretVariable}; it is not set.");
        }

        await channel.WriteLineAsync(Wire.SecretAuth(secret), cancellation).ConfigureAwait(false);
        Answer answer = await NextAsync(options, channel, "the shared secret", cancellation).ConfigureAwait(false);
        if (answer is not Answer.Message { Value: var message })
        {
            return Unreachable(((Answer.Missing)answer).Why);
        }

        return message.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
            ? null
            : Wire.Child(message, "error") is { } error
                ? new Connecting.Failed(new CallOutcome.Refused(error))
                : Refused("unauthorized", $"{target.Description} rejected the shared secret.");
    }

    // The next line during sign-in, within the handshake timeout.
    private static async Task<Answer> NextAsync(ClientOptions options, LineChannel channel, string what,
        CancellationToken cancellation)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(options.HandshakeTimeout);
        byte[]? line;
        try
        {
            line = await channel.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new Answer.Missing(
                $"{options.Target.Description} did not answer {what} within {options.HandshakeTimeout.TotalSeconds:0} s.");
        }

        return line == null
            ? new Answer.Missing($"{options.Target.Description} closed the connection after {what}.")
            : Wire.Parse(line) is { } message
                ? new Answer.Message(message)
                : new Answer.Missing($"{options.Target.Description} answered {what} with a line that is not a JSON object: {Wire.Start(line)}");
    }

    private static Connecting Unreachable(string message) =>
        new Connecting.Failed(new CallOutcome.NoAnswer(message, MaybeRan: false));

    private static Connecting Refused(string code, string message) =>
        new Connecting.Failed(CallOutcome.Refused.Of(code, message));

    private static string Shorten(JsonElement message)
    {
        string text = message.GetRawText();
        return text.Length <= 120 ? text : text[..120];
    }

    private abstract record Answer
    {
        private Answer()
        {
        }

        internal sealed record Message(JsonElement Value) : Answer;

        internal sealed record Missing(string Why) : Answer;
    }
}
