#nullable enable

using System.Collections.Generic;
using Objects.Rockets;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect rocket (catalogue inspect.json): where each rocket is and goes, what it weighs, its fuel, engines, thrust,
/// landing checks, cargo, power and burn time (RocketStatuses); compact the short form, self_test the forecast's
/// checks against the game's numbers, include parts every part's pose and explain what the numbers mean. id names a
/// rocket by its id, name, network or any part; left out, every rocket; a list answers one row each. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectRocket
{
    internal static VerbOutcome Run(InspectRocketOptions options)
    {
        bool compact = options.Compact == true;
        bool selfTest = options.SelfTest == true;
        bool withParts = options.Fields.Includes("parts");
        bool explain = options.Fields.Includes("explain");
        if (options.Selector.Ids is not { } ids)
        {
            List<Rocket> rockets = RocketLocator.All();
            return WorkResult<RocketStatusListView>.Of(answer => RocketStatuses.Reading(rockets, compact, selfTest,
                withParts, explain, answer), statuses => Answer.Of(statuses));
        }

        return IdWork.Of<RocketStatusListView>(ids, id =>
        {
            Rocket rocket = RocketLocator.Find(id);
            return (rocket.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                answer => RocketStatuses.Reading(new List<Rocket> { rocket }, compact, selfTest, withParts, explain,
                    answer));
        });
    }
}
