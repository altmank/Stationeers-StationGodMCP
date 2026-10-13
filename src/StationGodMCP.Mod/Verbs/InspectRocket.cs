#nullable enable

using System.Collections.Generic;
using Objects.Rockets;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect rocket (catalogue inspect.json): where each rocket is and goes, what it weighs, its fuel, engines, thrust,
/// landing checks, cargo, power and burn time (RocketStatuses); compact the short form, self_test the forecast's
/// checks against the game's numbers, include parts every part's pose and explain what the numbers mean. id names a
/// rocket by its id, name, network or any part; left out, every rocket; a list answers one row each. Read only.
/// </summary>
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
            return Answer.Of(RocketStatuses.Read(RocketLocator.All(), compact, selfTest, withParts, explain));
        }

        return IdAnswers.Of(ids, id =>
        {
            Rocket rocket = RocketLocator.Find(id);
            return (rocket.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RocketStatuses.Read(new List<Rocket> { rocket }, compact, selfTest, withParts, explain));
        });
    }
}
