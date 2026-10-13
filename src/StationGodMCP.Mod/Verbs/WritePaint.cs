#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write paint (catalogue write.json): things painted as a spray can paints them (Paints): ids in one color, items a
/// color each, or a pipe, cable or chute network's pieces (and devices with include devices) in one color. One row per
/// thing, {previous_color, color} or its error, each failing alone. Without confirm the call reports every paint and
/// changes nothing. With no target it lists the game's colours.
/// </summary>
internal static class WritePaint
{
    internal static VerbOutcome Run(WritePaintOptions options)
    {
        string label = WritePaintOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        PaintRequest? request = PaintRequest.Of(options.Selector.Ids, options.Selector.Network, options.Kind,
            options.Fields.Includes("devices"), options.Color, options.Items, problems);
        if (request == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        List<ColorSwatch> swatches = Paints.Swatches();
        IReadOnlyList<PaintOrder> orders;
        switch (request)
        {
            case PaintRequest.Things things:
                orders = things.Orders;
                break;
            case PaintRequest.Network network:
                orders = Paints.NetworkOrders(network);
                break;
            default:
                return new VerbOutcome.Answered(PaintReplies.Colors(Paints.Colors(swatches)));
        }

        bool report = !options.Report.Acts;
        List<PaintResult> results = Paints.WriteMany(orders, report, label, swatches);
        return new VerbOutcome.Answered(report ? PaintReplies.Reported(results) : PaintReplies.Acted(results));
    }
}
