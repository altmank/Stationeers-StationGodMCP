#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find item (catalogue find.json): every item a filter keeps, wherever it is, with machine stock and silo stores
/// (FoundItems), nearest first or by id, paged; with group_by holder or prefab, the groups are the list. The first page
/// keeps the sorted entries (the scan's records, not their views) under its cursor and describes only its own page; a
/// later page (wire cursor) describes its entries from them: an item as it is then (gone ones passed over), a machine's
/// stock or a silo's store as the scan read it while its holder stands, a group as the scan summed it. group_by place
/// answers the totals per prefab instead (ItemTotals), the first limit of them, each with its holders top holders; it
/// takes no offset or exclude_in_use. near names a centre for radius (a point, "looking_at" or a thing: at most 1000
/// m; "player": any); box is the other area. Read only.
/// </summary>
internal static class FindItem
{
    private const string List = "items";
    private const string GroupByPlace = "place";
    private const double MaximumRadiusM = 1000.0;


    internal static VerbOutcome Run(FindItemOptions options)
    {
        Selector selector = options.Selector;
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        if (walk.Continues)
        {
            FoundList? kept = walk.Kept() as FoundList;
            PlayerOrigin now = PlayerOrigin.Current();
            return ThingListPage<IFoundItemView, FoundList>.Later(walk, paging,
                index => kept?.Entry(index, now), (page, cursorId, list) => Reply(page, cursorId, now, list));
        }

        bool place = string.Equals(options.GroupBy, GroupByPlace, StringComparison.Ordinal);
        List<OptionProblem> problems = selector.AreaProblems(MaximumRadiusM);
        if (place && paging.Offset > 0)
        {
            problems.Add(new OptionProblem("offset", "goes without group_by place: the totals are not paged"));
        }

        if (place && options.ExcludeInUse == true)
        {
            problems.Add(new OptionProblem("exclude_in_use", "goes without group_by place"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindItemOptions.Spec.Label, problems));
        }

        SelectorPlace where = NearCentres.Place(selector, FindItemOptions.Spec.Label);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(where.AroundPlayerM.HasValue);
        ItemFilter filter = ItemFilter.Of(selector.Prefab, selector.Name, options.Location, selector.Within,
            where.AroundPlayerM, where.Area);
        return place
            ? new VerbOutcome.Answered(Totals(filter, origin, paging.Limit ?? int.MaxValue, (int)(options.Holders ?? 0)))
            : Listed(walk, filter, origin, paging, options.GroupBy, options.ExcludeInUse == true);
    }

    // The items, or their groups, as one page; in_use_left_out when parts in use were left out.
    private static VerbOutcome Listed(WalkRequest walk, ItemFilter filter, PlayerOrigin origin, Paging paging,
        string? groupBy, bool excludeInUse)
    {
        ItemGroupings.TryParse(groupBy, out ItemGrouping grouping);
        FoundBuild build = new FoundBuild(new FoundScan(filter, origin, excludeInUse), paging.Order, grouping,
            excludeInUse);
        return ThingListPage<IFoundItemView, FoundList>.Scan(walk, paging, build, () => build.List!,
            index => build.List!.Entry(index, origin), (page, cursorId, kept) => Reply(page, cursorId, origin, kept));
    }

    private static ReplyObject Reply(Page<IFoundItemView> page, string? cursorId, PlayerOrigin origin, FoundList? list)
    {
        return list?.InUseLeftOut is { } inUse
            ? ThingListReply.Of(List, page, cursorId, ("local_player", origin.View), ("in_use_left_out", inUse))
            : ThingListReply.Of(List, page, cursorId, ("local_player", origin.View));
    }

    // group_by place: the first limit prefabs' totals, most first, each with its holders top holders (0: none listed).
    private static ReplyObject Totals(ItemFilter filter, PlayerOrigin origin, int limit, int holders)
    {
        Totalled totalled = ItemTotals.Of(filter, origin);
        List<PrefabTotalView> totals = new List<PrefabTotalView>(Math.Min(limit, totalled.Rows.Count));
        for (int index = 0; index < totalled.Rows.Count && index < limit; index++)
        {
            totals.Add(totalled.Rows[index].ToView(holders));
        }

        return ReplyFields.Of(("totals", totals), ("prefab_count", totalled.Rows.Count),
            ("item_count", totalled.Items), ("machine_stock_entries", totalled.StockEntries),
            ("silo_entries", totalled.SiloEntries));
    }

    /// <summary>
    /// A find item walk's build: the scan and its sort (ScanBuild over FoundScan), then the list in order, grouped when
    /// asked, in one pass over the kept rows; a walk's key is an entry's index in the list.
    /// </summary>
    private sealed class FoundBuild : IWalkBuild
    {
        private readonly FoundScan _scan;
        private readonly ScanBuild _build;
        private readonly ItemGrouping _grouping;
        private readonly bool _excludeInUse;

        internal FoundBuild(FoundScan scan, ListOrder order, ItemGrouping grouping, bool excludeInUse)
        {
            _scan = scan;
            _build = new ScanBuild(scan.Scan, scan.Order(order));
            _grouping = grouping;
            _excludeInUse = excludeInUse;
        }

        /// <summary>The list in order, once Done.</summary>
        internal FoundList? List { get; private set; }

        public bool Done => List != null;

        public int Candidates => _build.Candidates;

        public bool Run(IMonotonicClock clock, double untilMs)
        {
            if (List == null && _build.Run(clock, untilMs))
            {
                long[] order = _build.Ids();
                List<FoundRow> rows = new List<FoundRow>(order.Length);
                foreach (long index in order)
                {
                    rows.Add(_scan.Rows[(int)index]);
                }

                List = new FoundList(rows, _grouping == ItemGrouping.None ? null : FoundItems.Groups(rows, _grouping),
                    _grouping, _excludeInUse ? _scan.InUse : null);
            }

            return Done;
        }

        public long[] Ids()
        {
            long[] keys = new long[List!.Count];
            for (int index = 0; index < keys.Length; index++)
            {
                keys[index] = index;
            }

            return keys;
        }
    }

    /// <summary>
    /// A find item walk's entries in order, kept under its cursor: the scan's rows, or their groups, and the parts in use
    /// the scan left out. A walk's key is an entry's index.
    /// </summary>
    private sealed class FoundList
    {
        private readonly List<FoundRow> _rows;
        private readonly List<ItemGroup<FoundRow>>? _groups;
        private readonly ItemGrouping _grouping;

        internal FoundList(List<FoundRow> rows, List<ItemGroup<FoundRow>>? groups, ItemGrouping grouping, int? inUse)
        {
            _rows = rows;
            _groups = groups;
            _grouping = grouping;
            InUseLeftOut = inUse;
        }

        internal int Count => _groups?.Count ?? _rows.Count;

        internal int? InUseLeftOut { get; }

        /// <summary>The entry at index as a page lists it now; null when its thing or holder is gone.</summary>
        internal IFoundItemView? Entry(long index, PlayerOrigin origin)
        {
            if (index < 0 || index >= Count)
            {
                return null;
            }

            return _groups != null
                ? FoundItems.GroupView(_groups[(int)index], _grouping)
                : _rows[(int)index].Now(origin);
        }
    }
}
