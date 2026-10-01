#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// show_preview's in-game ghosts: wire boxes drawn with LineRenderers on game objects of the mod's own, never the
/// game's construction cursor or any thing. Each lives for its seconds and is destroyed by Tick (the mod's Update);
/// Clear removes them all (also when the mod unloads). Only the local game draws them: other players see nothing.
/// With xray the lines use XRay's see-through material, so walls, frames and terrain no longer hide them.
/// </summary>
internal static class Previews
{
    private const float LineWidth = 0.02f;

    // A path over all twelve edges of a box: bottom loop, up, top loop, and the three other uprights.
    private static readonly int[] Path = { 0, 1, 2, 3, 0, 4, 5, 1, 5, 6, 2, 6, 7, 3, 7, 4 };

    private static readonly List<(GameObject Box, float Until)> Shown = new List<(GameObject, float)>();
    private static Material? _material;
    private static GameObject? _root;

    internal static int Count => Shown.Count;

    /// <summary>Whether this build of the game has a shader to draw lines with.</summary>
    internal static bool CanDraw => MaterialOf() != null;

    /// <summary>Draws a box until the given seconds have passed; false when no line material can be made.</summary>
    internal static bool Box(Box3 box, Color color, float seconds, string name, bool xray = false)
    {
        Material? material = xray ? XRay.LineMaterial : MaterialOf();
        if (material == null)
        {
            return false;
        }

        if (_root == null)
        {
            _root = new GameObject("StationGodPreview");
            Object.DontDestroyOnLoad(_root);
        }

        GameObject line = new GameObject(name);
        line.transform.SetParent(_root.transform, false);
        LineRenderer renderer = line.AddComponent<LineRenderer>();
        renderer.sharedMaterial = material;
        renderer.useWorldSpace = true;
        renderer.startWidth = LineWidth;
        renderer.endWidth = LineWidth;
        renderer.startColor = color;
        renderer.endColor = color;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        Vector3[] corners = Corners(box);
        renderer.positionCount = Path.Length;
        for (int index = 0; index < Path.Length; index++)
        {
            renderer.SetPosition(index, corners[Path[index]]);
        }

        Shown.Add((line, Time.realtimeSinceStartup + seconds));
        return true;
    }

    /// <summary>Destroys ghosts whose time is up; every frame.</summary>
    internal static void Tick()
    {
        if (Shown.Count == 0)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        for (int index = Shown.Count - 1; index >= 0; index--)
        {
            if (Shown[index].Until <= now)
            {
                Destroy(Shown[index].Box);
                Shown.RemoveAt(index);
            }
        }
    }

    internal static int Clear()
    {
        int count = Shown.Count;
        foreach ((GameObject box, float _) in Shown)
        {
            Destroy(box);
        }

        Shown.Clear();
        return count;
    }

    private static void Destroy(GameObject box)
    {
        if (box != null)
        {
            Object.Destroy(box);
        }
    }

    // The first of the built-in line shaders this build of the game ships.
    private static Material? MaterialOf()
    {
        if (_material != null)
        {
            return _material;
        }

        foreach (string name in new[] { "Sprites/Default", "Hidden/Internal-Colored", "Unlit/Color" })
        {
            Shader shader = Shader.Find(name);
            if (shader != null)
            {
                _material = new Material(shader) { renderQueue = 4000 };
                return _material;
            }
        }

        return null;
    }

    private static Vector3[] Corners(Box3 box)
    {
        Vector3 a = Bodies.U(box.Min);
        Vector3 b = Bodies.U(box.Max);
        return new[]
        {
            new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z),
            new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z),
            new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z)
        };
    }
}
