#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Util;
using StationGodMCP.Api.Views;
using StationGodMCP.Net;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// What this game knows of the player's camera (the player PlayerOrigin names): seen (a camera to read), stale (a
/// remote view the player has since moved away from, ViewFreshness) or unseen (why there is none). The local player's
/// camera is this game's own; a player on another machine is seen through the view their StationGod sends
/// (RemoteViews). Every camera tool asks here, so a dedicated server answers for its player as the player's own game
/// would, and refuses, saying why, when it cannot.
/// </summary>
internal abstract class PlayerView
{
    private PlayerView()
    {
    }

    internal sealed class Seen : PlayerView
    {
        internal Seen(CameraView camera)
        {
            Camera = camera;
        }

        internal CameraView Camera { get; }
    }

    internal sealed class Stale : PlayerView
    {
        internal Stale(CameraView camera, string reason)
        {
            Camera = camera;
            Reason = reason;
        }

        internal CameraView Camera { get; }

        internal string Reason { get; }
    }

    internal sealed class Unseen : PlayerView
    {
        internal Unseen(string reason)
        {
            Reason = reason;
        }

        /// <summary>Why there is no view, a whole sentence.</summary>
        internal string Reason { get; }
    }

    internal static PlayerView Current()
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        Human? player = origin.Player;
        if (player == null)
        {
            return new Unseen(origin.Absence!);
        }

        if (player == PlayerOrigin.Local())
        {
            CameraView? local = CameraView.Local.Find(player);
            return local != null
                ? new Seen(local)
                : new Unseen("This game has no camera yet (the world is still loading).");
        }

        return Remote(player);
    }

    /// <summary>The camera to read; no_view or view_stale, saying why, when there is none to trust.</summary>
    internal CameraView Require(string name) => this switch
    {
        Seen seen => seen.Camera,
        Stale stale => throw ApiErrors.Refused("view_stale",
            $"{name}: the view {stale.Camera.PlayerName}'s game last sent is stale: {stale.Reason}. Their game has " +
            "stopped sending it (still loading, frozen or not running in the background); it is current again once " +
            "it sends, within a second."),
        Unseen unseen => throw ApiErrors.Refused("no_view", $"{name}: {unseen.Reason}"),
        _ => throw new System.InvalidOperationException("Unknown player view.")
    };

    /// <summary>The player's screen, to draw on: a stale view still names it. no_view when there is none.</summary>
    internal PlayerScreen RequireScreen(string name) => this switch
    {
        Seen seen => seen.Camera.Screen,
        Stale stale => stale.Camera.Screen,
        Unseen unseen => throw ApiErrors.Refused("no_view", $"{name}: {unseen.Reason}"),
        _ => throw new System.InvalidOperationException("Unknown player view.")
    };

    /// <summary>The player's screen when there is one, else this game's own (for clearing what it drew).</summary>
    internal PlayerScreen ScreenOrLocal() => this switch
    {
        Seen seen => seen.Camera.Screen,
        Stale stale => stale.Camera.Screen,
        _ => PlayerScreen.Local.Instance
    };

    /// <summary>The view used, for a reply's view; null when unseen.</summary>
    internal ViewSourceView? Source => this switch
    {
        Seen seen => seen.Camera.Source,
        Stale stale => stale.Camera.Source,
        _ => null
    };

    private static PlayerView Remote(Human player)
    {
        string name = player.DisplayName;
        (ReceivedView View, long ConnectionId)? held = RemoteViews.Find(player);
        if (held == null)
        {
            return new Unseen(NoRemoteView(player, name));
        }

        ReceivedView view = held.Value.View;
        double age = view.AgeAt(RemoteViews.Now);
        CameraView camera = new CameraView.Remote(view.Report, age, name, held.Value.ConnectionId);
        return ViewFreshness.Judge(view.Report.PlayerPosition, Bodies.V(player.Position), age) switch
        {
            ViewFreshness.Stale stale => new Stale(camera, stale.Reason),
            _ => new Seen(camera)
        };
    }

    private static string NoRemoteView(Human player, string name)
    {
        string ignored = RemoteViews.IgnoredFor(player) ?? string.Empty;
        if (!StationGodNet.Active)
        {
            return $"{name} plays on another machine, and this game's StationGod gets no views from other games: " +
                   $"{StationGodNet.Inactive}.";
        }

        if (ignored.Length > 0)
        {
            return $"{name}'s game sends views this server cannot read: {ignored}. Run the same StationGod version on both.";
        }

        return $"{name} plays on another machine and their game has sent no view: it needs StationGod " +
               $"{StationGodMod.Version} (view protocol {ViewProtocol.Current}) to share it. StationGod is optional " +
               "there; without it the camera tools cannot see what they see.";
    }
}

/// <summary>Where the look ray hit: the point, the surface normal, the distance from the eye and the thing hit.</summary>
internal sealed class CameraHit
{
    internal CameraHit(Vec3 point, Vec3 normal, double distanceM, Thing? thing)
    {
        Point = point;
        Normal = normal;
        DistanceM = distanceM;
        Thing = thing;
    }

    internal Vec3 Point { get; }

    internal Vec3 Normal { get; }

    internal double DistanceM { get; }

    /// <summary>The thing hit; null for terrain, or a thing this game no longer has.</summary>
    internal Thing? Thing { get; }
}

/// <summary>
/// A player's camera as the tools read it: the eye and basis of the cursor's ray, the camera mode, the thing and
/// interactable under the cursor, the first surface the ray hits within a reach, and the screen to draw on.
/// </summary>
internal abstract class CameraView
{
    private CameraView()
    {
    }

    internal abstract Vec3 Eye { get; }

    internal abstract ViewBasis Basis { get; }

    internal abstract bool ThirdPerson { get; }

    internal abstract bool Seated { get; }

    /// <summary>The thing the game's cursor is on (within 3 m); null on none.</summary>
    internal abstract Thing? Target { get; }

    /// <summary>The button, switch, port or slot on Target under the cursor; null on none.</summary>
    internal abstract Interactable? Interactable { get; }

    internal abstract string PlayerName { get; }

    internal abstract ViewSourceView Source { get; }

    internal abstract PlayerScreen Screen { get; }

    /// <summary>The first surface along the ray within reach (cursor layers); null when it hits none.</summary>
    internal abstract CameraHit? HitWithin(double reachM);

    /// <summary>
    /// This game's own camera: InputHelpers.GetCameraRay (CameraController.CameraOrigin, moved up to the player in
    /// third person as the game's own ray is, and MainCameraForward) with the camera's up, and CursorManager.
    /// </summary>
    internal sealed class Local : CameraView
    {
        private static readonly ViewSourceView LocalSource = new ViewSourceView("local", 0.0);

        private readonly Ray _ray;
        private readonly Human _player;

        private Local(Ray ray, Vector3 up, Human player)
        {
            _ray = ray;
            _player = player;
            Eye = Bodies.V(ray.origin);
            Basis = ViewBasis.Of(Bodies.V(ray.direction), Bodies.V(up));
        }

        /// <summary>The local camera; null before it exists.</summary>
        internal static Local? Find(Human player)
        {
            if (CameraController.Instance == null || CameraController.CurrentCamera == null)
            {
                return null;
            }

            return new Local(InputHelpers.GetCameraRay(), CameraController.CurrentCamera.transform.up, player);
        }

        internal override Vec3 Eye { get; }

        internal override ViewBasis Basis { get; }

        internal override bool ThirdPerson => CameraController.IsThirdPerson;

        internal override bool Seated => _player.MovementController != null &&
                                         _player.MovementController.ControlMode == MovementController.Mode.Seated;

        internal override Thing? Target
        {
            get
            {
                CursorManager cursor = CursorManager.Instance;
                Thing? thing = cursor != null ? cursor.FoundThing : null;
                return thing != null && !thing.IsBeingDestroyed ? thing : null;
            }
        }

        internal override Interactable? Interactable
        {
            get
            {
                CursorManager cursor = CursorManager.Instance;
                Thing? thing = Target;
                return thing != null && cursor.CursorTargetCollider != null
                    ? thing.GetInteractable(cursor.CursorTargetCollider)
                    : null;
            }
        }

        internal override string PlayerName => _player.DisplayName;

        internal override ViewSourceView Source => LocalSource;

        internal override PlayerScreen Screen => PlayerScreen.Local.Instance;

        internal override CameraHit? HitWithin(double reachM)
        {
            CursorManager cursor = CursorManager.Instance;
            if (cursor == null || !Physics.Raycast(_ray, out RaycastHit hit, (float)reachM, cursor.CursorHitMask))
            {
                return null;
            }

            Thing? thing = hit.transform != null ? hit.transform.GetComponentInParent<Thing>() : null;
            return new CameraHit(Bodies.V(hit.point), Bodies.V(hit.normal), hit.distance, thing);
        }
    }

    /// <summary>The view a remote player's StationGod sent: their own game's cursor, read there (ViewReporter).</summary>
    internal sealed class Remote : CameraView
    {
        private readonly ViewReport _report;
        private readonly long _connectionId;

        internal Remote(ViewReport report, double ageS, string playerName, long connectionId)
        {
            _report = report;
            _connectionId = connectionId;
            PlayerName = playerName;
            Source = new ViewSourceView("remote", ageS);
            Basis = ViewBasis.Of(report.Forward, report.Up);
        }

        internal override Vec3 Eye => _report.Eye;

        internal override ViewBasis Basis { get; }

        internal override bool ThirdPerson => _report.ThirdPerson;

        internal override bool Seated => _report.Seated;

        internal override Thing? Target => _report.Target is ViewTarget target ? Live(target.ThingId) : null;

        internal override Interactable? Interactable
        {
            get
            {
                Thing? thing = Target;
                int? index = _report.Target?.InteractableId;
                return thing != null && index is int found && found >= 0 && found < thing.Interactables.Count
                    ? thing.Interactables[found]
                    : null;
            }
        }

        internal override string PlayerName { get; }

        internal override ViewSourceView Source { get; }

        internal override PlayerScreen Screen => new PlayerScreen.Remote(_connectionId, PlayerName);

        // The client cast up to ViewWire.ReachM, so its first hit is the first within any shorter reach.
        internal override CameraHit? HitWithin(double reachM) =>
            _report.Hit is ViewHit hit && hit.DistanceM <= reachM
                ? new CameraHit(hit.Point, hit.Normal, hit.DistanceM, hit.ThingId != 0 ? Live(hit.ThingId) : null)
                : null;

        private static Thing? Live(long id)
        {
            Thing thing = Thing.Find(id);
            return thing != null && !thing.IsBeingDestroyed ? thing : null;
        }
    }
}

/// <summary>A highlight to draw: its target and, for things or a network, the things it found.</summary>
internal sealed class ScreenMark
{
    internal ScreenMark(HighlightTarget target, List<Thing> things)
    {
        Target = target;
        Things = things;
    }

    internal HighlightTarget Target { get; }

    internal List<Thing> Things { get; }
}

/// <summary>
/// Where show_preview and highlight draw: this game's screen (Previews, Highlights), or a remote player's, by sending
/// the drawing to their StationGod (RemoteDrawings), which draws it with the same code. Each call answers how many
/// earlier drawings of its kind it cleared.
/// </summary>
internal abstract class PlayerScreen
{
    private PlayerScreen()
    {
    }

    /// <summary>The player whose game draws, when that is another machine; null for this game's screen.</summary>
    internal abstract string? DrawnOn { get; }

    /// <summary>What highlight's marks are drawn with, for its reply's renderer.</summary>
    internal abstract string Renderer { get; }

    internal abstract int Preview(bool replace, IReadOnlyList<PreviewBox> boxes);

    internal abstract int ClearPreviews();

    internal abstract int Highlight(bool replace, IReadOnlyList<ScreenMark> marks, double seconds, List<string> notes);

    internal abstract int ClearHighlights();

    internal sealed class Local : PlayerScreen
    {
        internal static readonly Local Instance = new Local();

        private Local()
        {
        }

        internal override string? DrawnOn => null;

        internal override string Renderer => XRay.MeshSource;

        internal override int Preview(bool replace, IReadOnlyList<PreviewBox> boxes)
        {
            if (!Previews.CanDraw)
            {
                throw NoLines("lines");
            }

            foreach (PreviewBox box in boxes)
            {
                if (box.XRay && XRay.LineMaterial == null)
                {
                    throw NoLines("see-through lines");
                }
            }

            int cleared = replace ? Previews.Clear() : 0;
            foreach (PreviewBox box in boxes)
            {
                if (!Previews.Box(box.Box, RemoteDrawing.ColorOf(box.Color), box.Seconds, "StationGodPreview " + box.Name,
                        box.XRay))
                {
                    throw NoLines("lines");
                }
            }

            return cleared;
        }

        internal override int ClearPreviews() => Previews.Clear();

        internal override int Highlight(bool replace, IReadOnlyList<ScreenMark> marks, double seconds, List<string> notes)
        {
            if (XRay.MeshMaterial == null)
            {
                throw ApiErrors.Refused("no_xray_material",
                    "Neither the T-Ray SPU's material nor a built-in shader to draw through walls with was found.");
            }

            int cleared = replace ? Highlights.Clear() : 0;
            float until = Time.realtimeSinceStartup + (float)seconds;
            foreach (ScreenMark mark in marks)
            {
                Highlights.Add(mark.Target is HighlightTarget.Point point
                    ? new PointMark(point, until)
                    : new ThingsMark(mark.Target, until, mark.Things));
            }

            return cleared;
        }

        internal override int ClearHighlights() => Highlights.Clear();

        private static ApiException NoLines(string what) =>
            ApiErrors.Refused("no_line_shader", $"This build of the game has no built-in shader to draw {what} with.");
    }

    internal sealed class Remote : PlayerScreen
    {
        private readonly long _connectionId;
        private readonly string _player;

        internal Remote(long connectionId, string player)
        {
            _connectionId = connectionId;
            _player = player;
        }

        internal override string? DrawnOn => _player;

        internal override string Renderer => "remote";

        internal override int Preview(bool replace, IReadOnlyList<PreviewBox> boxes) =>
            Send(new DrawCommand.Previews(replace, boxes));

        internal override int ClearPreviews() => Send(new DrawCommand.Previews(true, new List<PreviewBox>()));

        internal override int Highlight(bool replace, IReadOnlyList<ScreenMark> marks, double seconds, List<string> notes)
        {
            List<MarkSpec> specs = new List<MarkSpec>(marks.Count);
            int room = DrawWire.MaximumIds;
            bool cut = false;
            foreach (ScreenMark mark in marks)
            {
                HighlightTarget target = mark.Target;
                Rgba tint = new Rgba((float)target.Tint.Red, (float)target.Tint.Green, (float)target.Tint.Blue,
                    (float)target.Tint.Alpha);
                if (target is HighlightTarget.Point point)
                {
                    specs.Add(new MarkSpec.Point(point.At, tint, target.Label, target.Pulse, (float)seconds));
                    continue;
                }

                List<long> ids = new List<long>(System.Math.Min(mark.Things.Count, room));
                foreach (Thing thing in mark.Things)
                {
                    if (ids.Count == room)
                    {
                        cut = true;
                        break;
                    }

                    ids.Add(thing.ReferenceId);
                }

                room -= ids.Count;
                specs.Add(new MarkSpec.Things(ids, tint, target.Label, target.Pulse, (float)seconds));
            }

            if (cut)
            {
                notes.Add($"Only the first {DrawWire.MaximumIds} things were sent to {_player}'s game; the rest are " +
                          "not drawn there.");
            }

            return Send(new DrawCommand.Highlights(replace, specs));
        }

        internal override int ClearHighlights() => Send(new DrawCommand.Highlights(true, new List<MarkSpec>()));

        private int Send(DrawCommand command) =>
            RemoteDrawings.Send(_connectionId, command) ??
            throw ApiErrors.Refused("no_view",
                $"{_player}'s game could not be reached to draw on (they left, or the drawing was too large).");
    }
}
