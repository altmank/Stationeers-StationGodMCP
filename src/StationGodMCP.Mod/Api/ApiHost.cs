#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// Every method by name, and one request in and one reply out. Requests run on the main thread, one at a time
/// (StationGodRequestDispatcher.ProcessPendingRequests). A refusal is the tool's ApiException; a game member this
/// build no longer has is game_changed; anything else is internal_error, logged.
/// </summary>
internal static class ApiHost
{
    private const int ElapsedDecimals = 2;

    internal static readonly Dictionary<string, Func<Args, object>> Methods =
        new Dictionary<string, Func<Args, object>>(StringComparer.Ordinal)
        {
            ["list_gateways"] = static args => ListGatewaysApi.Handle(args),
            ["list_devices"] = static args => ListDevicesApi.Handle(args),
            ["describe_device"] = static args => DescribeDeviceApi.Handle(args),
            ["read_logic"] = static args => ReadLogicApi.Handle(args),
            ["write_logic"] = static args => WriteLogicApi.Handle(args),
            ["read_logic_many"] = static args => ReadLogicManyApi.Handle(args),
            ["write_logic_many"] = static args => WriteLogicManyApi.Handle(args),
            ["read_memory"] = static args => ReadMemoryApi.Handle(args),
            ["write_memory"] = static args => WriteMemoryApi.Handle(args),
            ["inspect_slots"] = static args => InspectSlotsApi.Handle(args),
            ["network_snapshot"] = static args => NetworkSnapshotApi.Handle(args),
            ["get_ic_source"] = static args => GetIcSourceApi.Handle(args),
            ["set_ic_source"] = static args => SetIcSourceApi.Handle(args),
            ["get_ic_status"] = static args => GetIcStatusApi.Handle(args),
            ["control_ic_execution"] = static args => ControlIcExecutionApi.Handle(args),
            ["resolve_ic_selectors"] = static args => ResolveIcSelectorsApi.Handle(args),
            ["run_console_command"] = static args => RunConsoleCommandApi.Handle(args),
            ["read_console"] = static args => ReadConsoleApi.Handle(args),
            ["game_clock"] = static args => GameClockApi.Handle(args),
            ["find_items"] = static args => FindItemsApi.Handle(args),
            ["find_things"] = static args => FindThingsApi.Handle(args),
            ["label"] = static args => LabelApi.Handle(args),
            ["item_totals"] = static args => ItemTotalsApi.Handle(args),
            ["list_containers"] = static args => ListContainersApi.Handle(args),
            ["container_contents"] = static args => ContainerContentsApi.Handle(args),
            ["player_vitals"] = static args => PlayerVitalsApi.Handle(args),
            ["consumables"] = static args => ConsumablesApi.Handle(args),
            ["atmosphere_contents"] = static args => AtmosphereContentsApi.Handle(args),
            ["water_sources"] = static args => WaterSourcesApi.Handle(args),
            ["trader_contacts"] = static args => TraderContactsApi.Handle(args),
            ["dish_aim"] = static args => DishAimApi.Handle(args),
            ["trader_inventory"] = static args => TraderInventoryApi.Handle(args),
            ["plants"] = static args => PlantsApi.Handle(args),
            ["reagents"] = static args => ReagentsApi.Handle(args),
            ["planet"] = static args => PlanetApi.Handle(args),
            ["deep_miner_spots"] = static args => DeepMinerSpotsApi.Handle(args),
            ["set_ic_pins"] = static args => SetIcPinsApi.Handle(args),
            ["solar_aim"] = static args => SolarAimApi.Handle(args),
            ["thing_health"] = static args => ThingHealthApi.Handle(args),
            ["paint"] = static args => PaintApi.Handle(args),
            ["outer_frames"] = static args => OuterFramesApi.Handle(args),
            ["rooms"] = static args => RoomsApi.Handle(args),
            ["weather"] = static args => WeatherApi.Handle(args),
            ["ignition_risk"] = static args => IgnitionRiskApi.Handle(args),
            ["looking_at"] = static args => LookingAtApi.Handle(args),
            ["connections"] = static args => ConnectionsApi.Handle(args),
            ["plant_genes"] = static args => PlantGenesApi.Handle(args),
            ["move_gas"] = static args => MoveGasApi.Handle(args),
            ["landing_pads"] = static args => LandingPadsApi.Handle(args),
            ["rocket_status"] = static args => RocketStatusApi.Handle(args),
            ["rocket_forecast"] = static args => RocketForecastApi.Handle(args),
            ["rocket_flight_log"] = static args => RocketFlightLogApi.Handle(args),
            ["move_item"] = static args => MoveItemApi.Handle(args),
            ["upgrade_cables"] = static args => UpgradeCablesApi.Handle(args),
            ["upgrade_pipes"] = static args => UpgradePipesApi.Handle(args),
            ["clean_cables"] = static args => CleanCablesApi.Handle(args),
            ["clean_pipes"] = static args => CleanPipesApi.Handle(args),
            ["replace_walls"] = static args => ReplaceWallsApi.Handle(args),
            ["replace_frames"] = static args => ReplaceFramesApi.Handle(args),
            ["place_cables"] = static args => PlaceCablesApi.Handle(args),
            ["remove_cables"] = static args => RemoveCablesApi.Handle(args),
            ["place_pipes"] = static args => PlacePipesApi.Handle(args),
            ["remove_pipes"] = static args => RemovePipesApi.Handle(args),
            ["place_chutes"] = static args => PlaceChutesApi.Handle(args),
            ["remove_chutes"] = static args => RemoveChutesApi.Handle(args),
            ["place_structure"] = static args => PlaceStructureApi.Handle(args),
            ["describe_prefab"] = static args => DescribePrefabApi.Handle(args),
            ["wall_map"] = static args => WallMapApi.Handle(args),
            ["find_spot"] = static args => FindSpotApi.Handle(args),
            ["lint_layout"] = static args => LintLayoutApi.Handle(args),
            ["lint_rules"] = static args => LintRulesApi.Handle(args),
            ["check_replaceable"] = static args => CheckReplaceableApi.Handle(args),
            ["show_preview"] = static args => ShowPreviewApi.Handle(args),
            ["highlight"] = static args => HighlightApi.Handle(args),
            ["undo_job"] = static args => UndoJobApi.Handle(args),

            ["remove_structure"] = static args => RemoveStructureApi.Handle(args),
            ["grid_survey"] = static args => GridSurveyApi.Handle(args),
            ["plan_cable_route"] = static args => PlanRouteApi.Handle(args, new CableRunKind()),
            ["plan_pipe_route"] = static args => PlanRouteApi.Handle(args, new PipeRunKind()),
            ["plan_chute_route"] = static args => PlanRouteApi.Handle(args, new ChuteRunKind()),
            ["plan_removal"] = static args => RunApi.PlanRemoval(args),
            ["feed_paths"] = static args => FeedPathsApi.Handle(args),
            ["trader_buy"] = static args => TraderBuyApi.Handle(args),
            ["trader_sell"] = static args => TraderSellApi.Handle(args),
            ["paste_blueprint"] = static args => PasteBlueprintApi.Handle(args),
            ["vault_contents"] = static args => VaultContentsApi.Handle(args),
            ["vault_deposit"] = static args => VaultDepositApi.Handle(args),
            ["vault_withdraw"] = static args => VaultWithdrawApi.Handle(args),
            ["mod_info"] = static args => ModInfoApi.Handle(args)
        };

    internal static string Handle(string requestJson)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string? requestId = null;
        string? method = null;
        try
        {
            JObject request = ParseRequest(requestJson);
            requestId = request.Value<string>("id");
            method = request.Value<string>("method");
            if (method == null || !Methods.TryGetValue(method, out Func<Args, object> handler))
            {
                throw ApiErrors.Refused("method_not_found", $"Unknown StationGodMCP method '{method}'.");
            }

            ResolvedNetworks.Begin();
            GasHoldReply.Begin();
            object result = ResolvedNetworks.Attach(handler(new Args(request["params"] as JObject)),
                ResolvedNetworks.Take());
            result = GasHoldReply.Attach(result, GasHoldReply.Take());
            return Serialize(new ReplyView(requestId, result, MethodStats.Record(method, watch, true)));
        }
        catch (ApiException exception)
        {
            return Failed(requestId, method, watch, exception.Code, exception.Message);
        }
        catch (GameChangedException exception)
        {
            return Failed(requestId, method, watch, ApiErrors.GameChangedCode, exception.Message);
        }
        catch (Exception exception)
        {
            // The request boundary: a bug in any tool's game calls must answer the client, not break the pipe.
            StationGodMod.LogWarning($"API request failed: {exception}");
            return Failed(requestId, method, watch, "internal_error", exception.Message);
        }
    }

    // A key given twice would otherwise let the last one win silently (a write aimed at the wrong device).
    private static readonly JsonLoadSettings RequestLoad = new JsonLoadSettings
    {
        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
    };

    // A line Newtonsoft cannot read, e.g. a number past a double's range (1e309) or a key given twice, is a bad
    // request, not a bug in a tool: the reply says invalid_argument rather than internal_error.
    private static JObject ParseRequest(string requestJson)
    {
        try
        {
            return JObject.Parse(requestJson, RequestLoad);
        }
        catch (JsonReaderException exception)
        {
            throw ApiErrors.InvalidArgument(
                "The request is not JSON the mod accepts (every number must be finite and no key may be given " +
                $"twice): {exception.Message}");
        }
    }

    internal static string Serialize(object reply) => JsonConvert.SerializeObject(reply, ApiJson.Settings);

    private static string Failed(string? requestId, string? method, Stopwatch watch, string code, string message)
    {
        double elapsed = MethodStats.Record(method, watch, false);
        return Serialize(new ErrorReplyView(requestId, new ErrorView(code, message), elapsed));
    }

    /// <summary>The main-thread time spent on a request so far, rounded to 0.01 ms.</summary>
    internal static double Elapsed(Stopwatch watch) => Math.Round(watch.Elapsed.TotalMilliseconds, ElapsedDecimals);
}

/// <summary>Per-method counters since the mod loaded, for mod_info. In memory only; nothing is logged.</summary>
internal sealed class MethodStats
{
    private static readonly Dictionary<string, MethodStats> ByMethod =
        new Dictionary<string, MethodStats>(StringComparer.Ordinal);

    internal long Calls { get; private set; }

    internal long Errors { get; private set; }

    internal double TotalMilliseconds { get; private set; }

    internal double MaximumMilliseconds { get; private set; }

    /// <summary>
    /// One Stopwatch read, counted for a known method only, so a client sending made-up names cannot grow the table.
    /// </summary>
    internal static double Record(string? method, Stopwatch watch, bool ok)
    {
        double elapsed = ApiHost.Elapsed(watch);
        if (method == null || !ApiHost.Methods.ContainsKey(method))
        {
            return elapsed;
        }

        if (!ByMethod.TryGetValue(method, out MethodStats stats))
        {
            stats = new MethodStats();
            ByMethod[method] = stats;
        }

        stats.Calls++;
        stats.Errors += ok ? 0 : 1;
        stats.TotalMilliseconds += elapsed;
        stats.MaximumMilliseconds = Math.Max(stats.MaximumMilliseconds, elapsed);
        return elapsed;
    }

    internal static MethodStatsView ViewOf(string method)
    {
        return ByMethod.TryGetValue(method, out MethodStats stats)
            ? new MethodStatsView(method, stats.Calls, stats.Errors, stats.TotalMilliseconds,
                stats.MaximumMilliseconds)
            : new MethodStatsView(method, 0, 0, 0.0, 0.0);
    }
}
