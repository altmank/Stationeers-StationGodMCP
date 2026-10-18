#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;
using UnityEngine;
using StationGodMCP.Pure.Profiling;

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

    /// <summary>The ids of an id list option (spare, only) as reference ids; empty when it is left out.</summary>
    internal static HashSet<long> IdSet(IReadOnlyList<string>? ids, string label)
    {
        HashSet<long> set = new HashSet<long>();
        foreach (string id in ids ?? System.Array.Empty<string>())
        {
            set.Add(TargetIds.Of(id, label).Value);
        }

        return set;
    }

    /// <summary>
    /// swap cable or pipe: the upgrade (into, or into's default when tidy is left out or false) or the tidy
    /// operations; every problem with into, tidy, spare, only, older_than, root and the pieces at once.
    /// </summary>
    internal static VerbOutcome Tidyable(UpgradeFamily family, WhatSpec spec, string noun, NetworkSwapCall call,
        string? into, JToken? tidy, TidyOptions narrowing, System.Func<string?, SwapGoal> upgrade, string tidyTool)
    {
        string label = spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        List<string>? operations = NetworkSwapOrders.Tidy(tidy, into, narrowing.Given, problems);
        NetworkSwapOrders.Form(call.Selector, false, label, noun, problems, out VerbError? missing);
        GasHoldArgs.Check(call.AcknowledgeGasLost, problems);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        if (missing != null)
        {
            return new VerbOutcome.Refused(missing);
        }

        List<ThingId?> named = new List<ThingId?>();
        List<ReportProblem> unread = new List<ReportProblem>();
        PieceSelection selection = Selection(family, label, noun, false, call, named, unread, out _)!;
        SwapGoal goal = operations == null
            ? upgrade(into)
            : new EndCleanupGoal(tidyTool, CleanOperationCatalogue.Parse(operations,
                new CleanOptions(IdSet(narrowing.Spare, label), new RedundancyOptions(
                    narrowing.Only != null ? IdSet(narrowing.Only, label) : null,
                    narrowing.OlderThan != null ? TargetIds.Of(narrowing.OlderThan, label).Value : null,
                    narrowing.Root != null ? TargetIds.Of(narrowing.Root, label).Value : null))));
        return Run(Request(family, goal, selection, call, label), spec, call, named, unread);
    }

    /// <summary>The request the planner takes: the goal, the pieces, the payers, rows for the job's result.</summary>
    internal static UpgradeRequest Request(UpgradeFamily family, SwapGoal goal, PieceSelection selection,
        NetworkSwapCall call, string label)
    {
        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(call.Materials, label, more);
        return new UpgradeRequest(family, goal, selection, from,
            new UpgradeOptions(call.SkipUnmatched, call.Materials.RefundTo,
                call.Report.Rows ?? ReplyDefaults.UpgradeListed, call.Fields.Includes("notes"),
                call.Fields.Includes("network_devices")), more);
    }

    /// <summary>The report or the job's start, as report and confirm say.</summary>
    internal static VerbOutcome Run(UpgradeRequest request, WhatSpec spec, NetworkSwapCall call,
        List<ThingId?> named, List<ReportProblem> unread)
    {
        string mark = spec.Write!.UndoMark;
        bool pipes = request.Family is PipeFamily;
        return WriteCheck<UpgradePlan>.Planned(answer => UpgradePlanner.Planning(request, answer), call.Report,
            plan => Act(plan, request, call, named, unread, mark, pipes),
            plan => WorkResult<ReportReply>.Of(
                answer => Describing(plan, named, unread, mark, pipes, call.AcknowledgeGasLost, answer),
                static report => report));
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
        return new VerbOutcome.Answered(reply);
    }

    private static ReportReply Described(UpgradePlan plan, List<ThingId?> named, List<ReportProblem> unread,
        string mark, bool pipes, string? acknowledge)
    {
        Pure.Scheduling.Answer<ReportReply> report = new Pure.Scheduling.Answer<ReportReply>();
        foreach (object? _ in Describing(plan, named, unread, mark, pipes, acknowledge, report))
        {
        }

        return report.Value;
    }

    // The report as work over frames (A3): its targets a batch a step, then the plan's report view (UpgradeReports).
    [Profiled]
    private static IEnumerable<object?> Describing(UpgradePlan plan, List<ThingId?> named, List<ReportProblem> unread,
        string mark, bool pipes, string? acknowledge, Pure.Scheduling.Answer<ReportReply> answer)
    {
        List<ReportProblem> problems = new List<ReportProblem>(unread);
        problems.AddRange(JobReports.Problems(plan.Problems, named));
        yield return null;
        GasHold.Preview(pipes, acknowledge);
        if (GasHold.Judge(pipes, acknowledge) is GasHoldVerdict.Refusing refusing)
        {
            problems.Add(new ReportProblem(null, null, VerbError.Of(GasHold.Refusal(refusing))));
        }

        List<ReportTarget> targets = new List<ReportTarget>(plan.Swaps.Count);
        for (int at = 0; at < plan.Swaps.Count; at++)
        {
            PlannedSwap swap = plan.Swaps[at];
            for (int index = 0; index < swap.Olds.Count; index++)
            {
                SmallGrid old = swap.Olds[index].Piece;
                string becomes = index == 0 && swap.First is { } first ? first.Prefab.PrefabName : Removed;
                targets.Add(new ReportTarget(new ThingId(old.ReferenceId).ToString(), null, Names.Of(old),
                    old.PrefabName, becomes, mark));
            }

            if (at % UpgradeReports.PiecesAStep == UpgradeReports.PiecesAStep - 1)
            {
                yield return null;
            }
        }

        Pure.Scheduling.Answer<UpgradeReportView> view = new Pure.Scheduling.Answer<UpgradeReportView>();
        foreach (object? step in UpgradeReports.Building(plan, UpgradeReports.DryRun, null, UpgradePlanner.MaximumPieces,
                     view))
        {
            yield return step;
        }

        answer.Value = Report.Of(targets, JobReports.Body(view.Value, problems.Count == 0), problems);
    }
}

/// <summary>
/// swap chute (catalogue swap.json): chute pieces no item can pass to a consumer removed, junctions left with one way
/// made plain (RemoveDeadChutes); spare is never removed or replaced, riding says what a piece holding an item does.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class SwapChute
{
    // The job's name in its status and in inspect job.
    private const string JobTool = "swap chute";

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

        HashSet<long> spare = SwapNetworks.IdSet(options.Spare, label);
        RidingPolicy riding = options.Riding == "refuse" ? RidingPolicy.Refuse : RidingPolicy.Skip;
        SwapGoal goal = new EndCleanupGoal(JobTool,
            new List<ICleanOperation> { new RemoveDeadChutes(spare, riding) });
        return SwapNetworks.Run(SwapNetworks.Request(family, goal, selection, call, label), SwapChuteOptions.Spec,
            call, named, unread);
    }
}

/// <summary>spare, only, older_than and root of a swap cable or pipe call: the options that narrow a tidy.</summary>
internal sealed class TidyOptions
{
    internal TidyOptions(IReadOnlyList<string>? spare, IReadOnlyList<string>? only, string? olderThan, string? root)
    {
        Spare = spare;
        Only = only;
        OlderThan = olderThan;
        Root = root;
    }

    public IReadOnlyList<string>? Spare { get; }

    public IReadOnlyList<string>? Only { get; }

    public string? OlderThan { get; }

    public string? Root { get; }

    internal TidyLimits Given => new TidyLimits(Spare != null, Only != null, OlderThan != null, Root != null);
}

/// <summary>
/// swap cable (catalogue swap.json): cable upgraded in place to heavy or super_heavy (heavy when tidy is left out), or
/// a network tidied.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class SwapCable
{
    // The jobs' names in their status and in inspect job.
    private const string TidyTool = "swap cable";

    internal static VerbOutcome Run(SwapCableOptions options) =>
        SwapNetworks.Tidyable(new CableFamily(), SwapCableOptions.Spec, "cable",
            new NetworkSwapCall(options.Selector, options.Materials, options.SkipUnmatched ?? false, options.Fields,
                options.Report, options.Queue ?? true, null),
            options.Into, options.Tidy,
            new TidyOptions(options.Spare, options.Only, options.OlderThan, options.Root),
            static into => new CableUpgrade(into == "super_heavy" ? Cable.Type.superHeavy : Cable.Type.heavy),
            TidyTool);
}

/// <summary>
/// swap pipe (catalogue swap.json): pipe made insulated in place (insulated when tidy is left out) or its burst pieces
/// made new (repair), or a network tidied, keeping each network's contents.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class SwapPipe
{
    // The jobs' names in their status and in inspect job.
    private const string TidyTool = "swap pipe";

    internal static VerbOutcome Run(SwapPipeOptions options) =>
        SwapNetworks.Tidyable(new PipeFamily(), SwapPipeOptions.Spec, "pipe",
            new NetworkSwapCall(options.Selector, options.Materials, options.SkipUnmatched ?? false, options.Fields,
                options.Report, options.Queue ?? true, options.AcknowledgeGasLost),
            options.Into, options.Tidy,
            new TidyOptions(options.Spare, options.Only, options.OlderThan, options.Root),
            static into => into == "repair" ? new PipeRepair() : new PipeUpgrade(),
            TidyTool);
}
