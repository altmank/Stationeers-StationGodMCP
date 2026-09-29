#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>Which pieces a request names: every member of one network, or a list of pieces.</summary>
internal abstract class PieceSelection
{
    private PieceSelection()
    {
    }

    internal sealed class Network : PieceSelection
    {
        internal Network(ThingId id)
        {
            Id = id;
        }

        internal ThingId Id { get; }
    }

    internal sealed class Pieces : PieceSelection
    {
        internal Pieces(List<ThingId> ids)
        {
            Ids = ids;
        }

        internal List<ThingId> Ids { get; }
    }
}

/// <summary>One request as parsed: family, goal, pieces, coil source and options.</summary>
internal sealed class UpgradeRequest
{
    internal UpgradeRequest(UpgradeFamily family, SwapGoal goal, PieceSelection selection, ThingId? from,
        UpgradeOptions options)
    {
        Family = family;
        Goal = goal;
        Selection = selection;
        From = from;
        SkipUnmatched = options.SkipUnmatched;
        RefundTo = options.RefundTo;
        ListLimit = options.ListLimit;
    }

    internal UpgradeFamily Family { get; }

    /// <summary>What each piece is replaced with: a higher grade, or the smallest piece that serves.</summary>
    internal SwapGoal Goal { get; }

    internal PieceSelection Selection { get; }

    /// <summary>The thing coils are taken from; null for the local player.</summary>
    internal ThingId? From { get; }

    internal bool SkipUnmatched { get; }

    /// <summary>Whether anything is given back (refund_to not none, refund not false).</summary>
    internal bool Refund => RefundTo.GivesBack;

    /// <summary>refund_to (with the refund flag): where the refund goes.</summary>
    internal RefundRoute RefundTo { get; }

    internal int ListLimit { get; }
}

internal sealed class UpgradeOptions
{
    internal UpgradeOptions(bool skipUnmatched, RefundRoute refundTo, int listLimit)
    {
        SkipUnmatched = skipUnmatched;
        RefundTo = refundTo;
        ListLimit = listLimit;
    }

    internal bool SkipUnmatched { get; }

    internal RefundRoute RefundTo { get; }

    internal int ListLimit { get; }
}

/// <summary>A piece a swap takes away, with its model as it stands now.</summary>
internal sealed class OldPiece
{
    internal OldPiece(SmallGrid piece, PieceModel live)
    {
        Piece = piece;
        Live = live;
    }

    internal SmallGrid Piece { get; }

    internal PieceModel Live { get; }
}

/// <summary>
/// One swap: the pieces it takes away and the pieces it builds, the coils it takes and what it gives back. One piece
/// for one (an upgrade, a simplified junction), one for several (a split long straight, in line order), several for
/// one or more (merged straights, in line order), or one for none (a removed dead end). The first old piece names
/// the swap: its id is the group id every old and new piece of the swap is counted under when links are compared.
/// </summary>
internal sealed class PlannedSwap
{
    internal PlannedSwap(SmallGrid old, PieceModel live, Kit target, List<Twin> parts, SwapPrice price,
        CleanDetail? detail = null)
        : this(new List<OldPiece> { new OldPiece(old, live) }, target, parts, price, detail)
    {
    }

    internal PlannedSwap(List<OldPiece> olds, Kit target, List<Twin> parts, SwapPrice price, CleanDetail? detail)
    {
        Olds = olds;
        OldRotation = olds[0].Piece.ThingTransformRotation;
        Target = target;
        Parts = parts;
        Cost = price.Cost;
        Refund = price.Refund;
        Detail = detail;
    }

    /// <summary>At least one; more only when straights are merged.</summary>
    internal List<OldPiece> Olds { get; }

    /// <summary>The first old piece, which names the swap.</summary>
    internal SmallGrid Old => Olds[0].Piece;

    internal PieceModel Live => Olds[0].Live;

    internal long GroupId => Olds[0].Piece.ReferenceId;

    internal UnityEngine.Quaternion OldRotation { get; }

    internal Kit Target { get; }

    /// <summary>What is built: none for a removal, several for a split long straight.</summary>
    internal List<Twin> Parts { get; }

    internal bool Removes => Parts.Count == 0;

    /// <summary>The first replacement, for the report; null for a removal.</summary>
    internal Twin? First => Parts.Count > 0 ? Parts[0] : null;

    internal int Cost { get; }

    internal List<ItemAmount> Refund { get; }

    /// <summary>What a clean tool did with the pieces and why; null for the upgrade tools.</summary>
    internal CleanDetail? Detail { get; }

    /// <summary>Every replacement stands at exactly the first old piece's rotation.</summary>
    internal bool KeepsRotation => Parts.TrueForAll(part => part.KeepsRotation(OldRotation));

    /// <summary>The index of the replacement standing in the cell; -1 when none does.</summary>
    internal int PartIndexAt(GridCell cell) => Parts.FindIndex(part => part.Model.Occupies(cell));

    internal bool Takes(Thing member)
    {
        foreach (OldPiece old in Olds)
        {
            if (old.Piece == member)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The coils or kits a swap takes and what it gives back.</summary>
internal sealed class SwapPrice
{
    internal SwapPrice(int cost, List<ItemAmount> refund)
    {
        Cost = cost;
        Refund = refund;
    }

    internal int Cost { get; }

    internal List<ItemAmount> Refund { get; }
}

/// <summary>A clean tool's reason for a swap: the operation, the first old piece's ends, the connected ones.</summary>
internal sealed class CleanDetail
{
    internal CleanDetail(string operation, List<string> ends, List<string> connectedEnds, int? round = null)
    {
        Operation = operation;
        Ends = ends;
        ConnectedEnds = connectedEnds;
        Round = round;
    }

    /// <summary>simplify_junction, split_long_straight, merge_straights or remove_dead_end.</summary>
    internal string Operation { get; }

    internal List<string> Ends { get; }

    internal List<string> ConnectedEnds { get; }

    /// <summary>For a removal: its round (1 for a stub now, 2 for one the first round leaves, ...).</summary>
    internal int? Round { get; }
}

/// <summary>A member left as it is, and why.</summary>
internal sealed class SkippedPiece
{
    internal SkippedPiece(Thing thing, string reason, string message)
    {
        Thing = thing;
        Reason = reason;
        Message = message;
    }

    internal Thing Thing { get; }

    internal string Reason { get; }

    internal string Message { get; }
}

/// <summary>Everything the checks found for one request. Ready when no problem was found.</summary>
internal sealed class UpgradePlan
{
    internal UpgradePlan(UpgradeRequest request)
    {
        Request = request;
    }

    internal UpgradeRequest Request { get; }

    internal int Total { get; set; }

    internal List<PlannedSwap> Swaps { get; } = new List<PlannedSwap>();

    internal List<SkippedPiece> Kept { get; } = new List<SkippedPiece>();

    internal List<SkippedPiece> Unmatched { get; } = new List<SkippedPiece>();

    /// <summary>Pieces with one connected end or none the clean tools leave in place, reported.</summary>
    internal List<DeadEndPiece> DeadEnds { get; } = new List<DeadEndPiece>();

    /// <summary>The loops remove_loops found, with what it cuts; null when remove_loops was not asked for.</summary>
    internal List<LoopRecord>? Loops { get; set; }

    /// <summary>What remove_redundant removed and kept, with why; null when it was not asked for.</summary>
    internal RedundancyRecord? Redundancy { get; set; }

    internal List<UpgradeProblemView> Problems { get; } = new List<UpgradeProblemView>();

    internal Thing? From { get; set; }

    /// <summary>Where the refund goes (refund_to resolved); null until the coils are counted.</summary>
    internal RefundReceivers? Refunds { get; set; }

    internal List<ItemStock> Stocks { get; } = new List<ItemStock>();

    internal LinkSurvey? Links { get; set; }

    internal List<NetworkRecord> Networks { get; } = new List<NetworkRecord>();

    internal bool Ready => Problems.Count == 0;

    internal void Problem(string code, string message, Thing? thing = null) =>
        Problems.Add(new UpgradeProblemView(code, message, thing != null ? new ThingId(thing.ReferenceId) : null));
}

/// <summary>
/// The whole preflight: nothing here changes the game. It reads the pieces, finds each one's replacement, counts the
/// coils or kits, records the connectivity as the game has it and predicts it with every replacement in place,
/// and reads each network's state. Every problem is listed; none stops the others from being looked for.
/// </summary>
internal static class UpgradePlanner
{
    internal const int MaximumPieces = 4096;

    internal static UpgradePlan Plan(UpgradeRequest request)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host rebuilds pieces.");
        }

        UpgradePlan plan = new UpgradePlan(request);
        List<SmallGrid> members = Members(request, plan);
        plan.Total = members.Count;
        PlanContext context = new PlanContext(plan, KitCatalogue.Of(request.Family), new TwinFinder());
        foreach (SmallGrid member in members)
        {
            request.Goal.Classify(context, member);
        }

        request.Goal.Finish(context, members);

        if (plan.Unmatched.Count > 0 && !request.SkipUnmatched)
        {
            plan.Problem("unmatched_pieces",
                $"{plan.Unmatched.Count} piece(s) have no replacement (see unmatched_pieces); pass skip_unmatched to " +
                "leave them as they are.");
        }

        CountCoils(plan, request);
        if (plan.Swaps.Count > 0)
        {
            plan.Links = LinkSurvey.Take(plan.Swaps, request.Family);
            plan.Links.AddProblems(plan);
            NetworkRecord.RecordAll(plan);
        }
        else
        {
            plan.Problem("nothing_to_swap", request.Goal.NothingToSwap);
        }

        return plan;
    }

    private static List<SmallGrid> Members(UpgradeRequest request, UpgradePlan plan)
    {
        UpgradeFamily family = request.Family;
        switch (request.Selection)
        {
            case PieceSelection.Network network:
                return Capped(family.NetworkMembers(network.Id));
            case PieceSelection.Pieces pieces:
                return Capped(Listed(family, pieces.Ids, plan));
            default:
                throw ApiErrors.InvalidArgument("Pass network_id or reference_ids.");
        }
    }

    private static List<SmallGrid> Capped(List<SmallGrid> members) =>
        members.Count <= MaximumPieces
            ? members
            : throw ApiErrors.Refused("too_many_pieces",
                $"{members.Count} pieces; at most {MaximumPieces} per run. Name them with reference_ids in parts.");

    private static List<SmallGrid> Listed(UpgradeFamily family, List<ThingId> ids, UpgradePlan plan)
    {
        List<SmallGrid> pieces = new List<SmallGrid>(ids.Count);
        HashSet<long> seen = new HashSet<long>();
        foreach (ThingId id in ids)
        {
            if (!seen.Add(id.Value))
            {
                continue;
            }

            if (!GameLookup.TryFindThing(id, out Thing thing) || thing.IsBeingDestroyed)
            {
                plan.Problems.Add(new UpgradeProblemView(ApiErrors.ThingNotFoundCode,
                    $"No thing with reference id {id}.", id));
            }
            else if (!(thing is SmallGrid piece) || !family.IsPiece(thing))
            {
                plan.Problem($"not_a_{family.NetworkKind}_piece",
                    $"{Names.Of(thing)} ({thing.PrefabName}) is not a {family.NetworkKind} piece" +
                    (thing is CableRuptured
                        ? "; a burnt cable is on no network: remove it with remove_cables."
                        : "."), thing);
            }
            else
            {
                pieces.Add(piece);
            }
        }

        return pieces;
    }

    private static void CountCoils(UpgradePlan plan, UpgradeRequest request)
    {
        plan.From = Source(plan, request);
        List<GuardFinding> findings = new List<GuardFinding>();
        plan.Refunds = RefundReceivers.Resolve(request.RefundTo, plan.From, null, findings);
        foreach (GuardFinding finding in findings.FindAll(static finding => finding.Level == GuardLevel.Refusal))
        {
            // A skipped target is named in the report's refund_plan; only a refusal stops the run.
            plan.Problem(finding.Code, finding.Message);
        }

        Dictionary<int, int> needed = new Dictionary<int, int>();
        List<Kit> kits = new List<Kit>();
        foreach (PlannedSwap swap in plan.Swaps)
        {
            if (!needed.ContainsKey(swap.Target.Item.PrefabHash))
            {
                needed[swap.Target.Item.PrefabHash] = 0;
                kits.Add(swap.Target);
            }

            needed[swap.Target.Item.PrefabHash] += swap.Cost;
        }

        foreach (Kit kit in kits)
        {
            ItemStock stock = plan.From != null ? ItemStock.In(plan.From, kit.Item) : ItemStock.Empty(kit.Item);
            stock.Needed = needed[kit.Item.PrefabHash];
            plan.Stocks.Add(stock);
            if (plan.From != null && stock.Available < stock.Needed)
            {
                plan.Problem("not_enough_coils",
                    $"{stock.Needed} {Names.Of(kit.Item)} needed, {stock.Available} held by " +
                    $"{Names.Of(plan.From)}.", plan.From);
            }
        }
    }

    private static Thing? Source(UpgradePlan plan, UpgradeRequest request)
    {
        if (request.From.HasValue)
        {
            if (GameLookup.TryFindThing(request.From.Value, out Thing from) && !from.IsBeingDestroyed)
            {
                return from;
            }

            plan.Problems.Add(new UpgradeProblemView(ApiErrors.ThingNotFoundCode,
                $"No thing with reference id {request.From.Value} to take coils from and give the refund to.",
                request.From.Value));
            return null;
        }

        Human human = Human.LocalHuman;
        if (human == null)
        {
            plan.Problem("no_local_player",
                "There is no local player to take coils from and give the refund to; pass from_id.");
            return null;
        }

        return human;
    }
}
