#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
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

        foreach (Structure member in members)
        {
            Classify(plan, targets, named, member);
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
                    $"{thing.DisplayName} ({thing.PrefabName}) is not a {family.Noun}.", thing);
            }
            else
            {
                members.Add(structure);
            }
        }
    }

    private static void Classify(StructureSwapPlan plan, StructureTargets targets, Structure? named,
        Structure member)
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

        AddMaterials(plan, swap);
        family.Assess(swap, plan);
        swap.AffectedCells = family.AffectedCells(swap);
        plan.Swaps.Add(swap);
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
        plan.From = Source(plan);
        List<IReadOnlyList<MaterialLine>> lines = new List<IReadOnlyList<MaterialLine>>(plan.Swaps.Count);
        foreach (PlannedStructureSwap swap in plan.Swaps)
        {
            lines.Add(swap.Materials);
        }

        plan.Totals.AddRange(MaterialRule.Sum(lines));
        foreach (MaterialTotal total in plan.Totals)
        {
            Item item = plan.Items[total.Item];
            ItemStock stock = plan.From != null ? ItemStock.In(plan.From, item) : ItemStock.Empty(item);
            stock.Needed = total.Charge;
            plan.Stocks.Add(stock);
            if (plan.From != null && stock.Available < stock.Needed)
            {
                plan.Problem("not_enough_materials",
                    $"{stock.Needed} {item.DisplayName} ({item.PrefabName}) needed, {stock.Available} held by " +
                    $"{plan.From.DisplayName}.", plan.From);
            }
        }
    }

    private static Thing? Source(StructureSwapPlan plan)
    {
        ThingId? from = plan.Request.Arguments.From;
        if (from.HasValue)
        {
            if (GameLookup.TryFindThing(from.Value, out Thing thing) && !thing.IsBeingDestroyed)
            {
                return thing;
            }

            plan.Problems.Add(new UpgradeProblemView(ApiErrors.ThingNotFoundCode,
                $"No thing with reference id {from.Value} to take materials from.", from.Value));
            return null;
        }

        Human human = Human.LocalHuman;
        if (human == null)
        {
            plan.Problem("no_local_player", "There is no local player to take materials from; pass from_id.");
            return null;
        }

        return human;
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
