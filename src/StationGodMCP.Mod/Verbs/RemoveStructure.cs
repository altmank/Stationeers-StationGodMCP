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
/// (RemovePlanner, BuildJobs), every build state refunded down to the kit; a list is one job. allow lifts the guards
/// (contents, breach, broken, burst, unsupported); gas_to keeps a removed device's gas. Without confirm, a Report.Of
/// report: every structure the job takes down a target [id, name, prefab -> "removed"] [undo] ([cannot undo: burnt] for
/// a burnt cable: its undo record keeps it as burnt, so undo leaves it gone and undoes the rest), the planned view the
/// body, every problem with the index of its id, a gas hold the confirmed call would be refused on among them; verbose
/// lists every port a split cuts. Confirmed with a problem, the report answers and nothing starts; confirmed and
/// ready, one job starts and its undo is recorded (JobSnapshots).
/// </summary>
internal static class RemoveStructure
{
    private const string Removed = "removed";

    // The job's name in its status, in inspect job and in its undo record.
    private const string JobTool = "remove_structure";

    internal static VerbOutcome Run(RemoveStructureOptions options)
    {
        string label = RemoveStructureOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        GasTarget? gasTo = GasTarget.Of(options.GasTo, problems);
        GasHoldArgs.Check(options.AcknowledgeGasLost, problems);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        List<ReportProblem> unread = new List<ReportProblem>();
        List<ThingId?> named = JobTargets.Ids(options.Selector.Ids!.Ids, label, unread);
        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(options.Materials, label, more);
        bool verbose = options.Fields.Verbose;
        RemoveArguments arguments = new RemoveArguments(JobTargets.Read(named), Allowance(options.Allow),
            options.Materials.RefundTo, from, options.Fields.Includes("notes"), gasTo, verbose);
        string mark = RemoveStructureOptions.Spec.Write!.UndoMark;
        string? acknowledge = options.AcknowledgeGasLost;
        JobSource source = new JobSource(from?.Value, false, options.Materials.RefundTo, null);
        return WriteCheck<RemovePlan>.Run(() => RemovePlanner.Plan(arguments))
            .Outcome(options.Report, plan => Act(plan, arguments, named, unread, mark, acknowledge,
                    options.Queue ?? true, verbose, source),
                plan => Described(plan, named, unread, mark, acknowledge));
    }

    private static RemovalAllowance Allowance(IReadOnlyList<string>? allow)
    {
        List<string> words = new List<string>(allow ?? System.Array.Empty<string>());
        return new RemovalAllowance(words.Contains("contents"), words.Contains("breach"), words.Contains("broken"),
            words.Contains("burst"), words.Contains("unsupported"));
    }

    private static VerbOutcome Act(RemovePlan plan, RemoveArguments arguments, List<ThingId?> named,
        List<ReportProblem> unread, string mark, string? acknowledge, bool queue, bool verbose, JobSource source)
    {
        if (unread.Count > 0 || !plan.Ready ||
            GasHold.Judge(plan.TouchesPipes, acknowledge) is GasHoldVerdict.Refusing)
        {
            return new VerbOutcome.Answered(Described(plan, named, unread, mark, acknowledge));
        }

        RemoveReportView preflight = BuildReports.Of(plan, BuildReports.Scheduled, null);
        RemoveWork removing = new RemoveWork(arguments, preflight, plan.TouchesPipes);
        List<Structure> burnt = new List<Structure>();
        List<Structure> removed = new List<Structure>();
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            (takedown.Piece is CableRuptured ? burnt : removed).Add(takedown.Piece);
        }

        object started = JobSnapshots.Record(BuildJobs.Start("remove", removing, queue, acknowledge), JobTool,
            removed, source, burnt);
        object reply = verbose ? started : JobStartReplies.Brief(started, JobPreflightSummaryView.Of(preflight));
        return new VerbOutcome.Answered(JObject.FromObject(reply, ApiJson.Fresh()));
    }

    private static JObject Described(RemovePlan plan, List<ThingId?> named, List<ReportProblem> unread, string mark,
        string? acknowledge)
    {
        List<ReportProblem> problems = new List<ReportProblem>(unread);
        foreach (BuildIssueView issue in plan.Problems)
        {
            problems.Add(new ReportProblem(issue.Index is { } index ? Given(named, index) : null,
                issue.ReferenceId?.ToString(), new VerbError(issue.Code, issue.Message)));
        }

        GasHold.Preview(plan.TouchesPipes, acknowledge);
        if (GasHold.Judge(plan.TouchesPipes, acknowledge) is GasHoldVerdict.Refusing refusing)
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
