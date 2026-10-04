#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>
/// replace_walls and replace_frames before anything is changed: each piece and what replaces it, what it blocks
/// before and after, the materials, the rooms around the pieces, and every problem found. status is dry_run,
/// scheduled (a job was started; poll it with job_id) or refused (nothing was changed; problems says why).
/// </summary>
internal sealed class StructureSwapReportView
{
    internal StructureSwapReportView(UpgradeHeader header, UpgradeCounts counts, StructureSwapLists lists,
        StructureSwapResources resources)
    {
        Tool = header.Tool;
        Target = header.Target;
        Status = header.Status;
        JobId = header.JobId;
        Ready = lists.Problems.Count == 0;
        Problems = lists.Problems;
        PiecesTotal = counts.Total;
        ToSwap = counts.ToSwap;
        Kept = counts.Kept;
        Unmatched = counts.Unmatched;
        Pieces = lists.Pieces;
        PiecesListed = lists.Pieces.Count;
        ByPrefab = lists.ByPrefab;
        KeptPieces = lists.KeptPieces;
        UnmatchedPieces = lists.UnmatchedPieces;
        From = resources.From;
        Materials = resources.Materials;
        RefundEnabled = resources.RefundEnabled;
        RefundPlan = resources.RefundPlan;
        Rooms = resources.Rooms;
        Notes = header.Notes;
    }

    public string Tool { get; }

    /// <summary>The target prefab name, or own_prefab (replace_frames without to).</summary>
    public string Target { get; }

    /// <summary>dry_run, scheduled or refused.</summary>
    public string Status { get; }

    public string? JobId { get; }

    /// <summary>No problems: a confirmed run would start.</summary>
    public bool Ready { get; }

    public List<UpgradeProblemView> Problems { get; }

    public int PiecesTotal { get; }

    public int ToSwap { get; }

    public int Kept { get; }

    public int Unmatched { get; }

    public List<StructurePieceView> Pieces { get; }

    public int PiecesListed { get; }

    public List<StructureMappingView> ByPrefab { get; }

    public List<UpgradeSkippedView> KeptPieces { get; }

    public List<UpgradeSkippedView> UnmatchedPieces { get; }

    public ThingView? From { get; }

    /// <summary>Per item over the whole run: gross cost and refund, what is taken and what is given back.</summary>
    public List<StructureMaterialView> Materials { get; }

    public bool RefundEnabled { get; }

    /// <summary>Where each refunded item goes (refund_to, target per part); absent with refund off.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RefundPlanView? RefundPlan { get; }

    /// <summary>Every room next to a piece to swap, as it is now.</summary>
    public List<StructureRoomView> Rooms { get; }

    /// <summary>The tool's fixed explanations; left out unless include_notes.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; }
}

internal sealed class StructureSwapLists
{
    internal StructureSwapLists(List<UpgradeProblemView> problems, List<StructurePieceView> pieces,
        List<StructureMappingView> byPrefab, List<UpgradeSkippedView> keptPieces,
        List<UpgradeSkippedView> unmatchedPieces)
    {
        Problems = problems;
        Pieces = pieces;
        ByPrefab = byPrefab;
        KeptPieces = keptPieces;
        UnmatchedPieces = unmatchedPieces;
    }

    internal List<UpgradeProblemView> Problems { get; }

    internal List<StructurePieceView> Pieces { get; }

    internal List<StructureMappingView> ByPrefab { get; }

    internal List<UpgradeSkippedView> KeptPieces { get; }

    internal List<UpgradeSkippedView> UnmatchedPieces { get; }
}

internal sealed class StructureSwapResources
{
    internal StructureSwapResources(ThingView? from, List<StructureMaterialView> materials, bool refundEnabled,
        List<StructureRoomView> rooms, RefundPlanView? refundPlan = null)
    {
        From = from;
        Materials = materials;
        RefundEnabled = refundEnabled;
        Rooms = rooms;
        RefundPlan = refundEnabled ? refundPlan : null;
    }

    internal ThingView? From { get; }

    internal List<StructureMaterialView> Materials { get; }

    internal bool RefundEnabled { get; }

    internal List<StructureRoomView> Rooms { get; }

    internal RefundPlanView? RefundPlan { get; }
}

/// <summary>One piece to swap: where it is, what replaces it, what it blocks before and after, what it costs.</summary>
internal sealed class StructurePieceView
{
    internal StructurePieceView(ThingView piece, PositionView position, RotationView rotationDeg,
        StructureTargetView target, StructureBlockingView blocking, List<StructureFaceView>? faces,
        List<string> roomIds, List<StructureMaterialLineView> materials)
    {
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        Position = position;
        RotationDeg = rotationDeg;
        BuildState = target.BuildState;
        TargetPrefabName = target.PrefabName;
        TargetBuildState = target.TargetBuildState;
        BlocksAirBefore = blocking.AirBefore;
        BlocksAirAfter = blocking.AirAfter;
        BlocksGravityBefore = blocking.GravityBefore;
        BlocksGravityAfter = blocking.GravityAfter;
        AirChange = blocking.Change;
        Seals = blocking.Change == "seals";
        Stressed = blocking.Stressed;
        Faces = faces;
        RoomIds = roomIds;
        Materials = materials;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public PositionView Position { get; }

    public RotationView RotationDeg { get; }

    /// <summary>Its build state now (CurrentBuildStateIndex).</summary>
    public int BuildState { get; }

    public string? TargetPrefabName { get; }

    /// <summary>The new piece's final build state, which it is raised to at once.</summary>
    public int TargetBuildState { get; }

    public bool BlocksAirBefore { get; }

    public bool BlocksAirAfter { get; }

    public bool BlocksGravityBefore { get; }

    public bool BlocksGravityAfter { get; }

    /// <summary>keeps, seals (blocks air or gravity it let through) or would_open (refused).</summary>
    public string AirChange { get; }

    public bool Seals { get; }

    /// <summary>replace_walls: a face's pressure difference is above the new wall's stress mark (a warning).</summary>
    public bool Stressed { get; }

    /// <summary>replace_walls only: each face the wall blocks.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<StructureFaceView>? Faces { get; }

    /// <summary>The rooms next to the piece (the two sides of a wall; a frame's cell and its six neighbours).</summary>
    public List<string> RoomIds { get; }

    public List<StructureMaterialLineView> Materials { get; }
}

internal sealed class StructureTargetView
{
    internal StructureTargetView(string? prefabName, int buildState, int targetBuildState)
    {
        PrefabName = prefabName;
        BuildState = buildState;
        TargetBuildState = targetBuildState;
    }

    internal string? PrefabName { get; }

    internal int BuildState { get; }

    internal int TargetBuildState { get; }
}

internal sealed class StructureBlockingView
{
    internal StructureBlockingView(bool airBefore, bool airAfter, bool gravityBefore, bool gravityAfter, string change,
        bool stressed)
    {
        AirBefore = airBefore;
        AirAfter = airAfter;
        GravityBefore = gravityBefore;
        GravityAfter = gravityAfter;
        Change = change;
        Stressed = stressed;
    }

    internal bool AirBefore { get; }

    internal bool AirAfter { get; }

    internal bool GravityBefore { get; }

    internal bool GravityAfter { get; }

    internal string Change { get; }

    internal bool Stressed { get; }
}

/// <summary>A face a wall blocks: its two cells' pressures and what the face bears with the old and new wall.</summary>
internal sealed class StructureFaceView
{
    internal StructureFaceView(PositionView position, List<PositionView> cells, StructureFacePressure pressure,
        string verdict)
    {
        Position = position;
        Cells = cells;
        PressureKpaA = pressure.PressureA;
        PressureKpaB = pressure.PressureB;
        DifferenceKpa = pressure.Difference;
        MaxPressureDeltaKpaBefore = pressure.FaceSumBefore;
        MaxPressureDeltaKpaAfter = pressure.FaceSumAfter;
        Verdict = verdict;
    }

    public PositionView Position { get; }

    /// <summary>The two cells the face separates, a then b.</summary>
    public List<PositionView> Cells { get; }

    public double PressureKpaA { get; }

    public double PressureKpaB { get; }

    public double DifferenceKpa { get; }

    /// <summary>The face's summed MaxPressureDelta with the old wall (what the game damages above).</summary>
    public double MaxPressureDeltaKpaBefore { get; }

    public double MaxPressureDeltaKpaAfter { get; }

    /// <summary>ok, stressed (warning), overstressed (refused), or shielded (a frame beside it).</summary>
    public string Verdict { get; }
}

internal sealed class StructureFacePressure
{
    internal StructureFacePressure(double pressureA, double pressureB, double faceSumBefore, double faceSumAfter)
    {
        PressureA = pressureA;
        PressureB = pressureB;
        Difference = System.Math.Abs(pressureA - pressureB);
        FaceSumBefore = faceSumBefore;
        FaceSumAfter = faceSumAfter;
    }

    internal double PressureA { get; }

    internal double PressureB { get; }

    internal double Difference { get; }

    internal double FaceSumBefore { get; }

    internal double FaceSumAfter { get; }
}

/// <summary>
/// One item of one swap: cost (new piece to final), refund (old piece from its state, which pays toward the cost
/// whether or not refund is on), net.
/// </summary>
internal sealed class StructureMaterialLineView
{
    internal StructureMaterialLineView(string? prefabName, int cost, int refund, bool refundEnabled)
    {
        PrefabName = prefabName;
        Cost = cost;
        Refund = refund;
        Net = RefundShown.Net(refundEnabled, cost, refund);
    }

    public string? PrefabName { get; }

    public int Cost { get; }

    public int Refund { get; }

    /// <summary>Positive: taken from the source; negative: given back, only with refund on (0 with it off).</summary>
    public int Net { get; }
}

/// <summary>One item over the run: what the source must hold (charge) and has, and what is given back.</summary>
internal sealed class StructureMaterialView
{
    internal StructureMaterialView(string? prefabName, string? displayName, StructureMaterialCounts counts,
        List<UpgradeStackView> stacks)
    {
        PrefabName = prefabName;
        DisplayName = displayName;
        Cost = counts.Cost;
        Refund = counts.Refund;
        Charge = counts.Charge;
        GiveBack = counts.GiveBack;
        Available = counts.Available;
        Stacks = stacks;
    }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public int Cost { get; }

    public int Refund { get; }

    /// <summary>Taken from the source: each swap's cost less its refund where positive, summed.</summary>
    public int Charge { get; }

    /// <summary>Given back with refund on: each swap's refund less its cost where positive, summed.</summary>
    public int GiveBack { get; }

    public int Available { get; }

    public List<UpgradeStackView> Stacks { get; }
}

internal sealed class StructureMaterialCounts
{
    internal StructureMaterialCounts(int cost, int refund, int charge, int giveBack, int available)
    {
        Cost = cost;
        Refund = refund;
        Charge = charge;
        GiveBack = giveBack;
        Available = available;
    }

    internal int Cost { get; }

    internal int Refund { get; }

    internal int Charge { get; }

    internal int GiveBack { get; }

    internal int Available { get; }
}

/// <summary>A source prefab, what it becomes, and how many pieces.</summary>
internal sealed class StructureMappingView
{
    internal StructureMappingView(string? prefabName, string? targetPrefabName, int count)
    {
        PrefabName = prefabName;
        TargetPrefabName = targetPrefabName;
        Count = count;
    }

    public string? PrefabName { get; }

    public string? TargetPrefabName { get; }

    public int Count { get; }
}

/// <summary>A room next to the pieces, as the rooms tool reports it: id, cells and air summed over them.</summary>
internal sealed class StructureRoomView
{
    internal StructureRoomView(string roomId, int cellCount, double totalMol, double energyJ)
    {
        RoomId = roomId;
        CellCount = cellCount;
        TotalMol = totalMol;
        EnergyJ = energyJ;
    }

    public string RoomId { get; }

    public int CellCount { get; }

    public double TotalMol { get; }

    public double EnergyJ { get; }
}

/// <summary>
/// A confirmed replace_walls or replace_frames run, polled by job_id. status: waiting (for the game tick to stop),
/// verifying (swapped and checked with the tick held; the tick runs again and the rooms are being re-evaluated),
/// applied (every check passed), applied_with_differences (a check found something: see held_check and room_check),
/// stopped (a swap failed part way: swapped lists what was done, stopped_at why and whether the piece was restored),
/// applied_unchecked (swapped, but a check could not run: see error), refused (nothing was changed).
/// </summary>
internal sealed class StructureSwapJobView
{
    internal StructureSwapJobView(string jobId, string tool, string status, StructureSwapReportView preflight,
        StructureSwapJobResult? result)
    {
        JobId = jobId;
        Tool = tool;
        Status = status;
        Preflight = preflight;
        FinalCheck = result?.FinalCheck;
        Swapped = result?.Swapped ?? new List<UpgradeSwappedView>();
        SwappedCount = Swapped.Count;
        NotSwapped = result?.NotSwapped ?? new List<ThingId>();
        StoppedAt = result?.StoppedAt;
        Used = result?.Used ?? new List<UpgradeAmountView>();
        RefundDelivered = result?.Refunded ?? new List<UpgradeRefundView>();
        RefundError = result?.RefundError;
        HeldCheck = result?.HeldCheck;
        RoomCheck = result?.RoomCheck;
        Error = result?.Error;
    }

    public string JobId { get; }

    public string Tool { get; }

    public string Status { get; }

    /// <summary>The report when the run was confirmed.</summary>
    public StructureSwapReportView Preflight { get; }

    /// <summary>The same checks, made again once the game tick had stopped, right before the swap.</summary>
    public StructureSwapReportView? FinalCheck { get; }

    public List<UpgradeSwappedView> Swapped { get; }

    public int SwappedCount { get; }

    /// <summary>Planned pieces left as they were (after a stop).</summary>
    public List<ThingId> NotSwapped { get; }

    public StructureStopView? StoppedAt { get; }

    public List<UpgradeAmountView> Used { get; }

    public List<UpgradeRefundView> RefundDelivered { get; }

    public ErrorView? RefundError { get; }

    /// <summary>The check the frame after the swap, with the tick still held.</summary>
    public StructureHeldCheckView? HeldCheck { get; }

    /// <summary>The check once the game has run again and re-evaluated its rooms.</summary>
    public StructureRoomCheckView? RoomCheck { get; }

    public ErrorView? Error { get; }
}

internal sealed class StructureSwapJobResult
{
    internal StructureSwapJobResult(StructureSwapReportView? finalCheck, StructureSwapLogView log,
        StructureChecks checks, ErrorView? error)
    {
        FinalCheck = finalCheck;
        Swapped = log.Swapped;
        NotSwapped = log.NotSwapped;
        StoppedAt = log.StoppedAt;
        Used = log.Used;
        Refunded = log.Refunded;
        RefundError = log.RefundError;
        HeldCheck = checks.Held;
        RoomCheck = checks.Rooms;
        Error = error;
    }

    internal StructureSwapReportView? FinalCheck { get; }

    internal List<UpgradeSwappedView> Swapped { get; }

    internal List<ThingId> NotSwapped { get; }

    internal StructureStopView? StoppedAt { get; }

    internal List<UpgradeAmountView> Used { get; }

    internal List<UpgradeRefundView> Refunded { get; }

    internal ErrorView? RefundError { get; }

    internal StructureHeldCheckView? HeldCheck { get; }

    internal StructureRoomCheckView? RoomCheck { get; }

    internal ErrorView? Error { get; }
}

internal sealed class StructureChecks
{
    internal StructureChecks(StructureHeldCheckView? held, StructureRoomCheckView? rooms)
    {
        Held = held;
        Rooms = rooms;
    }

    internal static StructureChecks None => new StructureChecks(null, null);

    internal StructureHeldCheckView? Held { get; }

    internal StructureRoomCheckView? Rooms { get; }
}

/// <summary>What a swap loop did, piece by piece, and what it used and gave back.</summary>
internal sealed class StructureSwapLogView
{
    internal List<UpgradeSwappedView> Swapped { get; } = new List<UpgradeSwappedView>();

    internal List<ThingId> NotSwapped { get; } = new List<ThingId>();

    internal StructureStopView? StoppedAt { get; set; }

    internal List<UpgradeAmountView> Used { get; } = new List<UpgradeAmountView>();

    internal List<UpgradeRefundView> Refunded { get; } = new List<UpgradeRefundView>();

    internal ErrorView? RefundError { get; set; }
}

/// <summary>
/// The piece a stopped run stopped at and why. piece_intact: the old piece holds all its slots (it was never
/// replaced, or it was restored); rolled_back: a new piece was built and taken away again; replacement_id: the new
/// piece when it was kept.
/// </summary>
internal sealed class StructureStopView
{
    internal StructureStopView(ThingId referenceId, ErrorView error, bool pieceIntact, bool rolledBack,
        ThingId? replacementId)
    {
        ReferenceId = referenceId;
        Error = error;
        PieceIntact = pieceIntact;
        RolledBack = rolledBack;
        ReplacementId = replacementId;
    }

    public ThingId ReferenceId { get; }

    public ErrorView Error { get; }

    public bool PieceIntact { get; }

    public bool RolledBack { get; }

    public ThingId? ReplacementId { get; }
}

/// <summary>
/// The check the frame after the swap, the tick still held: new pieces in the planned slots at their final state
/// and blocking where planned, old pieces gone, face structures around swapped frames in place, and the air of every
/// room and cell around the pieces exactly as recorded before the swap (nothing ran, so any change is a fault).
/// </summary>
internal sealed class StructureHeldCheckView
{
    internal StructureHeldCheckView(List<UpgradeProblemView> problems)
    {
        Ok = problems.Count == 0;
        Problems = problems;
    }

    public bool Ok { get; }

    public List<UpgradeProblemView> Problems { get; }
}

/// <summary>
/// The rooms once the game ran again: each room next to a swapped piece under the same id, with the same cells (less
/// cells a sealing frame now blocks) and the same air within tolerance. settled: the game ran its ticks and emptied
/// its room queue before the check; false when it did not within the wait (a paused game), and then it is a problem.
/// </summary>
internal sealed class StructureRoomCheckView
{
    internal StructureRoomCheckView(bool settled, int ticksRun, List<UpgradeProblemView> problems,
        List<StructureRoomResultView> rooms, List<StructureRoomView> newRooms)
    {
        Settled = settled;
        TicksRun = ticksRun;
        Ok = problems.Count == 0;
        Problems = problems;
        Rooms = rooms;
        NewRooms = newRooms;
    }

    public bool Settled { get; }

    public int TicksRun { get; }

    public bool Ok { get; }

    public List<UpgradeProblemView> Problems { get; }

    public List<StructureRoomResultView> Rooms { get; }

    /// <summary>Rooms around the pieces that did not exist before (a seal closed a space): not a problem.</summary>
    public List<StructureRoomView> NewRooms { get; }
}

/// <summary>One room before and after, the differences and the tolerance they were judged with.</summary>
internal sealed class StructureRoomResultView
{
    internal StructureRoomResultView(string roomId, bool exists, StructureRoomCells cells, StructureRoomAir air,
        List<string> problems)
    {
        RoomId = roomId;
        Exists = exists;
        CellCountBefore = cells.Before;
        CellCountExpected = cells.Expected;
        CellCountAfter = cells.After;
        CellsLost = cells.Lost;
        CellsGained = cells.Gained;
        TotalMolBefore = air.MolBefore;
        TotalMolAfter = air.MolAfter;
        DeltaMol = air.DeltaMol;
        ToleranceMol = air.ToleranceMol;
        EnergyJBefore = air.EnergyBefore;
        EnergyJAfter = air.EnergyAfter;
        DeltaEnergyJ = air.DeltaEnergy;
        ToleranceEnergyJ = air.ToleranceEnergy;
        Problems = problems;
    }

    public string RoomId { get; }

    public bool Exists { get; }

    public int CellCountBefore { get; }

    /// <summary>Before, less cells a sealing frame now blocks.</summary>
    public int CellCountExpected { get; }

    public int? CellCountAfter { get; }

    public int CellsLost { get; }

    public int CellsGained { get; }

    public double TotalMolBefore { get; }

    public double? TotalMolAfter { get; }

    public double? DeltaMol { get; }

    public double ToleranceMol { get; }

    public double EnergyJBefore { get; }

    public double? EnergyJAfter { get; }

    public double? DeltaEnergyJ { get; }

    public double ToleranceEnergyJ { get; }

    /// <summary>room_gone, room_cells_changed, room_air_changed.</summary>
    public List<string> Problems { get; }
}

internal sealed class StructureRoomCells
{
    internal StructureRoomCells(int before, int expected, int? after, int lost, int gained)
    {
        Before = before;
        Expected = expected;
        After = after;
        Lost = lost;
        Gained = gained;
    }

    internal int Before { get; }

    internal int Expected { get; }

    internal int? After { get; }

    internal int Lost { get; }

    internal int Gained { get; }
}

internal sealed class StructureRoomAir
{
    internal StructureRoomAir(double molBefore, double? molAfter, double toleranceMol, double energyBefore,
        double? energyAfter, double toleranceEnergy)
    {
        MolBefore = molBefore;
        MolAfter = molAfter;
        DeltaMol = molAfter - molBefore;
        ToleranceMol = toleranceMol;
        EnergyBefore = energyBefore;
        EnergyAfter = energyAfter;
        DeltaEnergy = energyAfter - energyBefore;
        ToleranceEnergy = toleranceEnergy;
    }

    internal double MolBefore { get; }

    internal double? MolAfter { get; }

    internal double? DeltaMol { get; }

    internal double ToleranceMol { get; }

    internal double EnergyBefore { get; }

    internal double? EnergyAfter { get; }

    internal double? DeltaEnergy { get; }

    internal double ToleranceEnergy { get; }
}
