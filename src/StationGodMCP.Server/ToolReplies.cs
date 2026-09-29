using System.Text.Json;

namespace StationGodMCP.Server;

/// <summary>
/// A tool call the sidecar answers with an error itself (a bad argument, the game out of reach): the code and message
/// become the same {code, message} error object the mod gives.
/// </summary>
internal sealed class ToolFailure(string code, string message) : Exception(message)
{
    internal const string InvalidArgument = "invalid_argument";
    internal const string GameUnavailable = "game_unavailable";

    internal string Code { get; } = code;

    internal static ToolFailure Argument(string message) => new(InvalidArgument, message);

    internal static ToolFailure Unavailable(string message) => new(GameUnavailable, message);
}

/// <summary>
/// The one shape of every tools/call result, whoever answered it: content holds the reply object as JSON text,
/// structuredContent the same object, isError whether it is an error. An error object is {code, message}.
/// </summary>
internal static class ToolReplies
{
    internal static object Of(JsonElement body, bool isError) => new
    {
        content = new[] { new { type = "text", text = body.GetRawText() } },
        structuredContent = body,
        isError
    };

    internal static object Failure(ToolFailure failure) =>
        Of(JsonSerializer.SerializeToElement(new { code = failure.Code, message = failure.Message }), isError: true);
}
