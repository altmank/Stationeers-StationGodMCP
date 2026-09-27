#nullable enable

using System;

namespace StationGodMCP.Api.Shared;

/// <summary>A refused request: its code and message become the reply's error.</summary>
internal sealed class ApiException : Exception
{
    internal ApiException(string code, string message) : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
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
