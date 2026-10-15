#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure;

/// <summary>
/// survey wall's free_rects: where a w x h metre rectangle fits on the map, in one section only (one_section, default
/// true), on wall only (require_wall, default true), the first limit places (default 20, at most 50).
/// </summary>
internal sealed class FreeRectRequest
{
    internal FreeRectRequest(double w, double h, bool oneSection, bool requireWall, int limit)
    {
        W = w;
        H = h;
        OneSection = oneSection;
        RequireWall = requireWall;
        Limit = limit;
    }

    public double W { get; }

    public double H { get; }

    public bool OneSection { get; }

    public bool RequireWall { get; }

    public int Limit { get; }

    private static readonly string[] Keys = { "w", "h", "one_section", "require_wall", "limit" };

    internal const int DefaultLimit = 20;
    internal const int MaximumLimit = 50;

    /// <summary>The request the option gives; null without one, or with what is wrong with it in problems.</summary>
    internal static FreeRectRequest? Of(JToken? given, List<OptionProblem> problems)
    {
        if (given is not JObject request)
        {
            return null;
        }

        int unknown = problems.Count;
        NestedKeys.Check(request, "free_rects", Keys, problems);
        if (problems.Count > unknown)
        {
            return null;
        }

        double? w = Positive(request["w"]);
        double? h = Positive(request["h"]);
        long? limit = request["limit"] is { Type: JTokenType.Integer } count ? (long)count : null;
        int before = problems.Count;
        if (w == null)
        {
            problems.Add(new OptionProblem("free_rects.w", "is needed: the width in metres, above 0"));
        }

        if (h == null)
        {
            problems.Add(new OptionProblem("free_rects.h", "is needed: the height in metres, above 0"));
        }

        if (limit is < 1 or > MaximumLimit)
        {
            problems.Add(new OptionProblem("free_rects.limit", $"is 1 to {MaximumLimit}"));
        }

        return problems.Count > before
            ? null
            : new FreeRectRequest(w!.Value, h!.Value, Flag(request["one_section"]), Flag(request["require_wall"]),
                (int)(limit ?? DefaultLimit));
    }

    private static double? Positive(JToken? token) =>
        OptionReader.NumberOf(token) is { } number && number > 0.0 ? number : null;

    private static bool Flag(JToken? token) => token is not { Type: JTokenType.Boolean } flag || (bool)flag;
}
