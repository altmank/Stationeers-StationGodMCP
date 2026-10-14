#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// remove structure (catalogue remove.json): built structures by id taken apart as deconstructing them by hand would
/// (RemovePlanner, BuildJobs), every build state refunded down to the kit; a list is one job. The ids are read against
/// the world here (a reference id, or what "looking_at" found); the rest of the call is RemoveStructureOrder's, and
/// the run is RemoveStructureRun's.
/// </summary>
internal static class RemoveStructure
{
    internal static VerbOutcome Run(RemoveStructureOptions options)
    {
        string label = RemoveStructureOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        List<ReportProblem> unread = new List<ReportProblem>();
        List<ThingId?> named = JobTargets.Ids(options.Selector.Ids!.Ids, label, unread);
        RemoveStructureOrder? order = RemoveStructureOrder.Of(options, named, unread, problems);
        return order != null
            ? RemoveStructureRun.Run(order)
            : new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
    }
}

/// <summary>
/// remove structure over RemovePlanner, BuildReports and BuildJobs: built structures by id taken apart as
/// deconstructing them by hand would, giving back what that gives back; allow lifts the guards (contents, breach,
/// broken, burst, unsupported); gas_to keeps a removed device's gas. Without confirm, a Report.Of report: every
/// structure the job takes down a target [id, name, prefab -> "removed"] [undo] ([cannot undo: burnt] for a burnt
/// cable: its undo record keeps it as burnt, so undo leaves it gone and undoes the rest), the planned view the body,
/// every problem with the index of its id, a gas hold the confirmed call would be refused on among them; verbose lists
/// every port a split cuts. Confirmed with a problem, the report answers and nothing starts; confirmed and ready, one
/// job starts and its undo is recorded (JobSnapshots). Reply is the same call for a caller that runs it itself (the
/// undo's replay).
/// </summary>
internal static class RemoveStructureRun
{
    private const string Removed = "removed";

    // The job's name in its status, in inspect job and in its undo record.
    private const string JobTool = "remove structure";

    internal static VerbOutcome Run(RemoveStructureOrder order)
    {
        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(order.Materials, order.Label, more);
        RemoveRequest arguments = order.Arguments(from, more);
        JobSource source = new JobSource(from?.Value, false, order.Materials.RefundTo, null);
        return WriteCheck<RemovePlan>.Run(() => RemovePlanner.Plan(arguments))
            .Outcome(order.Report, plan => Act(plan, arguments, order, source), plan => Described(plan, order));
    }

    /// <summary>
    /// One removal call's reply, for a caller that runs the call itself (undo's replay): the report until it acts, or
    /// the started job. A refusal the call cannot be read with is thrown, as any call's is.
    /// </summary>
    internal static object Reply(RemoveStructureOrder order) => Run(order) switch
    {
        VerbOutcome.Answered answered => answered.Reply,
        VerbOutcome.Refused refused => throw ApiErrors.Refused(refused.Error.Code, refused.Error.Message),
        _ => throw new System.InvalidOperationException("A removal call answers in the call that reads it.")
    };

    private static VerbOutcome Act(RemovePlan plan, RemoveRequest arguments, RemoveStructureOrder order,
        JobSource source)
    {
        if (order.Unread.Count > 0 || !plan.Ready ||
            GasHold.Judge(plan.TouchesPipes, order.AcknowledgeGasLost) is GasHoldVerdict.Refusing)
        {
            return new VerbOutcome.Answered(Described(plan, order));
        }

        RemoveReportView preflight = BuildReports.Of(plan, BuildReports.Scheduled, null);
        RemoveWork removing = new RemoveWork(arguments, preflight, plan.TouchesPipes);
        List<Structure> burnt = new List<Structure>();
        List<Structure> removed = new List<Structure>();
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            (takedown.Piece is CableRuptured ? burnt : removed).Add(takedown.Piece);
        }

        object started = JobSnapshots.Record(BuildJobs.Start("remove", removing, order.Queue,
                order.AcknowledgeGasLost), JobTool, removed, source, burnt);
        object reply = order.SplitDetail
            ? started
            : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
        return new VerbOutcome.Answered(JObject.FromObject(reply, ApiJson.Fresh()));
    }

    private static JObject Described(RemovePlan plan, RemoveStructureOrder order)
    {
        string mark = order.Mark;
        List<ReportProblem> problems = new List<ReportProblem>(order.Unread);
        foreach (BuildIssueView issue in plan.Problems)
        {
            problems.Add(new ReportProblem(issue.Index is { } index ? Given(order.Named, index) : null,
                issue.ReferenceId?.ToString(), new VerbError(issue.Code, issue.Message)));
        }

        GasHold.Preview(plan.TouchesPipes, order.AcknowledgeGasLost);
        if (GasHold.Judge(plan.TouchesPipes, order.AcknowledgeGasLost) is GasHoldVerdict.Refusing refusing)
        {
            problems.Add(new ReportProblem(null, null, VerbError.Of(GasHold.Refusal(refusing))));
        }

        List<ReportTarget> targets = new List<ReportTarget>(plan.Takedowns.Count);
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            targets.Add(new ReportTarget(new ThingId(takedown.Piece.ReferenceId).ToString(), null,
                Names.Of(takedown.Piece), new JValue(takedown.Piece.PrefabName), new JValue(Removed),
                takedown.Piece is CableRuptured ? RemoveRuns.BurntMark : mark));
        }

        RemoveReportView view = BuildReports.Of(plan, BuildReports.DryRun, null);
        return Report.Of(targets, JobReports.Body(view, problems.Count == 0), problems);
    }

    // The place in the ids given of the planner's index-th id (the planner counts only the ids that could be read).
    private static int? Given(List<ThingId?> named, int index)
    {
        int read = -1;
        for (int at = 0; at < named.Count; at++)
        {
            if (named[at] != null && ++read == index)
            {
                return at;
            }
        }

        return null;
    }
}
