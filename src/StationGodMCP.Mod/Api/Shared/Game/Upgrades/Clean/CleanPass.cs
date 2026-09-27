#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// One of the clean tools' operations. It plans on the pieces no earlier operation claimed: it proposes swaps
/// (CleanPass.Replace: old pieces for new ones, any number of each) or removals (CleanPass.Remove), reports what it
/// left and why, and claims every piece it decided about. Pricing, the link survey, the swap, the check after it and
/// the network records are shared and know nothing of the operation.
/// </summary>
internal interface ICleanOperation
{
    /// <summary>The name the operations argument uses (CleanOperationSet).</summary>
    string Name { get; }

    void Plan(CleanPass pass);
}

/// <summary>What the operations take from the request besides their names: the pieces whose loops stay.</summary>
internal sealed class CleanOptions
{
    internal CleanOptions(HashSet<long> keep)
    {
        Keep = keep;
    }

    internal static CleanOptions None => new CleanOptions(new HashSet<long>());

    /// <summary>remove_loops spares every loop holding one of these pieces.</summary>
    internal HashSet<long> Keep { get; }
}

/// <summary>The operations by name; the only place a new operation is registered.</summary>
internal static class CleanOperationCatalogue
{
    internal static ICleanOperation Create(string name, CleanOptions options) =>
        name switch
        {
            CleanOperationSet.RemoveDeadEnds => new RemoveDeadEnds(),
            CleanOperationSet.RemoveLoops => new RemoveLoops(options.Keep),
            CleanOperationSet.SplitLongStraights => new SplitLongStraights(),
            CleanOperationSet.MergeStraights => new MergeStraights(),
            CleanOperationSet.SimplifyJunctions => new SimplifyJunctions(),
            _ => throw ApiErrors.InvalidArgument($"Unknown operation '{name}'.")
        };

    /// <summary>The operations the request named, in run order; invalid_argument when the names do not parse.</summary>
    internal static List<ICleanOperation> Parse(IReadOnlyList<string> names, CleanOptions options)
    {
        List<string>? ordered = CleanOperationSet.Parse(names, out string? error);
        if (ordered == null)
        {
            throw ApiErrors.InvalidArgument(error ?? "operations is not valid.");
        }

        return ordered.ConvertAll(name => Create(name, options));
    }
}

/// <summary>
/// The state the operations of one request share: the selected pieces, their models now, what earlier operations
/// claimed and removed, and the one way to put a swap into the plan.
/// </summary>
internal sealed class CleanPass
{
    private readonly Dictionary<long, PieceModel> _live = new Dictionary<long, PieceModel>();
    private readonly HashSet<long> _claimed = new HashSet<long>();
    private readonly HashSet<long> _removed = new HashSet<long>();

    internal CleanPass(PlanContext context, List<SmallGrid> members)
    {
        Context = context;
        foreach (SmallGrid member in members)
        {
            if (context.Family.IsPiece(member))
            {
                Pieces.Add(member);
                _live[member.ReferenceId] = PieceShapes.Live(member);
            }
            else
            {
                context.Plan.Kept.Add(PlanContext.NotAPiece(context.Family, member));
            }
        }
    }

    internal PlanContext Context { get; }

    internal UpgradePlan Plan => Context.Plan;

    internal UpgradeFamily Family => Context.Family;

    /// <summary>Every selected run piece (not the devices on the run).</summary>
    internal List<SmallGrid> Pieces { get; } = new List<SmallGrid>();

    /// <summary>A second twin search for pieces placed away from an old piece's own position (split, merge).</summary>
    internal TwinFinder PlacedTwins { get; } = new TwinFinder();

    /// <summary>The selected pieces no operation has claimed yet.</summary>
    internal List<SmallGrid> Open => Pieces.FindAll(piece => !_claimed.Contains(piece.ReferenceId));

    internal bool IsSelected(long id) => _live.ContainsKey(id);

    internal bool IsRemoved(long id) => _removed.Contains(id);

    internal PieceModel LiveOf(SmallGrid piece) => _live[piece.ReferenceId];

    /// <summary>
    /// Every other thing in the cells the model reaches, as models, less what an earlier operation removes. Selected
    /// pieces are read once; anything else is read here.
    /// </summary>
    internal List<PieceModel> NeighboursOf(PieceModel model) => ModelsOf(Around(model), model.Id);

    internal Dictionary<long, SmallGrid> Around(PieceModel model) =>
        LinkSurvey.Neighbourhood(new List<PieceModel> { model });

    internal List<PieceModel> ModelsOf(Dictionary<long, SmallGrid> things, long except)
    {
        List<PieceModel> models = new List<PieceModel>(things.Count);
        foreach (SmallGrid thing in things.Values)
        {
            if (thing.ReferenceId != except && !_removed.Contains(thing.ReferenceId))
            {
                models.Add(_live.TryGetValue(thing.ReferenceId, out PieceModel known)
                    ? known
                    : PieceShapes.Live(thing));
            }
        }

        return models;
    }

    /// <summary>The piece's ends something is connected to, with earlier removals gone.</summary>
    internal List<PieceEnd> ConnectedEnds(PieceModel model) =>
        Connectivity.ConnectedEnds(model, NeighboursOf(model));

    /// <summary>Whether a device of this family's kind (a fuse, a meter) is mounted in one of its cells.</summary>
    internal bool HasMountedDevice(PieceModel model)
    {
        GridController world = GridController.World;
        foreach (GridCell cell in model.Cells)
        {
            Device? device = world.GetSmallCell(PieceShapes.Grid(cell))?.Device;
            if (device != null && Family.IsMountedOn(device))
            {
                return true;
            }
        }

        return false;
    }

    internal void Claim(SmallGrid piece) => _claimed.Add(piece.ReferenceId);

    /// <summary>Old pieces for new ones, after each old piece's shape check; priced by the kit's merge rule.</summary>
    internal bool Replace(List<SmallGrid> olds, Kit kit, List<Twin> parts, CleanDetail detail)
    {
        List<OldPiece> pieces = new List<OldPiece>(olds.Count);
        foreach (SmallGrid old in olds)
        {
            PieceModel live = LiveOf(old);
            if (!Context.MatchesOwnPrefab(old, live))
            {
                olds.ForEach(Claim);
                return false;
            }

            pieces.Add(new OldPiece(old, live));
        }

        foreach (SmallGrid old in olds)
        {
            Context.CheckCondition(old, parts[0]);
        }

        for (int index = 1; index < parts.Count; index++)
        {
            Context.CheckCondition(olds[0], parts[index]);
        }

        olds.ForEach(Claim);
        Plan.Swaps.Add(new PlannedSwap(pieces, kit, parts, PlanContext.Priced(pieces, kit, parts), detail));
        return true;
    }

    /// <summary>A piece taken away with nothing in its place; gives back what deconstructing it would.</summary>
    internal void Remove(SmallGrid old, Kit kit, CleanDetail detail)
    {
        List<OldPiece> pieces = new List<OldPiece> { new OldPiece(old, LiveOf(old)) };
        Claim(old);
        _removed.Add(old.ReferenceId);
        Plan.Swaps.Add(new PlannedSwap(pieces, kit, new List<Twin>(),
            new SwapPrice(0, PlanContext.RefundOf(old)), detail));
    }

    /// <summary>A dead end left in place, reported with the reason it stays (null: no removal was asked for).</summary>
    internal void DeadEnd(SmallGrid piece, List<PieceEnd> connected, string? stoppedBy, string? stopDetail = null)
    {
        Claim(piece);
        string reason = connected.Count == 0 ? "isolated" : "dead_end";
        Plan.DeadEnds.Add(new DeadEndPiece(piece, reason, LiveOf(piece), connected, stoppedBy, stopDetail));
    }

    /// <summary>Every piece no operation claimed: a dead end is reported, anything else is kept.</summary>
    internal void ReportLeftovers()
    {
        foreach (SmallGrid piece in Open)
        {
            PieceModel live = LiveOf(piece);
            List<PieceEnd> connected = ConnectedEnds(live);
            EndUse use = EndCleanup.Of(live.Ends.Count, connected.Count);
            if (use == EndUse.DeadEnd || use == EndUse.Isolated)
            {
                DeadEnd(piece, connected, null);
                continue;
            }

            Claim(piece);
            Plan.Kept.Add(use == EndUse.AllConnected
                ? new SkippedPiece(piece, "minimal", $"All {live.Ends.Count} ends of {piece.PrefabName} are connected.")
                : new SkippedPiece(piece, "unchanged",
                    $"{piece.PrefabName} has open ends; no operation asked for changes it."));
        }
    }

    internal static CleanDetail Detail(string operation, PieceModel live, List<PieceEnd> connected,
        int? round = null) =>
        new CleanDetail(operation, EndCleanup.DirectionsOf(live.Ends), EndCleanup.DirectionsOf(connected), round);

}

/// <summary>A piece with one connected end or none, as the clean tools report it.</summary>
internal sealed class DeadEndPiece
{
    internal DeadEndPiece(SmallGrid thing, string reason, PieceModel live, List<PieceEnd> connected,
        string? stoppedBy = null, string? stopDetail = null)
    {
        Thing = thing;
        Reason = reason;
        Ends = EndCleanup.DirectionsOf(live.Ends);
        Connected = EndCleanup.DirectionsOf(connected);
        StoppedBy = stopDetail != null ? $"{stoppedBy}: {stopDetail}" : stoppedBy;
    }

    internal SmallGrid Thing { get; }

    /// <summary>dead_end (one connected end) or isolated (none).</summary>
    internal string Reason { get; }

    internal List<string> Ends { get; }

    internal List<string> Connected { get; }

    /// <summary>Why remove_dead_ends left it, with any detail; null when no removal was asked for.</summary>
    internal string? StoppedBy { get; }
}

/// <summary>
/// clean_cables and clean_pipes: the requested operations, composed in their fixed order over one shared pass. Each
/// member is collected first; the operations then plan on the whole selection, each on what the earlier ones left.
/// </summary>
internal sealed class EndCleanupGoal : SwapGoal
{
    private const string CleanupNote =
        "Every connection stays, less those of removed pieces: a simplified piece has exactly the connected ends of " +
        "the piece it replaces, the singles of a split and the long piece of a merge cover exactly the old cells in " +
        "the old line, and the link survey refuses the run otherwise. A device never loses a link.";

    private const string CostNote =
        "Coils or kits are charged as the coil's or kit's own merge placement charges pieces placed over others " +
        "(MultiMergeConstructor.Construct: the new pieces' entry quantity less the old ones'). A split costs more " +
        "than it gives back; the shortfall is taken from the source's inventory (from_id, default the local player) " +
        "and the run is refused with not_enough_coils when it holds too few. Nothing is ever made for free. A merge " +
        "or a removal gives back: with refund, as deconstruction gives it, made at the source.";

    private readonly List<ICleanOperation> _operations;

    internal EndCleanupGoal(string tool, List<ICleanOperation> operations)
    {
        Tool = tool;
        _operations = operations;
    }

    internal override string Tool { get; }

    internal override string Target => "minimal";

    internal override string NothingToSwap
    {
        get
        {
            List<string> names = _operations.ConvertAll(static operation => operation.Name);
            return $"Nothing to change for {string.Join(", ", names)}.";
        }
    }

    internal override bool ReportsEnds => true;

    internal override List<string> Notes(UpgradeFamily family) =>
        new List<string> { CleanupNote, family.CleanNote, CostNote, TickNote };

    // Every member is planned in Finish, together with the others.
    internal override void Classify(PlanContext context, SmallGrid member)
    {
    }

    internal override void Finish(PlanContext context, List<SmallGrid> members)
    {
        CleanPass pass = new CleanPass(context, members);
        foreach (ICleanOperation operation in _operations)
        {
            operation.Plan(pass);
        }

        pass.ReportLeftovers();
    }
}
