#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// plan aim (catalogue plan.json): the Horizontal and Vertical that point each thing, by what it is: a solar panel at
/// the sun (SolarAiming: its own pivots, the twin pose nearer its current aim), a satellite dish at the trader contact
/// (DishAiming: its own model posed on a coarse grid, then a shrinking step, and put back). contact is for a dish only,
/// and a dish needs it; a thing that is neither is not_aimable. A list answers one row each. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "dishes, panels and their targets")]
internal static class PlanAim
{
    internal static VerbOutcome Run(PlanAimOptions options)
    {
        string label = PlanAimOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        AimOrder? order = AimOrder.Of(options, problems);
        return order == null
            ? new VerbOutcome.Refused(OptionProblem.Refusal(label, problems))
            : IdAnswers.Things(order.Ids, label, id => Aim(id, order.Contact, label));
    }

    private static object Aim(ThingId id, long? contact, string label)
    {
        Thing thing = GameLookup.RequireThing(id);
        switch (thing)
        {
            case SolarPanel panel when contact != null:
                throw ApiErrors.InvalidArgument(
                    $"{label}: contact: {thing.DisplayName} ({id}) is a solar panel; contact is for a satellite dish only.");
            case SolarPanel panel:
                return SolarAiming.Aim(panel, id);
            case SatelliteDish dish when contact is { } trader:
                return DishAiming.Aim(dish, id, new ThingId(trader), label);
            case SatelliteDish:
                throw ApiErrors.InvalidArgument(
                    $"{label}: contact: {thing.DisplayName} ({id}) is a satellite dish and needs contact, the trader " +
                    "contact to point at; trade contacts lists them.");
            default:
                throw ApiErrors.Refused("not_aimable",
                    $"{label}: {thing.DisplayName} ({id}) is neither a solar panel nor a satellite dish.");
        }
    }
}
