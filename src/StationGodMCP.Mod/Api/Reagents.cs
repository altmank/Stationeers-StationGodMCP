#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Reagents;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// reagents: the reagents a thing holds, a furnace's melted load, a centrifuge's or mixer's contents, a microwave's
/// food. Logic gives only the total (LogicType.Reagents); this reads each one from Thing.ReagentMixture with
/// ReagentMixture.Get, in the game's order (Reagent.AllReagents), with its unit (Reagent.Unit: g, or ml for alcohol,
/// milk and oil), and ReagentMixture.TotalReagents, which adds them regardless of unit. Read only.
/// </summary>
internal static class ReagentsApi
{
    /// <summary>Amounts at or below this are rounding left in the mixture, not a reagent.</summary>
    private const double TraceAmount = 1e-6;

    internal static ReagentsView Handle(Args args)
    {
        ThingId id = args.ThingId("reference_id");
        Thing thing = GameLookup.RequireThing(id);
        ReagentMixture? mixture = thing.ReagentMixture;
        return new ReagentsView(GameLookup.ViewOf(thing), Total(mixture), ReadReagents(mixture));
    }

    /// <summary>The thing's total and reagents, as Handle reports them.</summary>
    internal static ReagentsReadView ReadingOf(Thing thing)
    {
        ReagentMixture? mixture = thing.ReagentMixture;
        return new ReagentsReadView(Total(mixture), ReadReagents(mixture));
    }

    private static double Total(ReagentMixture? mixture) => mixture?.TotalReagents ?? 0.0;

    private static List<ReagentView> ReadReagents(ReagentMixture? mixture)
    {
        List<ReagentView> reagents = new List<ReagentView>();
        if (mixture == null)
        {
            return reagents;
        }

        foreach (Reagent reagent in Reagent.AllReagents)
        {
            double quantity = mixture.Get(reagent);
            if (quantity > TraceAmount)
            {
                reagents.Add(new ReagentView(reagent.TypeName, reagent.DisplayName, quantity, reagent.Unit));
            }
        }

        return reagents;
    }
}
