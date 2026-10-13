#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write battery (catalogue write.json): batteries and cells charged (BatteryCharges): full when neither ratio nor
/// joules is given, a share of capacity, or joules clamped to it. The batteries are named by id (up to 256, each
/// failing alone), a rocket's, or every cell within a holder's slots ("player" for the player); one form a call. A
/// cheat: without confirm the call reports every battery from the same checks and charges nothing.
/// </summary>
internal static class WriteBattery
{
    internal static VerbOutcome Run(WriteBatteryOptions options)
    {
        string label = WriteBatteryOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        ChargeGoal? goal = ChargeGoal.Of(options.Ratio, options.Joules, problems);
        Selector selector = options.Selector;
        int forms = (selector.Ids != null ? 1 : 0) + (options.Rocket != null ? 1 : 0) +
                    (selector.Within != null ? 1 : 0);
        if (forms != 1)
        {
            problems.Add(new OptionProblem("id", forms == 0
                ? "or rocket or within is needed: the batteries to charge"
                : "goes without rocket and within: give one of them"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string undoMark = WriteBatteryOptions.Spec.Write!.UndoMark;
        return WriteCheck<BatteryPlan>.Run(() =>
            {
                BatteryTargets targets = selector.Ids is { } ids ? BatteryTargets.Named(ids.Ids, label)
                    : options.Rocket is { } rocket ? BatteryTargets.OfRocket(rocket)
                    : BatteryTargets.Within(selector.Within!, label);
                return new BatteryPlan(targets, BatteryCharges.Charge(targets, goal!, act: false));
            })
            .Outcome(options.Report,
                plan => new VerbOutcome.Answered(BatteryReplies.Acted(goal!, plan.Targets.FoundIn,
                    BatteryCharges.Charge(plan.Targets, goal!, act: true))),
                plan => BatteryReplies.Reported(goal!, plan.Targets.FoundIn, plan.Checked, undoMark));
    }

    private sealed class BatteryPlan
    {
        internal BatteryPlan(BatteryTargets targets, List<BatteryResult> @checked)
        {
            Targets = targets;
            Checked = @checked;
        }

        public BatteryTargets Targets { get; }

        public List<BatteryResult> Checked { get; }
    }
}
