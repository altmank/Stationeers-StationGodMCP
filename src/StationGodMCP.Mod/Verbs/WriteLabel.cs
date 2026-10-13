#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write label (catalogue write.json): things renamed as the hand Labeller does (Labels); an empty name gives back the
/// game's name. One id answers its rename; a list (each named '&lt;label&gt; 1', '&lt;label&gt; 2' in order) or labels
/// answer one row per rename, each failing alone. Without confirm the call reports every rename and changes nothing.
/// </summary>
internal static class WriteLabel
{
    internal static VerbOutcome Run(WriteLabelOptions options)
    {
        string label = WriteLabelOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        LabelOrders? orders = LabelOrders.Of(options.Selector.Ids, options.Label, options.Labels, problems);
        if (orders == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        bool report = !options.Report.Acts;
        List<LabelResult> results = Labels.WriteMany(orders.Orders, report, label);
        return report
            ? new VerbOutcome.Answered(LabelReplies.Reported(results))
            : LabelReplies.Acted(results, orders.AnswersRows);
    }
}
