#nullable enable

using Objects.Rockets;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// plan mining (catalogue plan.json): what a rocket can collect at each site by the game's formulas (RocketMining):
/// the rocket by id or name, or the only one; a list of rockets one row each; node narrows to one node and its
/// discovered sites. Read only. Each rocket's plan is work over frames (RocketMining.Planning).
/// </summary>
[ReplyFact(FactRule.Live, "rockets, sites and the game's mining formulas")]
internal static class PlanMining
{
    private const string Explain = "explain";

    internal static VerbOutcome Run(PlanMiningOptions options)
    {
        string? node = string.IsNullOrWhiteSpace(options.Node) ? null : options.Node!.Trim();
        bool collectableOnly = options.CollectableOnly ?? false;
        bool explain = options.Fields.Includes(Explain);
        if (options.Selector.Ids is { } ids)
        {
            return IdWork.Of<MiningPlanView>(ids, id =>
            {
                Rocket rocket = RocketLocator.Find(id);
                return (rocket.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    answer => RocketMining.Planning(rocket, node, collectableOnly, explain, answer));
            });
        }

        Rocket only = RocketLocator.One((string?)null);
        return WorkResult<MiningPlanView>.Of(answer => RocketMining.Planning(only, node, collectableOnly, explain,
            answer), plan => Answer.Of(plan));
    }
}
