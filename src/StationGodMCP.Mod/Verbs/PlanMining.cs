#nullable enable

using Objects.Rockets;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// plan mining (catalogue plan.json): what a rocket can collect at each site by the game's formulas (RocketMining):
/// the rocket by id or name, or the only one; a list of rockets one row each; node narrows to one node and its
/// discovered sites. Read only.
/// </summary>
internal static class PlanMining
{
    private const string Explain = "explain";

    internal static VerbOutcome Run(PlanMiningOptions options)
    {
        string? node = string.IsNullOrWhiteSpace(options.Node) ? null : options.Node!.Trim();
        bool collectableOnly = options.CollectableOnly ?? false;
        bool explain = options.Fields.Includes(Explain);
        return options.Selector.Ids is { } ids
            ? IdAnswers.Of(ids, id =>
            {
                Rocket rocket = RocketLocator.Find(id);
                return (rocket.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    RocketMining.Plan(rocket, node, collectableOnly, explain));
            })
            : Answer.Of(RocketMining.Plan(RocketLocator.One((string?)null), node, collectableOnly, explain));
    }
}
