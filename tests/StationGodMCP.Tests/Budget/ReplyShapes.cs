#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using W = StationGodMCP.Tests.Budget.LargeWorld;
using D = StationGodMCP.Api.Shared.ReplyDefaults;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// Every method's default reply on the large world (LargeWorld), as ReplySynth builds it: which view, how long each
/// list is and why, which parts a default reply leaves out. This table is the heavy-payload inventory: a list that
/// grows with the world, a page or a request says so here, with the bound that keeps it in the budget. A method the
/// table does not cover must be in Exemptions with its reason, so a new tool fails ReplyBudgetTests until it is placed.
/// </summary>
internal static class ReplyShapes
{
    /// <summary>Members whose length is fixed by what they are, whatever the tool.</summary>
    internal static readonly ReplyShape Common = new ReplyShape(typeof(object))
        // Vectors and pairs written as arrays.
        .List("*.PadSizeTiles", 2).List("*.Direction", 3).List("DishView.Forward", 3).List("DishNowView.Forward", 3)
        .List("DishView.TransformUp", 3).List("DishAimView.Pointing", 3).List("DishAimView.Target", 3)
        .List("MountView.Axes", 2).List("MountView.Min", 2).List("MountView.Max", 2).List("MinerBeaconView.At", 3)
        .List("MinerProfileView.Quantity", 2).List("MinerProfileView.TimeS", 2)
        .List("BlueprintPasteStartedView.Anchor", 3).List("BandView.Ideal", 2).List("BandView.Survivable", 2)
        .List("PartPoseView.Offset", 3)
        // A piece's ends (at most 6, mostly 2) and an end's flow.
        .List("*.Ends", 2).List("*.ConnectedEnds", 2).List("RunFlowView.In", 1).List("RunFlowView.Out", 1)
        .List("RunCellView.Joins", 1)
        // sp and ra name two of 18 registers.
        .List("RegisterView.Aliases", 1)
        // A thing's holders from its own slot outwards: a suit in a locker, an ore in a backpack on a player.
        .List("*.HeldIn", 2)
        // A gas mixture's gases: the large world's tanks hold four or five.
        .List("HeldAtmosphereView.Contents", 5).List("RoomView.Gases", 5).List("RunPipeNetworkView.Gases", 5)
        .List("ReservoirView.Contents", 3).List("StartingAirView.Gases", 5).List("BurstView.Gases", 4)
        .List("PlanetView.Gases", 9).List("PlantAirView.Ratios", 8).List("FuelLineView.Mix", 3)
        // Material lines: a piece costs or gives back one or two kinds of item.
        // A build step takes one or two items and uses a tool.
        .List("BuildStateView.Next", 3)
        .List("*.Refund", 2).List("*.Cost", 2).List("*.Needed", 2).List("*.Stacks", 2).List("*.Used", 2)
        .List("PlacementView.Ports", 2).List("SurveyDeviceView.Ports", 3)
        // Problems and warnings a report carries on a typical large-world request.
        .List("*.Problems", 2).List("*.Warnings", 2)
        // The controllers writing a written logic type on a device: one chip or writer, rarely two.
        .List("*.DrivenBy", 2)
        .Build(typeof(JObject), () => new JObject { ["prefab"] = "StructureWallLight", ["at"] = new JArray(1, 2, 3) });

    internal static IReadOnlyDictionary<string, ReplyShape[]> ByMethod { get; } = Build();

    /// <summary>Methods held to no budget, each with why.</summary>
    internal static IReadOnlyDictionary<string, string> Exemptions { get; } = new Dictionary<string, string>
    {
        ["tool_info"] =
            "Answered by the MCP server from the catalogue, never by the game; CatalogueTextTests holds every node it " +
            "answers under 1.5 KB.",
    };

    private static ReplyShape S<T>() => new ReplyShape(typeof(T));

    private static Dictionary<string, ReplyShape[]> Build()
    {
        Dictionary<string, ReplyShape[]> shapes = new Dictionary<string, ReplyShape[]>(StringComparer.Ordinal);

        shapes["check_replaceable"] = new[]
        {
            S<CheckReplaceableView>().List("CheckReplaceableView.Results", W.TypicalBatch)
        };
        // One structure's step: a state or two of one or two items each, and a tool.
        shapes["advance_build_state"] = new[]
        {
            S<AdvanceBuildStateView>().List("AdvanceBuildStateView.Cost", 3).List("AdvanceBuildStateView.Tools", 1)
                .Absent("BuildCostView.PaidBy")
        };
        shapes["dish_aim"] = new[] { S<DishAimView>() };
        shapes["find_spot"] = new[]
        {
            S<FindSpotView>().List("FindSpotView.Planes", 2).List("FindSpotView.Spots", D.FindSpots)
                .List("SpotView.Conflicts", 1).List("FindSpotView.Reasons", 8)
        };
        shapes["highlight"] = new[]
        {
            S<HighlightView>().List("HighlightView.Targets", 3).List("HighlightTargetView.Missing", 0)
                .List("HighlightView.Notes", 0)
        };
        shapes["ignition_risk"] = new[]
        {
            S<IgnitionRiskView>().List("IgnitionRiskView.Items", 20).Absent("IgnitionRiskView.Prefabs")
        };
        shapes["lint_layout"] = new[]
        {
            S<LintLayoutView>().List("LintLayoutView.Counts", 12).List("LintLayoutView.Findings", D.LintFindings)
                .List("LintRuleSourceView.FromSave", 0).List("LintRuleSourceView.Disabled", 0)
                .List("LintRuleSourceView.Errors", 0)
        };
        shapes["lint_rules"] = new[]
        {
            // action list, the default: the rules in effect, each in brief.
            S<LintRulesView>().List("LintRulesView.Rules", W.LintRules).List("LintRuleView.On", 2)
                .List("LintRuleView.Let", 0).Absent("LintRulesView.Types", "LintRulesView.Sets", "LintRulesView.Functions",
                    "LintRulesView.Tests", "LintRulesView.Explained", "LintRuleView.Select", "LintRuleView.Assert",
                    "LintRuleView.Message", "LintRuleView.Other", "LintRuleView.LevelWhen")
                .List("LintRuleSourceView.FromSave", 0).List("LintRuleSourceView.Disabled", 0)
                .List("LintRuleSourceView.Errors", 0)
        };
        shapes["mod_info"] = new[]
        {
            // The game counters come with include_counters (about 515 bytes more).
            S<ModInfoView>().Absent("RuntimeView.JobHolds", "RuntimeView.PrintLog", "RuntimeView.LintChipPrograms",
                    "RuntimeView.BatchConsole", "RuntimeView.FrameMs", "RuntimeView.JobStores", "PrefabIndexView.Arrived",
                    "PrefabIndexView.Left")
                .List("ModInfoView.Methods", W.Methods).List("ModInfoView.Reflection", W.ReflectedMembers)
                .List("RuntimeView.Methods", D.RuntimeMethods).List("RuntimeView.CatalogueDrift", 0).List("RuntimeView.Connections", 2).List("ConnectionView.Methods", ConnectionView.MethodsShown)
                .List("ProfilingSummaryView.Top", ProfilingSummaryView.TopShown)
        };
        shapes["paste_blueprint"] = new[]
        {
            S<BlueprintPasteStatusView>(), S<BlueprintUndoView>(), S<BlueprintPasteStartedView>()
        };
        foreach (string tool in new[] { "place_cables", "place_pipes", "place_chutes", "remove_cables", "remove_pipes", "remove_chutes" })
        {
            shapes[tool] = RunTool(tool);
        }

        shapes["plan_removal"] = new[] { RunReport(S<RunReportView>(), "plan_removal") };
        foreach (string tool in new[] { "plan_cable_route", "plan_pipe_route", "plan_chute_route" })
        {
            shapes[tool] = new[]
            {
                RunReport(S<PlanRouteView>(), tool).List("RouteView.Waypoints", 6).List("RouteView.Removes", 0)
                    .List("RouteView.Air", 0).List("RouteView.Branches", 0).List("RouteAssumedView.Pieces", 0)
                    .List("RouteAssumedView.Others", 0).List("RouteAssumedView.Missing", 0)
                    .List("RouteAssumedView.InTheWay", 0).List("RouteView.RemovalRefund", 0)
                    .List("PlanRouteView.Notes", 2).Absent("PlanRouteView.DryRunSummary"),
                // summary: the dry run in short in place of the whole one.
                S<PlanRouteView>().Absent("PlanRouteView.DryRun").List("RouteView.Waypoints", 6)
                    .List("RouteView.Removes", 0).List("RouteView.Air", 0).List("RouteView.Branches", 0)
                    .List("RouteAssumedView.Pieces", 0).List("RouteAssumedView.Others", 0)
                    .List("RouteAssumedView.Missing", 0).List("RouteAssumedView.InTheWay", 0)
                    .List("RouteView.RemovalRefund", 0).List("PlanRouteView.Notes", 2)
                    .List("RunSummaryView.Problems", 2).List("RunSummaryView.Warnings", 4)
                    .List("RunSummaryView.Needed", 2)
            };
        }

        shapes["place_structure"] = new[]
        {
            PlaceReport(), S<JobQueuedView>().Absent("JobQueuedView.Preflight"), BuildPoll()
        };
        shapes["remove_structure"] = new[]
        {
            S<RemoveReportView>().List("RemoveReportView.Removals", 4).List("RemoveReportView.WillBurst", 0)
                .List("RefundPlanView.Skipped", 0).List("RefundPlanView.Destinations", 2)
                .Absent("RemoveReportView.Notes"),
            BuildPoll()
        };
        // Bounded by what exists: the scopes and timed methods, the methods called, the last 16 tick holds; scopes and
        // calls are cut by default (x-default-limits), the worst frame's pieces and calls by the view (Shown).
        shapes["profiling"] = new[]
        {
            S<ProfilingView>().List("ProfilingView.UntimedMethods", 0).List("ProfilingView.Scopes", 30)
                .List("ProfilingView.Calls", 20).List("WorstFrameView.Scopes", WorstFrameView.Shown).List("WorstFrameView.Calls", WorstFrameView.Shown)
                .List("TickHoldsView.Recent", StationGodMCP.Pure.Profiling.TickHolds.Kept)
        };
        shapes["read_console"] = new[]
        {
            S<ConsoleReadView>().List("ConsoleReadView.Lines", D.ConsoleLines)
                .Text("ConsoleLineView.Text", new string('t', 90))
        };
        shapes["rocket_forecast"] = new[]
        {
            S<RocketForecastView>().List("RocketForecastView.Route", 4).List("RocketForecastView.Uncharted", 0)
                // The model's fixed rules come with explain; a request's what-ifs, stop and warnings stay.
                .List("RocketForecastView.Assumptions", 3).List("RocketForecastView.Legs", 3)
                .List("RocketForecastView.Profiles", 4).List("ColumnView.Blockers", 0)
                .List("ForecastMiningView.Problems", 1)
        };
        shapes["rocket_mining_options"] = new[]
        {
            S<RocketMiningOptionsView>().List("MiningLoadoutView.Miners", 2).List("MiningLoadoutView.Collectors", 1)
                .List("MiningLoadoutView.Scanners", 1).List("MiningLoadoutView.Collects", 4)
                .List("RocketMiningOptionsView.Collectable", 6).List("RocketMiningOptionsView.NotCollectable", 6)
                .List("RocketMiningOptionsView.Sites", W.MiningSites).List("MiningSiteView.Materials", 3)
                .List("SiteMaterialView.PerUnit", 3).List("MiningSiteView.Machines", 3)
                // How mining works comes with explain; a note says why collectable_only kept nothing.
                .List("MachineYieldView.Problems", 1).List("RocketMiningOptionsView.Notes", 1)
        };
        shapes["run_console_command"] = new[]
        {
            S<ConsoleRunView>().List("ConsoleRunView.Arguments", 10).List("ConsoleRunView.Output", 3)
        };
        shapes["show_preview"] = new[]
        {
            PlaceReportInto(S<ShowPreviewView>()).List("ShowPreviewView.Notes", 0)
        };
        shapes["solar_aim"] = new[] { S<SolarFixedView>(), S<SolarTurnView>() };
        foreach (string tool in new[] { "trader_buy", "trader_sell" })
        {
            shapes[tool] = new[]
            {
                S<TradeView>().List("TradeView.Results", 4).Holds("TradeView.Results", typeof(TradedView))
                    .List("TradeView.Delivered", 4)
            };
        }

        shapes["trader_contacts"] = new[]
        {
            S<TraderContactsView>().List("TraderContactsView.Contacts", W.TraderContacts).List("TraderContactsView.Dishes", 2)
        };
        shapes["trader_inventory"] = new[]
        {
            S<TraderInventoryView>().List("TraderInventoryView.Contacts", W.TraderContacts).List("TraderStockView.Buys", 8)
                .List("TraderBuysView.Conditions", 1).List("TraderStockView.Sells", 8)
        };
        shapes["undo_job"] = new[]
        {
            // The plan with the removal's and the placement's dry runs (no piece runs: a structure was removed).
            PlaceReportInto(S<UndoJobView>()).List("UndoPlanView.Remove", 10).List("UndoPlanView.Restore", 10)
                .List("UndoPlanView.Diverged", 0).List("UndoPlanView.Notes", 2).List("UndoPlanView.UndoneBy", 0)
                .List("UndoJobView.PieceRuns", 0).Holds("UndoJobView.Removal", typeof(RemoveReportView))
                .Holds("UndoJobView.Placement", typeof(PlaceReportView)).List("RemoveReportView.Removals", 4)
                .List("RemoveReportView.WillBurst", 0).List("RefundPlanView.Skipped", 0)
                .List("RefundPlanView.Destinations", 2).Absent("RemoveReportView.Notes")
        };
        foreach (string tool in new[] { "upgrade_cables", "upgrade_pipes", "clean_cables", "clean_pipes" })
        {
            shapes[tool] = new[] { UpgradeReport() };
        }

        return shapes;
    }

    // A place_* or remove_* tool: its dry run (the default), a confirmed run's reply and a poll of its job.
    private static ReplyShape[] RunTool(string tool) => new[]
    {
        RunReport(S<RunReportView>(), tool),
        S<RunJobView>().Absent("RunJobView.Preflight", "RunJobView.FinalCheck", "RunJobView.Log", "RunJobView.Verification",
            "RunJobView.GasCheck", "RunJobView.Error"),
        S<RunJobView>().Absent("RunJobView.Preflight", "RunJobView.FinalCheck", "RunJobView.PreflightSummary",
                "RunJobView.Error", "RunLogView.StoppedAt", "RunLogView.RefundError")
            .List("RunLogView.Removed", 2).List("RunLogView.Changed", 4).List("RunLogView.PlacedPieces", W.RunCells)
            .List("RunLogView.Refunded", 2).List("RunLogView.CreatedIds", W.RunCells + 4)
            .List("RunLogView.CreatedByPart", 2).List("RunCreatedPartView.Ids", W.RunCells / 2)
            .List("RunVerificationView.NetworksNow", 2).Absent("RunJobView.GasCheck")
    };

    // A run report as a dry run answers it by default: notes and link lists left out, device lists as counts.
    private static ReplyShape RunReport(ReplyShape shape, string tool) => With(shape
        .List("RunReportView.Cells", Math.Min(W.RunCells, D.RunListed)).List("RunReportView.Removals", 2)
        .List("RefundPlanView.Skipped", 0).List("RefundPlanView.Destinations", 2)
        .List("RunReportView.NetworksBefore", 2)
        .Holds("RunReportView.NetworksBefore", tool.EndsWith("chutes", StringComparison.Ordinal) || tool == "plan_chute_route"
            ? typeof(RunChuteNetworkView)
            : tool.Contains("pipe") ? typeof(RunPipeNetworkView) : typeof(RunCableNetworkView))
        .Absent("RunCableNetworkView.Devices", "RunPipeNetworkView.Devices", "RunChuteNetworkView.Devices",
            "RunNetworkAfterView.Devices")
        .Absent("RunChuteNetworkView.Items")
        .List("RunReportView.NetworksAfter", 1).List("RunNetworkAfterView.NetworksBefore", 2)
        .Holds("RunNetworkAfterView.Guard", typeof(RunPowerAfterView))
        .List("RunReportView.WouldBridge", 0).List("RunReportView.WouldSplit", 0)
        .Absent("RunLinksView.Added", "RunLinksView.Lost", "RunReportView.Notes").List("RunLinksView.ModelDifferences", 0),
        // A new cell has no piece standing in it (existing), and only a chute cell has a flow.
        tool.Contains("chute") ? null : new[] { "RunCellView.Existing", "RunCellView.Flow" });

    private static ReplyShape With(ReplyShape shape, string[]? absent) => absent == null ? shape : shape.Absent(absent);

    private static ReplyShape PlaceReport() => PlaceReportInto(S<PlaceReportView>());

    // place_structure's dry run of one placement with its layout preview.
    private static ReplyShape PlaceReportInto(ReplyShape shape) => shape
        .List("PlaceReportView.Placements", 1).Absent("PlacementView.Orient", "FootprintView.LargeCells")
        .List("CellListView.Cells", 0).List("SectionsView.Walls", 1).List("PlacementLayoutView.Conflicts", 1)
        .List("PlacementLayoutView.PortChecks", 2).List("PlaceReportView.Materials", 2)
        .Absent("PlaceReportView.Notes");

    // A build job's poll: status and result (placed, used, verification), the reports already answered left out.
    private static ReplyShape BuildPoll() => S<BuildJobView>()
        .Absent("BuildJobView.Preflight", "BuildJobView.PreflightSummary", "BuildJobResult.FinalCheck",
            "BuildJobResult.RefundError", "BuildJobResult.StoppedAt", "BuildJobResult.Error", "BuildJobResult.GasCheck")
        .List("BuildJobResult.Placed", 4).List("BuildJobResult.Removed", 0).List("BuildJobResult.Refunded", 0)
        .List("BuildJobResult.Verification", 4).List("BuildCheckView.Issues", 0);

    private static ReplyShape UpgradeReport() => S<UpgradeReportView>()
        .List("UpgradeReportView.Pieces", Math.Min(W.CableNetworkMembers, D.UpgradeListed))
        .List("UpgradePieceView.MergedReferenceIds", 0).List("UpgradeReportView.ByPrefab", 6)
        .List("UpgradeReportView.KeptPieces", D.UpgradeListed).List("UpgradeReportView.UnmatchedPieces", 0)
        .List("UpgradeReportView.DeadEndPieces", 0).List("UpgradeReportView.Loops", 0)
        .Absent("UpgradeReportView.Redundant").Absent("UpgradeReportView.Riding").List("UpgradeReportView.Coils", 2)
        .List("RefundPlanView.Skipped", 0).List("RefundPlanView.Destinations", 2)
        .List("UpgradeReportView.Networks", 1).Holds("UpgradeReportView.Networks", typeof(CableNetworkReportView))
        .Absent("CableNetworkReportView.Devices")
        .List("UpgradeReportView.Devices", W.DataNetworkDevices).List("UpgradeDeviceView.NetworkIds", 1)
        .List("UpgradeConnectivityView.Added", 0).List("UpgradeConnectivityView.Lost", 0)
        .List("UpgradeConnectivityView.ModelDifferences", 0).List("UpgradeConnectivityView.Mounted", 4)
        .List("UpgradeConnectivityView.Devices", W.DataNetworkDevices).Absent("UpgradeReportView.Notes");

    // clean_chutes' dry run at the default limit: removals and dead pieces kept each listed up to it. kept_pieces holds
    // only pieces on a path left with an open end (a piece serving a path is not listed). riding: an item rides in a
    // dead piece only on its way to the floor, and in a junction for one tick, so two at once is already rare. It
    // builds nothing, so no warning (materials_from_player) is listed.
}
