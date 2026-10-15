#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Reagents;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The reagents a thing holds, a furnace's melted load, a centrifuge's or mixer's contents, a microwave's
/// food. Logic gives only the total (LogicType.Reagents); this reads each one from Thing.ReagentMixture with
/// ReagentMixture.Get, in the game's order (Reagent.AllReagents), with its unit (Reagent.Unit: g, or ml for alcohol,
/// milk and oil), and ReagentMixture.TotalReagents, which adds them regardless of unit. Read only.
/// </summary>
internal static class ThingReagents
{
    /// <summary>Amounts at or below this are rounding left in the mixture, not a reagent.</summary>
    private const double TraceAmount = 1e-6;

    /// <summary>The thing with its total and reagents, as inspect reagents answers.</summary>
    internal static ReagentsView Read(Thing thing)
    {
        ReagentMixture? mixture = thing.ReagentMixture;
        return new ReagentsView(GameLookup.ViewOf(thing), Total(mixture), ReadReagents(mixture));
    }

    /// <summary>The thing's total and reagents, as Read reports them.</summary>
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

        foreach (Reagent reagent in World.Reagents)
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
