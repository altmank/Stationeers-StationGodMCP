#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>A problem (the run is refused) or a warning of place_structure or remove_structure.</summary>
internal sealed class BuildIssueView
{
    internal BuildIssueView(string code, string message, int? index = null, ThingId? referenceId = null)
    {
        Code = code;
        Message = message;
        Index = index;
        ReferenceId = referenceId;
    }

    public string Code { get; }

    public string Message { get; }

    /// <summary>The placement's or removal's index in the request; null for the run as a whole.</summary>
    public int? Index { get; }

    public ThingId? ReferenceId { get; }
}

/// <summary>
/// A piece's orientation: where it faces (its front) and where its top points, and the Euler angles in degrees. For a
/// piece on the grid's axes the angles are the quarter turns place_structure's rotation takes, and facing with up is
/// the same turn as place_structure's facing and up, so either form places it again as it stands (facing reversed:
/// turned 180 degrees). facing and up are null for a piece turned off the grid's axes; euler is then as the game holds it.
/// </summary>
internal sealed class OrientationView
{
    internal OrientationView(string? facing, string? up, RotationView euler)
    {
        Facing = facing;
        Up = up;
        Euler = euler;
    }

    public string? Facing { get; }

    public string? Up { get; }

    public RotationView Euler { get; }

    /// <summary>A quarter-turn rotation, its Euler angles as place_structure's rotation takes them.</summary>
    internal static OrientationView Of(CubeRotation turn)
    {
        (int x, int y, int z) = turn.EulerTurns();
        return new OrientationView(turn.Forward.Name, turn.Up.Name, new RotationView(x * 90, y * 90, z * 90));
    }

    /// <summary>A rotation off the grid's axes: no facing or up, only the Euler angles as the game holds them.</summary>
    internal static OrientationView OffGrid(RotationView euler) => new OrientationView(null, null, euler);
}

/// <summary>One placement as planned: the prefab, where and how it would stand, its state, look and cost.</summary>
internal sealed class PlacementView
{
    internal PlacementView(int index, PlacementPrefabView prefab, PlacementSpotView spot, PlacementLookView look,
        List<UpgradeAmountView> cost, List<SurveyPortView>? ports = null, PlacementLayoutView? layout = null,
        OrientResultView? orient = null, ResolvedPlacementView? resolved = null)
    {
        Resolved = resolved;
        Orient = orient;
        Layout = layout;
        Ports = ports;
        Index = index;
        PrefabName = prefab.PrefabName;
        PrefabHash = prefab.PrefabHash;
        DisplayName = prefab.DisplayName;
        Placement = spot.Placement;
        Position = spot.Position;
        Orientation = spot.Orientation;
        Face = spot.Face;
        BuildState = look.BuildState;
        BuildStates = prefab.BuildStates;
        Label = look.Label;
        Color = look.Color;
        Cost = cost;
    }

    public int Index { get; }

    public string? PrefabName { get; }

    public int? PrefabHash { get; }

    public string? DisplayName { get; }

    /// <summary>grid, face or face_mount (how the cursor snaps it).</summary>
    public string? Placement { get; }

    /// <summary>Where the piece stands (the cursor's position), after snapping.</summary>
    public PositionView? Position { get; }

    public OrientationView? Orientation { get; }

    /// <summary>For a piece on a cell face (a wall): which face of the cell it sits on.</summary>
    public string? Face { get; }

    /// <summary>
    /// A device (or another thing with ends that is not a cable, pipe or chute piece): its cable, pipe and chute ports
    /// as they would stand at this position and turn, in grid_survey's shape (network_id is null: nothing is joined
    /// yet). Left out for anything else.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SurveyPortView>? Ports { get; }

    public int? BuildState { get; }

    public int? BuildStates { get; }

    public string? Label { get; }

    public ColorView? Color { get; }

    /// <summary>What it costs, per item; empty for a free placement.</summary>
    public List<UpgradeAmountView> Cost { get; }

    /// <summary>
    /// The layout preview: footprint (small cells, 2 m cells, body, mount), sections, conflicts and port_checks.
    /// Left out when the placement did not resolve that far.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PlacementLayoutView? Layout { get; }

    /// <summary>With orient: the turn chosen, the next best and a Mode flip when needed; left out otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public OrientResultView? Orient { get; }

    /// <summary>
    /// resolved_at and resolved_facing: the point at was read as (and how: as given, the crosshair, an offset from a
    /// thing or the player, a face; above_floor_m applied) and a named facing's axis.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ResolvedPlacementView? Resolved { get; }
}

internal sealed class PlacementPrefabView
{
    internal PlacementPrefabView(string? prefabName, int? prefabHash, string? displayName, int? buildStates)
    {
        PrefabName = prefabName;
        PrefabHash = prefabHash;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        BuildStates = buildStates;
    }

    internal string? PrefabName { get; }

    internal int? PrefabHash { get; }

    internal string? DisplayName { get; }

    internal int? BuildStates { get; }
}

internal sealed class PlacementSpotView
{
    internal PlacementSpotView(string? placement, PositionView? position, OrientationView? orientation, string? face)
    {
        Placement = placement;
        Position = position;
        Orientation = orientation;
        Face = face;
    }

    internal string? Placement { get; }

    internal PositionView? Position { get; }

    internal OrientationView? Orientation { get; }

    internal string? Face { get; }
}

internal sealed class PlacementLookView
{
    internal PlacementLookView(int? buildState, string? label, ColorView? color)
    {
        BuildState = buildState;
        Label = label;
        Color = color;
    }

    internal int? BuildState { get; }

    internal string? Label { get; }

    internal ColorView? Color { get; }
}

/// <summary>One item over the whole run: how many are needed and how many the source holds.</summary>
internal sealed class BuildMaterialView
{
    internal BuildMaterialView(string? prefabName, int needed, int available)
    {
        PrefabName = prefabName;
        Needed = needed;
        Available = available;
    }

    public string? PrefabName { get; }

    public int Needed { get; }

    public int Available { get; }
}

/// <summary>
/// place_structure before anything is built: each placement, the materials, and every problem and warning. status
/// is dry_run, scheduled (a job was started; poll it with job_id) or refused (nothing was changed).
/// </summary>
internal sealed class PlaceReportView
{
    internal PlaceReportView(BuildHeader header, List<PlacementView> placements, List<BuildMaterialView> materials,
        ThingView? from, bool free)
    {
        Tool = "place_structure";
        Status = header.Status;
        JobId = header.JobId;
        Ready = header.Problems.Count == 0;
        Problems = header.Problems;
        Warnings = header.Warnings;
        Placements = placements;
        Materials = materials;
        From = from;
        Free = free;
        Notes = header.Notes;
    }

    public string Tool { get; }

    public string Status { get; }

    public string? JobId { get; }

    public bool Ready { get; }

    public List<BuildIssueView> Problems { get; }

    public List<BuildIssueView> Warnings { get; }

    public List<PlacementView> Placements { get; }

    public List<BuildMaterialView> Materials { get; }

    public ThingView? From { get; }

    public bool Free { get; }

    /// <summary>The tool's fixed explanations; left out unless include_notes.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; }
}

/// <summary>The fields every build report starts with.</summary>
internal sealed class BuildHeader
{
    internal BuildHeader(string status, string? jobId, List<BuildIssueView> problems, List<BuildIssueView> warnings,
        List<string>? notes)
    {
        Status = status;
        JobId = jobId;
        Problems = problems;
        Warnings = warnings;
        Notes = notes;
    }

    internal string Status { get; }

    internal string? JobId { get; }

    internal List<BuildIssueView> Problems { get; }

    internal List<BuildIssueView> Warnings { get; }

    /// <summary>The tool's fixed explanations, with include_notes; null otherwise.</summary>
    internal List<string>? Notes { get; }
}

/// <summary>One piece to remove: what it is, where, and what deconstructing it gives back.</summary>
internal sealed class RemovalView
{
    internal RemovalView(int index, ThingView piece, PositionView position, int buildState, string kind,
        List<UpgradeAmountView> refund)
    {
        Index = index;
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        DisplayName = piece.DisplayName;
        Position = position;
        BuildState = buildState;
        Kind = kind;
        Refund = refund;
    }

    public int Index { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }

    public int BuildState { get; }

    /// <summary>structure, or cable, pipe or chute for a network piece (removed as remove_cables etc. would).</summary>
    public string Kind { get; }

    public List<UpgradeAmountView> Refund { get; }
}

/// <summary>remove_structure before anything is removed: each piece, the refund, every problem and warning.</summary>
internal sealed class RemoveReportView
{
    internal RemoveReportView(BuildHeader header, List<RemovalView> removals, List<UpgradeAmountView> refund,
        object refundTo, ThingView? from, List<BurstView>? willBurst = null, RefundPlanView? refundPlan = null)
    {
        Tool = "remove_structure";
        Status = header.Status;
        JobId = header.JobId;
        Ready = header.Problems.Count == 0;
        Problems = header.Problems;
        Warnings = header.Warnings;
        Removals = removals;
        Refund = refund;
        RefundTo = refundTo;
        RefundPlan = refundPlan;
        From = from;
        WillBurst = willBurst ?? new List<BurstView>();
        Notes = header.Notes;
    }

    public string Tool { get; }

    public string Status { get; }

    public string? JobId { get; }

    public bool Ready { get; }

    public List<BuildIssueView> Problems { get; }

    public List<BuildIssueView> Warnings { get; }

    public List<RemovalView> Removals { get; }

    /// <summary>Everything the removals give back, per item.</summary>
    public List<UpgradeAmountView> Refund { get; }

    /// <summary>source, ground or none (the single words), or the list of targets tried in turn.</summary>
    public object RefundTo { get; }

    /// <summary>Where each refunded item goes (target, merged/slot/ground); absent with refund_to none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RefundPlanView? RefundPlan { get; }

    public ThingView? From { get; }

    /// <summary>Networks left over their weakest pipe that allow_burst lets the request leave so; empty otherwise.</summary>
    public List<BurstView> WillBurst { get; }

    /// <summary>The tool's fixed explanations; left out unless include_notes.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; }
}

/// <summary>
/// A pipe network left over its weakest pipe by a remove_structure request with allow_burst (will_burst): the
/// forecast pressure against the rating, the pipes expected to burst and where each leaks (a room id, or outdoors),
/// and the gas expected out (released_mol of the holds_mol it holds, gas by gas).
/// </summary>
internal sealed class BurstView
{
    internal BurstView(BurstForecast forecast)
    {
        NetworkSqueeze squeeze = forecast.Squeeze;
        NetworkId = new ThingId(squeeze.Network);
        NetworksLeft = squeeze.Parts;
        VolumeLeftL = squeeze.LeftL;
        PressureNowKpa = squeeze.BeforeKpa;
        PressureAfterKpa = squeeze.AfterKpa;
        RatingKpa = squeeze.LowestKpa ?? 0.0;
        Pipes = forecast.Pipes.ConvertAll(static pipe => new BurstPipeView(pipe));
        Where = forecast.Where;
        HoldsMol = forecast.HoldsMol;
        ReleasedMol = forecast.ReleasedMol;
        Gases = forecast.Released.ConvertAll(static gas => new GasAmountView(gas.Gas, gas.Mol));
    }

    /// <summary>The network the request takes from (its id before the job).</summary>
    public ThingId NetworkId { get; }

    /// <summary>How many networks the job leaves of it; above 1, the rest of this entry is one of them.</summary>
    public int NetworksLeft { get; }

    public double VolumeLeftL { get; }

    public double PressureNowKpa { get; }

    public double PressureAfterKpa { get; }

    /// <summary>The weakest pipe left.</summary>
    public double RatingKpa { get; }

    /// <summary>The weakest pipes left in cells that hold air; empty when none is, and then none bursts.</summary>
    public List<BurstPipeView> Pipes { get; }

    /// <summary>Where they leak: room ids, or outdoors.</summary>
    public List<string> Where { get; }

    public double HoldsMol { get; }

    public double ReleasedMol { get; }

    public List<GasAmountView> Gases { get; }
}

/// <summary>A pipe expected to burst, and where it leaks.</summary>
internal sealed class BurstPipeView
{
    internal BurstPipeView(BurstPipe pipe)
    {
        ReferenceId = new ThingId(pipe.Id);
        PrefabName = pipe.Prefab;
        RatingKpa = pipe.RatingKpa;
        Where = pipe.Where;
        PressureThereKpa = pipe.OutsideKpa;
    }

    public ThingId ReferenceId { get; }

    public string PrefabName { get; }

    public double RatingKpa { get; }

    /// <summary>The room id of the cell it leaks into, or outdoors.</summary>
    public string Where { get; }

    public double PressureThereKpa { get; }
}

/// <summary>A piece a job placed or removed, by its placement's or removal's index.</summary>
internal sealed class BuiltPieceView
{
    internal BuiltPieceView(int index, ThingView piece)
    {
        Index = index;
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        DisplayName = piece.DisplayName;
    }

    public int Index { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }
}

/// <summary>The check of one piece after the job: it stands as planned (placed), or it is gone (removed).</summary>
internal sealed class BuildCheckView
{
    internal BuildCheckView(int index, ThingId? referenceId, List<string> issues)
    {
        Index = index;
        ReferenceId = referenceId;
        Ok = issues.Count == 0;
        Issues = issues;
    }

    public int Index { get; }

    public ThingId? ReferenceId { get; }

    public bool Ok { get; }

    public List<string> Issues { get; }
}

/// <summary>What a job did: the final check's report, the pieces, the materials, where it stopped, the checks.</summary>
internal sealed class BuildJobResult
{
    internal BuildJobResult(object? finalCheck, BuildLog log, List<BuildCheckView> verification, ErrorView? error,
        GasCheckView? gasCheck = null)
    {
        FinalCheck = finalCheck;
        Placed = log.Placed;
        Removed = log.Removed;
        Used = log.Used;
        Refunded = log.Refunded;
        RefundError = log.RefundError;
        StoppedAt = log.StoppedAt;
        Verification = verification;
        Error = error;
        GasCheck = gasCheck;
    }

    /// <summary>The check made as the job started; null in a brief poll unless it refused (it holds why).</summary>
    public object? FinalCheck { get; }

    public List<BuiltPieceView> Placed { get; }

    public List<BuiltPieceView> Removed { get; }

    public List<UpgradeAmountView> Used { get; }

    public List<UpgradeRefundView> Refunded { get; }

    public ErrorView? RefundError { get; }

    public BuildStopView? StoppedAt { get; }

    public List<BuildCheckView> Verification { get; }

    public ErrorView? Error { get; }

    /// <summary>The pipe networks' contents before and after the job.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GasCheckView? GasCheck { get; }

    /// <summary>The same result without its final check (a brief poll).</summary>
    internal BuildJobResult WithoutFinalCheck() => new BuildJobResult(null, this);

    private BuildJobResult(object? finalCheck, BuildJobResult result)
    {
        FinalCheck = finalCheck;
        Placed = result.Placed;
        Removed = result.Removed;
        Used = result.Used;
        Refunded = result.Refunded;
        RefundError = result.RefundError;
        StoppedAt = result.StoppedAt;
        Verification = result.Verification;
        Error = result.Error;
        GasCheck = result.GasCheck;
    }
}

/// <summary>What a job has done so far, as it goes.</summary>
internal sealed class BuildLog
{
    internal List<BuiltPieceView> Placed { get; } = new List<BuiltPieceView>();

    internal List<BuiltPieceView> Removed { get; } = new List<BuiltPieceView>();

    internal List<UpgradeAmountView> Used { get; } = new List<UpgradeAmountView>();

    internal List<UpgradeRefundView> Refunded { get; } = new List<UpgradeRefundView>();

    internal ErrorView? RefundError { get; set; }

    internal BuildStopView? StoppedAt { get; set; }
}

/// <summary>The placement or removal a job stopped at, and why; everything after it was left undone.</summary>
internal sealed class BuildStopView
{
    internal BuildStopView(int index, ErrorView error)
    {
        Index = index;
        Error = error;
    }

    public int Index { get; }

    public ErrorView Error { get; }
}

/// <summary>
/// A place_structure or remove_structure job as polled. status: waiting (for the game tick to stop), applied (done
/// and every check passed), applied_with_differences (done, some check failed; see verification), stopped (stopped
/// part way; see stopped_at), refused (the final check found problems; nothing changed) or applied_unchecked.
/// </summary>
internal sealed class BuildJobView
{
    private const string RefusedStatus = "refused";

    internal BuildJobView(string jobId, string tool, string status, object? preflight, BuildJobResult? result,
        JobPreflightSummaryView? preflightSummary = null)
    {
        PreflightSummary = preflightSummary;
        JobId = jobId;
        Tool = tool;
        Status = status;
        Preflight = preflight;
        Result = result;
    }

    public string JobId { get; }

    public string Tool { get; }

    public string Status { get; }

    /// <summary>The dry run made when the job was asked for; null in a brief poll.</summary>
    public object? Preflight { get; }

    /// <summary>The dry run in short, in a brief reply to the run that started the job.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JobPreflightSummaryView? PreflightSummary { get; }

    public BuildJobResult? Result { get; }

    /// <summary>
    /// A poll without verbose: a job view without its preflight and final check, the reports the real run already
    /// answered with; a refused job keeps its final check, which holds the problems it refused for. Any other view
    /// (queued, dropped) is returned as it is.
    /// </summary>
    internal static object Brief(object polled) => polled is BuildJobView job ? Brief(job, null) : polled;

    /// <summary>The job as a brief poll gives it, with the preflight in short when summary is given.</summary>
    internal static BuildJobView Brief(BuildJobView job, JobPreflightSummaryView? summary) =>
        new BuildJobView(job.JobId, job.Tool, job.Status, null,
            job.Status == RefusedStatus ? job.Result : job.Result?.WithoutFinalCheck(), summary);
}
