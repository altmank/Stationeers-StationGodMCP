#nullable enable

using System;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A refused request: its code, message and details (the error's data, e.g. cursor_expired's position) become the
/// reply's error.
/// </summary>
internal sealed class ApiException : Exception
{
    internal ApiException(string code, string message, object? details = null) : base(message)
    {
        Code = code;
        Details = details;
    }

    internal string Code { get; }

    /// <summary>The error's data; null for none.</summary>
    internal object? Details { get; }
}

/// <summary>The error codes the tools share, with their messages.</summary>
internal static class ApiErrors
{
    internal const string InvalidArgumentCode = "invalid_argument";
    internal const string ThingNotFoundCode = "thing_not_found";
    internal const string GameChangedCode = "game_changed";

    internal static ApiException InvalidArgument(string message) => new ApiException(InvalidArgumentCode, message);

    internal static ApiException ThingNotFound(ThingId id) =>
        new ApiException(ThingNotFoundCode, $"No thing with reference id {id}.");

    internal static ApiException Refused(string code, string message) => new ApiException(code, message);
}
