#nullable enable

using System;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// A list of things answered one page at a time (find container, frame, damaged): a first page from a list sorted in
/// one go as the call starts (ReadyWalk), a later page from the walk its cursor keeps. Either is taken over as many
/// frames as describing its things needs (FramedPage), deferred meanwhile (VerbOutcome.Pending), and answered once.
/// With the wire cursor "open" the rest of the list stays under a cursor (cursor_id) when more follows; a later page
/// with that cursor id takes the walk's next things as they are then, passing over things gone meanwhile.
/// </summary>
internal sealed class ThingListPage<TView> : IPendingResult where TView : class
{
    private readonly FramedPage<TView> _page;
    private readonly EntryCost _cost;
    private readonly Func<Page<TView>, string?, JObject> _reply;

    private ThingListPage(FramedPage<TView> page, EntryCost cost, Func<Page<TView>, string?, JObject> reply)
    {
        _page = page;
        _cost = cost;
        _reply = reply;
    }

    /// <summary>
    /// A first page of ids sorted in the list's order, from scanned candidates; live gives an id's entry, reply the
    /// what's reply for the page and the cursor id (null when none).
    /// </summary>
    internal static VerbOutcome First(WalkRequest walk, Paging paging, long[] sorted, int scanned,
        Func<long, TView?> live, EntryCost cost, Func<Page<TView>, string?, JObject> reply) =>
        Run(new ThingListPage<TView>(
            FramedPage<TView>.Scan(walk, paging.Offset, LimitOf(paging), new ReadyWalk(sorted, scanned), live), cost,
            reply));

    /// <summary>A walk's later page: its next things, each described as it is now by live.</summary>
    internal static VerbOutcome Later(WalkRequest walk, Paging paging, Func<long, TView?> live, EntryCost cost,
        Func<Page<TView>, string?, JObject> reply) =>
        Run(new ThingListPage<TView>(FramedPage<TView>.Later(walk, LimitOf(paging), live), cost, reply));

    /// <summary>
    /// The list's reply: ListReply's page under list, the what's other keys, then cursor_id while the walk has more
    /// pages (a wire key the hub and sg read, never shown to an agent).
    /// </summary>
    internal static JObject Reply(string list, Page<TView> page, string? cursorId, params (string Key, object? Value)[] rest)
    {
        JObject keys = ReplyFields.Of(rest);
        if (cursorId != null)
        {
            keys["cursor_id"] = cursorId;
        }

        Newtonsoft.Json.JsonSerializer serializer = ApiJson.Fresh();
        return ListReply.OfPage(list, page, view => JToken.FromObject(view, serializer), keys);
    }

    public bool Step(double untilMs) => _page.Step(StopwatchClock.Instance, untilMs, _cost.MsPerEntry);

    public object Result()
    {
        FramedResult<TView> found = _page.Result();
        _cost.Expect(found.Page.Entries.Count);
        return _reply(found.Page, found.CursorId);
    }

    private static VerbOutcome Run(ThingListPage<TView> page) =>
        page.Step(CurrentFrame.EndMs) ? VerbOutcome.Of(page.Result()) : new VerbOutcome.Pending(page);

    private static int LimitOf(Paging paging) => paging.Limit ?? int.MaxValue;
}
