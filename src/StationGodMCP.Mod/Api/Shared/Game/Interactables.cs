#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A thing's switches and buttons (Thing.Interactables) as inspect lists them, and a press made as the player makes it:
/// the game is asked whether the press works (Thing.InteractWith with doAction false; a refusal's reason is the dry
/// answer's state message, DelayedActionInstance.GetStateMessage), then it is made (InteractWith with doAction true,
/// which runs OnServer.Interact). Host only, main thread.
/// </summary>
internal static class Interactables
{
    /// <summary>One row per interactable; the live state when the thing stands in the world, none for a prefab.</summary>
    internal static List<InteractableRow> Rows(Thing thing, bool live)
    {
        List<InteractableRow> rows = new List<InteractableRow>(thing.Interactables.Count);
        for (int index = 0; index < thing.Interactables.Count; index++)
        {
            Interactable interactable = thing.Interactables[index];
            rows.Add(new InteractableRow(index, NameOf(interactable), interactable.Action.ToString(),
                live ? interactable.State : null, StatesOf(interactable)));
        }

        return rows;
    }

    /// <summary>The press: before and after states, with the game's own answer text.</summary>
    internal static InteractedView Press(Thing thing, string interactableWanted, int? state, bool held, bool force,
        Human player)
    {
        List<InteractableRow> rows = Rows(thing, true);
        switch (InteractableChoice.Of(rows, interactableWanted))
        {
            case InteractableChoice.Found found:
                return Press(thing, thing.Interactables[found.Index], rows[found.Index], state, held, force, player);
            case InteractableChoice.Ambiguous ambiguous:
                throw ApiErrors.InvalidArgument($"interactable: '{ambiguous.Wanted}' names several of the thing's " +
                    $"interactables (indexes {string.Join(", ", ambiguous.Indexes)}); give an index.");
            case InteractableChoice.Missing missing:
                throw ApiErrors.Refused("interactable_not_found", $"{Names.Of(thing)} has no interactable " +
                    $"'{missing.Wanted}'. It has: {(missing.Names.Count == 0 ? "none" : string.Join(", ", missing.Names))}.");
            default:
                throw new System.InvalidOperationException("Unknown interactable choice.");
        }
    }

    private static InteractedView Press(Thing thing, Interactable interactable, InteractableRow row, int? state,
        bool held, bool force, Human player)
    {
        if (NetworkManager.IsClient && !NetworkManager.IsServer)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host presses.");
        }

        Interaction interaction = new Interaction(player, held ? HeldSlot(player) : null, thing, false);
        Vec3 eye = Bodies.V(player.ThingTransformPosition) + new Vec3(0, StandSearch.EyeHeightM, 0);
        double distance = (StandSearch.Nearest(Bodies.RenderBox(thing), eye) - eye).Length;
        PressDecision decision = PressRule.Decide(distance, force, StandOptions.MaxReachM, () =>
        {
            Thing.DelayedActionInstance dry = thing.InteractWith(interactable, interaction, false);
            return new DryPress(!dry.IsDisabled, dry.GetStateMessage());
        });
        if (decision is PressDecision.WouldRefuse refused)
        {
            throw ApiErrors.Refused(refused.Kind == PressRefusal.OutOfRange ? "out_of_range" : "not_accepted",
                refused.Message);
        }

        int before = interactable.State;
        Thing.DelayedActionInstance made;
        if (state is int wanted)
        {
            OnServer.Interact(interactable, wanted);
            made = new Thing.DelayedActionInstance();
        }
        else
        {
            made = thing.InteractWith(interactable, interaction, true);
        }

        string text = made.GetStateMessage();
        return new InteractedView(GameLookup.ViewOf(thing), new InteractedName(row.Name, row.Index), before,
            interactable.State, player.DisplayName ?? "player", string.IsNullOrWhiteSpace(text) ? null : text.Trim());
    }

    private static Slot? HeldSlot(Human player)
    {
        if (player.RightHandSlot != null && player.RightHandSlot.IsNotEmpty())
        {
            return player.RightHandSlot;
        }

        return player.LeftHandSlot != null && player.LeftHandSlot.IsNotEmpty() ? player.LeftHandSlot : null;
    }

    private static string NameOf(Interactable interactable)
    {
        string name = interactable.DisplayName;
        return string.IsNullOrEmpty(name) ? interactable.Action.ToString() : name;
    }

    private static int? StatesOf(Interactable interactable) =>
        interactable.Action switch
        {
            InteractableType.OnOff or InteractableType.Open or InteractableType.Lock or InteractableType.Activate => 2,
            _ => null
        };
}
