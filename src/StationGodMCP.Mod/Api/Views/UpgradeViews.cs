#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Views;

/// <summary>
/// upgrade_cables, upgrade_pipes, clean_cables and clean_pipes before anything is changed: what would be swapped for
/// what, every problem found, the coils or kits it takes, the predicted connectivity and the networks' state. status is
/// dry_run, scheduled (a job was started; poll it with job_id) or refused (nothing was changed; problems says why).
/// </summary>
internal sealed class UpgradeReportView : ITruncatingView
{
    internal UpgradeReportView(UpgradeHeader header, UpgradeCounts counts, UpgradeLists lists,
        UpgradeResources resources, UpgradeConnectivityView? connectivity, UpgradeDeadEnds? deadEnds = null,
        List<UpgradeLoopView>? loops = null, UpgradeRedundancyView? redundant = null,
        UpgradeRidingList? riding = null)
    {
        Riding = riding?.Pieces;
        RidingCount = riding?.Count;
        Loops = loops;
        Redundant = redundant;
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
        DeadEnds = deadEnds?.Count;
        DeadEndPieces = deadEnds?.Pieces;
        From = resources.From;
        Coils = resources.Coils;
        RefundEnabled = resources.RefundEnabled;
        Refund = resources.Refund;
        RefundPlan = resources.RefundPlan;
        Networks = resources.Networks;
        Devices = connectivity?.Devices ?? new List<UpgradeDeviceView>();
        Connectivity = connectivity;
        Notes = header.Notes;
    }

    public string Tool { get; }

    /// <summary>heavy, super_heavy, insulated, or minimal (clean_cables).</summary>
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

    public List<UpgradePieceView> Pieces { get; }

    public int PiecesListed { get; }

    /// <summary>A CableMappingView or PipeMappingView per source prefab.</summary>
    public List<object> ByPrefab { get; }

    public List<UpgradeSkippedView> KeptPieces { get; }

    public List<UpgradeSkippedView> UnmatchedPieces { get; }

    /// <summary>Clean tools only: pieces with one connected end or none, all counted, up to limit listed.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? DeadEnds { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeDeadEndView>? DeadEndPieces { get; }

    /// <summary>remove_loops only: every loop found, its pieces, what is cut, and whether it was spared.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeLoopView>? Loops { get; }

    /// <summary>remove_redundant only: what it removed, and each candidate it kept with why.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public UpgradeRedundancyView? Redundant { get; }

    /// <summary>clean_chutes only: pieces the run would change that carry an item, all counted, up to limit listed.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? RidingCount { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeRidingView>? Riding { get; }

    public ThingView? From { get; }

    public List<UpgradeCoilView> Coils { get; }

    public bool RefundEnabled { get; }

    public List<UpgradeAmountView> Refund { get; }

    /// <summary>Where each refunded item goes (refund_to, target per part); absent with refund off.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RefundPlanView? RefundPlan { get; }

    /// <summary>A CableNetworkReportView or PipeNetworkReportView per network a swapped piece is in.</summary>
    public List<object> Networks { get; }

    public List<UpgradeDeviceView> Devices { get; }

    /// <summary>Null when no piece has a replacement: the links are surveyed around pieces to swap only.</summary>
    public UpgradeConnectivityView? Connectivity { get; }

    /// <summary>The tool's fixed explanations; left out unless include_notes.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; }

    public void NoteTruncations(string path)
    {
        Truncations.Capped(path + "pieces", Pieces.Count, ToSwap, "limit", ReplyDefaults.ReportListMaximum);
        Truncations.Capped(path + "kept_pieces", KeptPieces.Count, Kept, "limit", ReplyDefaults.ReportListMaximum);
        Truncations.Capped(path + "unmatched_pieces", UnmatchedPieces.Count, Unmatched, "limit",
            ReplyDefaults.ReportListMaximum);
        if (DeadEndPieces != null && DeadEnds.HasValue)
        {
            Truncations.Capped(path + "dead_end_pieces", DeadEndPieces.Count, DeadEnds.Value, "limit",
                ReplyDefaults.ReportListMaximum);
        }

        if (Riding != null && RidingCount.HasValue)
        {
            Truncations.Capped(path + "riding", Riding.Count, RidingCount.Value, "limit",
                ReplyDefaults.ReportListMaximum);
        }

        if (Redundant != null)
        {
            Truncations.Capped(path + "redundant.kept", Redundant.Kept.Count, Redundant.KeptCount, "limit",
                ReplyDefaults.ReportListMaximum);
        }
    }
}

/// <summary>The report's identity: which tool, which target, what happened to the request.</summary>
internal sealed class UpgradeHeader
{
    internal UpgradeHeader(string tool, string target, string status, string? jobId, List<string>? notes)
    {
        Tool = tool;
        Target = target;
        Status = status;
        JobId = jobId;
        Notes = notes;
    }

    internal string Tool { get; }

    internal string Target { get; }

    internal string Status { get; }

    internal string? JobId { get; }

    /// <summary>The tool's fixed explanations, with include_notes; null otherwise.</summary>
    internal List<string>? Notes { get; }
}

internal sealed class UpgradeCounts
{
    internal UpgradeCounts(int total, int toSwap, int kept, int unmatched)
    {
        Total = total;
        ToSwap = toSwap;
        Kept = kept;
        Unmatched = unmatched;
    }

    internal int Total { get; }

    internal int ToSwap { get; }

    internal int Kept { get; }

    internal int Unmatched { get; }
}

internal sealed class UpgradeLists
{
    internal UpgradeLists(List<UpgradeProblemView> problems, List<UpgradePieceView> pieces, List<object> byPrefab,
        List<UpgradeSkippedView> keptPieces, List<UpgradeSkippedView> unmatchedPieces)
    {
        Problems = problems;
        Pieces = pieces;
        ByPrefab = byPrefab;
        KeptPieces = keptPieces;
        UnmatchedPieces = unmatchedPieces;
    }

    internal List<UpgradeProblemView> Problems { get; }

    internal List<UpgradePieceView> Pieces { get; }

    internal List<object> ByPrefab { get; }

    internal List<UpgradeSkippedView> KeptPieces { get; }

    internal List<UpgradeSkippedView> UnmatchedPieces { get; }
}

internal sealed class UpgradeResources
{
    internal UpgradeResources(ThingView? from, List<UpgradeCoilView> coils, bool refundEnabled,
        List<UpgradeAmountView> refund, List<object> networks, RefundPlanView? refundPlan = null)
    {
        From = from;
        Coils = coils;
        RefundEnabled = refundEnabled;
        Refund = RefundShown.Items(refundEnabled, refund);
        Networks = networks;
        RefundPlan = refundEnabled ? refundPlan : null;
    }

    internal ThingView? From { get; }

    internal List<UpgradeCoilView> Coils { get; }

    internal bool RefundEnabled { get; }

    internal List<UpgradeAmountView> Refund { get; }

    internal List<object> Networks { get; }

    internal RefundPlanView? RefundPlan { get; }
}

/// <summary>One reason a run cannot start (or, after a run, one difference found).</summary>
internal sealed class UpgradeProblemView
{
    internal UpgradeProblemView(string code, string message, ThingId? referenceId = null)
    {
        Code = code;
        Message = message;
        ReferenceId = referenceId;
    }

    public string Code { get; }

    public string Message { get; }

    public ThingId? ReferenceId { get; }
}

/// <summary>A rotation as Euler angles in degrees (Quaternion.eulerAngles), rounded to 0.1.</summary>
internal sealed class RotationView
{
    internal RotationView(double x, double y, double z)
    {
        X = System.Math.Round(x, 1);
        Y = System.Math.Round(y, 1);
        Z = System.Math.Round(z, 1);
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }
}

/// <summary>One piece to swap: where it is and what replaces it.</summary>
internal sealed class UpgradePieceView
{
    internal UpgradePieceView(ThingView piece, PositionView position, RotationView rotationDeg,
        UpgradeTargetView target, ThingId? networkId, UpgradeCleanView? clean = null)
    {
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        Position = position;
        RotationDeg = rotationDeg;
        TargetPrefabName = target.PrefabName;
        TargetRotationDeg = target.RotationDeg;
        RotationKept = target.RotationKept;
        Cost = target.Cost;
        NetworkId = networkId;
        Operation = clean?.Operation;
        Ends = clean?.Ends;
        ConnectedEnds = clean?.ConnectedEnds;
        ReplacementCount = clean?.ReplacementCount;
        Round = clean?.Extras.Round;
        Why = clean?.Extras.Why;
        MergedReferenceIds = clean?.Extras.Merged;
        RefundCount = clean != null ? clean.Extras.Refund : (int?)null;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public PositionView Position { get; }

    public RotationView RotationDeg { get; }

    public string? TargetPrefabName { get; }

    public RotationView TargetRotationDeg { get; }

    /// <summary>The replacement stands at exactly the old rotation (false: the same shape, turned).</summary>
    public bool RotationKept { get; }

    /// <summary>Coils or kits it takes (the target's first build state's EntryQuantity).</summary>
    public int Cost { get; }

    public ThingId? NetworkId { get; }

    /// <summary>Clean tools only: simplify_junction or split_long_straight.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Operation { get; }

    /// <summary>Clean tools only: every end of the old piece, as world axes (+x, -y, ...).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Ends { get; }

    /// <summary>Clean tools only: the ends something is connected to (a simplified replacement's ends).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? ConnectedEnds { get; }

    /// <summary>Clean tools only: how many pieces replace it (0 when removed; one per cell of a split).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? ReplacementCount { get; }

    /// <summary>remove_dead_ends only: the piece's round (1: a stub now; 2: made one by round 1; ...).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Round { get; }

    /// <summary>clean_chutes removals only: why no item reaches a consumer through it (orphan, no_consumer, no_source).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Why { get; }

    /// <summary>merge_straights only: every single the long piece replaces, in line order.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingId>? MergedReferenceIds { get; }

    /// <summary>Clean tools only: items given back for this swap (see refund for which).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? RefundCount { get; }
}

/// <summary>The clean view's fields only some operations have.</summary>
internal sealed class UpgradeCleanExtras
{
    internal UpgradeCleanExtras(int? round, List<ThingId>? merged, int refund, string? why = null)
    {
        Round = round;
        Merged = merged;
        Refund = refund;
        Why = why;
    }

    internal int? Round { get; }

    internal string? Why { get; }

    internal List<ThingId>? Merged { get; }

    internal int Refund { get; }
}

/// <summary>What a clean tool does with one piece: operation, ends, connected ends, how many pieces after.</summary>
internal sealed class UpgradeCleanView
{
    internal UpgradeCleanView(string operation, List<string> ends, List<string> connectedEnds, int replacementCount,
        UpgradeCleanExtras? extras = null)
    {
        Operation = operation;
        Ends = ends;
        ConnectedEnds = connectedEnds;
        ReplacementCount = replacementCount;
        Extras = extras ?? new UpgradeCleanExtras(null, null, 0);
    }

    internal string Operation { get; }

    internal List<string> Ends { get; }

    internal List<string> ConnectedEnds { get; }

    internal int ReplacementCount { get; }

    internal UpgradeCleanExtras Extras { get; }
}

/// <summary>
/// A loop remove_loops found: pieces joined to the rest in more than one way. cut lists the pieces removed to break
/// it; spared is true when keep_ids named one of its pieces; unbroken says why a cycle stays.
/// </summary>
internal sealed class UpgradeLoopView
{
    internal UpgradeLoopView(int index, List<UpgradeLoopPieceView> pieces, List<ThingId> cut, bool spared,
        string? unbroken)
    {
        Index = index;
        Pieces = pieces;
        Cut = cut;
        Spared = spared;
        Unbroken = unbroken;
    }

    public int Index { get; }

    public List<UpgradeLoopPieceView> Pieces { get; }

    public List<ThingId> Cut { get; }

    public bool Spared { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Unbroken { get; }
}

/// <summary>One piece of a loop, where it is, and whether remove_loops cuts it.</summary>
internal sealed class UpgradeLoopPieceView
{
    internal UpgradeLoopPieceView(ThingView piece, PositionView position, bool cut)
    {
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        Position = position;
        Cut = cut;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public PositionView Position { get; }

    public bool Cut { get; }
}

/// <summary>The clean tools' dead ends: how many, and the first of them.</summary>
internal sealed class UpgradeDeadEnds
{
    internal UpgradeDeadEnds(int count, List<UpgradeDeadEndView> pieces)
    {
        Count = count;
        Pieces = pieces;
    }

    internal int Count { get; }

    internal List<UpgradeDeadEndView> Pieces { get; }
}

/// <summary>
/// A piece with one connected end (dead_end: a stub feeding nothing further) or none (isolated). Reported only:
/// removing it would remove a connection point, so that is left to the player.
/// </summary>
internal sealed class UpgradeDeadEndView
{
    internal UpgradeDeadEndView(ThingView piece, PositionView position, string reason, List<string> ends,
        List<string> connectedEnds, string? stoppedBy = null)
    {
        StoppedBy = stoppedBy;
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        Position = position;
        Reason = reason;
        Ends = ends;
        ConnectedEnds = connectedEnds;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public PositionView Position { get; }

    /// <summary>dead_end or isolated.</summary>
    public string Reason { get; }

    public List<string> Ends { get; }

    public List<string> ConnectedEnds { get; }

    /// <summary>
    /// remove_dead_ends only: why this dead end stays (device_connected, device_mounted, holds_contents,
    /// indestructible, rocket_internal, no_kit, not_selected: outside reference_ids).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? StoppedBy { get; }
}

/// <summary>clean_chutes' riding pieces: how many, and the first limit of them.</summary>
internal sealed class UpgradeRidingList
{
    internal UpgradeRidingList(int count, List<UpgradeRidingView> pieces)
    {
        Count = count;
        Pieces = pieces;
    }

    internal int Count { get; }

    internal List<UpgradeRidingView> Pieces { get; }
}

/// <summary>
/// A chute piece the run would remove or replace that has an item riding in it: the piece (dead_end_pieces or
/// kept_pieces gives where it is), what it carries, and what the run does instead (kept: a dead piece left in place;
/// not_simplified: a junction, overflow or splitter left whole).
/// </summary>
internal sealed class UpgradeRidingView
{
    internal UpgradeRidingView(ThingId piece, ThingView carries, string held)
    {
        ReferenceId = piece;
        Carries = carries;
        Held = held;
    }

    public ThingId ReferenceId { get; }

    public ThingView Carries { get; }

    public string Held { get; }
}

internal sealed class UpgradeTargetView
{
    internal UpgradeTargetView(string? prefabName, RotationView rotationDeg, bool rotationKept, int cost)
    {
        PrefabName = prefabName;
        RotationDeg = rotationDeg;
        RotationKept = rotationKept;
        Cost = cost;
    }

    internal string? PrefabName { get; }

    internal RotationView RotationDeg { get; }

    internal bool RotationKept { get; }

    internal int Cost { get; }
}

/// <summary>A piece that stays: already at or above the target, not a run piece, or without a counterpart.</summary>
internal sealed class UpgradeSkippedView
{
    internal UpgradeSkippedView(ThingView piece, string reason, string message)
    {
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        Reason = reason;
        Message = message;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string Reason { get; }

    public string Message { get; }
}

/// <summary>A cable prefab and what it becomes, with the ratings.</summary>
internal sealed class CableMappingView
{
    internal CableMappingView(UpgradeMappingCount mapping, double maxPowerWBefore, double maxPowerWAfter)
    {
        PrefabName = mapping.PrefabName;
        TargetPrefabName = mapping.TargetPrefabName;
        Count = mapping.Count;
        CostEach = mapping.CostEach;
        CostTotal = mapping.CostEach * mapping.Count;
        RefundEach = mapping.RefundEach;
        MaxPowerWBefore = maxPowerWBefore;
        MaxPowerWAfter = maxPowerWAfter;
    }

    public string? PrefabName { get; }

    public string? TargetPrefabName { get; }

    public int Count { get; }

    public int CostEach { get; }

    public int CostTotal { get; }

    public int RefundEach { get; }

    /// <summary>Cable.MaxVoltage: the flow above which PowerTick burns it.</summary>
    public double MaxPowerWBefore { get; }

    public double MaxPowerWAfter { get; }
}

/// <summary>A pipe prefab and what it becomes, with its rating, volume and heat exchange.</summary>
internal sealed class PipeMappingView
{
    internal PipeMappingView(UpgradeMappingCount mapping, PipePrefabNumbers before, PipePrefabNumbers after)
    {
        PrefabName = mapping.PrefabName;
        TargetPrefabName = mapping.TargetPrefabName;
        Count = mapping.Count;
        CostEach = mapping.CostEach;
        CostTotal = mapping.CostEach * mapping.Count;
        RefundEach = mapping.RefundEach;
        MaxPressureKpaBefore = before.MaxPressureKpa;
        MaxPressureKpaAfter = after.MaxPressureKpa;
        VolumeLBefore = before.VolumeL;
        VolumeLAfter = after.VolumeL;
        HeatExchangeFactorBefore = before.HeatExchangeFactor;
        HeatExchangeFactorAfter = after.HeatExchangeFactor;
    }

    public string? PrefabName { get; }

    public string? TargetPrefabName { get; }

    public int Count { get; }

    public int CostEach { get; }

    public int CostTotal { get; }

    public int RefundEach { get; }

    /// <summary>Pipe.MaxPressure.</summary>
    public double MaxPressureKpaBefore { get; }

    public double MaxPressureKpaAfter { get; }

    public double VolumeLBefore { get; }

    public double VolumeLAfter { get; }

    /// <summary>Thing.ThermodynamicsScale: the pipe's convection and radiation scale with it.</summary>
    public double HeatExchangeFactorBefore { get; }

    public double HeatExchangeFactorAfter { get; }
}

internal sealed class UpgradeMappingCount
{
    internal UpgradeMappingCount(string? prefabName, string? targetPrefabName, int count, int costEach, int refundEach)
    {
        PrefabName = prefabName;
        TargetPrefabName = targetPrefabName;
        Count = count;
        CostEach = costEach;
        RefundEach = refundEach;
    }

    internal string? PrefabName { get; }

    internal string? TargetPrefabName { get; }

    internal int Count { get; }

    internal int CostEach { get; }

    internal int RefundEach { get; }
}

internal sealed class PipePrefabNumbers
{
    internal PipePrefabNumbers(double maxPressureKpa, double volumeL, double heatExchangeFactor)
    {
        MaxPressureKpa = maxPressureKpa;
        VolumeL = volumeL;
        HeatExchangeFactor = heatExchangeFactor;
    }

    internal double MaxPressureKpa { get; }

    internal double VolumeL { get; }

    internal double HeatExchangeFactor { get; }
}

/// <summary>A coil or kit the run takes: how many, how many the source holds, and the stacks.</summary>
internal sealed class UpgradeCoilView
{
    internal UpgradeCoilView(string? prefabName, string? displayName, int needed, int available,
        List<UpgradeStackView> stacks, List<PaidByView>? paidBy = null)
    {
        PaidBy = paidBy;
        PrefabName = prefabName;
        DisplayName = displayName;
        Needed = needed;
        Available = available;
        Stacks = stacks;
    }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public int Needed { get; }

    public int Available { get; }

    public List<UpgradeStackView> Stacks { get; }

    /// <summary>With from_id listing several things: what each pays, in order; left out with one source.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PaidByView>? PaidBy { get; }
}

/// <summary>A stack in the source's inventory and the thing whose slot holds it.</summary>
internal sealed class UpgradeStackView
{
    internal UpgradeStackView(ThingId referenceId, int quantity, ThingId? heldIn, int? slotIndex)
    {
        ReferenceId = referenceId;
        Quantity = quantity;
        HeldIn = heldIn;
        SlotIndex = slotIndex;
    }

    public ThingId ReferenceId { get; }

    public int Quantity { get; }

    public ThingId? HeldIn { get; }

    public int? SlotIndex { get; }
}

/// <summary>An item and a count.</summary>
internal sealed class UpgradeAmountView
{
    internal UpgradeAmountView(string? prefabName, int quantity)
    {
        PrefabName = prefabName;
        Quantity = quantity;
    }

    public string? PrefabName { get; }

    public int Quantity { get; }
}

/// <summary>A device connected to or mounted on a swapped piece.</summary>
internal sealed class UpgradeDeviceView
{
    internal UpgradeDeviceView(ThingView device, int links, bool mounted, List<ThingId> networkIds)
    {
        ReferenceId = device.ReferenceId;
        PrefabName = device.PrefabName;
        DisplayName = device.DisplayName;
        Links = links;
        Mounted = mounted;
        NetworkIds = networkIds;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>Links between it and swapped pieces (either direction).</summary>
    public int Links { get; }

    /// <summary>A cable fuse or analyser, or a pipe meter, sitting on a swapped piece's cell.</summary>
    public bool Mounted { get; }

    /// <summary>Its networks of this family (Device.ConnectedCableNetworks or ConnectedPipeNetworks).</summary>
    public List<ThingId> NetworkIds { get; }
}

/// <summary>
/// The connectivity check: the game's links now (SmallGrid.FillConnected) touching the pieces to swap, the same links
/// computed by the model from the pieces as they are (model_matches_game), and the links computed with every
/// replacement in place. added and lost list what the swap would change; the run needs both empty.
/// </summary>
internal sealed class UpgradeConnectivityView
{
    internal UpgradeConnectivityView(UpgradeLinkCounts counts, List<UpgradeLinkView> added, List<UpgradeLinkView> lost,
        List<UpgradeLinkView> modelDifferences, List<UpgradeMountedView> mounted, List<UpgradeDeviceView> devices)
    {
        LinksBefore = counts.Before;
        LinksAfterPredicted = counts.After;
        ModelMatchesGame = modelDifferences.Count == 0;
        Added = added;
        Lost = lost;
        ModelDifferences = modelDifferences;
        Mounted = mounted;
        Devices = devices;
    }

    public int LinksBefore { get; }

    public int LinksAfterPredicted { get; }

    public bool ModelMatchesGame { get; }

    public List<UpgradeLinkView> Added { get; }

    public List<UpgradeLinkView> Lost { get; }

    /// <summary>Links where the game and the model disagree about the pieces as they are now.</summary>
    public List<UpgradeLinkView> ModelDifferences { get; }

    public List<UpgradeMountedView> Mounted { get; }

    internal List<UpgradeDeviceView> Devices { get; }
}

internal sealed class UpgradeLinkCounts
{
    internal UpgradeLinkCounts(int before, int after)
    {
        Before = before;
        After = after;
    }

    internal int Before { get; }

    internal int After { get; }
}

/// <summary>A directed link: to is among what from's FillConnected finds.</summary>
internal sealed class UpgradeLinkView
{
    internal UpgradeLinkView(ThingView from, ThingView to)
    {
        FromId = from.ReferenceId;
        FromPrefabName = from.PrefabName;
        ToId = to.ReferenceId;
        ToPrefabName = to.PrefabName;
    }

    public ThingId FromId { get; }

    public string? FromPrefabName { get; }

    public ThingId ToId { get; }

    public string? ToPrefabName { get; }
}

/// <summary>A mounted device on a swapped piece: attached now, and attached after the swap as predicted.</summary>
internal sealed class UpgradeMountedView
{
    internal UpgradeMountedView(ThingId deviceId, ThingId pieceId, bool attachedBefore, bool? attachedAfter)
    {
        DeviceId = deviceId;
        PieceId = pieceId;
        AttachedBefore = attachedBefore;
        AttachedAfter = attachedAfter;
    }

    public ThingId DeviceId { get; }

    public ThingId PieceId { get; }

    public bool AttachedBefore { get; }

    /// <summary>Null when it cannot be predicted (the replacement is turned; the run is refused then).</summary>
    public bool? AttachedAfter { get; }
}

/// <summary>clean_chutes' counts for one chute network.</summary>
internal sealed class ChuteNetworkCounts
{
    internal ChuteNetworkCounts(int pieces, int removed, int replaced, int riding)
    {
        Pieces = pieces;
        Removed = removed;
        Replaced = replaced;
        Riding = riding;
    }

    internal int Pieces { get; }

    internal int Removed { get; }

    internal int Replaced { get; }

    internal int Riding { get; }
}

/// <summary>
/// A chute network a clean_chutes run touches: its pieces, how many the run removes and replaces, how many items ride
/// in it, and its devices. After a run the game has given it a new id (renumbered_from names the old one).
/// </summary>
internal sealed class ChuteNetworkReportView : IListsDevices
{
    internal ChuteNetworkReportView(ThingId networkId, ChuteNetworkCounts counts, List<ThingView> devices,
        ThingId? renumberedFrom = null)
    {
        NetworkId = networkId;
        RenumberedFrom = renumberedFrom;
        ChuteCount = counts.Pieces;
        RemoveCount = counts.Removed;
        ReplaceCount = counts.Replaced;
        ItemsRiding = counts.Riding;
        Devices = devices;
        DeviceCount = devices.Count;
    }

    public ThingId NetworkId { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? RenumberedFrom { get; }

    public int ChuteCount { get; }

    public int RemoveCount { get; }

    public int ReplaceCount { get; }

    public int ItemsRiding { get; }

    public int DeviceCount { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Devices { get; private set; }

    public object WithoutDevices()
    {
        ChuteNetworkReportView copy = (ChuteNetworkReportView)MemberwiseClone();
        copy.Devices = null;
        return copy;
    }
}

/// <summary>A cable network a swapped piece is in, before and as predicted after.</summary>
internal sealed class CableNetworkReportView : IListsDevices
{
    internal CableNetworkReportView(ThingId networkId, CableNetworkCounts counts, CableNetworkRatings ratings,
        List<ThingView> devices, ThingId? renumberedFrom = null)
    {
        NetworkId = networkId;
        RenumberedFrom = renumberedFrom;
        CableCount = counts.Cables;
        SwapCount = counts.Swaps;
        FuseCount = counts.Fuses;
        RequiredW = ratings.RequiredW;
        PotentialW = ratings.PotentialW;
        ActualW = ratings.ActualW;
        LowestCableMaxWBefore = ratings.LowestBefore;
        LowestCableMaxWAfter = ratings.LowestAfter;
        LowestFuseBreakW = ratings.LowestFuse;
        Devices = devices;
        DeviceCount = devices.Count;
    }

    public ThingId NetworkId { get; }

    /// <summary>
    /// After a run only: the id the network had before, when the game renumbered it (a network part of the swap
    /// built merged the old one into its own); null when it kept its id.
    /// </summary>
    public ThingId? RenumberedFrom { get; }

    public int CableCount { get; }

    public int SwapCount { get; }

    public int FuseCount { get; }

    public double RequiredW { get; }

    public double PotentialW { get; }

    /// <summary>min(potential, required): the flow PowerTick compares with each cable's and fuse's rating.</summary>
    public double ActualW { get; }

    public double? LowestCableMaxWBefore { get; }

    /// <summary>The weakest cable once the swap is done: its new rating.</summary>
    public double? LowestCableMaxWAfter { get; }

    public double? LowestFuseBreakW { get; }

    public int DeviceCount { get; }

    /// <summary>Left out of a dry run's report (device_count counts them); a job's verification lists them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Devices { get; private set; }

    public object WithoutDevices()
    {
        CableNetworkReportView copy = (CableNetworkReportView)MemberwiseClone();
        copy.Devices = null;
        return copy;
    }
}

internal sealed class CableNetworkCounts
{
    internal CableNetworkCounts(int cables, int swaps, int fuses)
    {
        Cables = cables;
        Swaps = swaps;
        Fuses = fuses;
    }

    internal int Cables { get; }

    internal int Swaps { get; }

    internal int Fuses { get; }
}

internal sealed class CableNetworkRatings
{
    internal CableNetworkRatings(double requiredW, double potentialW, double? lowestBefore, double? lowestAfter,
        double? lowestFuse)
    {
        RequiredW = requiredW;
        PotentialW = potentialW;
        ActualW = System.Math.Min(requiredW, potentialW);
        LowestBefore = lowestBefore;
        LowestAfter = lowestAfter;
        LowestFuse = lowestFuse;
    }

    internal double RequiredW { get; }

    internal double PotentialW { get; }

    internal double ActualW { get; }

    internal double? LowestBefore { get; }

    internal double? LowestAfter { get; }

    internal double? LowestFuse { get; }
}

/// <summary>A pipe network a swapped piece is in: its contents, and its volume and pressure before and after.</summary>
internal sealed class PipeNetworkReportView : IListsDevices
{
    internal PipeNetworkReportView(ThingId networkId, string content, PipeNetworkCounts counts,
        PipeNetworkAir before, PipeNetworkAir after, double? lowestMaxPressureKpaAfter, List<ThingView> devices,
        ThingId? renumberedFrom = null)
    {
        NetworkId = networkId;
        RenumberedFrom = renumberedFrom;
        Content = content;
        MemberCount = counts.Members;
        SwapCount = counts.Swaps;
        TotalMol = before.TotalMol;
        EnergyJ = before.EnergyJ;
        TemperatureK = before.TemperatureK;
        VolumeLBefore = before.VolumeL;
        VolumeLAfter = after.VolumeL;
        PressureKpaBefore = before.PressureKpa;
        PressureKpaAfter = after.PressureKpa;
        LowestMaxPressureKpaAfter = lowestMaxPressureKpaAfter;
        Devices = devices;
        DeviceCount = devices.Count;
    }

    public ThingId NetworkId { get; }

    /// <summary>
    /// After a run only: the id the network had before, when the game renumbered it (a network part of the swap
    /// built merged the old one into its own); null when it kept its id.
    /// </summary>
    public ThingId? RenumberedFrom { get; }

    public string Content { get; }

    public int MemberCount { get; }

    public int SwapCount { get; }

    /// <summary>The network's gas and liquid, which stays in the same Atmosphere through the swap.</summary>
    public double TotalMol { get; }

    public double EnergyJ { get; }

    public double TemperatureK { get; }

    public double VolumeLBefore { get; }

    public double VolumeLAfter { get; }

    public double PressureKpaBefore { get; }

    /// <summary>The same contents in the new volume.</summary>
    public double PressureKpaAfter { get; }

    /// <summary>The lowest Pipe.MaxPressure among the network's pipes once the swap is done.</summary>
    public double? LowestMaxPressureKpaAfter { get; }

    public int DeviceCount { get; }

    /// <summary>Left out of a dry run's report (device_count counts them); a job's verification lists them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Devices { get; private set; }

    public object WithoutDevices()
    {
        PipeNetworkReportView copy = (PipeNetworkReportView)MemberwiseClone();
        copy.Devices = null;
        return copy;
    }
}

internal sealed class PipeNetworkCounts
{
    internal PipeNetworkCounts(int members, int swaps)
    {
        Members = members;
        Swaps = swaps;
    }

    internal int Members { get; }

    internal int Swaps { get; }
}

internal sealed class PipeNetworkAir
{
    internal PipeNetworkAir(double totalMol, double energyJ, double temperatureK, double volumeL, double pressureKpa)
    {
        TotalMol = totalMol;
        EnergyJ = energyJ;
        TemperatureK = temperatureK;
        VolumeL = volumeL;
        PressureKpa = pressureKpa;
    }

    internal double TotalMol { get; }

    internal double EnergyJ { get; }

    internal double TemperatureK { get; }

    internal double VolumeL { get; }

    internal double PressureKpa { get; }
}

/// <summary>
/// A confirmed run, polled by job_id. status: waiting (for the game tick to stop), applied (swapped and every check
/// after it passed), applied_with_differences (swapped, but a check after it failed: see verification), stopped (a
/// swap failed part way: swapped lists what was done, stopped_at why), applied_unchecked (swapped, but the check
/// after it could not run: see error), gas_lost (pipe contents went missing: see gas_check), refused (nothing was
/// changed).
/// </summary>
internal sealed class UpgradeJobView : ITruncatingView
{
    internal UpgradeJobView(string jobId, string tool, string status, UpgradeReportView? preflight,
        UpgradeJobResult? result, JobPreflightSummaryView? preflightSummary = null)
    {
        PreflightSummary = preflightSummary;
        JobId = jobId;
        Tool = tool;
        Status = status;
        Preflight = preflight;
        FinalCheck = result?.FinalCheck;
        Swapped = result?.Swapped ?? new List<UpgradeSwappedView>();
        SwappedCount = Swapped.Count;
        Removed = result?.Removed;
        NotSwapped = result?.NotSwapped ?? new List<ThingId>();
        StoppedAt = result?.StoppedAt;
        Used = result?.Used ?? new List<UpgradeAmountView>();
        RefundDelivered = result?.Refunded ?? new List<UpgradeRefundView>();
        RefundError = result?.RefundError;
        Verification = result?.Verification;
        GasCheck = result?.GasCheck;
        Error = result?.Error;
    }

    public string JobId { get; }

    public string Tool { get; }

    public string Status { get; }

    /// <summary>The report when the run was confirmed; verbose only (the dry run answered it already).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public UpgradeReportView? Preflight { get; }

    /// <summary>The dry run in short, in a brief reply to the run that started the job.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JobPreflightSummaryView? PreflightSummary { get; }

    /// <summary>
    /// The same checks, made again once the game tick had stopped, right before the swap: in a refused job (it holds
    /// the problems) or with verbose; null otherwise.
    /// </summary>
    public UpgradeReportView? FinalCheck { get; }

    public List<UpgradeSwappedView> Swapped { get; }

    public int SwappedCount { get; }

    /// <summary>
    /// The job without verbose: no preflight (with summary in short instead, on the reply that starts it) and no final
    /// check unless the job was refused.
    /// </summary>
    internal UpgradeJobView Brief(JobPreflightSummaryView? summary) =>
        new UpgradeJobView(this, summary);

    private UpgradeJobView(UpgradeJobView full, JobPreflightSummaryView? summary)
    {
        JobId = full.JobId;
        Tool = full.Tool;
        Status = full.Status;
        Preflight = null;
        PreflightSummary = summary;
        FinalCheck = full.Status == JobBrief.RefusedStatus ? full.FinalCheck : null;
        Swapped = full.Swapped;
        SwappedCount = full.SwappedCount;
        Removed = full.Removed;
        NotSwapped = full.NotSwapped;
        StoppedAt = full.StoppedAt;
        Used = full.Used;
        RefundDelivered = full.RefundDelivered;
        RefundError = full.RefundError;
        Verification = full.Verification;
        GasCheck = full.GasCheck;
        Error = full.Error;
    }

    /// <summary>Pieces the clean tools removed (remove_dead_ends); absent when none was.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingId>? Removed { get; }

    /// <summary>Planned pieces left as they were (after a stop).</summary>
    public List<ThingId> NotSwapped { get; }

    public UpgradeStopView? StoppedAt { get; }

    public List<UpgradeAmountView> Used { get; }

    public List<UpgradeRefundView> RefundDelivered { get; }

    /// <summary>Why giving back stopped, when it did; the swap stands.</summary>
    public ErrorView? RefundError { get; }

    public UpgradeVerificationView? Verification { get; }

    /// <summary>The pipe networks' contents before and after (pipe jobs only).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GasCheckView? GasCheck { get; }

    public ErrorView? Error { get; }

    public void NoteTruncations(string path)
    {
        Preflight?.NoteTruncations(path + "preflight.");
        FinalCheck?.NoteTruncations(path + "final_check.");
    }
}

internal sealed class UpgradeJobResult
{
    internal UpgradeJobResult(UpgradeReportView? finalCheck, UpgradeSwapLog log, UpgradeVerificationView? verification,
        ErrorView? error, GasCheckView? gasCheck = null)
    {
        FinalCheck = finalCheck;
        Swapped = log.Swapped;
        Removed = log.Removed.Count > 0 ? log.Removed : null;
        NotSwapped = log.NotSwapped;
        StoppedAt = log.StoppedAt;
        Used = log.Used;
        Refunded = log.Refunded;
        RefundError = log.RefundError;
        Verification = verification;
        Error = error;
        GasCheck = gasCheck;
    }

    internal UpgradeReportView? FinalCheck { get; }

    internal List<UpgradeSwappedView> Swapped { get; }

    internal List<ThingId>? Removed { get; }

    internal List<ThingId> NotSwapped { get; }

    internal UpgradeStopView? StoppedAt { get; }

    internal List<UpgradeAmountView> Used { get; }

    internal List<UpgradeRefundView> Refunded { get; }

    internal ErrorView? RefundError { get; }

    internal UpgradeVerificationView? Verification { get; }

    internal ErrorView? Error { get; }

    internal GasCheckView? GasCheck { get; }
}

/// <summary>What a swap loop did, piece by piece, and what it used and gave back.</summary>
internal sealed class UpgradeSwapLog
{
    internal List<UpgradeSwappedView> Swapped { get; } = new List<UpgradeSwappedView>();

    internal List<ThingId> NotSwapped { get; } = new List<ThingId>();

    /// <summary>Pieces a clean tool removed (remove_dead_ends).</summary>
    internal List<ThingId> Removed { get; } = new List<ThingId>();

    internal UpgradeStopView? StoppedAt { get; set; }

    internal List<UpgradeAmountView> Used { get; } = new List<UpgradeAmountView>();

    internal List<UpgradeRefundView> Refunded { get; } = new List<UpgradeRefundView>();

    internal ErrorView? RefundError { get; set; }
}

internal sealed class UpgradeSwappedView
{
    internal UpgradeSwappedView(ThingId oldReferenceId, ThingId newReferenceId, string? prefabName,
        string? targetPrefabName)
    {
        OldReferenceId = oldReferenceId;
        NewReferenceId = newReferenceId;
        PrefabName = prefabName;
        TargetPrefabName = targetPrefabName;
    }

    public ThingId OldReferenceId { get; }

    public ThingId NewReferenceId { get; }

    public string? PrefabName { get; }

    public string? TargetPrefabName { get; }
}

/// <summary>The piece a stopped run stopped at, why, and whether that piece is still the old one.</summary>
internal sealed class UpgradeStopView
{
    internal UpgradeStopView(ThingId referenceId, ErrorView error, bool pieceIntact, ThingId? replacementId)
    {
        ReferenceId = referenceId;
        Error = error;
        PieceIntact = pieceIntact;
        ReplacementId = replacementId;
    }

    public ThingId ReferenceId { get; }

    public ErrorView Error { get; }

    /// <summary>The old piece is untouched (nothing was built for it).</summary>
    public bool PieceIntact { get; }

    /// <summary>A replacement built for it before the stop, if any.</summary>
    public ThingId? ReplacementId { get; }
}

/// <summary>An item given back: in the source's inventory (collected) or on the ground at the source.</summary>
internal sealed class UpgradeRefundView
{
    internal UpgradeRefundView(ThingId referenceId, string? prefabName, int quantity, string where,
        string? target = null)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        Quantity = quantity;
        Where = where;
        Target = target;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public int Quantity { get; }

    /// <summary>merged (onto a stack), slot (a new item in an empty slot) or ground.</summary>
    public string Where { get; }

    /// <summary>The refund_to target it went to: inventory, source, storage, container or ground.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Target { get; }
}

/// <summary>The checks after a swap: the game's links and networks compared with those recorded before it.</summary>
internal sealed class UpgradeVerificationView
{
    internal UpgradeVerificationView(List<UpgradeProblemView> problems, UpgradeLinkCounts counts,
        List<UpgradeLinkView> added, List<UpgradeLinkView> lost, List<object> networks)
    {
        Ok = problems.Count == 0;
        Problems = problems;
        LinksBefore = counts.Before;
        LinksAfter = counts.After;
        Added = added;
        Lost = lost;
        Networks = networks;
    }

    public bool Ok { get; }

    public List<UpgradeProblemView> Problems { get; }

    public int LinksBefore { get; }

    public int LinksAfter { get; }

    public List<UpgradeLinkView> Added { get; }

    public List<UpgradeLinkView> Lost { get; }

    /// <summary>Each network as it is after the swap (the same report shapes as before it).</summary>
    public List<object> Networks { get; }
}

/// <summary>
/// What remove_redundant decided: how many candidates it weighed, the pieces it removes, the roots it measured
/// against, and every candidate it keeps (counted by reason, up to limit listed) with the devices that need it.
/// </summary>
internal sealed class UpgradeRedundancyView
{
    internal UpgradeRedundancyView(int candidates, List<ThingId> removed, List<ThingView> root,
        Dictionary<string, int> keptByReason, List<UpgradeRedundantKeptView> kept, int keptCount)
    {
        Candidates = candidates;
        Removed = removed;
        Root = root;
        KeptByReason = keptByReason;
        Kept = kept;
        KeptCount = keptCount;
    }

    public int Candidates { get; }

    public List<ThingId> Removed { get; }

    public List<ThingView> Root { get; }

    public int KeptCount { get; }

    /// <summary>device_port, keep_ids, blocked:(why), needed.</summary>
    public Dictionary<string, int> KeptByReason { get; }

    public List<UpgradeRedundantKeptView> Kept { get; }
}

/// <summary>
/// A candidate remove_redundant keeps: device_port (devices: the ones whose port it joins), keep_ids, blocked:(why),
/// or needed (devices: those removing it would cut off from the root; pieces_cut_off: how many kept pieces with them).
/// </summary>
internal sealed class UpgradeRedundantKeptView
{
    internal UpgradeRedundantKeptView(ThingView piece, PositionView position, string reason, List<ThingView> devices,
        int piecesCutOff)
    {
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        Position = position;
        Reason = reason;
        Devices = devices;
        PiecesCutOff = piecesCutOff;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public PositionView Position { get; }

    public string Reason { get; }

    public List<ThingView> Devices { get; }

    public int PiecesCutOff { get; }
}
