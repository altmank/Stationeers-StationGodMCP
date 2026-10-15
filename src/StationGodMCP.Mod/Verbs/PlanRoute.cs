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
    internal static VerbOutcome Run(PlanRouteOptions options)
    {
        string label = PlanRouteOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        RouteOrder? order = RouteOrder.Of(options, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        return WorkResult<PlannedRoute>.Of(answer => RoutePlanning.Planning(order, label, answer),
            planned => Answered(order, planned));
    }

    private static VerbOutcome Answered(RouteOrder order, PlannedRoute planned)
    {
        JObject? report = null;
        if (planned.Report is { } view)
        {
            if (order.Summarize)
            {
                report = JobReports.Body(RunSummaryView.Of(view), view.Ready);
            }
            else
            {
                view.NoteTruncations(PlanRouteView.ReportKey + ".");
                report = JobReports.Body(view, view.Ready);
            }
        }

        PlanRouteView reply = new PlanRouteView(planned.Route, planned.Failure, planned.BuildArguments, planned.Notes);
        return new VerbOutcome.Answered(reply.Reply(order.Summarize ? PlanRouteView.SummaryKey : PlanRouteView.ReportKey,
            report));
    }
}
