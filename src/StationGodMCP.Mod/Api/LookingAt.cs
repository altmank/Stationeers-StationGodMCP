#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;

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
///
/// view is the camera the cursor's ray starts from, and hit casts that same ray on the cursor's layers
/// (CursorManager.CursorHitMask) up to max_distance_m, so it also finds a wall or floor past the game's 3 m reach, and
/// reads the grid where it lands. The camera is the player's (PlayerView): this game's own, or on a dedicated server
/// the view the player's StationGod sent, read there the same way.
/// </summary>
internal static class LookingAtApi
{
    private const double DefaultReachM = 10.0;
    private const double MaximumReachM = 50.0;

    // Within this of an axis, a surface normal names a face.
    private const double FaceDegrees = 10.0;

    internal static LookingAtView Handle(Args args)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        double reach = args.OptionalPositiveDouble("max_distance_m") ?? DefaultReachM;
        if (reach > MaximumReachM)
        {
            throw ApiErrors.InvalidArgument($"max_distance_m is at most {MaximumReachM}.");
        }

        CameraView camera = PlayerView.Current().Require("looking_at");
        Thing? thing = camera.Target;
        LookView view = new LookView(camera.Eye, camera.Basis, camera.ThirdPerson, camera.Seated, camera.Source);
        CameraHit? found = camera.HitWithin(reach);
        LookHitView? hit = found != null ? HitOf(found, thing) : null;
        if (thing == null)
        {
            return new LookingAtView(origin.View, null, null, view, hit);
        }

        Interactable? interactable = camera.Interactable;
        return new LookingAtView(origin.View, TargetOf(thing, origin, view),
            interactable != null ? InteractableOf(interactable) : null, view, hit);
    }

    private static LookHitView HitOf(CameraHit hit, Thing? target)
    {
        Vec3 point = hit.Point;
        Vec3 normal = hit.Normal.Normalized;
        GridStep? face = ViewBasis.Along(normal, FaceDegrees);
        string? plane = face.HasValue
            ? FacePlane.Near(face.Value.Axis, point[face.Value.Axis], MountRect.OnPlaneM)?.ToString()
            : null;

        GridCell small = ViewChangeRule.SmallCellOf(point);
        GridCell large = SmallCellCode.LargeOf(ViewChangeRule.SmallCellOf(point + normal * 0.3));
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        OpeningZone zone = facts.Opening(small);
        string support = zone.IsDoor ? "x"
            : zone.IsWindow ? "g"
            : CellSupports.Code(facts.Support(small), facts.Visibility(small)).ToString();
        return new LookHitView(point, hit.DistanceM, normal, face?.Name, plane,
            GameLookup.ViewOf(PieceShapes.CentreOf(large)), PointView.OfCell(small), support,
            hit.Thing != null ? GameLookup.ViewOf(hit.Thing) : null,
            target != null ? Bodies.Local(target, Bodies.U(point)) : null);
    }

    private static LookingAtTargetView TargetOf(Thing thing, PlayerOrigin origin, LookView? view) =>
        new LookingAtTargetView(
            GameLookup.ViewOf(thing),
            string.IsNullOrEmpty(thing.CustomName) ? null : Text.Plain(thing.CustomName),
            ThingKinds.Of(thing),
            thing.GetType().Name,
            GameLookup.ViewOf(thing.Position),
            origin.DistanceTo(thing.Position),
            thing is Device device && Devices.IsInAllDevices(device),
            AtmosphereContentsApi.HoldsAtmosphere(thing),
            ParentOf(thing),
            thing is Structure ? Orientations.Of(thing) : null,
            thing is Structure structure ? Bodies.ViewOf(structure) : null,
            thing is Structure && view != null ? FacingMe(thing, view) : (bool?)null);

    // Whether the thing's front points toward the camera.
    private static bool FacingMe(Thing thing, LookView view)
    {
        Vector3 eye = new Vector3((float)view.Eye.X, (float)view.Eye.Y, (float)view.Eye.Z);
        return Vector3.Dot(thing.ThingTransformRotation * Vector3.forward, eye - thing.ThingTransformPosition) > 0f;
    }

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
