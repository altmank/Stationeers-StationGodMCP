#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// highlight's marks, drawn every frame by Tick (the mod's Update) as the T-Ray SPU draws (SPUMesonScanner.Render from
/// SensorLenses.UpdateEachFrame: Graphics.DrawMesh calls with a see-through material, no game object per piece), plus
/// labels and screen-edge arrows from an OnGUI overlay on the mod's own game object. Only this game draws them;
/// nothing in the world changes. Each mark lives for its seconds; Clear removes them all (also when the mod unloads or
/// the world is left). A frame allocates nothing: one MeshDraws is refilled every frame, each mark keeps its meshes,
/// and the overlay is disabled (no OnGUI calls) while no mark is shown.
/// </summary>
internal static class Highlights
{
    private static readonly List<HighlightMark> Shown = new List<HighlightMark>();
    private static readonly MeshDraws Draws = new MeshDraws();
    private static GameObject? _root;
    private static HighlightOverlay? _overlay;

    internal static IReadOnlyList<HighlightMark> Marks => Shown;

    internal static void Add(HighlightMark mark)
    {
        if (_root == null)
        {
            _root = new GameObject("StationGodHighlight");
            Object.DontDestroyOnLoad(_root);
            _overlay = _root.AddComponent<HighlightOverlay>();
        }

        Shown.Add(mark);
        if (_overlay != null)
        {
            _overlay.enabled = true;
        }
    }

    /// <summary>Drops marks whose time is up and draws the rest; every frame.</summary>
    internal static void Tick()
    {
        if (Shown.Count == 0)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (Expiry.RemoveExpired(Shown, now) > 0 && Shown.Count == 0)
        {
            Idle();
            return;
        }

        Material? material = XRay.MeshMaterial;
        if (material == null)
        {
            return;
        }

        Draws.BeginFrame();
        Vector3 eye = CameraPosition();
        foreach (HighlightMark mark in Shown)
        {
            mark.Draw(Draws, eye, now);
        }

        Draws.Flush(material);
    }

    internal static int Clear()
    {
        int count = Shown.Count;
        Shown.Clear();
        Idle();
        return count;
    }

    // Nothing shown: the overlay stops getting OnGUI calls and the mesh groups are let go.
    private static void Idle()
    {
        if (_overlay != null)
        {
            _overlay.enabled = false;
        }

        Draws.Reset();
    }

    internal static Vector3 CameraPosition() =>
        CameraController.CurrentCamera != null ? CameraController.CurrentCamera.transform.position : Vector3.zero;
}

/// <summary>
/// One frame's meshes: grouped per mesh and submesh for the T-Ray material's instanced colours, else one by one. Kept
/// across frames (BeginFrame empties the groups but keeps their lists), with one property block and one pair of batch
/// arrays, so drawing allocates nothing.
/// </summary>
internal sealed class MeshDraws
{
    // DrawMeshInstanced's limit per call, as SPUMesonScanner.MaxBatchSize.
    private const int BatchSize = 1023;
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");

    private readonly FrameGroups<(Mesh, int), Matrix4x4, Vector4> _groups =
        new FrameGroups<(Mesh, int), Matrix4x4, Vector4>();

    private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
    private readonly Matrix4x4[] _matrices = new Matrix4x4[BatchSize];
    private readonly Vector4[] _colors = new Vector4[BatchSize];

    internal void BeginFrame() => _groups.BeginFrame();

    internal void Reset() => _groups.Reset();

    internal void Add(Mesh mesh, Matrix4x4 matrix, Color color)
    {
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            _groups.Add((mesh, submesh), matrix, color);
        }
    }

    internal void Flush(Material material)
    {
        foreach (KeyValuePair<(Mesh, int), FrameGroups<(Mesh, int), Matrix4x4, Vector4>.Group> entry in _groups)
        {
            (Mesh mesh, int submesh) = entry.Key;
            FrameGroups<(Mesh, int), Matrix4x4, Vector4>.Group group = entry.Value;
            if (group.Count == 0 || mesh == null)
            {
                continue;
            }

            if (XRay.Instanced)
            {
                for (int start = 0; start < group.Count; start += BatchSize)
                {
                    // The arrays are always BatchSize long (a property block keeps the first array size it is
                    // given); count says how many instances of them are drawn.
                    int count = Expiry.BatchCount(group.Count, start, BatchSize);
                    group.First.CopyTo(start, _matrices, 0, count);
                    group.Second.CopyTo(start, _colors, 0, count);
                    _block.Clear();
                    _block.SetVectorArray(ColorProperty, _colors);
                    Graphics.DrawMeshInstanced(mesh, submesh, material, _matrices, count, _block,
                        ShadowCastingMode.Off, false);
                }

                continue;
            }

            for (int index = 0; index < group.Count; index++)
            {
                _block.Clear();
                _block.SetColor(ColorProperty, group.Second[index]);
                Graphics.DrawMesh(mesh, group.First[index], material, 0, null, submesh, _block,
                    ShadowCastingMode.Off, false);
            }
        }
    }
}

/// <summary>One highlight target as drawn: its colour, label, pulse and time.</summary>
internal abstract class HighlightMark : IExpiring
{
    protected HighlightMark(HighlightTarget target, float until)
    {
        Target = target;
        Until = until;
    }

    internal HighlightTarget Target { get; }

    public float Until { get; }

    internal abstract void Draw(MeshDraws draws, Vector3 eye, float now);

    /// <summary>Where the label goes this frame, and its text; false when there is nothing to label.</summary>
    internal abstract bool Label(Vector3 eye, out Vector3 at, out string text);

    /// <summary>Whether the overlay pins it to the screen's edge when it is out of view (points only).</summary>
    internal abstract bool PinnedAtEdge { get; }

    internal Color ColorAt(float now)
    {
        Tint tint = Target.Tint;
        float strength = Target.Pulse ? (float)PulseCurve.Strength(now) : 1f;
        return new Color((float)tint.Red * strength, (float)tint.Green * strength, (float)tint.Blue * strength,
            (float)tint.Alpha * (0.35f + 0.5f * strength));
    }

    internal Color LabelColor => new Color((float)Target.Tint.Red, (float)Target.Tint.Green, (float)Target.Tint.Blue);
}

/// <summary>Things (by id, or a network's pieces), each drawn with its own meshes as its renderers hold them.</summary>
internal sealed class ThingsMark : HighlightMark
{
    private readonly List<Thing> _things;

    // Each thing's meshes and their transforms, read once at the first draw; the transforms' matrices are read every
    // frame, so a thing that moves is drawn where it is. A thing rebuilt to another build state meanwhile keeps the
    // meshes it had when the mark was first drawn.
    private List<(Mesh Mesh, Transform Transform)>[]? _parts;

    internal ThingsMark(HighlightTarget target, float until, List<Thing> things) : base(target, until)
    {
        _things = things;
    }

    internal override bool PinnedAtEdge => false;

    internal override void Draw(MeshDraws draws, Vector3 eye, float now)
    {
        _parts ??= ThingMeshes.PartsOf(_things);
        Color color = ColorAt(now);
        for (int index = 0; index < _things.Count; index++)
        {
            Thing thing = _things[index];
            if (thing == null || thing.IsBeingDestroyed)
            {
                continue;
            }

            List<(Mesh Mesh, Transform Transform)> parts = _parts[index];
            for (int part = 0; part < parts.Count; part++)
            {
                (Mesh mesh, Transform transform) = parts[part];
                if (mesh != null && transform != null)
                {
                    draws.Add(mesh, transform.localToWorldMatrix, color);
                }
            }
        }
    }

    // On the thing nearest the eye, so a long network is labelled where the player is.
    internal override bool Label(Vector3 eye, out Vector3 at, out string text)
    {
        at = Vector3.zero;
        text = Target.Label ?? string.Empty;
        float best = float.MaxValue;
        foreach (Thing thing in _things)
        {
            if (thing == null || thing.IsBeingDestroyed)
            {
                continue;
            }

            float distance = (thing.Position - eye).sqrMagnitude;
            if (distance < best)
            {
                best = distance;
                at = thing.Position + Vector3.up * 0.4f;
            }
        }

        return Target.Label != null && best < float.MaxValue;
    }
}

/// <summary>A world point: a tall beam standing on it, widening with distance so it stays seen from afar.</summary>
internal sealed class PointMark : HighlightMark
{
    private const float BeamHeight = 400f;
    private const float BeamBelow = 3f;
    private const float MinimumWidth = 0.3f;
    private const float WidthPerMetre = 0.004f;

    private static Mesh? _cube;
    private readonly Vector3 _at;

    internal PointMark(HighlightTarget.Point target, float until) : base(target, until)
    {
        _at = Bodies.U(target.At);
    }

    internal override bool PinnedAtEdge => true;

    internal override void Draw(MeshDraws draws, Vector3 eye, float now)
    {
        Mesh? cube = Cube();
        if (cube == null)
        {
            return;
        }

        float width = Mathf.Max(MinimumWidth, Vector3.Distance(eye, _at) * WidthPerMetre);
        Vector3 centre = _at + Vector3.up * (BeamHeight / 2 - BeamBelow);
        Color color = ColorAt(now);
        draws.Add(cube, Matrix4x4.TRS(centre, Quaternion.identity, new Vector3(width, BeamHeight, width)), color);
        draws.Add(cube, Matrix4x4.TRS(_at, Quaternion.Euler(45f, 45f, 0f), Vector3.one * width * 2.5f), color);
    }

    internal override bool Label(Vector3 eye, out Vector3 at, out string text)
    {
        at = _at;
        string heading = Heading.From(Bodies.V(eye), Bodies.V(_at)).Text();
        text = Target.Label != null ? Target.Label + "\n" + heading : heading;
        return true;
    }

    private static Mesh? Cube()
    {
        if (_cube == null)
        {
            _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        }

        return _cube;
    }
}

/// <summary>
/// A thing's meshes as the game draws them: each of its ThingRenderers that is enabled and active (the build state
/// shown now, a device's every part), with its renderer's transform; all of them when none reads enabled.
/// </summary>
internal static class ThingMeshes
{
    private static readonly List<(Mesh, Transform)> NoParts = new List<(Mesh, Transform)>(0);

    /// <summary>Each thing's parts, in the things' order; an empty list for a thing that is gone or has none.</summary>
    internal static List<(Mesh Mesh, Transform Transform)>[] PartsOf(List<Thing> things)
    {
        List<(Mesh, Transform)>[] parts = new List<(Mesh, Transform)>[things.Count];
        for (int index = 0; index < things.Count; index++)
        {
            Thing thing = things[index];
            parts[index] = thing == null || thing.IsBeingDestroyed ? NoParts : PartsOf(thing);
        }

        return parts;
    }

    private static List<(Mesh, Transform)> PartsOf(Thing thing)
    {
        List<(Mesh, Transform)> parts = new List<(Mesh, Transform)>();
        if (thing.Renderers == null)
        {
            return parts;
        }

        Collect(thing, parts, onlyShown: true);
        if (parts.Count == 0)
        {
            Collect(thing, parts, onlyShown: false);
        }

        return parts;
    }

    private static void Collect(Thing thing, List<(Mesh, Transform)> parts, bool onlyShown)
    {
        foreach (ThingRenderer renderer in thing.Renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            Mesh? mesh = renderer.SharedMesh;
            Transform? transform = renderer.GetRendererTransform();
            if (mesh == null || transform == null ||
                (onlyShown && !(renderer.Enabled && renderer.RendererGameObjectActiveInHierarchy())))
            {
                continue;
            }

            parts.Add((mesh, transform));
        }
    }
}

/// <summary>
/// Draws the marks' labels, and a point's arrow at the screen's edge while it is out of view. Enabled only while a
/// mark is shown (Highlights), so Unity makes no OnGUI calls otherwise.
/// </summary>
internal sealed class HighlightOverlay : MonoBehaviour
{
    private const float EdgeMargin = 0.04f;
    private const float LabelWidth = 320f;
    private const float LabelHeight = 44f;

    private GUIStyle? _style;

    // Labels are placed with GUI.Label rects only: no GUILayout pass.
    private void Awake()
    {
        useGUILayout = false;
    }

    private void OnGUI()
    {
        Camera camera = CameraController.CurrentCamera;
        if (camera == null || Highlights.Marks.Count == 0 || Event.current.type != EventType.Repaint)
        {
            return;
        }

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = false };
        }

        Vector3 eye = camera.transform.position;
        foreach (HighlightMark mark in Highlights.Marks)
        {
            if (!mark.Label(eye, out Vector3 at, out string text))
            {
                continue;
            }

            Vector3 viewport = camera.WorldToViewportPoint(at);
            ScreenMarker marker = ScreenMarker.Of(viewport.x, viewport.y, viewport.z, EdgeMargin);
            if (!marker.OnScreen && !mark.PinnedAtEdge)
            {
                continue;
            }

            Vector2 screen = new Vector2((float)marker.X * Screen.width, (1f - (float)marker.Y) * Screen.height);
            if (!marker.OnScreen)
            {
                Arrow(screen, (float)marker.Angle, mark.LabelColor);
            }

            Text(screen, text, mark.LabelColor, marker);
        }
    }

    private void Text(Vector2 screen, string text, Color color, ScreenMarker marker)
    {
        // Pinned to the right or bottom edge, the label grows back into the screen.
        float x = marker.X > 0.5 && !marker.OnScreen ? screen.x - LabelWidth - 24f : screen.x + 12f;
        float y = marker.Y < 0.5 && !marker.OnScreen ? screen.y - LabelHeight - 12f : screen.y - LabelHeight / 2;
        Rect rect = new Rect(x, y, LabelWidth, LabelHeight);
        GUI.color = Color.black;
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _style);
        GUI.color = color;
        GUI.Label(rect, text, _style);
        GUI.color = Color.white;
    }

    // ">>" turned toward the point (GUI angles run clockwise, the marker's counter-clockwise).
    private void Arrow(Vector2 screen, float angle, Color color)
    {
        Matrix4x4 before = GUI.matrix;
        GUIUtility.RotateAroundPivot(-angle, screen);
        GUI.color = color;
        GUI.Label(new Rect(screen.x - 14f, screen.y - 14f, 40f, 28f), ">>", _style);
        GUI.color = Color.white;
        GUI.matrix = before;
    }
}
