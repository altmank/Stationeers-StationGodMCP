#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// A prefab's control face (ControlFaceRule) read from its own data: the colliders of its Thing.Interactables (the
/// slots, buttons and switches a player points at; Interactable.Collider, centred as Interactable.CacheBounds centres
/// it) in the prefab's own frame, against the box its meshes fill (Thing.Bounds). Indicators a player cannot use
/// (Powered, Error, Color) are left out. Null for anything with no control a player uses: frames, walls, pieces, and a
/// device whose prefab lists none (a radiator). Cached per prefab name.
/// </summary>
internal static class PrefabControls
{
    private static readonly Dictionary<string, ControlFace?> Known = new Dictionary<string, ControlFace?>();

    internal static ControlFace? Of(Structure prefab)
    {
        string key = prefab.PrefabName ?? string.Empty;
        if (!Known.TryGetValue(key, out ControlFace? face))
        {
            face = Read(prefab);
            Known[key] = face;
        }

        return face;
    }

    /// <summary>
    /// The world side its controls face at a turn; its forward for anything with no control face (orient's
    /// controls_toward and find_spot's front aim this).
    /// </summary>
    internal static GridStep FrontOf(Structure prefab, CubeRotation turn) =>
        Of(prefab) is ControlFace controls ? turn.Turn(controls.Local) : turn.Forward;

    private static ControlFace? Read(Structure prefab)
    {
        if (prefab is SmallGrid piece && (new CableFamily().IsPiece(piece) || new PipeFamily().IsPiece(piece) ||
                                          new ChuteFamily().IsPiece(piece)))
        {
            return null;
        }

        List<Vec3> controls = new List<Vec3>();
        foreach (Interactable? control in prefab.Interactables ?? new List<Interactable>())
        {
            if (control == null || control.Collider == null || !IsControl(control.Action))
            {
                continue;
            }

            Vector3 world = control.Collider.transform.TransformPoint(CentreOf(control.Collider));
            controls.Add(Bodies.V(prefab.transform.InverseTransformPoint(world)));
        }

        return ControlFaceRule.Of(controls, Bodies.RenderBox(prefab, Vector3.zero, Quaternion.identity));
    }

    private static bool IsControl(InteractableType action) =>
        action != InteractableType.Powered && action != InteractableType.Error && action != InteractableType.Color;

    // The collider's centre in its own transform, as Interactable.CacheBounds takes it.
    private static Vector3 CentreOf(Collider collider) => collider switch
    {
        BoxCollider box => box.center,
        SphereCollider sphere => sphere.center,
        CapsuleCollider capsule => capsule.center,
        MeshCollider mesh when mesh.sharedMesh != null => mesh.sharedMesh.bounds.center,
        _ => Vector3.zero
    };
}
