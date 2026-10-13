#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Show;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// show preview (catalogue show.json): wire boxes of a planned structure layout (build structure's own report of the
/// placements, without tool, status and job_id), plus the cells and boxes the call gives, drawn on the player's screen
/// only and for the call's seconds. A new call replaces what the last drew unless keep; clear only removes what is
/// drawn; xray draws them through walls, frames and terrain. Read only: nothing in the world changes.
/// </summary>
internal static class ShowPreview
{
    internal static VerbOutcome Run(ShowPreviewOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        PreviewOrder? order = PreviewOrder.Of(options, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ShowPreviewOptions.Spec.Label, problems));
        }

        ShowPreviewView view = order.Clear ? PreviewScreen.Clear() : PreviewScreen.Show(order);
        return new VerbOutcome.Answered(JObject.FromObject(view, ApiJson.Fresh()));
    }
}
