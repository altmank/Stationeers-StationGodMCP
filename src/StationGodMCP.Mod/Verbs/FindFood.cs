#nullable enable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find food (catalogue find.json): every food and drink in the world, wherever it is, with what packages hold and
/// what does not count, and the totals (Consumables); {food, count, total, offset, has_more, drinks, packages,
/// not_counted, totals, local_player} (ListReply: count the food list, the list whole, so offset 0, no limit and
/// has_more false). Read only.
/// </summary>
internal static class FindFood
{
    internal static VerbOutcome Run(FindFoodOptions options)
    {
        ConsumablesView found = Consumables.Take().ToView();
        JsonSerializer serializer = ApiJson.Fresh();
        return new VerbOutcome.Answered(ListReply.Of("food", found.Food, Paging.All,
            food => JToken.FromObject(food, serializer),
            ReplyFields.Of(("drinks", found.Drinks), ("packages", found.Packages), ("not_counted", found.NotCounted),
                ("totals", found.Totals), ("local_player", found.LocalPlayer))));
    }
}
