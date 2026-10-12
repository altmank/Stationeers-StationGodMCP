#nullable enable

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A handler's result that is not ready in the frame its call started (a walk's build, find_things with cursor): the
/// call is deferred, and the scheduler's slice lane steps it each frame within what is left of the frame's budget
/// until Step says it is ready; Result then gives the reply object, which may still be refused (ApiException). A
/// handler returns one only after trying in its own frame first, so work that fits a frame answers at once.
/// </summary>
internal interface IPendingResult
{
    /// <summary>Goes on until the scheduler's clock reaches untilMs, at least one step; true once Result is ready.</summary>
    bool Step(double untilMs);

    object Result();
}
