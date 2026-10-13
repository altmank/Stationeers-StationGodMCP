#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Verbs;

/// <summary>What a swap cable, pipe or chute call gives besides its goal, in verb names.</summary>
internal sealed class NetworkSwapCall
{
    internal NetworkSwapCall(Selector selector, MaterialsSource materials, bool skipUnmatched, Fields fields,
        ReportConfirm report, bool queue, string? acknowledgeGasLost)
    {
        Selector = selector;
        Materials = materials;
        SkipUnmatched = skipUnmatched;
        Fields = fields;
        Report = report;
        Queue = queue;
        AcknowledgeGasLost = acknowledgeGasLost;
    }

    public Selector Selector { get; }

    public MaterialsSource Materials { get; }

    public bool SkipUnmatched { get; }

    public Fields Fields { get; }

    public ReportConfirm Report { get; }

    public bool Queue { get; }

    public string? AcknowledgeGasLost { get; }
}

/// <summary>
/// swap cable, pipe and chute over UpgradePlanner and UpgradeJobs: the pieces by id (one job for a list), network or
/// (chute) box. Without confirm, a Report.Of report: every piece the job swaps or removes a target [id, name, prefab
/// -> new prefab or "removed"] with the what's undo mark, the planned view the body with every piece listed, and every
/// problem, a gas hold the confirmed call would be refused on among them. Confirmed with a problem, the report answers
/// and nothing starts; confirmed and ready, one job starts. rows is how many entries each list of the job's result
/// shows.
/// </summary>
internal static class SwapNetworks
{
    private const string Removed = "removed";

    /// <summary>The most half-metre cells a box holds: a 32 m cube.</summary>
    internal const long MaximumBoxCells = 262144;

    /// <summary>The pieces the call names, or its refusal; ids that cannot be read are problems at their index.</summary>
    internal static PieceSelection? Selection(UpgradeFamily family, string label, string noun, bool takesBox,
        NetworkSwapCall call, List<ThingId?> named, List<ReportProblem> unread, out VerbError? refused)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        string? form = NetworkSwapOrders.Form(call.Selector, takesBox, label, noun, problems, out refused);
        if (form == null)
        {
            refused ??= OptionProblem.Refusal(label, problems);
            return null;
        }

        switch (form)
        {
            case NetworkSwapOrders.ById:
                named.AddRange(JobTargets.Ids(call.Selector.Ids!.Ids, label, unread));
                return new PieceSelection.Pieces(JobTargets.Read(named));
            case NetworkSwapOrders.ByNetwork:
                return new PieceSelection.Network(NetworkHandles.Resolve(call.Selector.Network!, "network", family));
            default:
                return BoxOf(call.Selector.Box!, label, out refused);
        }
    }

    private static PieceSelection.Box? BoxOf(BoxCorners box, string label, out VerbError? refused)
    {
        GridCell a = RunArgs.CellOf(new Vector3((float)box.First.X, (float)box.First.Y, (float)box.First.Z));
        GridCell b = RunArgs.CellOf(new Vector3((float)box.Second.X, (float)box.Second.Y, (float)box.Second.Z));
        GridCell min = new GridCell(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));
        GridCell max = new GridCell(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));
        long cells = SmallCellCode.SmallCountIn(min, max);
        if (cells <= MaximumBoxCells)
        {
            refused = null;
            return new PieceSelection.Box(min, max);
        }

        refused = OptionProblem.Refusal(label, new[]
        {
            new OptionProblem("box", $"holds {cells} half-metre cells; at most {MaximumBoxCells} (a 32 m cube)")
        });
        return null;
    }

    /// <summary>The request the planner takes: the goal, the pieces, the payers, rows for the job's result.</summary>
    internal static UpgradeRequest Request(UpgradeFamily family, SwapGoal goal, PieceSelection selection,
        NetworkSwapCall call, string label)
    {
        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(call.Materials, label, more);
        return new UpgradeRequest(family, goal, selection, from,
            new UpgradeOptions(call.SkipUnmatched, call.Materials.RefundTo,
                call.Report.Rows ?? ReplyDefaults.UpgradeListed, call.Fields.Includes("notes")), more);
    }

    /// <summary>The report or the job's start, as report and confirm say.</summary>
    internal static VerbOutcome Run(UpgradeRequest request, WhatSpec spec, NetworkSwapCall call,
        List<ThingId?> named, List<ReportProblem> unread)
    {
        string mark = spec.Write!.UndoMark;
        bool pipes = request.Family is PipeFamily;
        return WriteCheck<UpgradePlan>.Run(() => UpgradePlanner.Plan(request))
            .Outcome(call.Report, plan => Act(plan, request, call, named, unread, mark, pipes),
                plan => Described(plan, named, unread, mark, pipes, call.AcknowledgeGasLost));
    }

    private static VerbOutcome Act(UpgradePlan plan, UpgradeRequest request, NetworkSwapCall call,
        List<ThingId?> named, List<ReportProblem> unread, string mark, bool pipes)
    {
        if (unread.Count > 0 || !plan.Ready ||
            GasHold.Judge(pipes, call.AcknowledgeGasLost) is GasHoldVerdict.Refusing)
        {
            return new VerbOutcome.Answered(Described(plan, named, unread, mark, pipes, call.AcknowledgeGasLost));
        }

        object started = UpgradeJobs.Start(request, plan, call.Queue, call.AcknowledgeGasLost);
        object reply = JobBrief.Started(started,
            JobPreflightSummaryView.Of(UpgradeReports.Of(plan, UpgradeReports.DryRun, null)), call.Fields.Verbose);
        return new VerbOutcome.Answered(JObject.FromObject(reply, ApiJson.Fresh()));
    }

    private static JObject Described(UpgradePlan plan, List<ThingId?> named, List<ReportProblem> unread,
        string mark, bool pipes, string? acknowledge)
    {
        List<ReportProblem> problems = new List<ReportProblem>(unread);
        problems.AddRange(JobReports.Problems(plan.Problems, named));
        GasHold.Preview(pipes, acknowledge);
        if (GasHold.Judge(pipes, acknowledge) is GasHoldVerdict.Refusing refusing)
        {
            problems.Add(new ReportProblem(null, null, VerbError.Of(GasHold.Refusal(refusing))));
        }

        List<ReportTarget> targets = new List<ReportTarget>(plan.Swaps.Count);
        foreach (PlannedSwap swap in plan.Swaps)
        {
            for (int index = 0; index < swap.Olds.Count; index++)
            {
                SmallGrid old = swap.Olds[index].Piece;
                string becomes = index == 0 && swap.First is { } first ? first.Prefab.PrefabName : Removed;
                targets.Add(new ReportTarget(new ThingId(old.ReferenceId).ToString(), null, Names.Of(old),
                    new JValue(old.PrefabName), new JValue(becomes), mark));
            }
        }

        UpgradeReportView view = UpgradeReports.Of(plan, UpgradeReports.DryRun, null, UpgradePlanner.MaximumPieces);
        return Report.Of(targets, JobReports.Body(view, problems.Count == 0), problems);
    }
}

/// <summary>
/// swap chute (catalogue swap.json): chute pieces no item can pass to a consumer removed, junctions left with one way
/// made plain (RemoveDeadChutes); spare is never removed or replaced, riding says what a piece holding an item does.
/// </summary>
internal static class SwapChute
{
    // The job's name in its status and in inspect job.
    private const string JobTool = "clean_chutes";

    internal static VerbOutcome Run(SwapChuteOptions options)
    {
        string label = SwapChuteOptions.Spec.Label;
        NetworkSwapCall call = new NetworkSwapCall(options.Selector, options.Materials, options.SkipUnmatched ?? false,
            options.Fields, options.Report, options.Queue ?? true, null);
        UpgradeFamily family = new ChuteFamily();
        List<ThingId?> named = new List<ThingId?>();
        List<ReportProblem> unread = new List<ReportProblem>();
        PieceSelection? selection = SwapNetworks.Selection(family, label, "chute", true, call, named, unread,
            out VerbError? refused);
        if (selection == null)
        {
            return new VerbOutcome.Refused(refused!);
        }

        HashSet<long> spare = new HashSet<long>();
        foreach (string id in options.Spare ?? System.Array.Empty<string>())
        {
            spare.Add(TargetIds.Of(id, label).Value);
        }

        RidingPolicy riding = options.Riding == "refuse" ? RidingPolicy.Refuse : RidingPolicy.Skip;
        SwapGoal goal = new EndCleanupGoal(JobTool,
            new List<ICleanOperation> { new RemoveDeadChutes(spare, riding) });
        return SwapNetworks.Run(SwapNetworks.Request(family, goal, selection, call, label), SwapChuteOptions.Spec,
            call, named, unread);
    }
}
