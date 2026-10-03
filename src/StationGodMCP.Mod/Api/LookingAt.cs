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
/// view is the camera the cursor's ray starts from (Look), and hit casts that same ray on the cursor's layers
/// (CursorManager.CursorHitMask) up to max_distance_m, so it also finds a wall or floor past the game's 3 m reach, and
/// reads the grid where it lands.
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
        CursorManager cursor = CursorManager.Instance;
        double reach = args.OptionalPositiveDouble("max_distance_m") ?? DefaultReachM;
        if (reach > MaximumReachM)
        {
            throw ApiErrors.InvalidArgument($"max_distance_m is at most {MaximumReachM}.");
        }

        Thing? thing = cursor != null ? cursor.FoundThing : null;
        if (thing != null && thing.IsBeingDestroyed)
        {
            thing = null;
        }

        LookView? view = Look.Current();
        LookHitView? hit = cursor != null && view != null ? HitOf(cursor, reach, thing) : null;
        if (cursor == null || thing == null)
        {
            return new LookingAtView(origin.View, null, null, view, hit);
        }

        Interactable? interactable = cursor.CursorTargetCollider != null
            ? thing.GetInteractable(cursor.CursorTargetCollider)
            : null;
        return new LookingAtView(origin.View, TargetOf(thing, origin, view),
            interactable != null ? InteractableOf(interactable) : null, view, hit);
    }

    private static LookHitView? HitOf(CursorManager cursor, double reach, Thing? target)
    {
        if (!Look.Cast(cursor, reach, out RaycastHit hit))
        {
            return null;
        }

        Vec3 point = Bodies.V(hit.point);
        Vec3 normal = Bodies.V(hit.normal).Normalized;
        GridStep? face = ViewBasis.Along(normal, FaceDegrees);
        string? plane = face.HasValue
            ? FacePlane.Near(face.Value.Axis, point[face.Value.Axis], MountRect.OnPlaneM)?.ToString()
            : null;

        GridCell small = Look.SmallCellAt(point);
        GridCell large = SmallCellCode.LargeOf(Look.SmallCellAt(point + normal * 0.3));
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        OpeningZone zone = facts.Opening(small);
        string support = zone.IsDoor ? "x"
            : zone.IsWindow ? "g"
            : CellSupports.Code(facts.Support(small), facts.Visibility(small)).ToString();
        Thing? hitThing = hit.transform != null ? hit.transform.GetComponentInParent<Thing>() : null;
        return new LookHitView(point, hit.distance, normal, face?.Name, plane,
            GameLookup.ViewOf(PieceShapes.CentreOf(large)), PointView.OfCell(small), support,
            hitThing != null ? GameLookup.ViewOf(hitThing) : null,
            target != null ? Bodies.Local(target, hit.point) : null);
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

/// <summary>
/// The camera as the cursor's ray sees it: InputHelpers.GetCameraRay (CameraController.CameraOrigin, moved up to the
/// player in third person as the game's own ray is, and MainCameraForward) with the camera's up
/// (CameraController.CurrentCamera). Null before the camera exists (a dedicated server has none).
/// </summary>
internal static class Look
{
    internal static LookView? Current()
    {
        ViewBasis? basis = Basis(out Vector3 eye);
        if (basis == null)
        {
            return null;
        }

        Human? human = PlayerOrigin.Local();
        bool seated = human != null && human.MovementController != null &&
                      human.MovementController.ControlMode == MovementController.Mode.Seated;
        return new LookView(Bodies.V(eye), basis, CameraController.IsThirdPerson, seated);
    }

    internal static ViewBasis? Basis(out Vector3 eye)
    {
        eye = Vector3.zero;
        if (CameraController.Instance == null || CameraController.CurrentCamera == null)
        {
            return null;
        }

        Ray ray = InputHelpers.GetCameraRay();
        eye = ray.origin;
        return ViewBasis.Of(Bodies.V(ray.direction), Bodies.V(CameraController.CurrentCamera.transform.up));
    }

    /// <summary>Whether there is a player camera to look through (a dedicated server has none).</summary>
    internal static bool HasCamera => CameraController.Instance != null && CameraController.CurrentCamera != null;

    /// <summary>The look ray cast on the cursor's layers; false when it hits nothing within reach.</summary>
    internal static bool Cast(CursorManager cursor, double reach, out RaycastHit hit)
    {
        hit = default;
        return CameraController.Instance != null && CameraController.CurrentCamera != null &&
               Physics.Raycast(InputHelpers.GetCameraRay(), out hit, (float)reach, cursor.CursorHitMask);
    }

    /// <summary>The small cell (decimetres, multiples of 5) nearest a point in metres.</summary>
    internal static GridCell SmallCellAt(Vec3 point) =>
        new GridCell(Half(point.X), Half(point.Y), Half(point.Z));

    private static int Half(double metres) => (int)System.Math.Round(metres * 2.0) * 5;
}
