#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find food (catalogue find.json): every food and drink in the world, wherever it is, with what packages hold and
/// what does not count, and the totals (Consumables); {food, drinks, packages, not_counted, totals, local_player}.
/// Read only.
/// </summary>
internal static class FindFood
{
    internal static VerbOutcome Run(FindFoodOptions options)
    {
        ConsumablesView found = Consumables.Take().ToView();
        return new VerbOutcome.Answered(ReplyFields.Of(("food", found.Food), ("drinks", found.Drinks),
            ("packages", found.Packages), ("not_counted", found.NotCounted), ("totals", found.Totals),
            ("local_player", found.LocalPlayer)));
    }
}
