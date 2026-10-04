#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api;

/// <summary>
/// Every method by name, and one call in and one reply out. Calls run on the main thread, one at a time
/// (StationGodRequestDispatcher.RunCall). A refusal is the tool's ApiException; a game member this
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
            ["read_devices"] = static args => ReadDevicesApi.Handle(args),
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
            ["set_uplink"] = static args => SetUplinkApi.Handle(args),
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
            ["rocket_mining_options"] = static args => RocketMiningOptionsApi.Handle(args),
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

    /// <summary>
    /// One call: its reply message as JSON text (type reply, shaped, elapsed_ms, queue_ms, frame). Top-level argument
    /// names are checked here (with StrictArguments the full check already ran on the connection's thread); the result
    /// is shaped by the call's
    /// shape, and a reply larger than max_reply_bytes (or the shape's max_bytes) is answered reply_too_large.
    /// </summary>
    internal static HandledRequest HandleCall(CallRequest call, double queueWaitMs, long frame)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Answer answer = Run(call.Id, call.Method, call.Params, call.Shape, watch);
        double queueMs = Math.Round(queueWaitMs, ElapsedDecimals);
        long serializeStarted = Stopwatch.GetTimestamp();
        string json;
        try
        {
            json = SerializeCall(ref answer, queueMs, frame);
        }
        catch (Exception exception)
        {
            // The serializer failing on a tool's reply object: answered internal_error, as any tool failure is.
            StationGodMod.LogWarning($"API reply could not be serialised: {exception}");
            answer = answer.Failed(new ErrorView("internal_error", exception.Message));
            json = SerializeCall(ref answer, queueMs, frame);
        }

        double serializeMs = MillisecondsSince(serializeStarted);
        MethodStats.Record(answer.Method, answer.Ok, answer.HandlerMs, serializeMs, queueWaitMs);
        return new HandledRequest(json, MethodStats.Counted(answer.Method));
    }

    /// <summary>The reply message: always written through the shaping writer, which also counts the lists.</summary>
    private static string SerializeCall(ref Answer answer, double queueMs, long frame)
    {
        if (!answer.Ok)
        {
            return Serialize(CallReplyView.Failed(answer.RequestId, answer.Error!, answer.HandlerMs, queueMs, frame));
        }

        ShapeRequest shape = answer.Shape ?? ShapeRequest.None;
        ShapedText shaped = ApiJson.WriteReply(
            CallReplyView.Of(answer.RequestId, answer.Result!, answer.Shape != null, answer.HandlerMs, queueMs, frame), shape,
            answer.Truncations);
        int bytes = Encoding.UTF8.GetByteCount(shaped.Json);
        int limit = Math.Min(shape.MaxBytes ?? MaxReplyBytes, MaxReplyBytes);
        if (bytes > limit)
        {
            answer = answer.Failed(ReplyTooLarge.Of(bytes, limit, shaped.Outcome));
            return Serialize(CallReplyView.Failed(answer.RequestId, answer.Error!, answer.HandlerMs, queueMs, frame));
        }

        return shaped.Json;
    }

    /// <summary>The largest reply sent (limits.max_reply_bytes).</summary>
    internal const int MaxReplyBytes = 16777216;

    private static Answer Run(string? requestId, string? method, JObject? parameters, ShapeRequest? shape, Stopwatch watch)
    {
        try
        {
            DeclaredArguments declared = Declared.Value.Arguments ??
                                         throw ApiErrors.Refused("internal_error", Declared.Value.Problem!);
            if (method == null || !Methods.TryGetValue(method, out Func<Args, object> handler))
            {
                throw ApiErrors.Refused("method_not_found", $"Unknown StationGodMCP method '{method}'.");
            }

            declared.Check(method, parameters);
            ArgumentNames? names = declared.NamesOf(method);
            ResolvedNetworks.Begin();
            GasHoldReply.Begin();
            Pure.Shaping.Truncations.Begin();
            ShapeRequest replyShape = shape ?? ShapeRequest.None;
            object handled =
                handler(names != null ? new Args(parameters, names, replyShape) : new Args(parameters, replyShape));
            TruncatingViews.Note(handled);
            object result = ResolvedNetworks.Attach(handled, ResolvedNetworks.Take());
            result = GasHoldReply.Attach(result, GasHoldReply.Take());
            return Answer.Success(requestId, method, result, Elapsed(watch), shape, Pure.Shaping.Truncations.Take());
        }
        catch (ApiException exception)
        {
            return Answer.Failure(requestId, method, new ErrorView(exception.Code, exception.Message), Elapsed(watch));
        }
        catch (GameChangedException exception)
        {
            return Answer.Failure(requestId, method, new ErrorView(ApiErrors.GameChangedCode, exception.Message), Elapsed(watch));
        }
        catch (Exception exception)
        {
            // The request boundary: a bug in any tool's game calls must answer the client, not break the pipe.
            StationGodMod.LogWarning($"API request failed: {exception}");
            return Answer.Failure(requestId, method, new ErrorView("internal_error", exception.Message), Elapsed(watch));
        }
    }

    private const string CatalogueResource = "StationGodMCP.catalogue.json";

    // The method catalogue, read once from the DLL. One that does not load is fatal for requests: every call is
    // answered internal_error naming the problem, rather than run unchecked.
    private static readonly Lazy<LoadedCatalogue> Declared = new Lazy<LoadedCatalogue>(LoadCatalogue);

    /// <summary>Loads the embedded catalogue now (at mod start), so the log says at once whether it loaded.</summary>
    internal static void Prepare() => _ = Declared.Value;

    /// <summary>The embedded catalogue file (its text and hash for welcome); null when it did not load.</summary>
    internal static CatalogueFile? CatalogueFile => Declared.Value.File;

    private static LoadedCatalogue LoadCatalogue()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CatalogueResource);
            if (stream == null)
            {
                return LoadedCatalogue.Failed($"The mod's DLL has no {CatalogueResource}: the build is broken.");
            }

            using MemoryStream bytes = new MemoryStream();
            stream.CopyTo(bytes);
            byte[] raw = bytes.ToArray();
            Catalogue catalogue = Catalogue.Load(new System.Text.UTF8Encoding(false).GetString(raw));
            ArgumentDrift.FirstMiss = static (method, name) =>
                StationGodMod.LogWarning($"Catalogue drift: {method} read the argument '{name}', which its catalogue entry does not declare.");
            StationGodMod.Log($"Catalogue loaded: {catalogue.MethodCount} methods, mod version {catalogue.ModVersion}.");
            return LoadedCatalogue.Of(new DeclaredArguments(catalogue), new CatalogueFile(raw, catalogue));
        }
        catch (CatalogueException exception)
        {
            return LoadedCatalogue.Failed($"The method catalogue did not load: {exception.Message}");
        }
    }

    private sealed class LoadedCatalogue
    {
        private LoadedCatalogue(DeclaredArguments? arguments, CatalogueFile? file, string? problem)
        {
            Arguments = arguments;
            File = file;
            Problem = problem;
            if (problem != null)
            {
                StationGodMod.LogError(problem + " Every request is answered internal_error.");
            }
        }

        internal DeclaredArguments? Arguments { get; }

        internal CatalogueFile? File { get; }

        internal string? Problem { get; }

        internal static LoadedCatalogue Of(DeclaredArguments arguments, CatalogueFile file) =>
            new LoadedCatalogue(arguments, file, null);

        internal static LoadedCatalogue Failed(string problem) => new LoadedCatalogue(null, null, problem);
    }

    /// <summary>A reply as JSON text, through the one shared serializer: main thread only.</summary>
    internal static string Serialize(object reply) => ApiJson.WriteShared(reply);

    /// <summary>A reply as JSON text from a listener thread (game_timeout, the TCP handshake).</summary>
    internal static string SerializeOffMainThread(object reply) => ApiJson.WriteFresh(reply);

    private static double MillisecondsSince(long timestamp) =>
        (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The main-thread time spent on a request so far, rounded to 0.01 ms.</summary>
    internal static double Elapsed(Stopwatch watch) => Math.Round(watch.Elapsed.TotalMilliseconds, ElapsedDecimals);
}

/// <summary>A request's outcome before serialisation: its result or error, and what MethodStats records of it.</summary>
internal sealed class Answer
{
    private Answer(string? requestId, string? method, bool ok, object? result, ErrorView? error, double handlerMs,
        ShapeRequest? shape, List<Truncation>? truncations = null)
    {
        Truncations = truncations ?? new List<Truncation>();
        RequestId = requestId;
        Method = method;
        Ok = ok;
        Result = result;
        Error = error;
        HandlerMs = handlerMs;
        Shape = shape;
    }

    internal string? RequestId { get; }

    internal string? Method { get; }

    internal bool Ok { get; }

    /// <summary>The tool's reply object when Ok.</summary>
    internal object? Result { get; }

    /// <summary>The error when not Ok.</summary>
    internal ErrorView? Error { get; }

    internal double HandlerMs { get; }

    /// <summary>The request's shape, applied when the reply is serialised; errors are never shaped.</summary>
    internal ShapeRequest? Shape { get; }

    /// <summary>The lists the handler held back entries of (Pure.Shaping.Truncations).</summary>
    internal List<Truncation> Truncations { get; }

    internal static Answer Success(string? requestId, string? method, object result, double handlerMs, ShapeRequest? shape,
        List<Truncation>? truncations = null) =>
        new Answer(requestId, method, true, result, null, handlerMs, shape, truncations);

    internal static Answer Failure(string? requestId, string? method, ErrorView error, double handlerMs) =>
        new Answer(requestId, method, false, null, error, handlerMs, null);

    internal Answer Failed(ErrorView error) => Failure(RequestId, Method, error, HandlerMs);
}

/// <summary>A handled request's reply text, and its method when that is a known one (null otherwise).</summary>
internal sealed class HandledRequest
{
    internal HandledRequest(string json, string? method)
    {
        Json = json;
        Method = method;
    }

    internal string Json { get; }

    internal string? Method { get; }
}

/// <summary>
/// Per-method counters since the mod loaded, for mod_info (methods and runtime). In memory only; nothing is logged.
/// Counted for a known method only, so a client sending made-up names cannot grow the table. Thread-safe
/// (MethodTimings): the main thread records times, the listener thread the reply size.
/// </summary>
internal static class MethodStats
{
    private static readonly MethodTimings Timings = new MethodTimings();

    /// <summary>The method when it is one ApiHost knows, else null.</summary>
    internal static string? Counted(string? method) =>
        method != null && ApiHost.Methods.ContainsKey(method) ? method : null;

    internal static void Record(string? method, bool ok, double handlerMs, double serializeMs, double queueWaitMs)
    {
        string? known = Counted(method);
        if (known != null)
        {
            Timings.Record(known, ok, handlerMs, serializeMs, queueWaitMs);
        }
    }

    /// <summary>The reply's size, from the listener thread once it has the text.</summary>
    internal static void RecordReply(string? method, long bytes)
    {
        string? known = Counted(method);
        if (known != null)
        {
            Timings.RecordReply(known, bytes);
        }
    }

    /// <summary>mod_info's methods entry: calls, errors and the handler's time, as before 1.9.1.</summary>
    internal static MethodStatsView ViewOf(string method)
    {
        MethodTiming timing = Timings.Snapshot(method);
        return new MethodStatsView(method, timing.Calls, timing.Errors, timing.Handler.Total, timing.Handler.Maximum);
    }

    internal static List<MethodTiming> Called() => Timings.Called();
}
