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

        // One structure's step: a state or two of one or two items each, and a tool.
        shapes["advance_build_state"] = new[]
        {
            S<AdvanceBuildStateView>().List("AdvanceBuildStateView.Cost", 3).List("AdvanceBuildStateView.Tools", 1)
                .Absent("BuildCostView.PaidBy")
        };
        shapes["highlight"] = new[]
        {
            S<HighlightView>().List("HighlightView.Targets", 3).List("HighlightTargetView.Missing", 0)
                .List("HighlightView.Notes", 0)
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
        shapes["place_structure"] = new[]
        {
            PlaceReport(), S<JobQueuedView>().Absent("JobQueuedView.Preflight"), BuildPoll()
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
        shapes["run_console_command"] = new[]
        {
            S<ConsoleRunView>().List("ConsoleRunView.Arguments", 10).List("ConsoleRunView.Output", 3)
        };
        shapes["show_preview"] = new[]
        {
            PlaceReportInto(S<ShowPreviewView>()).List("ShowPreviewView.Notes", 0)
        };
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
        return shapes;
    }

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

    // clean_chutes' dry run at the default limit: removals and dead pieces kept each listed up to it. kept_pieces holds
    // only pieces on a path left with an open end (a piece serving a path is not listed). riding: an item rides in a
    // dead piece only on its way to the floor, and in a junction for one tick, so two at once is already rare. It
    // builds nothing, so no warning (materials_from_player) is listed.
}
