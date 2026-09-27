#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// looking_at: what the local player's crosshair is on. Read only.
///
/// The game keeps it in CursorManager.CursorThing (CursorManager.Instance.FoundThing): every frame
/// CursorManager.SetCursorTarget casts a ray from the camera up to CursorManager.MaxInteractDistance (3 m) and takes
/// the Thing above the collider it hits. Terrain, nothing in reach, an open console or a locked Stationpedia leave it
/// null. The interactable is what the game's own hand logic takes in the same frame (InventoryManager.NormalModeThing):
/// CursorThing.GetInteractable(CursorManager.Instance.CursorTargetCollider), the button, switch, port or slot under the
/// crosshair; null when that collider is none of those.
/// </summary>
internal static class LookingAtApi
{
    internal static LookingAtView Handle(Args args)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        CursorManager cursor = CursorManager.Instance;
        Thing? thing = cursor != null ? cursor.FoundThing : null;
        if (cursor == null || thing == null || thing.IsBeingDestroyed)
        {
            return new LookingAtView(origin.View, null, null);
        }

        Interactable? interactable = cursor.CursorTargetCollider != null
            ? thing.GetInteractable(cursor.CursorTargetCollider)
            : null;
        return new LookingAtView(origin.View, TargetOf(thing, origin),
            interactable != null ? InteractableOf(interactable) : null);
    }

    private static LookingAtTargetView TargetOf(Thing thing, PlayerOrigin origin) =>
        new LookingAtTargetView(
            GameLookup.ViewOf(thing),
            string.IsNullOrEmpty(thing.CustomName) ? null : Text.Plain(thing.CustomName),
            ThingKinds.Of(thing),
            thing.GetType().Name,
            GameLookup.ViewOf(thing.Position),
            origin.DistanceTo(thing.Position),
            thing is Device device && Devices.IsInAllDevices(device),
            AtmosphereContentsApi.HoldsAtmosphere(thing),
            ParentOf(thing));

    private static HeldInView? ParentOf(Thing thing)
    {
        Slot? slot = thing is DynamicThing dynamic ? dynamic.ParentSlot : null;
        return slot != null && slot.Parent != null
            ? new HeldInView(GameLookup.ViewOf(slot.Parent), slot.SlotIndex, slot.DisplayName)
            : null;
    }

    private static LookingAtInteractableView InteractableOf(Interactable interactable)
    {
        Slot? slot = interactable.Slot;
        InteractableSlotView? slotView = null;
        if (slot != null)
        {
            DynamicThing occupant = slot.Get();
            slotView = new InteractableSlotView(slot.SlotIndex, slot.DisplayName,
                occupant != null ? GameLookup.ViewOf(occupant) : null);
        }

        return new LookingAtInteractableView(interactable.Action.ToString(), Text.Plain(interactable.DisplayName),
            Text.Plain(interactable.ContextualName), interactable.State, slotView);
    }
}
