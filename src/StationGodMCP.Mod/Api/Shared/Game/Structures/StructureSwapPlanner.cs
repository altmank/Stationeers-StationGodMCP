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
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// The whole preflight of swap wall and swap frame: nothing here changes the game. It reads the pieces,
/// resolves each one's target, compares their slots, judges what each would open or seal, runs the family's checks,
/// counts the materials and records the rooms and air around the pieces. Every problem is listed; none stops the
/// others from being looked for.
/// </summary>
internal static class StructureSwapPlanner
{
    /// <summary>
    /// The preflight as a Work body (the report's and the job's), spread over frames: the members, then a member
    /// classified per step, the materials, and the air record in steps of its own.
    /// </summary>
    internal static IEnumerable<object?> Planning(StructureSwapRequest request, Answer<StructureSwapPlan> answer)
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
        answer.Value = plan;
        List<Structure> members = new List<Structure>();
        foreach (object? step in Membering(request, plan, members))
        {
            yield return step;
        }

        plan.Total = members.Count;
        StructureTargets targets = WorldModel.Prefabs.Targets;
        Structure? named = NamedTarget(request, targets, plan);
        if (request.Arguments.To != null && named == null)
        {
            yield break;
        }

        yield return null;
        BuildCatalogue catalogue = WorldModel.Prefabs.Build;
        foreach (Structure member in members)
        {
            Classify(plan, targets, catalogue, named, member);
            yield return null;
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
            yield break;
        }

        yield return null;
        Answer<StructureAirRecord> air = new Answer<StructureAirRecord>();
        foreach (object? step in StructureAirRecord.Taking(plan.Swaps, air))
        {
            yield return step;
        }

        plan.Air = air.Value;
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

    // The pieces in scope, added to members: the ids named, or the room's pieces a batch of its cells a step.
    private static IEnumerable<object?> Membering(StructureSwapRequest request, StructureSwapPlan plan,
        List<Structure> members)
    {
        HashSet<long> seen = new HashSet<long>();
        switch (request.Arguments.Scope)
        {
            case StructureScope.Pieces pieces:
                Listed(request.Family, pieces.Ids, plan, members, seen);
                break;
            case StructureScope.Room room:
                Room found = StructureAirRecord.FindRoom(room.Id) ??
                             throw ApiErrors.Refused("room_not_found",
                                 $"No room {room.Id}; take a room's id from find room.");
                foreach (object? step in request.Family.CollectingInRoom(found, members, seen))
                {
                    yield return step;
                }

                break;
        }

        if (members.Count > StructureSwapArguments.MaximumPieces)
        {
            throw ApiErrors.Refused("too_many_pieces",
                $"{members.Count} pieces; at most {StructureSwapArguments.MaximumPieces} per run.");
        }
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

    [Profiled]
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
        PrefabFilter? only = request.Arguments.Prefab;
        if (only != null && !only.Keeps(member.PrefabName, member.PrefabHash))
        {
            message = $"{member.PrefabName} does not match prefab.";
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
                "remove it with remove structure and allow: [\"broken\"], and build a new one.";
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

    [Profiled]
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

        GuardFinding? fromPlayer = PaySources.PlayerPays(charge > 0, plan.From, plan.MoreFrom,
            plan.Request.Arguments.From.HasValue);
        if (fromPlayer != null)
        {
            plan.Warnings.Add(new UpgradeProblemView(fromPlayer.Code, fromPlayer.Message));
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
                "to); pass materials.");
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
