#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// The check the frame after a swap, with the tick still held. Each new piece stands in exactly the planned slots,
/// at its final build state, blocking what was planned; each old piece it replaced is gone; each piece left as it
/// was (after a stop, or rolled back) still holds all its slots; the face structures recorded beside each swapped
/// frame are the same ones; and the air of every room and cell around the pieces is exactly as recorded.
/// </summary>
internal static class StructureCheck
{
    internal static StructureHeldCheckView Held(StructureSwapOutcome outcome)
    {
        List<UpgradeProblemView> problems = new List<UpgradeProblemView>();
        foreach (PlannedStructureSwap swap in outcome.Plan.Swaps)
        {
            if (outcome.Replacements.TryGetValue(swap.OldId, out Structure built))
            {
                CheckReplaced(problems, swap, built);
            }
            else
            {
                CheckKept(problems, swap);
            }
        }

        outcome.Plan.Air?.CheckHeld(problems);
        return new StructureHeldCheckView(problems);
    }

    private static void CheckReplaced(List<UpgradeProblemView> problems, PlannedStructureSwap swap, Structure built)
    {
        ThingId oldId = new ThingId(swap.OldId);
        if (built == null || built.IsBeingDestroyed)
        {
            problems.Add(new UpgradeProblemView("replacement_gone",
                $"The replacement of {swap.OldId} no longer exists.", oldId));
            return;
        }

        ThingId newId = new ThingId(built.ReferenceId);
        if (!StructureSlots.HoldsAll(swap.Slots, built))
        {
            problems.Add(new UpgradeProblemView("placement_mismatch",
                $"{built.PrefabName} {built.ReferenceId} does not hold every slot {swap.OldId} held.", newId));
        }

        if (built.CurrentBuildStateIndex != built.BuildStates.Count - 1)
        {
            problems.Add(new UpgradeProblemView("not_final_state",
                $"{built.PrefabName} {built.ReferenceId} is at build state {built.CurrentBuildStateIndex}, not " +
                $"{built.BuildStates.Count - 1}.", newId));
        }

        if ((swap.After.Air && built.CanAirPass) || (swap.After.Gravity && built.CanGravityPass))
        {
            problems.Add(new UpgradeProblemView("not_sealed",
                $"{built.PrefabName} {built.ReferenceId} does not block what was planned.", newId));
        }

        if (Thing.Find(swap.OldId) != null)
        {
            problems.Add(new UpgradeProblemView("old_piece_remains",
                $"{swap.Old.PrefabName} {swap.OldId} is still there after the swap.", oldId));
        }

        foreach (GuardedFace face in swap.GuardedFaces)
        {
            HashSet<long> now = StructureSlots.FaceHolders(face.Face);
            if (!now.SetEquals(face.Holders))
            {
                problems.Add(new UpgradeProblemView("face_structures_changed",
                    $"The face at {face.Face} beside {swap.OldId} held [{string.Join(", ", face.Holders)}] and " +
                    $"holds [{string.Join(", ", now)}].", newId));
            }
        }
    }

    private static void CheckKept(List<UpgradeProblemView> problems, PlannedStructureSwap swap)
    {
        Structure old = swap.Old;
        if (old == null || old.IsBeingDestroyed || !StructureSlots.HoldsAll(swap.Slots, old))
        {
            problems.Add(new UpgradeProblemView("old_piece_lost_slot",
                $"{swap.OldId} was not replaced but does not hold all its slots ({swap.Slots.Count}); its faces " +
                "may be open.", new ThingId(swap.OldId)));
        }
    }
}
