#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Paging;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// One page of one thing's list (a screen's elements, a recording's samples, a silo's entries), in the one paging model
/// (cost design A4): the first page takes the matching entries' keys in order, describes only its own entries, and with
/// the wire cursor keeps the rest under a cursor (WalkRequest); a later page describes its entries from the kept keys,
/// passing over keys whose entry is gone. Answered at once: a thing's list is bounded by the thing, so it needs no
/// slices over frames (ThingListPage does that for the world's lists). A cursor walks one thing's list, so a call that
/// names several things pages each by offset and opens none.
/// </summary>
internal static class KeyedPage
{
    /// <summary>A first page: from offset, at most limit entries described by describe (null: gone, passed over).</summary>
    internal static Slice<T> First<T>(WalkRequest walk, long[] keys, int offset, int limit, Func<long, T?> describe,
        object? kept = null)
        where T : class
    {
        int from = Math.Min(Math.Max(offset, 0), keys.Length);
        List<T> entries = new List<T>(Math.Min(Math.Max(limit, 0), keys.Length - from));
        int next = WalkCursors.Take(keys, from, Math.Max(limit, 0), describe, entries, default, out int skipped);
        string? cursorId = walk.KeepRest(keys, next, from + entries.Count, skipped, kept);
        return Slice<T>.Walked(entries, offset, limit, keys.Length, next < keys.Length, cursorId);
    }

    /// <summary>A later page of a walk: its next at most limit entries as describe gives them now.</summary>
    internal static Slice<T> Later<T>(WalkRequest walk, int limit, Func<long, T?> describe) where T : class
    {
        List<T> entries = new List<T>();
        WalkStep step = walk.Next(Math.Max(limit, 1), describe, entries);
        return Slice<T>.Walked(entries, step.Offset, limit, step.Total, step.HasMore, step.CursorId);
    }

    /// <summary>
    /// The paging problem of a call that names ids: a cursor id walks one thing's list (null when the call is fine).
    /// </summary>
    internal static Pure.Verbs.OptionProblem? OneThing(Pure.Verbs.Paging paging, int ids) =>
        paging.CursorId != null && ids != 1
            ? new Pure.Verbs.OptionProblem("cursor", "walks one thing's list: name one id")
            : null;

    /// <summary>The walk a call takes: its cursor when it names one thing; none (offset pages) for several.</summary>
    internal static WalkRequest WalkOf(Pure.Verbs.Paging paging, int ids) =>
        WalkRequest.Of(ids == 1 ? paging : new Pure.Verbs.Paging(paging.Limit, paging.Offset, paging.Order));
}
