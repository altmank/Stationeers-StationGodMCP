#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Views;

/// <summary>
/// place_cables, remove_cables, place_pipes and remove_pipes before anything is changed: every cell's piece, what
/// the run joins, the materials, the networks before and as the edit would leave them, and every refusal. status is
/// dry_run, scheduled (a job was started; poll it with job_id) or refused (nothing was changed; problems says why).
/// </summary>
internal sealed class RunReportView : ITruncatingView
{
    internal RunReportView(RunHeaderView header, RunCellsView cells, RunMaterialsView materials,
        RunNetworksView networks, List<RunIssueView> problems, List<RunIssueView> warnings)
    {
        Tool = header.Tool;
        Status = header.Status;
        JobId = header.JobId;
        Grade = header.Grade;
        Ready = problems.Count == 0;
        Problems = problems;
        Warnings = warnings;
        CellsTotal = cells.Total;
        Placed = cells.Placed;
        Changed = cells.Changed;
        Kept = cells.Kept;
        RemovedCount = cells.Removed;
        AirCells = cells.Air;
        Cells = cells.Listed;
        Removals = cells.Removals;
        Materials = materials;
        NetworksBefore = networks.Before;
        NetworksAfter = networks.After;
        WouldBridge = networks.Bridges;
        WouldSplit = networks.Splits;
        Links = networks.Links;
        Notes = header.Notes;
    }

    public string Tool { get; }

    /// <summary>dry_run, scheduled or refused.</summary>
    public string Status { get; }

    public string? JobId { get; }

    /// <summary>The grade new pieces are built of (heavy, gas, insulated_liquid, ...); null for a removal.</summary>
    public string? Grade { get; }

    public bool Ready { get; }

    public List<RunIssueView> Problems { get; }

    public List<RunIssueView> Warnings { get; }

    public int CellsTotal { get; }

    public int Placed { get; }

    public int Changed { get; }

    public int Kept { get; }

    public int RemovedCount { get; }

    /// <summary>
    /// New pieces of the run on no frame and no wall plane (floating in air); 0 for a run fully over frames or walls,
    /// left out when nothing is built. through_air (a warning) lists them.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? AirCells { get; }

    /// <summary>The run's cells in order, then neighbours changed to join it; up to limit (0: none).</summary>
    public List<RunCellView> Cells { get; }

    public List<RunRemovalView> Removals { get; }

    public RunMaterialsView Materials { get; }

    /// <summary>A RunCableNetworkView or RunPipeNetworkView per network the edit touches, as it is now.</summary>
    public List<object> NetworksBefore { get; }

    /// <summary>The networks the edit would leave (their ids are the game's to give), with the guards' numbers.</summary>
    public List<RunNetworkAfterView> NetworksAfter { get; }

    public List<RunBridgeView> WouldBridge { get; }

    public List<RunSplitView> WouldSplit { get; }

    /// <summary>
    /// Links the edit makes and ends, predicted; null when nothing is built. Counts only unless include_links.
    /// </summary>
    public RunLinksView? Links { get; }

    /// <summary>The tool's fixed explanations; left out unless include_notes.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; }

    public void NoteTruncations(string path)
    {
        Truncations.Capped(path + "cells", Cells.Count, CellsTotal, "limit", Pure.RunPath.MaximumCells);
        foreach (object network in NetworksBefore)
        {
            if (network is RunChuteNetworkView chute && chute.Items != null && chute.Items.Count < chute.ItemsRiding)
            {
                Truncations.Note(path + "networks_before[].items", chute.Items.Count, chute.ItemsRiding,
                    "a network lists its first riding items only; grid_survey kinds [\"chute\"] gives each chute's " +
                    "carries");
            }
        }
    }
}

internal sealed class RunHeaderView
{
    internal RunHeaderView(string tool, string status, string? jobId, string? grade, List<string>? notes)
    {
        Tool = tool;
        Status = status;
        JobId = jobId;
        Grade = grade;
        Notes = notes;
    }

    internal string Tool { get; }

    internal string Status { get; }

    internal string? JobId { get; }

    internal string? Grade { get; }

    internal List<string>? Notes { get; }
}

/// <summary>The report's cells and removals, with the counts over all of them.</summary>
internal sealed class RunCellsView
{
    internal RunCellsView(List<RunCellView> listed, int total, int placed, int changed, int kept,
        List<RunRemovalView> removals, int? air)
    {
        Air = air;
        Listed = listed;
        Total = total;
        Placed = placed;
        Changed = changed;
        Kept = kept;
        Removals = removals;
        Removed = removals.Count;
    }

    internal List<RunCellView> Listed { get; }

    internal int Total { get; }

    internal int Placed { get; }

    internal int Changed { get; }

    internal int Kept { get; }

    internal int Removed { get; }

    internal List<RunRemovalView> Removals { get; }

    internal int? Air { get; }
}

/// <summary>A problem or a warning: code, message, and the thing or cell it is about when there is one.</summary>
internal sealed class RunIssueView
{
    internal RunIssueView(string code, string message, ThingId? referenceId, PositionView? at)
    {
        Code = code;
        Message = message;
        ReferenceId = referenceId;
        At = at;
    }

    public string Code { get; }

    public string Message { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? ReferenceId { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PositionView? At { get; }
}

/// <summary>
/// One cell: place (a new piece), change (the piece there replaced by one with more ends), keep (it already has
/// them). ends are world axes; joins say what each end meets.
/// </summary>
internal sealed class RunCellView
{
    internal RunCellView(PositionView at, string action, RunPieceView piece, RunExistingView? existing,
        List<RunJoinView> joins, string? openEnd, RunFlowView? flow = null)
    {
        At = at;
        Action = action;
        PrefabName = piece.PrefabName;
        RotationDeg = piece.RotationDeg;
        Shape = piece.Shape;
        Ends = piece.Ends;
        Cost = piece.Cost;
        Refund = piece.Refund;
        Existing = existing;
        Joins = joins;
        OpenEnd = openEnd;
        Flow = flow;
    }

    public PositionView At { get; }

    public string Action { get; }

    /// <summary>The piece standing there after the edit; null when no piece of the kit has the ends.</summary>
    public string? PrefabName { get; }

    public PositionView? RotationDeg { get; }

    public string Shape { get; }

    public List<string> Ends { get; }

    /// <summary>Coils or kits this cell takes (a change: the new piece's cost less the old one's, at least 0).</summary>
    public int Cost { get; }

    /// <summary>Coils or kits a change gives back (the old piece cost more than the new one).</summary>
    public int Refund { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunExistingView? Existing { get; }

    public List<RunJoinView> Joins { get; }

    /// <summary>The end left open on a run end with nothing to join (a straight's far end).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? OpenEnd { get; }

    /// <summary>Chutes only: which ends take items in and which let them out after the edit.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunFlowView? Flow { get; }
}

internal sealed class RunPieceView
{
    internal RunPieceView(string? prefabName, PositionView? rotationDeg, string shape, List<string> ends, int cost,
        int refund)
    {
        PrefabName = prefabName;
        RotationDeg = rotationDeg;
        Shape = shape;
        Ends = ends;
        Cost = cost;
        Refund = refund;
    }

    internal string? PrefabName { get; }

    internal PositionView? RotationDeg { get; }

    internal string Shape { get; }

    internal List<string> Ends { get; }

    internal int Cost { get; }

    internal int Refund { get; }
}

/// <summary>The piece in a cell now.</summary>
internal sealed class RunExistingView
{
    internal RunExistingView(ThingId referenceId, string prefabName, List<string> ends, ThingId? networkId)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        Ends = ends;
        NetworkId = networkId;
    }

    public ThingId ReferenceId { get; }

    public string PrefabName { get; }

    public List<string> Ends { get; }

    public ThingId? NetworkId { get; }
}

/// <summary>What one end of a cell's piece meets: run, piece, port or open.</summary>
internal sealed class RunJoinView
{
    internal RunJoinView(string toward, string kind, ThingView? thing, int? port)
    {
        Toward = toward;
        Kind = kind;
        Thing = thing;
        Port = port;
    }

    public string Toward { get; }

    public string Kind { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingView? Thing { get; }

    /// <summary>The device end's index, as connections numbers them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Port { get; }
}

/// <summary>A piece to remove and what deconstructing it gives back.</summary>
internal sealed class RunRemovalView
{
    internal RunRemovalView(ThingView piece, PositionView at, ThingId? networkId, List<UpgradeAmountView> refund,
        bool? assumed = null)
    {
        ReferenceId = piece.ReferenceId;
        PrefabName = piece.PrefabName;
        At = at;
        NetworkId = networkId;
        Refund = refund;
        Assumed = assumed;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public PositionView At { get; }

    public ThingId? NetworkId { get; }

    public List<UpgradeAmountView> Refund { get; }

    /// <summary>
    /// true for a piece in assume_removed: checked as gone, never removed by this run, its refund not counted in
    /// materials; left out otherwise.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Assumed { get; }
}

/// <summary>What the edit takes from the source and gives back to it.</summary>
internal sealed class RunMaterialsView
{
    internal RunMaterialsView(ThingView? from, List<UpgradeCoilView> needed, bool refundEnabled,
        List<UpgradeAmountView> refund, RefundPlanView? refundPlan = null)
    {
        From = from;
        Needed = needed;
        RefundEnabled = refundEnabled;
        Refund = RefundShown.Items(refundEnabled, refund);
        RefundPlan = refundEnabled ? refundPlan : null;
    }

    public ThingView? From { get; }

    /// <summary>Per coil or kit: needed, available and the stacks.</summary>
    public List<UpgradeCoilView> Needed { get; }

    public bool RefundEnabled { get; }

    /// <summary>What comes back to the source; empty with refund off.</summary>
    public List<UpgradeAmountView> Refund { get; }

    /// <summary>Where each refunded item goes (refund_to, target per part); absent with refund off.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RefundPlanView? RefundPlan { get; }
}

/// <summary>The networks part of the report.</summary>
internal sealed class RunNetworksView
{
    internal RunNetworksView(List<object> before, List<RunNetworkAfterView> after, List<RunBridgeView> bridges,
        List<RunSplitView> splits, RunLinksView? links)
    {
        Before = before;
        After = after;
        Bridges = bridges;
        Splits = splits;
        Links = links;
    }

    internal List<object> Before { get; }

    internal List<RunNetworkAfterView> After { get; }

    internal List<RunBridgeView> Bridges { get; }

    internal List<RunSplitView> Splits { get; }

    internal RunLinksView? Links { get; }
}

/// <summary>
/// A network view that lists its devices: a run report gives device_count and leaves the list out unless
/// include_network_devices (one device list per network repeated every device of a big network in every report).
/// </summary>
internal interface IListsDevices
{
    /// <summary>The same view with its device (and riding item) lists left out; the counts stay.</summary>
    object WithoutDevices();
}

/// <summary>A cable network the edit touches, as it is now.</summary>
internal sealed class RunCableNetworkView : IListsDevices
{
    internal RunCableNetworkView(ThingId networkId, int cableCount, CableNetworkRatings ratings,
        List<ThingView> devices)
    {
        NetworkId = networkId;
        CableCount = cableCount;
        RequiredW = ratings.RequiredW;
        PotentialW = ratings.PotentialW;
        ActualW = ratings.ActualW;
        LowestCableMaxW = ratings.LowestBefore;
        LowestFuseBreakW = ratings.LowestFuse;
        Devices = devices;
        DeviceCount = devices.Count;
    }

    public ThingId NetworkId { get; }

    public int CableCount { get; }

    public double RequiredW { get; }

    public double PotentialW { get; }

    public double ActualW { get; }

    public double? LowestCableMaxW { get; }

    public double? LowestFuseBreakW { get; }

    public int DeviceCount { get; }

    /// <summary>Left out of a run report unless include_network_devices; device_count counts them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Devices { get; private set; }

    public object WithoutDevices()
    {
        RunCableNetworkView copy = (RunCableNetworkView)MemberwiseClone();
        copy.Devices = null;
        return copy;
    }
}

/// <summary>A pipe network the edit touches, as it is now: content kind, air, and the main gases.</summary>
internal sealed class RunPipeNetworkView : IListsDevices
{
    internal RunPipeNetworkView(ThingId networkId, string content, int memberCount, PipeNetworkAir air,
        double? lowestMaxPressureKpa, List<RunGasView> gases, List<ThingView> devices)
    {
        NetworkId = networkId;
        Content = content;
        MemberCount = memberCount;
        TotalMol = air.TotalMol;
        EnergyJ = air.EnergyJ;
        TemperatureK = air.TemperatureK;
        VolumeL = air.VolumeL;
        PressureKpa = air.PressureKpa;
        LowestMaxPressureKpa = lowestMaxPressureKpa;
        Gases = gases;
        Devices = devices;
        DeviceCount = devices.Count;
    }

    public ThingId NetworkId { get; }

    public string Content { get; }

    public int MemberCount { get; }

    public double TotalMol { get; }

    public double EnergyJ { get; }

    public double TemperatureK { get; }

    public double VolumeL { get; }

    public double PressureKpa { get; }

    public double? LowestMaxPressureKpa { get; }

    /// <summary>Gases and liquids over 0.1 % of the moles, most first.</summary>
    public List<RunGasView> Gases { get; }

    public int DeviceCount { get; }

    /// <summary>Left out of a run report unless include_network_devices; device_count counts them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Devices { get; private set; }

    public object WithoutDevices()
    {
        RunPipeNetworkView copy = (RunPipeNetworkView)MemberwiseClone();
        copy.Devices = null;
        return copy;
    }
}

/// <summary>A chute network the edit touches, as it is now: its pieces, the items riding in them, its devices.</summary>
internal sealed class RunChuteNetworkView : IListsDevices
{
    internal RunChuteNetworkView(ThingId networkId, int chuteCount, int itemsRiding, List<RunChuteItemView> items,
        List<ThingView> devices)
    {
        NetworkId = networkId;
        ChuteCount = chuteCount;
        ItemsRiding = itemsRiding;
        Items = items;
        Devices = devices;
        DeviceCount = devices.Count;
    }

    public ThingId NetworkId { get; }

    public int ChuteCount { get; }

    /// <summary>How many of its pieces carry an item now.</summary>
    public int ItemsRiding { get; }

    /// <summary>
    /// The first of those items, with the piece each rides in; left out of a run report unless
    /// include_network_devices, as the devices are (items_riding counts them).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<RunChuteItemView>? Items { get; private set; }

    public int DeviceCount { get; }

    /// <summary>Left out of a run report unless include_network_devices; device_count counts them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Devices { get; private set; }

    public object WithoutDevices()
    {
        RunChuteNetworkView copy = (RunChuteNetworkView)MemberwiseClone();
        copy.Devices = null;
        copy.Items = null;
        return copy;
    }
}

/// <summary>An item riding in a chute piece.</summary>
internal sealed class RunChuteItemView
{
    internal RunChuteItemView(ThingId chute, ThingView item)
    {
        Chute = chute;
        Item = item;
    }

    public ThingId Chute { get; }

    public ThingView Item { get; }
}

/// <summary>Which of a chute cell's ends take items in and which let them out after the edit (world directions).</summary>
internal sealed class RunFlowView
{
    internal RunFlowView(List<string> into, List<string> outOf)
    {
        In = into;
        Out = outOf;
    }

    public List<string> In { get; }

    public List<string> Out { get; }
}

internal sealed class RunGasView
{
    internal RunGasView(string gas, double mol, double fraction)
    {
        Gas = gas;
        Mol = mol;
        Fraction = fraction;
    }

    public string Gas { get; }

    public double Mol { get; }

    public double Fraction { get; }
}

/// <summary>
/// A network as the edit would leave it: which networks before it holds all or part of, how many new pieces, the
/// devices on it with the port each joins by, and the kind's guard numbers (power: flow against the weakest cable;
/// pipes: the pooled contents' pressure against the weakest pipe).
/// </summary>
internal sealed class RunNetworkAfterView : IListsDevices
{
    internal RunNetworkAfterView(int index, List<ThingId> networksBefore, int newPieces, List<RunPortView> devices,
        object? guard)
    {
        Index = index;
        NetworksBefore = networksBefore;
        NewPieces = newPieces;
        Devices = devices;
        DeviceCount = devices.Count;
        Guard = guard;
    }

    public int Index { get; }

    public List<ThingId> NetworksBefore { get; }

    public int NewPieces { get; }

    public int DeviceCount { get; }

    /// <summary>The device ports on it; left out unless include_network_devices (device_count counts them).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<RunPortView>? Devices { get; private set; }

    /// <summary>A RunPowerAfterView or RunPipeAfterView; null when there is nothing to check.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Guard { get; }

    public object WithoutDevices()
    {
        RunNetworkAfterView copy = (RunNetworkAfterView)MemberwiseClone();
        copy.Devices = null;
        return copy;
    }
}

/// <summary>A device port and the network it is on now (null for none).</summary>
internal sealed class RunPortView
{
    internal RunPortView(ThingView device, int port, bool bridging, ThingId? networkBefore)
    {
        Device = device;
        Port = port;
        Bridging = bridging;
        NetworkBefore = networkBefore;
    }

    public ThingView Device { get; }

    public int Port { get; }

    /// <summary>
    /// The edit bridges the device through this port: it ends on one network with another of the device's ports that it
    /// was not on one network with before (would_bridge names the device). False for a port whose network does not
    /// change.
    /// </summary>
    public bool Bridging { get; }

    public ThingId? NetworkBefore { get; }
}

internal sealed class RunPowerAfterView
{
    internal RunPowerAfterView(double potentialW, double requiredW, double flowW, double? lowestCableMaxW,
        double? lowestFuseBreakW, bool overloads)
    {
        PotentialW = potentialW;
        RequiredW = requiredW;
        FlowW = flowW;
        LowestCableMaxW = lowestCableMaxW;
        LowestFuseBreakW = lowestFuseBreakW;
        Overloads = overloads;
    }

    public double PotentialW { get; }

    public double RequiredW { get; }

    /// <summary>min(potential, required): what PowerTick compares with each fuse and cable.</summary>
    public double FlowW { get; }

    public double? LowestCableMaxW { get; }

    public double? LowestFuseBreakW { get; }

    public bool Overloads { get; }
}

internal sealed class RunPipeAfterView
{
    internal RunPipeAfterView(PipeNetworkAir air, double? lowestMaxPressureKpa, bool wouldBurst)
    {
        TotalMol = air.TotalMol;
        EnergyJ = air.EnergyJ;
        TemperatureK = air.TemperatureK;
        VolumeL = air.VolumeL;
        PressureKpa = air.PressureKpa;
        LowestMaxPressureKpa = lowestMaxPressureKpa;
        WouldBurst = wouldBurst;
    }

    public double TotalMol { get; }

    public double EnergyJ { get; }

    public double TemperatureK { get; }

    public double VolumeL { get; }

    public double PressureKpa { get; }

    public double? LowestMaxPressureKpa { get; }

    public bool WouldBurst { get; }
}

/// <summary>A join the caller must name in allow_bridge: networks merged, or two ports of one device joined.</summary>
internal sealed class RunBridgeView
{
    internal RunBridgeView(string kind, List<RunBridgeSideView> networks, ThingView? device, List<int> ports,
        bool allowed)
    {
        Kind = kind;
        Networks = networks;
        Device = device;
        Ports = ports;
        Allowed = allowed;
    }

    /// <summary>networks (two or more networks become one) or device (two ports of one device on one network).</summary>
    public string Kind { get; }

    public List<RunBridgeSideView> Networks { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingView? Device { get; }

    public List<int> Ports { get; }

    /// <summary>allow_bridge names it.</summary>
    public bool Allowed { get; }
}

/// <summary>One network on a side of a bridge, with the devices on it now.</summary>
internal sealed class RunBridgeSideView
{
    internal RunBridgeSideView(ThingId networkId, List<ThingView> devices)
    {
        NetworkId = networkId;
        Devices = devices;
    }

    public ThingId NetworkId { get; }

    public List<ThingView> Devices { get; }
}

/// <summary>
/// A network that would fall apart, the parts (networks_after indexes), and ports left joined to nothing. components:
/// each part with its devices and whether it holds a root; root: the devices that feed the network (the request's
/// root, else every supplier on it); cut_off: the devices no root reaches after the edit (null: no root on it).
/// </summary>
internal sealed class RunSplitView
{
    internal RunSplitView(ThingId? networkId, List<int> parts, List<RunPortView> cutPorts, bool allowed,
        RunSplitDevicesView? devices = null)
    {
        NetworkId = networkId;
        Parts = parts;
        CutPorts = cutPorts;
        Allowed = allowed;
        Components = devices?.Components ?? new List<RunSplitPartView>();
        Root = devices?.Roots ?? new List<ThingView>();
        CutOff = devices?.CutOff;
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? NetworkId { get; }

    public List<int> Parts { get; }

    public List<RunPortView> CutPorts { get; }

    public bool Allowed { get; }

    public List<RunSplitPartView> Components { get; }

    public List<ThingView> Root { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public List<ThingView>? CutOff { get; }
}

/// <summary>The device side of a split: every part's devices, the roots found and the devices cut off from them.</summary>
internal sealed class RunSplitDevicesView
{
    internal RunSplitDevicesView(List<RunSplitPartView> components, List<ThingView> roots, List<ThingView>? cutOff)
    {
        Components = components;
        Roots = roots;
        CutOff = cutOff;
    }

    internal List<RunSplitPartView> Components { get; }

    internal List<ThingView> Roots { get; }

    internal List<ThingView>? CutOff { get; }
}

/// <summary>One network a split leaves (networks_after index), its device ports and whether a root is among them.</summary>
internal sealed class RunSplitPartView
{
    internal RunSplitPartView(int index, List<RunPortView> devices, bool holdsRoot)
    {
        Index = index;
        Devices = devices;
        HoldsRoot = holdsRoot;
    }

    public int Index { get; }

    public List<RunPortView> Devices { get; }

    public bool HoldsRoot { get; }
}

/// <summary>
/// Links around the edit: the game's now, the ones the edit adds and ends (predicted with every new piece in
/// place), and whether the model reads the game's links exactly where it looked.
/// </summary>
internal sealed class RunLinksView
{
    internal RunLinksView(int before, int after, List<UpgradeLinkView> added, List<UpgradeLinkView> lost,
        List<UpgradeLinkView> modelDifferences)
    {
        CountBefore = before;
        CountAfter = after;
        Added = added;
        Lost = lost;
        AddedCount = added.Count;
        LostCount = lost.Count;
        ModelMatchesGame = modelDifferences.Count == 0;
        ModelDifferences = modelDifferences;
    }

    private RunLinksView(RunLinksView links)
    {
        CountBefore = links.CountBefore;
        CountAfter = links.CountAfter;
        AddedCount = links.AddedCount;
        LostCount = links.LostCount;
        ModelMatchesGame = links.ModelMatchesGame;
        ModelDifferences = links.ModelDifferences;
    }

    public int CountBefore { get; }

    public int CountAfter { get; }

    /// <summary>Every link the edit adds; left out unless include_links (added_count counts them).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeLinkView>? Added { get; }

    /// <summary>Every link the edit ends; left out unless include_links (lost_count counts them).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeLinkView>? Lost { get; }

    public int AddedCount { get; }

    public int LostCount { get; }

    public bool ModelMatchesGame { get; }

    /// <summary>Where the model reads the game's links differently; always listed (empty when it matches).</summary>
    public List<UpgradeLinkView> ModelDifferences { get; }

    /// <summary>The counts without the added and lost lists (a report without include_links).</summary>
    internal RunLinksView CountsOnly() => new RunLinksView(this);
}

/// <summary>
/// A confirmed run, polled by job_id. status: waiting (for the game tick), applied (built and every check passed),
/// applied_with_differences (built, but a check found a difference: see verification), stopped (a step failed part
/// way: log says what was done), applied_unchecked (built, the check could not run), refused (nothing changed).
/// </summary>
internal sealed class RunJobView : ITruncatingView
{
    private const string RefusedStatus = "refused";

    internal RunJobView(string jobId, string tool, string status, RunReportView? preflight, RunJobResultView? result,
        JobPreflightSummaryView? preflightSummary = null)
    {
        PreflightSummary = preflightSummary;
        JobId = jobId;
        Tool = tool;
        Status = status;
        Preflight = preflight;
        FinalCheck = result?.FinalCheck;
        Log = result?.Log;
        Verification = result?.Verification;
        GasCheck = result?.GasCheck;
        Error = result?.Error;
    }

    public string JobId { get; }

    public string Tool { get; }

    public string Status { get; }

    /// <summary>The dry run made when the run was asked for; null in a brief poll.</summary>
    public RunReportView? Preflight { get; }

    /// <summary>The dry run in short, in a brief reply to the run that started the job.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JobPreflightSummaryView? PreflightSummary { get; }

    /// <summary>The whole check made as the job started; left out of a brief poll unless the job was refused.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunReportView? FinalCheck { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunLogView? Log { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunVerificationView? Verification { get; }

    /// <summary>The pipe networks' contents before and after (pipe runs only).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GasCheckView? GasCheck { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ErrorView? Error { get; }

    /// <summary>
    /// A poll without verbose: a run job without its preflight and final check, the reports the real run already
    /// answered with; a refused job keeps its final check, which holds the problems it refused for. Any other view
    /// (queued, dropped, another tool's job) is returned as it is.
    /// </summary>
    internal static object Brief(object polled) => polled is RunJobView job ? Brief(job, null) : polled;

    /// <summary>The job as a brief poll gives it, with the preflight in short when summary is given.</summary>
    internal static RunJobView Brief(RunJobView job, JobPreflightSummaryView? summary) =>
        new RunJobView(job.JobId, job.Tool, job.Status, null,
            new RunJobResultView(job.Status == RefusedStatus ? job.FinalCheck : null, job.Log?.Brief(),
                job.Verification, job.Error, job.GasCheck), summary);

    public void NoteTruncations(string path)
    {
        Preflight?.NoteTruncations(path + "preflight.");
        FinalCheck?.NoteTruncations(path + "final_check.");
        if (Log != null && Log.Placed == null && Log.PlacedCount > 0)
        {
            Truncations.Note(path + "log.placed", 0, Log.PlacedCount,
                "poll with job_id and verbose: true (log.created_ids lists every id built)");
        }
    }
}

internal sealed class RunJobResultView
{
    internal RunJobResultView(RunReportView? finalCheck, RunLogView? log, RunVerificationView? verification,
        ErrorView? error, GasCheckView? gasCheck = null)
    {
        FinalCheck = finalCheck;
        Log = log;
        Verification = verification;
        Error = error;
        GasCheck = gasCheck;
    }

    internal GasCheckView? GasCheck { get; }

    internal RunReportView? FinalCheck { get; }

    internal RunLogView? Log { get; }

    internal RunVerificationView? Verification { get; }

    internal ErrorView? Error { get; }
}

/// <summary>
/// What a run did: pieces removed, changed (old for new) and placed, coils used and given back. A brief poll's log
/// (Brief) counts the placed pieces instead of naming each: created_ids already holds every id built.
/// </summary>
internal sealed class RunLogView
{
    private bool _brief;

    public List<ThingId> Removed { get; } = new List<ThingId>();

    public List<UpgradeSwappedView> Changed { get; } = new List<UpgradeSwappedView>();

    /// <summary>The new pieces, each named; left out of a brief poll (placed_count counts them).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Placed => _brief ? null : PlacedPieces;

    public int PlacedCount => PlacedPieces.Count;

    /// <summary>The new pieces as the builder records them.</summary>
    internal List<ThingView> PlacedPieces { get; } = new List<ThingView>();

    public List<UpgradeAmountView> Used { get; } = new List<UpgradeAmountView>();

    public List<UpgradeRefundView> Refunded { get; } = new List<UpgradeRefundView>();

    /// <summary>
    /// The reference id of every piece the run built, new and changed (a changed piece is a new thing with a new id),
    /// in build order: the keep set for clean_* remove_redundant, without guessing from id ranges.
    /// </summary>
    public List<ThingId> CreatedIds { get; } = new List<ThingId>();

    /// <summary>The same ids by part of the run: run, branch N, joined (a neighbour made a junction), fill.</summary>
    public List<RunCreatedPartView> CreatedByPart { get; } = new List<RunCreatedPartView>();

    /// <summary>The step that failed and why; null when every step was done.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ErrorView? StoppedAt { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ErrorView? RefundError { get; set; }

    /// <summary>The same log as a brief poll gives it: placed counted, not listed.</summary>
    internal RunLogView Brief()
    {
        RunLogView brief = (RunLogView)MemberwiseClone();
        brief._brief = true;
        return brief;
    }

    internal void AddCreated(string part, ThingId id)
    {
        CreatedIds.Add(id);
        RunCreatedPartView? entry = CreatedByPart.Find(known => known.Part == part);
        if (entry == null)
        {
            entry = new RunCreatedPartView(part);
            CreatedByPart.Add(entry);
        }

        entry.Ids.Add(id);
    }
}

/// <summary>One part of a run (run, branch N, joined, fill) and the ids built for it.</summary>
internal sealed class RunCreatedPartView
{
    internal RunCreatedPartView(string part)
    {
        Part = part;
    }

    public string Part { get; }

    public List<ThingId> Ids { get; } = new List<ThingId>();
}

/// <summary>
/// The check after a run: the links around it are the predicted ones, and the networks are as forecast (the pieces
/// of one predicted network share one network now, those of different ones do not, and every device port is where
/// the forecast put it).
/// </summary>
internal sealed class RunVerificationView
{
    internal RunVerificationView(List<RunIssueView> problems, List<ThingId> networksNow)
    {
        Ok = problems.Count == 0;
        Problems = problems;
        NetworksNow = networksNow;
    }

    public bool Ok { get; }

    public List<RunIssueView> Problems { get; }

    /// <summary>The ids of the networks the edited pieces are in now.</summary>
    public List<ThingId> NetworksNow { get; }
}

/// <summary>
/// plan_cable_route and plan_pipe_route: the route found (or why none was), the arguments that build it with
/// place_cables or place_pipes, and that tool's dry run of it.
/// </summary>
internal sealed class PlanRouteView : ITruncatingView
{
    internal PlanRouteView(string tool, RouteView? route, string? failure, object? placeArguments,
        RunReportView? dryRun, List<string> notes)
    {
        Tool = tool;
        Found = route != null;
        Route = route;
        Failure = failure;
        PlaceArguments = placeArguments;
        DryRun = dryRun;
        Notes = notes;
    }

    public string Tool { get; }

    public bool Found { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RouteView? Route { get; }

    /// <summary>no_route, too_long or search_limit, with why.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Failure { get; }

    /// <summary>The arguments for the place tool (add dry_run false and confirm true to build).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? PlaceArguments { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunReportView? DryRun { get; }

    public List<string> Notes { get; }

    public void NoteTruncations(string path) => DryRun?.NoteTruncations(path + "dry_run.");
}

internal sealed class RouteView
{
    internal RouteView(List<PositionView> waypoints, int length, int bends, double cost, int expanded,
        List<ThingId> removes, int airCells, List<PositionView>? air, List<RouteBranchView>? branches = null,
        int? extraEnds = null, RouteVisibilityView? visibility = null, RouteAssumedView? assumedRemoved = null,
        List<UpgradeAmountView>? removalRefund = null)
    {
        Visibility = visibility;
        AssumedRemoved = assumedRemoved;
        RemovalRefund = removalRefund;
        AirCells = airCells;
        Air = air;
        Branches = branches;
        ExtraEnds = extraEnds;
        Waypoints = waypoints;
        Length = length;
        Bends = bends;
        Cost = cost;
        Expanded = expanded;
        Removes = removes;
    }

    /// <summary>First cell, every turn, last cell.</summary>
    public List<PositionView> Waypoints { get; }

    /// <summary>Cells.</summary>
    public int Length { get; }

    public int Bends { get; }

    public double Cost { get; }

    /// <summary>Cells the search looked at.</summary>
    public int Expanded { get; }

    /// <summary>Pieces the reroute removes (the old run).</summary>
    public List<ThingId> Removes { get; }

    /// <summary>New cells (branches included) on no frame and no wall plane: 0 for a route fully over frames or walls.</summary>
    public int AirCells { get; }

    /// <summary>Where those cells are (through_air); left out when there are none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PositionView>? Air { get; }

    /// <summary>Several starts: each other start's branch, joined to the tree by a junction where it attaches.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<RouteBranchView>? Branches { get; }

    /// <summary>Starts the tree already passes through, joined there by an extra end.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? ExtraEnds { get; }

    /// <summary>The new cells (branches included) by how visible a piece in them is.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RouteVisibilityView? Visibility { get; }

    /// <summary>What assume_removed named, and which of it stands in the route's way; left out without it.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RouteAssumedView? AssumedRemoved { get; }

    /// <summary>
    /// What deconstructing the pieces the plan removes (the reroute's old run and the kind's assumed pieces) gives
    /// back, as remove_* would refund it; left out when nothing is removed.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UpgradeAmountView>? RemovalRefund { get; }
}

/// <summary>
/// Cells counted by how visible a piece in them is: inside a frame (every 2 m cell the small cell touches holds a
/// frame), on a frame's surface (face, edge or corner), on a wall's plane only, in air.
/// </summary>
internal sealed class RouteVisibilityView
{
    internal RouteVisibilityView(int inside, int frameSurface, int wall, int air)
    {
        Inside = inside;
        FrameSurface = frameSurface;
        Wall = wall;
        Air = air;
    }

    public int Inside { get; }

    public int FrameSurface { get; }

    public int Wall { get; }

    public int Air { get; }
}

/// <summary>
/// assume_removed as the plan used it: the kind's pieces (removed in the same job: place_arguments.remove_ids), other
/// things (only freed for the plan; their own tool removes them first: place_arguments.assume_removed), ids naming
/// nothing standing, and the pieces whose cells the new route takes (they cannot stay until a later job).
/// </summary>
internal sealed class RouteAssumedView
{
    internal RouteAssumedView(List<ThingId> pieces, List<ThingId> others, List<ThingId> missing,
        List<ThingId> inTheWay)
    {
        Pieces = pieces;
        Others = others;
        Missing = missing;
        InTheWay = inTheWay;
    }

    public List<ThingId> Pieces { get; }

    public List<ThingId> Others { get; }

    public List<ThingId> Missing { get; }

    public List<ThingId> InTheWay { get; }
}

/// <summary>A branch of a route: its waypoints from its start, its length, and the tree cell it joins.</summary>
internal sealed class RouteBranchView
{
    internal RouteBranchView(List<PositionView> waypoints, int length, PositionView attach)
    {
        Waypoints = waypoints;
        Length = length;
        Attach = attach;
    }

    public List<PositionView> Waypoints { get; }

    public int Length { get; }

    public PositionView Attach { get; }
}
