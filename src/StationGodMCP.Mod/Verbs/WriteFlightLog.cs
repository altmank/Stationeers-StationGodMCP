#nullable enable

using Objects.Rockets;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write flight_log (catalogue write.json): a rocket's flight recorder started (every interval_s game seconds,
/// capacity samples kept, csv beside the save), stopped or cleared (FlightLogWrites). id names the rocket by its id,
/// name, network or any part; left out, the only rocket. Server state, never the world; cannot be undone.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class WriteFlightLog
{
    internal static VerbOutcome Run(WriteFlightLogOptions options)
    {
        Rocket rocket = RocketLocator.One(options.Selector.Ids?.Ids[0]);
        return options.Action switch
        {
            "start" => Answer.Of(FlightLogWrites.Start(rocket, options.IntervalS!.Value, (int)options.Capacity!.Value,
                options.Csv == true)),
            "stop" => Answer.Of(FlightLogWrites.Stop(rocket)),
            _ => Answer.Of(FlightLogWrites.Clear(rocket))
        };
    }
}
