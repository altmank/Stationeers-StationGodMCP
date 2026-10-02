#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Shared;

/// <summary>reply_too_large: the reply the method built is larger than the caller accepts; the method ran.</summary>
internal static class ReplyTooLarge
{
    internal const string Code = "reply_too_large";

    internal static ErrorView Of(long bytes, long limit, ShapeOutcome outcome)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(outcome.Lists.Count);
        foreach (KeyValuePair<string, int> list in outcome.Lists)
        {
            counts[list.Key] = list.Value;
        }

        return new ErrorView(Code,
            $"The reply is {bytes} bytes, more than the {limit} allowed; narrow it with fields, limit or the method's own filters.",
            new ReplyTooLargeData(bytes, limit, counts));
    }
}

/// <summary>reply_too_large's data: the reply's size, the limit it passed, and the length of every top-level list.</summary>
internal sealed class ReplyTooLargeData
{
    internal ReplyTooLargeData(long bytes, long limit, Dictionary<string, int> counts)
    {
        Bytes = bytes;
        Limit = limit;
        Counts = counts;
    }

    public long Bytes { get; }

    public long Limit { get; }

    public Dictionary<string, int> Counts { get; }
}
