#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find item (catalogue find.json): every item a filter keeps, wherever it is, with machine stock and silo stores
/// (FoundItems), nearest first or by id, paged by offset (stock and silo entries are no things a walk could keep); with
/// group_by holder or prefab, the groups are the list. group_by place answers the totals per prefab instead (ItemTotals),
/// the first limit of them, each with its holders top holders; it takes no offset or exclude_in_use. near names a
/// centre for radius (a point, "looking_at" or a thing: at most 1000 m; "player": any); box is the other area. Read only.
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
        bool place = string.Equals(options.GroupBy, GroupByPlace, StringComparison.Ordinal);
        List<OptionProblem> problems = selector.AreaProblems(MaximumRadiusM);
        if (paging.OffsetOnly() is { } cursor)
        {
            problems.Add(cursor);
        }

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
        return new VerbOutcome.Answered(place
            ? Totals(filter, origin, paging.Limit ?? int.MaxValue, (int)(options.Holders ?? 0))
            : Listed(filter, origin, paging, options.GroupBy, options.ExcludeInUse == true));
    }

    // The items, or their groups, as one page; in_use_left_out when parts in use were left out.
    private static JObject Listed(ItemFilter filter, PlayerOrigin origin, Paging paging, string? groupBy,
        bool excludeInUse)
    {
        List<FoundRow> rows = FoundItems.Rows(filter, origin, excludeInUse, out int inUse);
        rows.Sort((a, b) => ListKey.Compare(paging.Order, a.Key, b.Key));
        JsonSerializer serializer = ApiJson.Fresh();
        JObject rest = ReplyFields.Of(("local_player", origin.View));
        if (excludeInUse)
        {
            rest["in_use_left_out"] = inUse;
        }

        ItemGroupings.TryParse(groupBy, out ItemGrouping grouping);
        return grouping == ItemGrouping.None
            ? ListReply.Of(List, rows, paging, row => JToken.FromObject(row.ToView(), serializer), rest)
            : ListReply.Of(List, FoundItems.Groups(rows, grouping), paging,
                group => JToken.FromObject(FoundItems.GroupView(group, grouping), serializer), rest);
    }

    // group_by place: the first limit prefabs' totals, most first, each with its holders top holders (0: none listed).
    private static JObject Totals(ItemFilter filter, PlayerOrigin origin, int limit, int holders)
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
}
