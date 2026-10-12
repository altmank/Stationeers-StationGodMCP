#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// One page of a paged find taken over as many frames as it needs, answered once, in the shape and with the entries a
/// one-shot page has (protocol.md, Walks; scheduling.md, the slice lane). Scan: a first call, with or without cursor,
/// whose list is built in slices (WalkBuild), then its page (offset and limit; a walk's from its start) taken from the
/// built ids; a walk's first call keeps the rest under a cursor. Later: a walk's later page taken from its cursor. Each
/// frame's share stops at the frame's budget and the next goes on where it stopped, until the page holds limit or the
/// list ends; live gives an id's entry, or null for a thing gone meanwhile, which is passed over. The page is
/// answered in the slice it finished in only when serialising it (serialiseMsPerEntry each) still fits the budget;
/// otherwise in the next slice, before any other work, so a long serialise does not follow the page's own work in one
/// frame. Main thread only.
/// </summary>
internal sealed class FramedPage<TView> where TView : class
{
    private readonly WalkRequest _walk;
    private readonly PageRequest _page;
    private readonly Func<long, TView?> _live;
    private readonly IWalkBuild? _build;
    private long[]? _ids;
    private int _next;
    private int _skipped;
    private bool _paged;
    private bool _started;
    private int _offset;
    private WalkStep _last;

    private FramedPage(WalkRequest walk, PageRequest page, Func<long, TView?> live, IWalkBuild? build)
    {
        _walk = walk;
        _page = page;
        _live = live;
        _build = build;
        Views = new List<TView>(page.Limit);
    }

    /// <summary>The entries taken so far.</summary>
    internal List<TView> Views { get; }

    /// <summary>The candidates the scan took (0 for a later page: no scan ran).</summary>
    internal int Scanned => _build?.Candidates ?? 0;

    /// <summary>A first call: its list built by build, its page from the built ids.</summary>
    internal static FramedPage<TView> Scan(WalkRequest walk, PageRequest page, IWalkBuild build,
        Func<long, TView?> live) =>
        new FramedPage<TView>(walk, page, live, build ?? throw new ArgumentNullException(nameof(build)));

    /// <summary>A walk's later page, from its cursor.</summary>
    internal static FramedPage<TView> Later(WalkRequest walk, PageRequest page, Func<long, TView?> live) =>
        new FramedPage<TView>(walk, page, live, null);

    /// <summary>Goes on until the clock reaches untilMs, at least one step; true once the page may be answered.</summary>
    internal bool Step(IMonotonicClock clock, double untilMs, double serialiseMsPerEntry)
    {
        bool worked = false;
        if (_build != null && !_build.Done)
        {
            worked = true;
            if (!_build.Run(clock, untilMs) || clock.NowMs >= untilMs)
            {
                return false;
            }
        }

        if (!_paged)
        {
            worked = true;
            _paged = Take(new PageBudget(clock, untilMs));
            if (!_paged)
            {
                return false;
            }
        }

        return !worked || clock.NowMs + Views.Count * serialiseMsPerEntry < untilMs;
    }

    /// <summary>
    /// Once Step returned true: notes the page's truncation as a one-shot page does and returns its slice, the cursor
    /// that goes on (a walk's, while ids remain) and the gone things passed over (a later page always gives it; a
    /// scan only when there were any).
    /// </summary>
    internal Slice<TView> Note(string list, string advice, out string? cursorId, out int? skipped)
    {
        if (_build == null)
        {
            cursorId = _last.CursorId;
            skipped = _skipped;
            _walk.Note(list, _page, Views.Count, _last.Total, cursorId);
            return Slice<TView>.Walked(Views, _offset, _page.Limit, _last.Total, _last.HasMore);
        }

        long[] ids = _ids!;
        bool more = _next < ids.Length;
        skipped = _skipped > 0 ? _skipped : (int?)null;
        cursorId = _walk.KeepRest(ids, _next, Views.Count, _skipped);
        int offset = _walk.Opens ? 0 : _page.Offset;
        _walk.Note(list, _page, Views.Count, cursorId != null || !_walk.Opens ? ids.Length : Views.Count, cursorId,
            advice);
        return Slice<TView>.Walked(Views, offset, _page.Limit, ids.Length, more);
    }

    // This frame's share of the page; true once it holds limit or the list has ended.
    private bool Take(PageBudget budget)
    {
        int wanted = _page.Limit - Views.Count;
        if (_build == null)
        {
            WalkStep step = _walk.Next(wanted, _live, Views, budget);
            if (!_started)
            {
                _offset = step.Offset;
                _started = true;
            }

            _skipped += step.Skipped;
            _last = step;
            return Views.Count >= _page.Limit || !step.HasMore;
        }

        if (_ids == null)
        {
            _ids = _build.Ids();
            _next = _walk.Opens ? 0 : Math.Min(_page.Offset, _ids.Length);
        }

        _next = WalkCursors.Take(_ids, _next, wanted, _live, Views, budget, out int skipped);
        _skipped += skipped;
        return Views.Count >= _page.Limit || _next >= _ids.Length;
    }
}
