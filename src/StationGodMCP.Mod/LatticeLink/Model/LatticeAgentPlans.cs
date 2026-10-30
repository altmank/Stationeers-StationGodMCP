#nullable enable

using System.Collections.Generic;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Model;

/// <summary>
/// The agent's plan ("StationGod agent") once it holds no ghost: finished, so it leaves Lattice's active plans (Loom's
/// plan menu) instead of staying open and empty in the save. The next agent placement opens a new one.
/// </summary>
internal static class LatticeAgentPlans
{
    /// <summary>Finishes every agent plan with no ghost left; the plans finished, and Lattice's refusals as text.</summary>
    internal static List<L.PlanId> CloseEmpty(L.IGhostSystem system, List<string> refused)
    {
        List<L.PlanId> empty = new List<L.PlanId>();
        foreach (L.Plan plan in system.Plans.All)
        {
            if (plan.Owner == LatticeNames.Agent && plan.Name == LatticeNames.AgentPlan &&
                system.Board.As(LatticeNames.Agent).Select(new L.Selector.InPlan(plan.Id))
                    .TryGet(out L.ValueList<L.Ghost> ghosts) && ghosts.Count == 0)
            {
                empty.Add(plan.Id);
            }
        }

        List<L.PlanId> finished = new List<L.PlanId>();
        foreach (L.PlanId id in empty)
        {
            if (system.Commands.Submit(L.CommandId.New(), new L.Command.FinishPlan(id), LatticeNames.Agent) is
                L.Submission.Answered { Outcome: L.Outcome.Refused why })
            {
                refused.Add($"plan {id.Value}: {LatticeText.Of(why.Why)}");
                continue;
            }

            finished.Add(id);
        }

        return finished;
    }
}
