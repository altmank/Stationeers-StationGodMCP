#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// write needs' report: the need values as they read now, each need asked for a target [player id, need, share now ->
/// share asked] with the what's undo mark; the reading's after is the asked value, which the call would set.
/// </summary>
internal static class NeedsReplies
{
    internal static ReportReply Reported(NeedsWrittenView reading, NeedsRequest request, string undoMark)
    {
        string id = reading.Player.ReferenceId.ToString();
        List<ReportTarget> targets = new List<ReportTarget>(3);
        if (request.Nutrition.HasValue)
        {
            targets.Add(new ReportTarget(id, null, "nutrition", reading.Nutrition.Before, request.Nutrition.Value,
                undoMark));
        }

        if (request.Hydration.HasValue)
        {
            targets.Add(new ReportTarget(id, null, "hydration", reading.Hydration.Before, request.Hydration.Value,
                undoMark));
        }

        if (request.Waste.HasValue && reading.Waste != null)
        {
            targets.Add(new ReportTarget(id, null, "waste", reading.Waste.Before, request.Waste.Value, undoMark));
        }

        return Report.Of(targets, reading);
    }
}
