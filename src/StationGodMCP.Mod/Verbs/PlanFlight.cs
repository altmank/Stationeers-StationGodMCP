#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Objects.Rockets;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Verbs;

/// <summary>
/// plan flight (catalogue plan.json): flies a copy of the rocket along the route the game lays, with the stop, the way
/// back and the what-ifs the call gives (ForecastRequest), by the game's own flight rules (RocketForecast); the rocket
/// by id, name, network or part, or the only one; a list of rockets one row each. Nothing in the game changes.
/// </summary>
[ReplyFact(FactRule.Live, "rockets and the game's flight formulas")]
internal static class PlanFlight
{
    internal static VerbOutcome Run(PlanFlightOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        ForecastRequest? request = ForecastRequest.Of(options, problems);
        if (request == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(PlanFlightOptions.Spec.Label, problems));
        }

        if (options.Selector.Ids is { } ids)
        {
            return IdWork.Of<RocketForecastView>(ids, id =>
            {
                Rocket rocket = RocketLocator.Find(id);
                return (rocket.ReferenceId.ToString(CultureInfo.InvariantCulture),
                    answer => RocketForecast.Flying(rocket, request, answer));
            });
        }

        Rocket only = RocketLocator.One((string?)null);
        return WorkResult<RocketForecastView>.Of(answer => RocketForecast.Flying(only, request, answer),
            forecast => Answer.Of(forecast));
    }
}
