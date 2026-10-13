#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Screens;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write screen (catalogue write.json): a touch on a ScriptedScreens screen's control, by element or at a pixel, as a
/// player's touch does (Screens.Press), the circuit holder in the gateway's scope, by id or "looking_at". Answers
/// {holder, surface, element, event, value, player, accepted}. Host only; cannot be undone.
/// </summary>
internal static class WriteScreen
{
    internal static VerbOutcome Run(WriteScreenOptions options)
    {
        string label = WriteScreenOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        ScreenPress? press = ScreenPress.Of(options.Surface, options.Element, options.Pixel, options.Event ?? "click",
            options.Text, options.AsPlayer, problems);
        if (press == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        DeviceScope scope = Devices.Scope(options.Gateway);
        IcTarget holder = CircuitHolders.Require(scope, TargetIds.Of(options.Selector.Ids!.Ids[0], label));
        return new VerbOutcome.Answered(IdAnswers.Payload(Screens.Press(holder, press), ApiJson.Fresh()));
    }
}
