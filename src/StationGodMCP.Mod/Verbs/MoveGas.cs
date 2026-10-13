#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// move gas (catalogue move.json): gas moved from source to to, or deleted, at the next atmospherics tick (GasPlan,
/// GasMoves); source "planet" with delete takes named gases out of the planet's air (PlanetGasRemoval). A cheat:
/// without confirm the call reports the prediction from the same checks and queues nothing. A confirmed move answers
/// status queued and its transfer_id, which inspect job follows.
/// </summary>
internal static class MoveGas
{
    internal static VerbOutcome Run(MoveGasOptions options)
    {
        string label = MoveGasOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        GasMoveOrder? order = GasMoveOrder.Of(options.Source, options.To, options.Delete == true, options.Gases,
            options.AmountMol, Allow.Lifts(options.Allow, "burst"), options.Joined != false, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string undoMark = MoveGasOptions.Spec.Write!.UndoMark;
        if (order.Source is GasPlaceOption.Planet)
        {
            return WriteCheck<PlanetGasRemovalView>.Run(() =>
                {
                    GasMoveChecks.RequireHost();
                    return PlanetGasRemoval.Remove(order.Gases!, order.AmountMol, act: false);
                })
                .Outcome(options.Report,
                    _ => new VerbOutcome.Answered(GasReplies.Acted(
                        PlanetGasRemoval.Remove(order.Gases!, order.AmountMol, act: true))),
                    removal => GasReplies.ReportedPlanet(GasReplies.Acted(removal), removal.BeforeMol,
                        removal.AfterMol, undoMark));
        }

        return WriteCheck<GasPlan>.Run(() => GasMoveChecks.Check(order, label))
            .Outcome(options.Report,
                plan => new VerbOutcome.Answered(GasReplies.Acted(plan.ToView(GasMoves.Enqueue(plan)))),
                plan => GasReplies.Reported(plan.ToPrediction(), undoMark));
    }
}
