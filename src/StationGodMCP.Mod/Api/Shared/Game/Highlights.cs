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
/// nothing in the world changes. Each mark lives for its seconds; Clear removes them all (also when the mod unloads).
/// </summary>
internal static class Highlights
{
    private static readonly List<HighlightMark> Shown = new List<HighlightMark>();
    private static GameObject? _root;

    internal static IReadOnlyList<HighlightMark> Marks => Shown;

    internal static void Add(HighlightMark mark)
    {
        if (_root == null)
        {
            _root = new GameObject("StationGodHighlight");
            Object.DontDestroyOnLoad(_root);
            _root.AddComponent<HighlightOverlay>();
        }

        Shown.Add(mark);
    }

    /// <summary>Drops marks whose time is up and draws the rest; every frame.</summary>
    internal static void Tick()
    {
        if (Shown.Count == 0)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        Shown.RemoveAll(mark => mark.Until <= now);
        Material? material = XRay.MeshMaterial;
        if (material == null || Shown.Count == 0)
        {
            return;
        }

        MeshDraws draws = new MeshDraws();
        foreach (HighlightMark mark in Shown)
        {
            mark.Draw(draws, CameraPosition(), now);
        }

        draws.Flush(material);
    }

    internal static int Clear()
    {
        int count = Shown.Count;
        Shown.Clear();
        return count;
    }

    internal static Vector3 CameraPosition() =>
        CameraController.CurrentCamera != null ? CameraController.CurrentCamera.transform.position : Vector3.zero;
}

/// <summary>One frame's meshes: grouped per mesh for the T-Ray material's instanced colours, else one by one.</summary>
internal sealed class MeshDraws
{
    // DrawMeshInstanced's limit per call, as SPUMesonScanner.MaxBatchSize.
    private const int BatchSize = 1023;
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");

    private readonly Dictionary<(Mesh, int), (List<Matrix4x4> Matrices, List<Vector4> Colors)> _groups =
        new Dictionary<(Mesh, int), (List<Matrix4x4>, List<Vector4>)>();

    internal void Add(Mesh mesh, Matrix4x4 matrix, Color color)
    {
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            if (!_groups.TryGetValue((mesh, submesh), out var group))
            {
                group = (new List<Matrix4x4>(), new List<Vector4>());
                _groups[(mesh, submesh)] = group;
            }

            group.Matrices.Add(matrix);
            group.Colors.Add(color);
        }
    }

    internal void Flush(Material material)
    {
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        foreach (KeyValuePair<(Mesh, int), (List<Matrix4x4> Matrices, List<Vector4> Colors)> group in _groups)
        {
            (Mesh mesh, int submesh) = group.Key;
            if (XRay.Instanced)
            {
                for (int start = 0; start < group.Value.Matrices.Count; start += BatchSize)
                {
                    int count = Mathf.Min(BatchSize, group.Value.Matrices.Count - start);
                    block.Clear();
                    block.SetVectorArray(ColorProperty, group.Value.Colors.GetRange(start, count));
                    Graphics.DrawMeshInstanced(mesh, submesh, material, group.Value.Matrices.GetRange(start, count),
                        block, ShadowCastingMode.Off, false);
                }

                continue;
            }

            for (int index = 0; index < group.Value.Matrices.Count; index++)
            {
                block.Clear();
                block.SetColor(ColorProperty, group.Value.Colors[index]);
                Graphics.DrawMesh(mesh, group.Value.Matrices[index], material, 0, null, submesh, block,
                    ShadowCastingMode.Off, false);
            }
        }
    }
}

/// <summary>One highlight target as drawn: its colour, label, pulse and time.</summary>
internal abstract class HighlightMark
{
    protected HighlightMark(HighlightTarget target, float until)
    {
        Target = target;
        Until = until;
    }

    internal HighlightTarget Target { get; }

    internal float Until { get; }

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

    internal ThingsMark(HighlightTarget target, float until, List<Thing> things) : base(target, until)
    {
        _things = things;
    }

    internal override bool PinnedAtEdge => false;

    internal override void Draw(MeshDraws draws, Vector3 eye, float now)
    {
        Color color = ColorAt(now);
        foreach (Thing thing in _things)
        {
            if (thing == null || thing.IsBeingDestroyed)
            {
                continue;
            }

            foreach ((Mesh mesh, Matrix4x4 matrix) in ThingMeshes.Of(thing))
            {
                draws.Add(mesh, matrix, color);
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
/// shown now, a device's every part), at its renderer's transform; all of them when none reads enabled.
/// </summary>
internal static class ThingMeshes
{
    internal static List<(Mesh, Matrix4x4)> Of(Thing thing)
    {
        List<(Mesh, Matrix4x4)> meshes = new List<(Mesh, Matrix4x4)>();
        if (thing.Renderers == null)
        {
            return meshes;
        }

        Collect(thing, meshes, onlyShown: true);
        if (meshes.Count == 0)
        {
            Collect(thing, meshes, onlyShown: false);
        }

        return meshes;
    }

    private static void Collect(Thing thing, List<(Mesh, Matrix4x4)> meshes, bool onlyShown)
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

            meshes.Add((mesh, transform.localToWorldMatrix));
        }
    }
}

/// <summary>Draws the marks' labels, and a point's arrow at the screen's edge while it is out of view.</summary>
internal sealed class HighlightOverlay : MonoBehaviour
{
    private const float EdgeMargin = 0.04f;
    private const float LabelWidth = 320f;
    private const float LabelHeight = 44f;

    private GUIStyle? _style;

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
