#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// One page of a list of things taken over as many frames as it needs, answered once, in the shape and with the entries
/// a one-shot page has (protocol.md, Walks; scheduling.md, the slice lane). Scan: a first page whose list is built in
/// slices (WalkBuild), then its page (offset and limit) taken from the built ids; one that opens a cursor keeps the
/// rest after its page under it. Later: a walk's later page taken from its cursor. Each frame's share stops
/// at the frame's budget and the next goes on where it stopped, until the page holds limit or the list ends; live gives
/// an id's entry, or null for a thing gone meanwhile, which is passed over. The page is answered in the slice it
/// finished in only when serialising it (serialiseMsPerEntry each) still fits the budget; otherwise in the next slice,
/// before any other work, so a long serialise does not follow the page's own work in one frame. Main thread only.
/// </summary>
internal sealed class FramedPage<TView> where TView : class
{
    private readonly WalkRequest _walk;
    private readonly int _offset;
    private readonly int _limit;
    private readonly Func<long, TView?> _live;
    private readonly IWalkBuild? _build;
    private long[]? _ids;
    private int _next;
    private int _skipped;
    private bool _paged;
    private bool _started;
    private int _walkOffset;
    private WalkStep _last;

    private FramedPage(WalkRequest walk, int offset, int limit, Func<long, TView?> live, IWalkBuild? build)
    {
        _walk = walk;
        _offset = Math.Max(offset, 0);
        _limit = Math.Max(limit, 1);
        _live = live;
        _build = build;
        Views = new List<TView>(Math.Min(_limit, 512));
    }

    /// <summary>The entries taken so far.</summary>
    internal List<TView> Views { get; }

    /// <summary>The candidates the scan took (0 for a later page: no scan ran).</summary>
    internal int Scanned => _build?.Candidates ?? 0;

    /// <summary>A first page, which scans; a walk's later page does not.</summary>
    internal bool IsScan => _build != null;

    /// <summary>A first page: its list built by build, its page (offset, at most limit) from the built ids.</summary>
    internal static FramedPage<TView> Scan(WalkRequest walk, int offset, int limit, IWalkBuild build,
        Func<long, TView?> live) =>
        new FramedPage<TView>(walk, offset, limit, live, build ?? throw new ArgumentNullException(nameof(build)));

    /// <summary>A walk's later page, from its cursor: at most limit.</summary>
    internal static FramedPage<TView> Later(WalkRequest walk, int limit, Func<long, TView?> live) =>
        new FramedPage<TView>(walk, 0, limit, live, null);

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
    /// Once Step returned true: the page as a one-shot page has it, and the cursor its walk goes on with (a first page
    /// that opens one while ids remain, or a later page's while its walk has more). A later page's offset is the
    /// walk's entries passed before it, its total the walk's length at its first call. kept is what a first page
    /// counted, kept under its cursor; a later page answers its walk's kept.
    /// </summary>
    internal FramedResult<TView> Result(object? kept = null)
    {
        if (_build == null)
        {
            return new FramedResult<TView>(
                new Page<TView>(Views, _last.Total, _walkOffset, _limit, _last.HasMore), _last.CursorId, _last.Kept);
        }

        long[] ids = _ids!;
        string? cursorId = _walk.KeepRest(ids, _next, _offset + Views.Count, _skipped, kept);
        return new FramedResult<TView>(
            new Page<TView>(Views, ids.Length, _offset, _limit, _next < ids.Length), cursorId, kept);
    }

    // This frame's share of the page; true once it holds limit or the list has ended.
    private bool Take(PageBudget budget)
    {
        int wanted = _limit - Views.Count;
        if (_build == null)
        {
            WalkStep step = _walk.Next(wanted, _live, Views, budget);
            if (!_started)
            {
                _walkOffset = step.Offset;
                _started = true;
            }

            _skipped += step.Skipped;
            _last = step;
            return Views.Count >= _limit || !step.HasMore;
        }

        if (_ids == null)
        {
            _ids = _build.Ids();
            _next = Math.Min(_offset, _ids.Length);
        }

        _next = WalkCursors.Take(_ids, _next, wanted, _live, Views, budget, out int skipped);
        _skipped += skipped;
        return Views.Count >= _limit || _next >= _ids.Length;
    }
}

/// <summary>
/// A framed page's answer: the page, the cursor id its walk goes on with (null when none), and what the walk's first
/// page counted (kept).
/// </summary>
internal sealed record FramedResult<TView>(Page<TView> Page, string? CursorId, object? Kept = null);
