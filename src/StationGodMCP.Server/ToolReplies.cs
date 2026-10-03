using System.Text.Json;

namespace StationGodMCP.Server;

/// <summary>The error codes the sidecar answers with itself: a bad argument, the game out of reach.</summary>
internal static class ToolFailure
{
    internal const string InvalidArgument = "invalid_argument";
    internal const string GameUnavailable = "game_unavailable";
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

    /// <summary>An error the sidecar answers itself, in the same {code, message} object the mod gives.</summary>
    internal static object Error(string code, string message) =>
        Of(JsonSerializer.SerializeToElement(new { code, message }), isError: true);
}
