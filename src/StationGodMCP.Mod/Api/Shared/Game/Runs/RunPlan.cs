#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>One request of the run tools as parsed: what to build (a run, or nothing), what to remove, the rules.</summary>
internal sealed class RunRequest
{
    internal RunRequest(RunKind kind, string tool, RunBuild? build, RunRemoval removal, RunOptions options)
    {
        Kind = kind;
        Tool = tool;
        Build = build;
        Removal = removal;
        Options = options;
    }

    internal RunKind Kind { get; }

    internal string Tool { get; }

    /// <summary>The run to build; null for the remove tools.</summary>
    internal RunBuild? Build { get; }

    internal RunRemoval Removal { get; }

    internal RunOptions Options { get; }

    /// <summary>The same request with another run (a tap added) and options.</summary>
    internal RunRequest With(RunShape shape, RunOptions options) =>
        new RunRequest(Kind, Tool, Build != null ? new RunBuild(shape, Build.Grade, Build.Join, Build.Extra) : null,
            Removal, options);
}

/// <summary>
/// A run to build: its cells (the main run in order, then any branches), the grade of new pieces, how it joins what
/// is there.
/// </summary>
internal sealed class RunBuild
{
    internal RunBuild(RunShape shape, Grade grade, JoinMode join, List<ExtraEnd> extra)
    {
        Shape = shape;
        Grade = grade;
        Join = join;
        Extra = extra;
    }

    internal RunShape Shape { get; }

    /// <summary>Every cell: the main run's in order, then each branch's.</summary>
    internal List<GridCell> Cells => Shape.Cells;

    internal Grade Grade { get; }

    internal JoinMode Join { get; }

    internal List<ExtraEnd> Extra { get; }
}

/// <summary>
/// Pieces to remove: by id, and the family's pieces standing in listed cells; and things assumed gone (assume_removed):
/// checked and forecast as if already removed, but never removed by this run (another job removes them). Alongside:
/// network members that are not pieces (an in-line tank, a passive vent) the same job removes another way
/// (remove_structure): forecast as removed, never removed by this run, and not an assume_removed.
/// </summary>
internal sealed class RunRemoval
{
    internal RunRemoval(List<ThingId> ids, List<GridCell> cells, List<ThingId>? assumed = null,
        List<ThingId>? alongside = null)
    {
        Ids = ids;
        Cells = cells;
        Assumed = assumed ?? new List<ThingId>();
        Alongside = alongside ?? new List<ThingId>();
    }

    /// <summary>Network members the same job removes itself; forecast as gone.</summary>
    internal List<ThingId> Alongside { get; }

    internal static RunRemoval None => new RunRemoval(new List<ThingId>(), new List<GridCell>());

    internal List<ThingId> Ids { get; }

    internal List<GridCell> Cells { get; }

    /// <summary>Things treated as gone without being removed (assume_removed).</summary>
    internal List<ThingId> Assumed { get; }

    internal bool IsEmpty => Ids.Count == 0 && Cells.Count == 0;
}

/// <summary>
/// What a run report lists beyond its counts: links (include_links: every link the edit adds and ends), notes
/// (include_notes: the tool's fixed explanations) and devices (include_network_devices: every device of each network
/// before and after). All are left out by default.
/// </summary>
internal sealed class RunDetail
{
    internal RunDetail(bool links, bool notes, bool devices = false)
    {
        Links = links;
        Notes = notes;
        Devices = devices;
    }

    internal static RunDetail Brief { get; } = new RunDetail(false, false);

    internal bool Links { get; }

    internal bool Notes { get; }

    internal bool Devices { get; }
}

internal sealed class RunOptions
{
    internal RunOptions(EditAllowance allow, ThingId? from, RefundRoute refundTo, int listLimit, bool splitLong = true,
        RunTargets? targets = null, bool allowDoorKeepOut = false, RunDetail? detail = null,
        List<ThingId>? moreFrom = null)
    {
        MoreFrom = moreFrom ?? new List<ThingId>();
        Detail = detail ?? RunDetail.Brief;
        AllowDoorKeepOut = allowDoorKeepOut;
        Allow = allow;
        From = from;
        RefundTo = refundTo;
        ListLimit = listLimit;
        SplitLong = splitLong;
        Targets = targets ?? RunTargets.None;
    }

    /// <summary>root, join_to and join_trunk.</summary>
    internal RunTargets Targets { get; }

    internal RunOptions WithTargets(RunTargets targets) =>
        new RunOptions(Allow, From, RefundTo, ListLimit, SplitLong, targets, AllowDoorKeepOut, Detail, MoreFrom);

    /// <summary>include_links and include_notes: what the report lists beyond its counts.</summary>
    internal RunDetail Detail { get; }

    /// <summary>allow_door_keepout: new pieces in a door's keep-out are a warning instead of a problem.</summary>
    internal bool AllowDoorKeepOut { get; }

    /// <summary>
    /// allow_split_long: a long straight the run must join in its middle or cross is split into singles in the same
    /// job (as split_long_straights does) instead of refusing with long_piece.
    /// </summary>
    internal bool SplitLong { get; }

    internal EditAllowance Allow { get; }

    /// <summary>The thing coils are taken from and given back to; null for the player.</summary>
    internal ThingId? From { get; }

    /// <summary>from_id's further things, tried after From for each coil or kit.</summary>
    internal List<ThingId> MoreFrom { get; }

    /// <summary>Whether anything is given back (refund_to not none, refund not false).</summary>
    internal bool Refund => RefundTo.GivesBack;

    /// <summary>refund_to (with the refund flag): where the refund goes.</summary>
    internal RefundRoute RefundTo { get; }

    internal int ListLimit { get; }
}

/// <summary>
/// What the run is meant to reach and feed from: root, the device a would_split measures cut-off devices against
/// (null: every supplier on the network); join_to, the network the run must end up on (checked, not_joined when it
/// does not); join_trunk, add the missing tap to join_to (a tip one cell short of it, or next to it without a matching
/// end) instead of only warning.
/// </summary>
internal sealed class RunTargets
{
    internal RunTargets(ThingId? root, ThingId? joinTo, bool joinTrunk)
    {
        Root = root;
        JoinTo = joinTo;
        JoinTrunk = joinTrunk;
    }

    internal static RunTargets None => new RunTargets(null, null, false);

    internal ThingId? Root { get; }

    internal ThingId? JoinTo { get; }

    internal bool JoinTrunk { get; }

    internal RunTargets WithJoinTo(ThingId? joinTo) => new RunTargets(Root, joinTo, JoinTrunk);

    internal RunTargets WithoutTrunk() => new RunTargets(Root, JoinTo, false);
}

/// <summary>
/// One cell to build: a new piece (Existing null) or the piece there replaced by one with more ends, the kit it comes
/// from, the choice (prefab and turn), its model, what it costs and gives back, and the id it goes by in the
/// forecast (a negative number for a new piece, the old piece's id for a change).
/// </summary>
internal sealed class PlannedCell
{
    internal PlannedCell(LayoutCell layout, Kit kit, RunChoice choice, PieceModel model, SwapPrice price,
        SmallGrid? existing, SmallGrid? splitFrom = null)
    {
        SplitFrom = splitFrom;
        Look = splitFrom != null ? PieceLook.Of(splitFrom) : null;
        Layout = layout;
        Kit = kit;
        Choice = choice;
        Model = model;
        Cost = price.Cost;
        Refund = price.Refund;
        Existing = existing;
    }

    internal LayoutCell Layout { get; }

    internal GridCell Cell => Layout.Cell;

    internal Kit Kit { get; }

    internal RunChoice Choice { get; }

    internal PieceModel Model { get; }

    internal long ForecastId => Model.Id;

    internal int Cost { get; }

    internal List<ItemAmount> Refund { get; }

    internal SmallGrid? Existing { get; }

    internal bool IsChange => Existing != null;

    /// <summary>The long straight this new piece is a single of (split in the same job); null otherwise.</summary>
    internal SmallGrid? SplitFrom { get; }

    /// <summary>The owner and colour a new piece takes over (a split long straight's); null for the player's.</summary>
    internal PieceLook? Look { get; }
}

/// <summary>A piece's owner and paint (CreateStructureInstance.OwnerClientId and CustomColor index).</summary>
internal sealed class PieceLook
{
    internal PieceLook(ulong owner, int colour)
    {
        Owner = owner;
        Colour = colour;
    }

    internal ulong Owner { get; }

    internal int Colour { get; }

    internal static PieceLook Of(Structure piece) =>
        new PieceLook(piece.OwnerClientId, piece.CustomColor != null ? piece.CustomColor.Index : 0);
}

/// <summary>
/// A piece to remove, its model now, its network and what deconstructing it gives back. Assumed: a piece the edit is
/// checked as if already gone (assume_removed); the run never removes it and its refund is not counted. Split: a long
/// straight the run splits into singles (its cells are laid again as new pieces, PlannedCell.SplitFrom).
/// </summary>
internal sealed class PlannedRemoval
{
    internal PlannedRemoval(SmallGrid piece, PieceModel live, IReferencable? network, List<ItemAmount> refund,
        bool assumed = false, bool debris = false, bool split = false)
    {
        Piece = piece;
        Live = live;
        Network = network;
        Refund = refund;
        Assumed = assumed;
        Debris = debris;
        Split = split;
    }

    internal bool Assumed { get; }

    internal bool Split { get; }

    /// <summary>What is left of a destroyed piece (a burnt cable, RunKind.IsDebris): no refund, no undo.</summary>
    internal bool Debris { get; }

    internal SmallGrid Piece { get; }

    internal PieceModel Live { get; }

    internal IReferencable? Network { get; }

    internal List<ItemAmount> Refund { get; }
}

/// <summary>Everything the preflight found for one request. Ready when no problem was found.</summary>
internal sealed class RunPlan
{
    internal RunPlan(RunRequest request)
    {
        Request = request;
    }

    internal RunRequest Request { get; }

    internal RunLayout? Layout { get; set; }

    internal List<PlannedCell> Cells { get; } = new List<PlannedCell>();

    internal List<LayoutCell> KeptCells { get; } = new List<LayoutCell>();

    /// <summary>Layout cells no piece could be chosen for (each also a problem).</summary>
    internal List<LayoutCell> Unchosen { get; } = new List<LayoutCell>();

    internal List<PlannedRemoval> Removals { get; } = new List<PlannedRemoval>();

    internal List<LayoutIssue> Problems { get; } = new List<LayoutIssue>();

    internal List<LayoutIssue> Warnings { get; } = new List<LayoutIssue>();

    /// <summary>The run's new cells on no frame and no wall plane (CellSupports).</summary>
    internal List<GridCell> AirCells { get; } = new List<GridCell>();

    internal Thing? From { get; set; }

    /// <summary>The further things materials are taken from, after From.</summary>
    internal List<Thing> MoreFrom { get; } = new List<Thing>();

    /// <summary>Where the refund goes (refund_to resolved); null until the materials are counted.</summary>
    internal RefundReceivers? Refunds { get; set; }

    internal List<ItemStock> Stocks { get; } = new List<ItemStock>();

    /// <summary>Every thing read around the edit, by id, for naming.</summary>
    internal Dictionary<long, SmallGrid> Things { get; } = new Dictionary<long, SmallGrid>();

    internal RunForecast? Forecast { get; set; }

    /// <summary>Which way items move at each touched cell after the edit (chutes only).</summary>
    internal Dictionary<GridCell, CellFlow> Flow { get; } = new Dictionary<GridCell, CellFlow>();

    internal bool Ready => Problems.Count == 0;

    /// <summary>What would_split measures cut-off devices against (the root, or every supplier port found).</summary>
    internal NetworkRootSet Roots { get; set; } = NetworkRootSet.None;

    /// <summary>The tap join_trunk added (its cells), or null.</summary>
    internal NearMiss? Tap { get; set; }

    internal void Problem(string code, string message, long? id = null, GridCell? cell = null) =>
        Problems.Add(new LayoutIssue(code, message, cell, id));

    /// <summary>Things of another kind assumed gone (assume_removed): free for placement, never removed.</summary>
    internal HashSet<long> AssumedOther { get; } = new HashSet<long>();

    /// <summary>Assumed things that still stand: a real run is refused until they are gone.</summary>
    internal List<long> AssumedPresent { get; } = new List<long>();

    /// <summary>Every thing placement and the survey treat as gone: the removals and AssumedOther.</summary>
    internal HashSet<long> IgnoredIds()
    {
        HashSet<long> ids = RemovedIds();
        ids.UnionWith(AssumedOther);
        return ids;
    }

    internal HashSet<long> RemovedIds()
    {
        HashSet<long> ids = new HashSet<long>();
        foreach (PlannedRemoval removal in Removals)
        {
            ids.Add(removal.Piece.ReferenceId);
        }

        return ids;
    }
}
