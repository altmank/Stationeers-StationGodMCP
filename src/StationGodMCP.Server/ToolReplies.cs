using System.Text.Json;
using System.Text.Json.Serialization;
using StationGodMCP.Client;

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
    // The text copy is written as the adapter writes structuredContent (plain escaping), so the two read the same.
    private static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static object Of(JsonElement body, bool isError) => Of(body, TextOf(body), isError);

    /// <summary>The result with a text copy of its own: a run's plain text, or the JSON text with notices after it.</summary>
    internal static object Of(JsonElement body, string text, bool isError) => new
    {
        content = new[] { new { type = "text", text } },
        structuredContent = body,
        isError
    };

    /// <summary>The reply's text copy, exactly as the result's content carries it.</summary>
    internal static string TextOf(JsonElement body) => JsonSerializer.Serialize(body, TextOptions);

    private static readonly JsonSerializerOptions ErrorOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// An error the sidecar answers itself, in the same {code, message, see} object the mod gives: see is the tool_info
    /// node that explains the code (absent when the catalogue gives none).
    /// </summary>
    internal static object Error(string code, string message, HelpPointer? see) =>
        Of(JsonSerializer.SerializeToElement(new ErrorBody(code, message, see), ErrorOptions), isError: true);

    private sealed record ErrorBody(string Code, string Message, HelpPointer? See);
}
