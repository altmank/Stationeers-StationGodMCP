#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Verbs;

/// <summary>
/// find thing (catalogue find.json): every thing a filter keeps (ThingSearch), nearest first or by reference id, paged;
/// only the page is described in full. Every page is taken over as many frames as it needs (FramedPage), deferred
/// meanwhile (VerbOutcome.Pending) and answered once, as a one-shot page would be: a first page takes its candidates as
/// it starts and builds its ordered list in slices (WalkBuild), distances measured from where the player stood then,
/// then each frame's share of the page is described until the frame's budget is reached. With the wire cursor "open"
/// the rest of the list stays under a cursor (cursor_id) when more follows; a later page with that cursor id takes the
/// walk's next things with no scan, skips things that no longer exist, checks no filter again and answers the first
/// page's scanned count. near names a centre
/// for radius (a point, "looking_at" or a thing: at most 1000 m; "player": any); box is the other area, not both. Read
/// only.
/// </summary>
internal static class FindThing
{
    private const double MaximumRadiusM = 1000.0;

    internal static VerbOutcome Run(FindThingOptions options)
    {
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        int limit = paging.Limit ?? 1;
        Page page;
        if (walk.Continues)
        {
            page = new Page(PlayerOrigin.Current(), origin => FramedPage<FoundThingView>.Later(walk, limit, Live(origin)));
        }
        else
        {
            List<OptionProblem> problems = options.Selector.AreaProblems(MaximumRadiusM);
            if (problems.Count > 0)
            {
                return new VerbOutcome.Refused(OptionProblem.Refusal(FindThingOptions.Spec.Label, problems));
            }

            ThingFilter filter = FilterOf(options);

            PlayerOrigin origin = PlayerOrigin.Current().RequireIf(filter.NearPlayerM.HasValue);
            WalkBuild<Thing> build = new WalkBuild<Thing>(ThingSearch.Candidates(filter),
                ThingSearch.WalkFilter(filter, origin.Player != null ? origin.Player.Position : (Vector3?)null),
                paging.Order);
            page = new Page(origin, standing => FramedPage<FoundThingView>.Scan(walk, paging.Offset, limit, build,
                Live(standing)));
        }

        return page.Step(CurrentFrame.EndMs) ? VerbOutcome.Of(page.Result()) : new VerbOutcome.Pending(page);
    }

    // The filter the options give, once their area options go together.
    private static ThingFilter FilterOf(FindThingOptions options)
    {
        Selector selector = options.Selector;
        double? nearPlayer = selector.Near is NearPoint.Player ? selector.Radius : null;
        PointArea area = selector.Near is { } near and not NearPoint.Player
            ? PointArea.Near(NearCentres.Of(near, FindThingOptions.Spec.Label), selector.Radius!.Value)
            : selector.AreaAround(null);
        return new ThingFilter(selector.Name, selector.Prefab, options.Category ?? ThingLocations.Any,
            options.RuntimeType, options.Labelled ?? false, options.HasAtmosphere, nearPlayer, options.Broken,
            MadeOf(options.MadeBy, options.MadeSince), options.Location ?? ThingLocations.Any, area);
    }

    // made_by: a maker's reference id, or text in its prefab or shown name; made_since: a game time, or a negative
    // number of seconds before now.
    private static PrintFilter MadeOf(string? by, double? since)
    {
        long? makerId = null;
        string? makerText = null;
        if (by != null)
        {
            if (ThingId.TryRead(new JValue(by), out ThingId id))
            {
                makerId = id.Value;
            }
            else
            {
                makerText = by;
            }
        }

        return new PrintFilter(makerId, makerText, since is < 0 ? Assets.Scripts.GameManager.GameTime + since : since);
    }

    // An id's thing described as it is taken, from where the player stands then; null for a thing gone meanwhile.
    private static System.Func<long, FoundThingView?> Live(PlayerOrigin origin)
    {
        List<ColorSwatch> swatches = ThingColors.Swatches();
        return id => ThingSearch.Live(id) is { } thing ? ThingSearch.ViewOf(thing, origin, swatches) : null;
    }

    // A find thing page over frames: the scan's build (none for a walk's later page), then the page.
    private sealed class Page : IPendingResult
    {
        private readonly FramedPage<FoundThingView> _page;
        private readonly PlayerOrigin _origin;

        internal Page(PlayerOrigin origin, System.Func<PlayerOrigin, FramedPage<FoundThingView>> page)
        {
            _origin = origin;
            _page = page(origin);
        }

        public bool Step(double untilMs) =>
            _page.Step(StopwatchClock.Instance, untilMs, EntryCost.FindThing.MsPerEntry);

        public object Result()
        {
            FramedResult<FoundThingView> found = _page.Result(_page.IsScan ? _page.Scanned : null);
            EntryCost.FindThing.Expect(found.Page.Entries.Count);
            return FoundThings.Reply(found.Page, found.Kept is int scanned ? scanned : 0, _origin.View,
                found.CursorId);
        }
    }
}
