#nullable enable

using System;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// A list of things answered one page at a time (find container, frame, damaged): a first page from a scan spread over
/// frames (ScanBuild: the candidates one per step, then a bounded sort) or from ids known at once (ReadyWalk), a later
/// page from the walk its cursor keeps. Either is taken over as many frames as its scan and describing its things need
/// (FramedPage), deferred meanwhile (VerbOutcome.Pending), and answered once.
/// With the wire cursor "open" the rest of the list stays under a cursor (cursor_id) when more follows; a later page
/// with that cursor id takes the walk's next things as they are then, passing over things gone meanwhile, and answers
/// the counts the first page answered (TCounts, kept under the cursor), since the walk is the first page's list.
/// </summary>
internal sealed class ThingListPage<TView, TCounts> : IPendingResult where TView : class where TCounts : class
{
    private readonly FramedPage<TView> _page;
    private readonly Func<TCounts?> _counts;
    private readonly Func<Page<TView>, string?, TCounts?, object> _reply;

    private ThingListPage(FramedPage<TView> page, Func<TCounts?> counts,
        Func<Page<TView>, string?, TCounts?, object> reply)
    {
        _page = page;
        _counts = counts;
        _reply = reply;
    }

    /// <summary>
    /// A first page of ids sorted in the list's order, from scanned candidates, with the scan's counts; live gives an
    /// id's entry, reply the what's reply for the page, the cursor id (null when none) and the counts.
    /// </summary>
    internal static VerbOutcome First(WalkRequest walk, Paging paging, long[] sorted, int scanned, TCounts counts,
        Func<long, TView?> live, Func<Page<TView>, string?, TCounts?, object> reply) =>
        Run(new ThingListPage<TView, TCounts>(
            FramedPage<TView>.Scan(walk, paging.Offset, LimitOf(paging), new ReadyWalk(sorted, scanned), live),
            () => counts, reply));

    /// <summary>
    /// A first page from a scan spread over frames (build): its counts are read once the scan has ended (counts), and
    /// kept under the cursor as First keeps them; live gives an id's entry, reply the what's reply.
    /// </summary>
    internal static VerbOutcome Scan(WalkRequest walk, Paging paging, IWalkBuild build, Func<TCounts> counts,
        Func<long, TView?> live, Func<Page<TView>, string?, TCounts?, object> reply) =>
        Run(new ThingListPage<TView, TCounts>(
            FramedPage<TView>.Scan(walk, paging.Offset, LimitOf(paging), build, live), counts, reply));

    /// <summary>
    /// A walk's later page: its next things, each described as it is now by live, with the counts its first page
    /// answered (null only when the walk kept none).
    /// </summary>
    internal static VerbOutcome Later(WalkRequest walk, Paging paging, Func<long, TView?> live,
        Func<Page<TView>, string?, TCounts?, object> reply) =>
        Run(new ThingListPage<TView, TCounts>(FramedPage<TView>.Later(walk, LimitOf(paging), live),
            static () => null, reply));

    public bool Step(double untilMs) => _page.Step(StopwatchClock.Instance, untilMs);

    public object Result()
    {
        FramedResult<TView> found = _page.Result(_page.IsScan ? _counts() : null);
        return _reply(found.Page, found.CursorId, found.Kept as TCounts);
    }

    private static VerbOutcome Run(ThingListPage<TView, TCounts> page) =>
        page.Step(CurrentFrame.EndMs) ? VerbOutcome.Of(page.Result()) : new VerbOutcome.Pending(page);

    private static int LimitOf(Paging paging) => paging.Limit ?? int.MaxValue;
}

/// <summary>A thing list page's reply (ThingListPage).</summary>
internal static class ThingListReply
{
    /// <summary>
    /// The list's reply: ListReply's page under list, the what's other keys, then cursor_id while the walk has more
    /// pages (a wire key the hub and sg read, never shown to an agent).
    /// </summary>
    internal static ReplyObject Of<TView>(string list, Page<TView> page, string? cursorId,
        params (string Key, object? Value)[] rest)
    {
        ReplyObject keys = ReplyFields.Of(rest);
        if (cursorId != null)
        {
            keys.Add("cursor_id", cursorId);
        }

        return ListReply.OfPage(list, page, keys);
    }
}
