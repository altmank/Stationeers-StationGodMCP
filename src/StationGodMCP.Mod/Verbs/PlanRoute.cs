#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// plan route (catalogue plan.json): a cable, pipe or chute route (RouteOrder reads the call, RoutePlanning searches),
/// with build_arguments, build's options for it, and build_report, build's planned view of it without the keys a job
/// has (JobReports.Body), rows cells listed; with summarize, build_report_summary in its place. Read only.
/// </summary>
internal static class PlanRoute
{
    private const string ReportPath = "build_report.";

    internal static VerbOutcome Run(PlanRouteOptions options)
    {
        string label = PlanRouteOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        RouteOrder? order = RouteOrder.Of(options, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        PlannedRoute planned = RoutePlanning.Plan(order, label);
        JObject? report = null;
        JObject? summary = null;
        if (planned.Report is { } view)
        {
            if (order.Summarize)
            {
                summary = JobReports.Body(RunSummaryView.Of(view), view.Ready);
            }
            else
            {
                view.NoteTruncations(ReportPath);
                report = JobReports.Body(view, view.Ready);
            }
        }

        PlanRouteView reply = new PlanRouteView(planned.Tool, planned.Route, planned.Failure, planned.BuildArguments,
            report, summary, planned.Notes);
        return new VerbOutcome.Answered(JObject.FromObject(reply, ApiJson.Fresh()));
    }
}
