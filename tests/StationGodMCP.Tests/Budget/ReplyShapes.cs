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
        .List("*.Refund", 2).List("*.Cost", 2).List("*.Needed", 2).List("*.Stacks", 2).List("*.Used", 2)
        .List("PlacementView.Ports", 2).List("SurveyDeviceView.Ports", 3)
        // Problems and warnings a report carries on a typical large-world request.
        .List("*.Problems", 2).List("*.Warnings", 2)
        .Build(typeof(JObject), () => new JObject { ["prefab"] = "StructureWallLight", ["at"] = new JArray(1, 2, 3) });

    internal static IReadOnlyDictionary<string, ReplyShape[]> ByMethod { get; } = Build();

    /// <summary>Methods held to no budget, each with why.</summary>
    internal static IReadOnlyDictionary<string, string> Exemptions { get; } = new Dictionary<string, string>
    {
        ["get_ic_source"] =
            "Its reply is the chip's source, as long as the chip's program (a 60 KB Lua hub): reading it is the call's " +
            "purpose. output_file writes it to a file and answers a pointer; get_ic_status reads everything else.",
        ["grid_survey"] =
            "A page lists everything in its cells, and a dense base holds 30 pieces in one 2 m cell, so a page runs past " +
            "the budget however few cells it has. It is read a page at a time when routing; the default page is 8 " +
            "cells with at most 40 pieces and 20 devices (truncated says what was cut), and kinds, network_ids, " +
            "sections and compact narrow it.",
        ["rocket_status"] =
            "Every rocket in full is about 7 KB (fuel lines, engines, power devices); four rockets pass the budget. " +
            "Left to the rocket tools, whose 1.13.0 compact mode and once-per-reply explanations are their call; " +
            "rocket_id asks for one rocket.",
        ["move_gas"] = "Its replies are built from JSON trees, not views; members of from/to are the request's atmospheres.",
        ["sample_logic"] = "A stream: its reply is the changes the caller asked to sample, built from JSON trees.",
    };

    private static ReplyShape S<T>() => new ReplyShape(typeof(T));

    private static Dictionary<string, ReplyShape[]> Build()
    {
        Dictionary<string, ReplyShape[]> shapes = new Dictionary<string, ReplyShape[]>(StringComparer.Ordinal);

        shapes["atmosphere_contents"] = new[]
        {
            // A device's atmospheres: its own, its pipe networks (a filtration unit has three), slot canisters.
            S<AtmosphereContentsView>().List("AtmosphereContentsView.Atmospheres", 4)
        };
        shapes["check_replaceable"] = new[]
        {
            S<CheckReplaceableView>().List("CheckReplaceableView.Results", W.TypicalBatch)
        };
        shapes["connections"] = new[]
        {
            S<ConnectionsView>().List("ConnectionsView.Ends", 4).List("ConnectionEndView.Connected", 2),
            S<NetworkMembersView>().List("NetworkMembersView.Members", Math.Min(W.CableNetworkMembers, D.ConnectionMembers))
                .Absent("NetworkMemberView.OpenEnds").Holds("NetworkMembersView.Summary", typeof(CableSummaryView))
        };
        shapes["consumables"] = new[]
        {
            S<ConsumablesView>().List("ConsumablesView.Food", W.Foods).List("ConsumablesView.Drinks", W.Drinks)
                .List("ConsumablesView.Packages", 20).List("PackageView.Contents", 1)
                .List("ConsumablesView.NotCounted", 10)
        };
        shapes["container_contents"] = new[]
        {
            // A player: 2 hands, suit, backpack (12 slots) of stacks, a tool belt (8); depth 3.
            // Only containers (suit, backpack, belt) have slots of their own: one level-two list per three occupants.
            S<ContainerContentsView>().List("ContainerContentsView.Slots", 10).List("OccupantView.Slots", 1)
        };
        shapes["control_ic_execution"] = new[] { S<IcControlView>().Absent("IcControlView.Lua") };
        shapes["deep_miner_spots"] = new[]
        {
            S<DeepMinerSpotsView>().List("MinerPointView.Regions", 2).List("DeepMinerSpotsView.Profiles", 12)
                .List("MinerProfileView.Reagents", 3).List("MinerProfileView.Regions", 2)
                .List("MinerSearchView.Ores", 1).List("MinerSearchView.Profiles", 2)
                .List("DeepMinerSpotsView.Spots", D.DeepMinerSpots).List("MinerSpotView.Regions", 2)
                .List("DeepMinerSpotsView.Beacons", D.DeepMinerSpots)
        };
        shapes["describe_device"] = new[]
        {
            // A big machine reads and writes about 45 logic types; an uplink's choices are the world's downlinks.
            S<DescribeDeviceView>().List("DescribeDeviceView.LogicTypes", 45).List("UmbilicalSearchView.Stops", 3)
                .List("UplinkView.Choices", W.Rockets)
        };
        shapes["describe_prefab"] = new[]
        {
            S<DescribePrefabView>().List("DescribePrefabView.AllowedRotations", 24).List("DescribePrefabView.SmallCells", 16)
                .List("DescribePrefabView.Ports", 4)
        };
        shapes["dish_aim"] = new[] { S<DishAimView>() };
        shapes["feed_paths"] = new[]
        {
            S<FeedPathsView>().List("FeedPathsView.Devices", W.DataNetworkDevices).List("FeedDeviceView.Rooms", 2)
                .List("FeedDeviceView.Through", 1).List("FeedPathsView.Rooms", 12).List("FeedRoomView.Entries", 2)
                .List("FeedRoomView.EntryAt", 2).List("FeedPathsView.Unreached", 2)
        };
        shapes["find_items"] = new[]
        {
            S<FindItemsView>().List("FindItemsView.Items", D.FindItems).Holds("FindItemsView.Items", typeof(ItemView))
        };
        shapes["find_spot"] = new[]
        {
            S<FindSpotView>().List("FindSpotView.Planes", 2).List("FindSpotView.Spots", D.FindSpots)
                .List("SpotView.Conflicts", 1).List("FindSpotView.Reasons", 8)
        };
        shapes["find_things"] = new[]
        {
            S<FindThingsView>().List("FindThingsView.Things", D.FindThings).Absent("FoundThingView.Made")
        };
        shapes["game_clock"] = new[] { S<GameClockView>() };
        shapes["get_ic_status"] = new[]
        {
            S<IcNoChipView>().List("IcNoChipView.Pins", 6),
            // IC10: 18 registers, a stack window, the program's aliases, defines and jump tags.
            S<IcStatusView>().List("IcStatusView.Pins", 6).Absent("IcStatusView.Source", "IcStatusView.Lua")
                .List("IcRuntimeView.Registers", 18).List("StackWindowView.Values", D.IcStackWindow)
                .List("IcRuntimeView.Aliases", 12).List("IcRuntimeView.Defines", 12).List("IcRuntimeView.JumpTags", 10),
            // Lua: the hub chip, its log tail; no IC10 runtime.
            S<IcStatusView>().List("IcStatusView.Pins", 6).Absent("IcStatusView.Source", "IcStatusView.Runtime")
                .List("LuaLogView.Lines", D.LuaLogLines).Text("LuaLogView.Lines[]", new string('l', 80))
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
        shapes["inspect_slots"] = new[]
        {
            // A machine: up to 12 slots, each with every readable slot logic value.
            S<InspectSlotsView>().List("InspectSlotsView.Slots", 12).List("SlotDetailView.SpecificPrefabHashes", 0)
                .List("SlotDetailView.LogicValues", 12)
        };
        shapes["item_totals"] = new[]
        {
            S<ItemTotalsView>().List("ItemTotalsView.Totals", Math.Min(W.ItemTypes, D.ItemTotals))
                .List("PrefabTotalView.TopHolders", D.ItemTotalHolders)
        };
        shapes["label"] = new[]
        {
            S<LabelledView>(),
            S<BatchResultView>().List("BatchResultView.Results", W.TypicalBatch).Holds("BatchResultView.Results", typeof(LabelledView))
        };
        shapes["landing_pads"] = new[]
        {
            S<LandingPadsView>().List("LandingPadsView.Pads", W.LandingPads).List("LandingPadView.FitsByShip", 6)
                .List("LandingPadView.Contacts", W.TraderContacts)
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
        shapes["list_containers"] = new[]
        {
            S<ListContainersView>().List("ListContainersView.Containers", D.Containers).List("ContainerView.Items", 6)
        };
        shapes["list_devices"] = new[] { S<DevicesView>().List("DevicesView.Devices", W.Devices) };
        shapes["list_gateways"] = new[] { S<GatewaysView>().List("GatewaysView.Gateways", W.Gateways + 1) };
        shapes["looking_at"] = new[] { S<LookingAtView>() };
        shapes["mod_info"] = new[]
        {
            S<ModInfoView>().List("ModInfoView.Methods", W.Methods).List("ModInfoView.Reflection", W.ReflectedMembers)
                .List("RuntimeView.Methods", D.RuntimeMethods).List("RuntimeView.CatalogueDrift", 0).List("RuntimeView.Connections", 2)
        };
        shapes["move_item"] = new[]
        {
            S<ItemMovedView>(),
            S<BatchResultView>().List("BatchResultView.Results", W.TypicalBatch).Holds("BatchResultView.Results", typeof(ItemMovedView))
        };
        shapes["network_snapshot"] = new[]
        {
            S<NetworkSnapshotView>().List("NetworkSnapshotView.Devices", Math.Min(W.DataNetworkDevices, D.SnapshotDevices))
                .List("DeviceSnapshotView.LogicValues", 30)
        };
        shapes["outer_frames"] = new[]
        {
            S<OuterFramesView>().List("OuterFramesView.Frames", Math.Min(W.OuterFrames, D.OuterFrames))
                .List("FrameView.ExposedFaces", 1)
        };
        shapes["paint"] = new[]
        {
            S<PaintColorsView>().List("PaintColorsView.Colors", 20),
            S<BatchResultView>().List("BatchResultView.Results", W.TypicalBatch).Holds("BatchResultView.Results", typeof(PaintedView))
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
                    .List("PlanRouteView.Notes", 2)
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
        shapes["planet"] = new[]
        {
            S<PlanetNotLoadedView>(), S<PlanetView>()
        };
        shapes["plant_genes"] = new[]
        {
            // Every gene (19); meanings only with include_notes.
            S<PlantGenesView>().List("PlantGenesView.Genes", 19).Absent("GeneView.Meaning"),
            // A batch answers every gene of each plant named: two seeds compared.
            S<BatchResultView>().List("BatchResultView.Results", 2).Holds("BatchResultView.Results", typeof(PlantGenesItemView))
                .List("PlantGenesView.Genes", 19).List("PlantGenesItemView.Genes", 19).Absent("GeneView.Meaning"),
            S<GenesWrittenView>().List("GenesWrittenView.Results", 12).Holds("GenesWrittenView.Results", typeof(GeneWrittenView))
        };
        shapes["plants"] = new[]
        {
            // Every plant in short; one plant (reference_id) in full.
            S<PlantsView>().List("PlantsView.Plants", W.Plants).Holds("PlantsView.Plants", typeof(PlantBriefView)),
            S<PlantsView>().List("PlantsView.Plants", 1).Holds("PlantsView.Plants", typeof(PlantView))
                .List("PlantView.Stages", 6)
                .List("PlantView.ActiveStates", 1).List("PlantView.Conditions", 6).List("PlantNeedsView.TakesIn", 1)
                .List("PlantNeedsView.GivesOut", 1).List("PlantNeedsView.Harmful", 2).List("PlantView.Genes", 8)
        };
        shapes["player_vitals"] = new[] { S<PlayerVitalsView>() };
        shapes["read_console"] = new[]
        {
            S<ConsoleReadView>().List("ConsoleReadView.Lines", D.ConsoleLines)
                .Text("ConsoleLineView.Text", new string('t', 90))
        };
        shapes["read_devices"] = new[]
        {
            // A poll of the devices the caller names, six logic values each; the reply holds what was asked.
            S<ReadDevicesView>().List("ReadDevicesView.Results", W.TypicalBatch)
                .Holds("ReadDevicesView.Results", typeof(DeviceReadItemView)).List("DeviceReadItemView.Logic", 6)
                .List("DeviceReadItemView.LogicErrors", 0).List("DeviceReadItemView.Errors", 0)
                .Absent("DeviceReadItemView.Slots", "DeviceReadItemView.Atmosphere", "DeviceReadItemView.Reagents"),
            // One device read whole: slots, its atmosphere, its reagents.
            S<ReadDevicesView>().List("ReadDevicesView.Results", 1)
                .Holds("ReadDevicesView.Results", typeof(DeviceReadItemView)).List("DeviceReadItemView.Logic", 6)
                .List("DeviceReadItemView.LogicErrors", 0).List("DeviceReadItemView.Errors", 0)
                .List("DeviceReadItemView.Slots", 2).List("SlotReadView.Logic", 3).List("SlotReadView.LogicErrors", 0)
                .List("AtmosphereReadView.Contents", 5).List("ReagentsReadView.Reagents", 4)
        };
        shapes["read_logic"] = new[] { S<LogicReadView>() };
        shapes["read_logic_many"] = new[]
        {
            S<LogicBatchView>().List("LogicBatchView.Results", W.TypicalBatch).Holds("LogicBatchView.Results", typeof(LogicReadItemView))
        };
        shapes["read_memory"] = new[] { S<MemoryReadView>().List("MemoryReadView.Values", D.IcStackWindow) };
        shapes["reagents"] = new[] { S<ReagentsView>().List("ReagentsView.Reagents", 12) };
        foreach (string tool in new[] { "replace_frames", "replace_walls" })
        {
            shapes[tool] = new[] { StructureSwapReport() };
        }

        shapes["resolve_ic_selectors"] = new[]
        {
            S<IcSelectorsView>().List("IcSelectorsView.Pins", 6).List("IcSelectorsView.Aliases", 12)
                .List("IcSelectorsView.StableSelectors", W.DataNetworkDevices)
        };
        shapes["rocket_flight_log"] = new[]
        {
            S<FlightLogView>().List("FlightLogView.Events", 10).List("FlightLogView.Rows", D.FlightLogRows),
            S<FlightLogClearedView>(),
            S<FlightLogListView>().List("FlightLogListView.Logs", W.Rockets).List("FlightLogView.Events", 10)
                .List("FlightLogView.Rows", 0)
        };
        shapes["rocket_forecast"] = new[]
        {
            S<RocketForecastView>().List("RocketForecastView.Route", 4).List("RocketForecastView.Uncharted", 0)
                .List("RocketForecastView.Assumptions", 4).List("RocketForecastView.Legs", 3)
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
                .List("MachineYieldView.Problems", 1).List("RocketMiningOptionsView.Notes", 2)
        };
        shapes["rooms"] = new[]
        {
            S<RoomsView>().List("RoomsView.Rooms", W.Rooms).Absent("RoomView.Devices", "RoomView.Cells")
        };
        shapes["run_console_command"] = new[]
        {
            S<ConsoleRunView>().List("ConsoleRunView.Arguments", 10).List("ConsoleRunView.Output", 3)
        };
        shapes["set_ic_pins"] = new[] { S<SetIcPinsView>().List("SetIcPinsView.Changes", 2).List("SetIcPinsView.Pins", 6) };
        shapes["set_ic_source"] = new[]
        {
            S<IcSourceSetView>().Absent("IcSourceSetView.Source").List("IcSourceSetView.Warnings", 1)
                .List("LuaLogView.Lines", 0)
        };
        shapes["set_uplink"] = new[] { S<UplinkSetView>().List("UplinkView.Choices", W.Rockets) };
        shapes["show_preview"] = new[]
        {
            PlaceReportInto(S<ShowPreviewView>()).List("ShowPreviewView.Notes", 0)
        };
        shapes["solar_aim"] = new[] { S<SolarFixedView>(), S<SolarTurnView>() };
        shapes["thing_health"] = new[]
        {
            S<HealthView>().List("HealthView.Networks", 1),
            S<BatchResultView>().List("BatchResultView.Results", W.TypicalBatch).Holds("BatchResultView.Results", typeof(HealthItemView))
                .List("HealthView.Networks", 1).List("HealthItemView.Networks", 1),
            S<HealthNetworkView>().List("HealthNetworkView.Things", Math.Min(W.CableNetworkMembers, D.HealthThings))
                .List("HealthView.Networks", 1),
            S<HealthScanView>().List("HealthScanView.Things", D.HealthThings).List("HealthView.Networks", 1)
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
        foreach (string tool in new[] { "upgrade_cables", "upgrade_pipes", "clean_cables", "clean_pipes" })
        {
            shapes[tool] = new[] { UpgradeReport() };
        }

        shapes["vault_contents"] = new[]
        {
            S<VaultContentsView>().List("VaultContentsView.Vaults", W.Vaults).List("VaultContentsEntryView.Stock", 20)
                .List("VaultContentsView.RemoteVaults", 2)
        };
        shapes["vault_deposit"] = new[]
        {
            S<VaultDepositView>().List("BatchResultView.Results", W.TypicalBatch)
                .Holds("BatchResultView.Results", typeof(DepositedView)).List("VaultDepositView.Stock", 6)
        };
        shapes["vault_withdraw"] = new[]
        {
            S<VaultWithdrawView>().List("VaultWithdrawView.Placed", 2).List("VaultWithdrawView.Stock", 1)
        };
        shapes["wall_map"] = new[]
        {
            // radius 4 m default: a 17 x 17 character map.
            S<WallMapView>().List("WallMapView.Rows", 18).Text("WallMapView.Rows[]", new string('W', 17))
                .List("WallMapView.Sections", 16).List("WallMapView.Things", 8).List("WallMapView.FreeRects", 0)
        };
        shapes["water_sources"] = new[]
        {
            S<WaterSourcesView>().List("WaterSourcesView.Sources", W.WaterSources)
        };
        shapes["weather"] = new[] { S<WeatherView>().List("WeatherView.Events", 6) };
        shapes["write_logic"] = new[] { S<LogicWriteView>() };
        shapes["write_logic_many"] = new[]
        {
            S<LogicBatchView>().List("LogicBatchView.Results", W.TypicalBatch).Holds("LogicBatchView.Results", typeof(LogicWriteItemView))
        };
        shapes["write_memory"] = new[]
        {
            S<MemoryWriteView>().List("MemoryWriteView.RequestedValues", 8).List("MemoryWriteView.PreviousValues", 8)
                .List("MemoryWriteView.CurrentValues", 8)
        };

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

    private static ReplyShape StructureSwapReport() => S<StructureSwapReportView>()
        .List("StructureSwapReportView.Pieces", D.SwapListed).List("StructurePieceView.Faces", 1)
        .List("StructureFaceView.Cells", 2).List("StructurePieceView.RoomIds", 1)
        .List("StructurePieceView.Materials", 2).List("StructureSwapReportView.ByPrefab", 2)
        .List("StructureSwapReportView.KeptPieces", 0).List("StructureSwapReportView.UnmatchedPieces", 0)
        .List("StructureSwapReportView.Materials", 2).List("RefundPlanView.Skipped", 0)
        .List("RefundPlanView.Destinations", 2).List("StructureSwapReportView.Rooms", 4)
        .Absent("StructureSwapReportView.Notes");

    private static ReplyShape UpgradeReport() => S<UpgradeReportView>()
        .List("UpgradeReportView.Pieces", Math.Min(W.CableNetworkMembers, D.UpgradeListed))
        .List("UpgradePieceView.MergedReferenceIds", 0).List("UpgradeReportView.ByPrefab", 6)
        .List("UpgradeReportView.KeptPieces", D.UpgradeListed).List("UpgradeReportView.UnmatchedPieces", 0)
        .List("UpgradeReportView.DeadEndPieces", 0).List("UpgradeReportView.Loops", 0)
        .Absent("UpgradeReportView.Redundant").List("UpgradeReportView.Coils", 2)
        .List("RefundPlanView.Skipped", 0).List("RefundPlanView.Destinations", 2)
        .List("UpgradeReportView.Networks", 1).Holds("UpgradeReportView.Networks", typeof(CableNetworkReportView))
        .Absent("CableNetworkReportView.Devices")
        .List("UpgradeReportView.Devices", W.DataNetworkDevices).List("UpgradeDeviceView.NetworkIds", 1)
        .List("UpgradeConnectivityView.Added", 0).List("UpgradeConnectivityView.Lost", 0)
        .List("UpgradeConnectivityView.ModelDifferences", 0).List("UpgradeConnectivityView.Mounted", 4)
        .List("UpgradeConnectivityView.Devices", W.DataNetworkDevices).Absent("UpgradeReportView.Notes");
}
