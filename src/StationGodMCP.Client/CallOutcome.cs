using System.Text.Json;

namespace StationGodMCP.Client;

/// <summary>
/// How a call ended. Every outcome a caller has to handle is one of these three; none is thrown.
/// </summary>
public abstract record CallOutcome
{
    private CallOutcome()
    {
    }

    public abstract TResult Match<TResult>(
        Func<Answered, TResult> answered, Func<Refused, TResult> refused, Func<NoAnswer, TResult> noAnswer);

    /// <summary>The mod answered with the method's result. Shaped is true when the mod applied the call's shape.</summary>
    public sealed record Answered(JsonElement Result, bool Shaped) : CallOutcome
    {
        public override TResult Match<TResult>(
            Func<Answered, TResult> answered, Func<Refused, TResult> refused, Func<NoAnswer, TResult> noAnswer) =>
            answered(this);
    }

    /// <summary>
    /// An error object {code, message, data?}: the mod's answer, or the sign-in's when the game refused the connection.
    /// Nothing is resent because of it, whatever the code.
    /// </summary>
    public sealed record Refused(JsonElement Error) : CallOutcome
    {
        public string Code =>
            Error.ValueKind == JsonValueKind.Object && Error.TryGetProperty("code", out JsonElement code) &&
            code.ValueKind == JsonValueKind.String
                ? code.GetString()!
                : "unknown";

        public override TResult Match<TResult>(
            Func<Answered, TResult> answered, Func<Refused, TResult> refused, Func<NoAnswer, TResult> noAnswer) =>
            refused(this);

        /// <summary>An error object made on this side of the wire.</summary>
        public static Refused Of(string code, string message) =>
            new(JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["code"] = code, ["message"] = message }));
    }

    /// <summary>
    /// No answer: no game, the connection broke, or no reply in time. MaybeRan is true when the call was written and is
    /// not safe to send again, so it may have changed the game; WorldChanged when the connection came back to another
    /// world and the call was therefore not sent again.
    /// </summary>
    public sealed record NoAnswer(string Message, bool MaybeRan, bool WorldChanged = false) : CallOutcome
    {
        public override TResult Match<TResult>(
            Func<Answered, TResult> answered, Func<Refused, TResult> refused, Func<NoAnswer, TResult> noAnswer) =>
            noAnswer(this);
    }
}
