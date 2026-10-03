#nullable enable

using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Util;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;

namespace StationGodMCP.Net;

/// <summary>
/// A client's side: once the server announced a StationGod that speaks this view protocol, this player's view is sent
/// to it when it changes (ViewChangeRule: at most every 0.1 s, every second regardless). The view is what the game's own
/// cursor uses: InputHelpers.GetCameraRay, CursorManager.FoundThing and the interactable under CursorTargetCollider,
/// and one ray cast on CursorManager.CursorHitMask up to 50 m. Ticked every frame on a game that is not the server.
/// </summary>
internal static class ViewReporter
{
    // Tells this run's reports apart from an earlier run's at the server.
    private static readonly int Session = new System.Random().Next(1, int.MaxValue);

    private static HostLink _link = HostLink.None;
    private static ViewReport? _sent;
    private static float _sentAt = float.NegativeInfinity;
    private static int _sequence;

    /// <summary>The server's announcement from the join data.</summary>
    internal static void Announced(Announcement announcement)
    {
        _link = HostLink.From(announcement);
        _sent = null;
        _sentAt = float.NegativeInfinity;
        switch (_link)
        {
            case HostLink.Compatible compatible:
                StationGodMod.Log($"The server runs StationGod {compatible.Version}: this player's view is shared " +
                                  "with it, so its camera tools see what this player sees.");
                break;
            case HostLink.Incompatible incompatible:
                StationGodMod.LogWarning(incompatible.Describe(StationGodMod.Version));
                break;
        }
    }

    internal static void Tick()
    {
        if (!NetworkManager.IsClient)
        {
            Forget();
            return;
        }

        if (!_link.MaySend || !StationGodNet.Active || GameManager.GameState != GameState.Running)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (!ViewChangeRule.MayTake(now - _sentAt))
        {
            return;
        }

        ViewReport? current = Take();
        if (current == null || !ViewChangeRule.ShouldSend(_sent, now - _sentAt, current))
        {
            return;
        }

        ViewReport numbered = current.Numbered(++_sequence);
        if (StationGodNet.SendView(ViewWire.Encode(numbered)))
        {
            _sent = numbered;
            _sentAt = now;
        }
    }

    // Left the server (or never joined one): the next server must announce itself again.
    private static void Forget()
    {
        if (_link is HostLink.Unannounced && _sent == null)
        {
            return;
        }

        _link = HostLink.None;
        _sent = null;
        _sentAt = float.NegativeInfinity;
    }

    // This player's view now; null before the player or the camera exists.
    private static ViewReport? Take()
    {
        Human human = Human.LocalHuman;
        if (human == null || CameraController.Instance == null || CameraController.CurrentCamera == null)
        {
            return null;
        }

        Ray ray = InputHelpers.GetCameraRay();
        CursorManager cursor = CursorManager.Instance;
        bool seated = human.MovementController != null &&
                      human.MovementController.ControlMode == MovementController.Mode.Seated;
        return new ViewReport(Session, 0, Bodies.V(ray.origin), Bodies.V(ray.direction),
            Bodies.V(CameraController.CurrentCamera.transform.up), CameraController.IsThirdPerson, seated,
            Bodies.V(human.Position), TargetOf(cursor), HitOf(cursor, ray));
    }

    private static ViewTarget? TargetOf(CursorManager? cursor)
    {
        Thing? thing = cursor != null ? cursor.FoundThing : null;
        if (cursor == null || thing == null || thing.IsBeingDestroyed)
        {
            return null;
        }

        Interactable? interactable = cursor.CursorTargetCollider != null
            ? thing.GetInteractable(cursor.CursorTargetCollider)
            : null;
        int index = interactable != null ? interactable.InteractableId : -1;
        return new ViewTarget(thing.ReferenceId, index >= 0 ? index : (int?)null);
    }

    private static ViewHit? HitOf(CursorManager? cursor, Ray ray)
    {
        if (cursor == null || !Physics.Raycast(ray, out RaycastHit hit, (float)ViewWire.ReachM, cursor.CursorHitMask))
        {
            return null;
        }

        Thing? thing = hit.transform != null ? hit.transform.GetComponentInParent<Thing>() : null;
        return new ViewHit(Bodies.V(hit.point), Bodies.V(hit.normal), hit.distance,
            thing != null ? thing.ReferenceId : 0L);
    }
}
