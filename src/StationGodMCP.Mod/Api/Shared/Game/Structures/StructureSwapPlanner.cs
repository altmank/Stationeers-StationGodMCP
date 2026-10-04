#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// The whole preflight of replace_walls and replace_frames: nothing here changes the game. It reads the pieces,
/// resolves each one's target, compares their slots, judges what each would open or seal, runs the family's checks,
/// counts the materials and records the rooms and air around the pieces. Every problem is listed; none stops the
/// others from being looked for.
/// </summary>
internal static class StructureSwapPlanner
{
    internal static StructureSwapPlan Plan(StructureSwapRequest request)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host rebuilds pieces.");
        }

        if (GridController.World == null || RoomController.World == null || AtmosphericsController.World == null)
        {
            throw ApiErrors.Refused("not_ready", "The world grid, rooms or atmospherics are not loaded.");
        }

        StructureSwapPlan plan = new StructureSwapPlan(request);
        List<Structure> members = Members(request, plan);
        plan.Total = members.Count;
        StructureTargets targets = StructureTargets.Load();
        Structure? named = NamedTarget(request, targets, plan);
        if (request.Arguments.To != null && named == null)
        {
            return plan;
        }

        BuildCatalogue catalogue = BuildCatalogue.Load();
        foreach (Structure member in members)
        {
            Classify(plan, targets, catalogue, named, member);
        }

        if (plan.Unmatched.Count > 0 && !request.Arguments.SkipUnmatched)
        {
            plan.Problem("unmatched_pieces",
                $"{plan.Unmatched.Count} piece(s) cannot be swapped (see unmatched_pieces); pass skip_unmatched to " +
                "leave them as they are.");
        }

        CountMaterials(plan);
        if (plan.Swaps.Count == 0)
        {
            plan.Problem("nothing_to_swap", $"No {request.Family.Noun} in scope needs replacing.");
            return plan;
        }

        plan.Air = StructureAirRecord.Take(plan.Swaps);
        return plan;
    }

    private static Structure? NamedTarget(StructureSwapRequest request, StructureTargets targets,
        StructureSwapPlan plan)
    {
        string? name = request.Arguments.To;
        if (name == null)
        {
            return null;
        }

        Structure? target = StructureTargets.Named(name);
        string? issue = target == null ? $"No loaded prefab is named {name}." : targets.IssueOf(target, request.Family);
        if (issue != null)
        {
            plan.Problem("invalid_target", issue);
            return null;
        }

        return target;
    }

    private static List<Structure> Members(StructureSwapRequest request, StructureSwapPlan plan)
    {
        List<Structure> members = new List<Structure>();
        HashSet<long> seen = new HashSet<long>();
        switch (request.Arguments.Scope)
        {
            case StructureScope.Pieces pieces:
                Listed(request.Family, pieces.Ids, plan, members, seen);
                break;
            case StructureScope.Room room:
                Room found = StructureAirRecord.FindRoom(room.Id) ??
                             throw ApiErrors.Refused("room_not_found",
                                 $"No room {room.Id}; take a room_id from rooms.");
                request.Family.CollectInRoom(found, members, seen);
                break;
        }

        return members.Count <= StructureSwapArgs.MaximumPieces
            ? members
            : throw ApiErrors.Refused("too_many_pieces",
                $"{members.Count} pieces; at most {StructureSwapArgs.MaximumPieces} per run.");
    }

    private static void Listed(StructureFamily family, List<ThingId> ids, StructureSwapPlan plan,
        List<Structure> members, HashSet<long> seen)
    {
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
            else if (!(thing is Structure structure) || !family.IsMember(thing))
            {
                plan.Problem($"not_a_{family.Noun}",
                    $"{Names.Of(thing)} ({thing.PrefabName}) is not a {family.Noun}.", thing);
            }
            else
            {
                members.Add(structure);
            }
        }
    }

    private static void Classify(StructureSwapPlan plan, StructureTargets targets, BuildCatalogue catalogue,
        Structure? named, Structure member)
    {
        StructureFamily family = plan.Request.Family;
        string? kept = KeptReason(plan.Request, member, out string message);
        if (kept != null)
        {
            plan.Kept.Add(new SkippedPiece(member, kept, message));
            return;
        }

        Structure? target = named != null ? named : family.DefaultTarget(member);
        string? issue = target == null
            ? $"{member.PrefabName} is not a loaded prefab."
            : targets.IssueOf(target, family);
        if (issue != null)
        {
            plan.Unmatched.Add(new SkippedPiece(member, "invalid_target", issue));
            return;
        }

        int final = target!.BuildStates.Count - 1;
        if (target.PrefabHash == member.PrefabHash && member.CurrentBuildStateIndex == final)
        {
            plan.Kept.Add(new SkippedPiece(member, "already_at_target",
                $"{member.PrefabName} is already at its final build state."));
            return;
        }

        List<StructureSlot> slots = StructureSlots.Live(member);
        List<StructureSlot>? predicted = StructureSlots.Predicted(target, member);
        if (predicted == null || slots.Count == 0 || !FaceMath.SameSlots(slots, predicted))
        {
            plan.Unmatched.Add(new SkippedPiece(member, "footprint_mismatch",
                $"{target.PrefabName} would not register in exactly the slots {member.PrefabName} holds " +
                $"({Describe(slots)} against {Describe(predicted)})."));
            return;
        }

        BuildState last = target.BuildStates[final];
        PlannedStructureSwap swap = new PlannedStructureSwap(member, target, slots,
            new PieceBlocking(!member.CanAirPass, !member.CanGravityPass),
            new PieceBlocking(last.BlockAir, last.BlockGravity));
        if (swap.Change == AirChange.WouldOpen)
        {
            string opened = swap.Before.Air && !swap.After.Air ? "air" : "gravity";
            plan.Problem("would_open",
                $"{member.PrefabName} {member.ReferenceId} blocks {opened}; " +
                $"{target.PrefabName} at its final state does not, so the swap would open it.", member);
        }

        Placeable(plan, catalogue, member, target);
        AddMaterials(plan, swap);
        family.Assess(swap, plan);
        swap.AffectedCells = family.AffectedCells(swap);
        plan.Swaps.Add(swap);
    }

    // The player-placement rule (PlayerPlacement.Replacing): a player could place the new piece where the old one
    // stands once it is gone. Without a placement cursor for the target it cannot be asked and is not refused.
    private static void Placeable(StructureSwapPlan plan, BuildCatalogue catalogue, Structure member,
        Structure target)
    {
        PlacementVerdict verdict = PlayerPlacement.Replacing(target, catalogue.CursorOf(target),
            member.ThingTransformPosition, member.ThingTransformRotation, new[] { member });
        if (verdict is PlacementVerdict.Refused refused)
        {
            plan.Problem("cannot_place",
                $"{target.PrefabName} where {member.PrefabName} {member.ReferenceId} stands: a player could not " +
                $"place it there ({refused.Reason}).", member);
        }
    }

    private static string? KeptReason(StructureSwapRequest request, Structure member, out string message)
    {
        List<string>? only = request.Arguments.FromPrefabs;
        if (only != null && !only.Contains(member.PrefabName))
        {
            message = $"{member.PrefabName} is not among from_prefabs.";
            return "not_selected";
        }

        if (!request.Family.IsPlain(member))
        {
            message = $"{member.PrefabName} is a {member.GetType().Name}, not {request.Family.PlainClasses}; " +
                      "it is never touched.";
            return "special_piece";
        }

        if (member.IsBeingDestroyed)
        {
            message = "It is being destroyed.";
            return "being_destroyed";
        }

        if (member.Indestructable)
        {
            message = "It is indestructible.";
            return "indestructible";
        }

        if (member.CurrentBuildStateIndex < 0 || member.BuildStates == null || member.BuildStates.Count == 0)
        {
            message = "It is broken (the game's broken state; the game cannot repair it, only deconstruct it); " +
                      "remove it with remove_structure allow_broken and build a new one.";
            return "broken";
        }

        message = string.Empty;
        return null;
    }

    // Cost: the target's states 0 to final; refund: the old piece's states 0 to its current one; netted per item.
    private static void AddMaterials(StructureSwapPlan plan, PlannedStructureSwap swap)
    {
        List<ItemCount> cost = MaterialRule.Totals(BuildMaterials.EntriesOf(swap.Target, plan.Items),
            swap.FinalState);
        List<ItemCount> refund = MaterialRule.Totals(BuildMaterials.EntriesOf(swap.Old, plan.Items),
            swap.Old.CurrentBuildStateIndex);
        swap.Materials.AddRange(MaterialRule.Net(cost, refund));
    }

    private static void CountMaterials(StructureSwapPlan plan)
    {
        List<IReadOnlyList<MaterialLine>> lines = new List<IReadOnlyList<MaterialLine>>(plan.Swaps.Count);
        foreach (PlannedStructureSwap swap in plan.Swaps)
        {
            lines.Add(swap.Materials);
        }

        plan.Totals.AddRange(MaterialRule.Sum(lines));
        int charge = 0;
        plan.Totals.ForEach(total => charge += total.Charge);
        RefundRoute route = plan.Request.Arguments.RefundTo;
        plan.From = Source(plan, RefundChainRule.NeedsPlayer(charge, route));
        List<GuardFinding> findings = new List<GuardFinding>();
        plan.Refunds = RefundReceivers.Resolve(route, plan.From, plan.Request.Arguments.From.HasValue,
            plan.Swaps.Count > 0 ? plan.Swaps[0].Old.ThingTransformPosition : (UnityEngine.Vector3?)null, findings);
        foreach (GuardFinding finding in findings.FindAll(static finding => finding.Level == GuardLevel.Refusal))
        {
            // A skipped target is named in the report's refund_plan; only a refusal stops the run.
            plan.Problem(finding.Code, finding.Message);
        }

        HolderRemoved(plan, route);
        foreach (MaterialTotal total in plan.Totals)
        {
            Item item = plan.Items[total.Item];
            ItemStock stock = PaySources.StockOf(plan.From, plan.MoreFrom, item);
            stock.Needed = total.Charge;
            plan.Stocks.Add(stock);
            if (plan.From != null && stock.Available < stock.Needed)
            {
                plan.Problem("not_enough_materials",
                    $"{stock.Needed} {Names.Of(item)} ({item.PrefabName}) needed, {stock.Available} held by " +
                    $"{PaySources.Holders(plan.From, plan.MoreFrom)}.", plan.From);
            }
        }
    }

    // from_id is a structure the request swaps away while refund_to gives into it: the holder is gone before the
    // refund.
    private static void HolderRemoved(StructureSwapPlan plan, RefundRoute route)
    {
        Thing? from = plan.From;
        if (from == null || !plan.Request.Arguments.From.HasValue || !route.GivesBack ||
            !RefundChainRule.AsksForHolder(route) || !plan.Swaps.Exists(swap => swap.OldId == from.ReferenceId))
        {
            return;
        }

        plan.Problem("refund_holder_removed", RemovalRule.HolderRemoved(
            $"{Names.Of(from)} ({from.PrefabName} {from.ReferenceId})", null), from);
    }

    // from_id, else the player; with neither, a swap that charges nothing and whose refund_to is a chain goes on
    // without one (the chain skips inventory, source and storage and ends on the ground where the first piece stood).
    private static Thing? Source(StructureSwapPlan plan, bool needsPlayer)
    {
        ThingId? from = plan.Request.Arguments.From;
        if (from.HasValue)
        {
            List<ThingId> missing = new List<ThingId>();
            plan.MoreFrom.AddRange(PaySources.Resolve(plan.Request.Arguments.MoreFrom, missing));
            foreach (ThingId id in missing)
            {
                plan.Problems.Add(new UpgradeProblemView(ApiErrors.ThingNotFoundCode,
                    $"No thing with reference id {id} to take materials from.", id));
            }

            if (GameLookup.TryFindThing(from.Value, out Thing thing) && !thing.IsBeingDestroyed)
            {
                return thing;
            }

            plan.Problems.Add(new UpgradeProblemView(ApiErrors.ThingNotFoundCode,
                $"No thing with reference id {from.Value} to take materials from.", from.Value));
            return null;
        }

        PlayerOrigin player = PlayerOrigin.Current();
        if (player.Player == null && needsPlayer)
        {
            plan.Problem("no_local_player",
                $"{player.Absence} Nothing to take materials from (or, with refund_to source, to give the refund " +
                "to); pass from_id.");
        }

        return player.Player;
    }

    private static string Describe(List<StructureSlot>? slots)
    {
        if (slots == null)
        {
            return "unreadable";
        }

        List<string> parts = new List<string>(slots.Count);
        foreach (StructureSlot slot in slots)
        {
            parts.Add(slot.ToString());
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "none";
    }
}
