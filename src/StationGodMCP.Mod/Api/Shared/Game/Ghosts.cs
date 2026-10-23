#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Net;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// show preview's ghosts: a structure's own meshes, from its prefab at a build state (GhostMeshes), drawn tinted where a
/// placement or a run's piece would stand, every frame by Tick (the mod's Update) with Graphics.DrawMesh, as the
/// highlights are: no game object, nothing in the world. Two passes: the game's construction-cursor shader
/// (CursorManager.CursorShader, the look of a structure in the hand), hidden by what stands in front as a solid thing is;
/// and with xray a fainter pass in XRay's see-through material on top of everything, so a ghost behind walls, frames or
/// terrain shows dimly and the parts in view brighter. Each ghost lives for its seconds; Clear removes them all (also
/// when the mod unloads or the world is left). Every ghost keeps its source (an agent's call, or a player), so ghosts a
/// player places later go through the same store and drawing.
/// </summary>
internal static class Ghosts
{
    /// <summary>How much fainter the see-through pass is than the ghost's own alpha.</summary>
    private const float SeeThroughShare = 0.5f;

    private static readonly int OffsetProperty = Shader.PropertyToID("_Offset");
    private static readonly List<GhostMark> Shown = new List<GhostMark>();
    private static readonly MeshDraws Solid = new MeshDraws();
    private static readonly MeshDraws SeeThrough = new MeshDraws();
    private static Material? _solid;
    private static string _source = "none";

    internal static int Count => Shown.Count;

    internal static IReadOnlyList<GhostMark> Marks => Shown;

    /// <summary>What the ghosts are drawn with: "cursor (shader name)" or "built-in", for notes.</summary>
    internal static string Source
    {
        get
        {
            _ = SolidMaterial();
            return _source;
        }
    }

    /// <summary>Whether this game has a material to draw ghosts with.</summary>
    internal static bool CanDraw => SolidMaterial() != null;

    /// <summary>
    /// Shows each ghost (replacing what is shown first when asked); the number of ghosts drawn: a ghost whose prefab
    /// this game does not have, or that has no meshes, is left out.
    /// </summary>
    internal static int Show(IReadOnlyList<GhostSpec> ghosts)
    {
        float now = Time.realtimeSinceStartup;
        int drawn = 0;
        foreach (GhostSpec ghost in ghosts)
        {
            if (!(Prefab.Find(ghost.PrefabHash) is Structure prefab) || prefab == null)
            {
                OnceLog.Warning($"ghost_prefab:{ghost.PrefabHash}",
                    $"No structure prefab with hash {ghost.PrefabHash} in this game: its ghost is not drawn.");
                continue;
            }

            List<GhostPart> parts = GhostMeshes.Of(prefab, ghost.BuildState);
            if (parts.Count == 0)
            {
                continue;
            }

            Matrix4x4 pose = Matrix4x4.TRS(Bodies.U(ghost.Position), Quaternion.Euler(Bodies.U(ghost.Euler)),
                prefab.transform.localScale);
            Shown.Add(new GhostMark(parts, pose, ghost, now + ghost.Seconds));
            drawn++;
        }

        return drawn;
    }

    /// <summary>Drops ghosts whose time is up and draws the rest; every frame.</summary>
    internal static void Tick()
    {
        if (Shown.Count == 0)
        {
            return;
        }

        if (Expiry.RemoveExpired(Shown, Time.realtimeSinceStartup) > 0 && Shown.Count == 0)
        {
            Idle();
            return;
        }

        Material? solid = SolidMaterial();
        if (solid == null)
        {
            return;
        }

        Material? seeThrough = XRay.MeshMaterial;
        Solid.BeginFrame();
        SeeThrough.BeginFrame();
        bool anySeeThrough = false;
        foreach (GhostMark mark in Shown)
        {
            mark.Draw(Solid, mark.SolidColor);
            if (mark.XRay && seeThrough != null)
            {
                mark.Draw(SeeThrough, mark.SeeThroughColor);
                anySeeThrough = true;
            }
        }

        Solid.Flush(solid, instanced: false);
        if (anySeeThrough)
        {
            SeeThrough.Flush(seeThrough!, XRay.Instanced);
        }
    }

    internal static int Clear()
    {
        int count = Shown.Count;
        Shown.Clear();
        Idle();
        return count;
    }

    private static void Idle()
    {
        Solid.Reset();
        SeeThrough.Reset();
    }

    // The construction cursor's shader as the game sets it up (InventoryManager.HandleStructurePrefab: white, no
    // texture, _Offset -300), else the built-in coloured shader, alpha blended and depth tested; null without either.
    private static Material? SolidMaterial()
    {
        if (_solid != null)
        {
            return _solid;
        }

        Shader? cursor = CursorManager.Instance != null ? CursorManager.Instance.CursorShader : null;
        if (cursor != null)
        {
            _solid = new Material(cursor) { color = Color.white, mainTexture = null };
            if (_solid.HasProperty(OffsetProperty))
            {
                _solid.SetFloat(OffsetProperty, -300f);
            }

            _source = $"cursor ({cursor.name})";
            return _solid;
        }

        Shader colored = Shader.Find("Hidden/Internal-Colored");
        if (colored == null)
        {
            return null;
        }

        _solid = new Material(colored) { renderQueue = 3000 };
        _solid.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        _solid.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        _solid.SetInt("_Cull", (int)CullMode.Back);
        _solid.SetInt("_ZWrite", 0);
        _solid.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        _source = "built-in";
        return _solid;
    }

    internal static float SeeThroughAlpha(float alpha) => alpha * SeeThroughShare;
}

/// <summary>One mesh of a prefab and where it sits relative to the prefab's root.</summary>
internal readonly struct GhostPart
{
    internal GhostPart(Mesh mesh, Matrix4x4 local)
    {
        Mesh = mesh;
        Local = local;
    }

    internal Mesh Mesh { get; }

    internal Matrix4x4 Local { get; }
}

/// <summary>A ghost as drawn: its meshes placed in the world, its colours, its time and who put it there.</summary>
internal sealed class GhostMark : IExpiring
{
    private readonly Matrix4x4[] _matrices;
    private readonly Mesh[] _meshes;

    internal GhostMark(List<GhostPart> parts, Matrix4x4 pose, GhostSpec spec, float until)
    {
        _meshes = new Mesh[parts.Count];
        _matrices = new Matrix4x4[parts.Count];
        for (int index = 0; index < parts.Count; index++)
        {
            _meshes[index] = parts[index].Mesh;
            _matrices[index] = pose * parts[index].Local;
        }

        Spec = spec;
        Until = until;
        Rgba tint = spec.Tint;
        SolidColor = new Color(tint.Red, tint.Green, tint.Blue, tint.Alpha);
        SeeThroughColor = new Color(tint.Red, tint.Green, tint.Blue, Ghosts.SeeThroughAlpha(tint.Alpha));
    }

    internal GhostSpec Spec { get; }

    public float Until { get; }

    internal bool XRay => Spec.XRay;

    internal Color SolidColor { get; }

    internal Color SeeThroughColor { get; }

    internal void Draw(MeshDraws draws, Color color)
    {
        for (int index = 0; index < _meshes.Length; index++)
        {
            if (_meshes[index] != null)
            {
                draws.Add(_meshes[index], _matrices[index], color);
            }
        }
    }
}

/// <summary>
/// A structure prefab's meshes at a build state, as Structure.UpdateStateVisualizer shows them on a built one: each
/// state's batched mesh (InitialDrawData, drawn at the root) or its Visualizer renderer, on for the states up to this
/// one (OnMineAndPreviousStates) or for this one only (OnMyState); the renderers under this state's LinkedGameObjects;
/// and every other renderer the prefab has switched on, outside any state's renderers and linked objects (a device's
/// screens and dials), without text, particles, lines and LOD levels past the first. Read once per prefab and state.
/// </summary>
internal static class GhostMeshes
{
    private static readonly Dictionary<(int Hash, int State), List<GhostPart>> Cache =
        new Dictionary<(int, int), List<GhostPart>>();

    /// <summary>The prefab's parts at the state (past the last state, the last; below 0, the first).</summary>
    internal static List<GhostPart> Of(Structure prefab, int state)
    {
        int states = prefab.BuildStates != null ? prefab.BuildStates.Count : 0;
        int shown = states == 0 ? 0 : Mathf.Clamp(state, 0, states - 1);
        if (Cache.TryGetValue((prefab.PrefabHash, shown), out List<GhostPart>? cached))
        {
            return cached;
        }

        List<GhostPart> parts = Read(prefab, shown);
        Cache[(prefab.PrefabHash, shown)] = parts;
        return parts;
    }

    internal static void Clear() => Cache.Clear();

    private static List<GhostPart> Read(Structure prefab, int shown)
    {
        List<GhostPart> parts = new List<GhostPart>();
        Transform root = prefab.transform;
        Matrix4x4 toRoot = root.worldToLocalMatrix;
        HashSet<Renderer> stateRenderers = new HashSet<Renderer>();
        HashSet<Transform> linked = new HashSet<Transform>();
        HashSet<Renderer> added = new HashSet<Renderer>();
        List<BuildState> states = prefab.BuildStates ?? new List<BuildState>();

        // A batched structure draws its states' meshes itself and destroys the root's renderer at Awake.
        MeshRenderer? rootRenderer = prefab.GetComponent<MeshRenderer>();
        if (rootRenderer != null && Batched(states))
        {
            stateRenderers.Add(rootRenderer);
        }

        for (int index = 0; index < states.Count; index++)
        {
            BuildState buildState = states[index];
            if (buildState == null)
            {
                continue;
            }

            bool on = buildState.RenderMode == BuildStateRenderMode.OnMyState ? index == shown : index <= shown;
            Renderer? visualizer = buildState.Visualizer != null ? buildState.Visualizer
                : index == 0 ? prefab.GetComponent<MeshRenderer>() : null;
            if (visualizer != null)
            {
                stateRenderers.Add(visualizer);
            }

            if (buildState.InitialDrawData.IsValid())
            {
                if (on)
                {
                    parts.Add(new GhostPart(buildState.InitialDrawData.mesh, Matrix4x4.identity));
                }
            }
            else if (on && visualizer != null)
            {
                Add(visualizer, toRoot, parts, added);
            }

            foreach (GameObject linkedObject in buildState.LinkedGameObjects)
            {
                if (linkedObject != null)
                {
                    linked.Add(linkedObject.transform);
                }
            }
        }

        if (shown < states.Count && states[shown] != null)
        {
            foreach (GameObject linkedObject in states[shown].LinkedGameObjects)
            {
                if (linkedObject == null)
                {
                    continue;
                }

                foreach (Renderer renderer in linkedObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer.enabled && Drawable(renderer))
                    {
                        Add(renderer, toRoot, parts, added);
                    }
                }
            }
        }

        HashSet<Renderer> farLods = FarLods(prefab);
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            if (stateRenderers.Contains(renderer) || added.Contains(renderer) || farLods.Contains(renderer) ||
                !renderer.enabled || !Drawable(renderer) || !ActiveUpTo(renderer.transform, root) ||
                Under(renderer.transform, linked, root))
            {
                continue;
            }

            Add(renderer, toRoot, parts, added);
        }

        return parts;
    }

    private static bool Batched(List<BuildState> states)
    {
        foreach (BuildState buildState in states)
        {
            if (buildState != null && buildState.InitialDrawData.IsValid())
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(Renderer renderer, Matrix4x4 toRoot, List<GhostPart> parts, HashSet<Renderer> added)
    {
        Mesh? mesh = MeshOf(renderer);
        if (mesh == null || !added.Add(renderer))
        {
            return;
        }

        parts.Add(new GhostPart(mesh, toRoot * renderer.transform.localToWorldMatrix));
    }

    private static Mesh? MeshOf(Renderer renderer)
    {
        switch (renderer)
        {
            case SkinnedMeshRenderer skinned:
                return skinned.sharedMesh;
            case MeshRenderer _:
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            default:
                return null;
        }
    }

    // A mesh the game draws as the structure's body: not text (a label or a screen's numbers).
    private static bool Drawable(Renderer renderer) =>
        (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) && renderer.GetComponent<TextMesh>() == null;

    // Every game object from the renderer's up to the prefab's root is switched on.
    private static bool ActiveUpTo(Transform at, Transform root)
    {
        for (Transform? step = at; step != null; step = step.parent)
        {
            if (!step.gameObject.activeSelf)
            {
                return false;
            }

            if (step == root)
            {
                return true;
            }
        }

        return true;
    }

    // Whether the transform is one of the linked objects or below one (below the root).
    private static bool Under(Transform at, HashSet<Transform> linked, Transform root)
    {
        for (Transform? step = at; step != null && step != root; step = step.parent)
        {
            if (linked.Contains(step))
            {
                return true;
            }
        }

        return false;
    }

    // The renderers of every LOD level past the first: the first is the one seen close up.
    private static HashSet<Renderer> FarLods(Structure prefab)
    {
        HashSet<Renderer> far = new HashSet<Renderer>();
        foreach (LODGroup group in prefab.GetComponentsInChildren<LODGroup>(true))
        {
            LOD[] lods = group.GetLODs();
            for (int level = 1; level < lods.Length; level++)
            {
                foreach (Renderer renderer in lods[level].renderers)
                {
                    if (renderer != null)
                    {
                        far.Add(renderer);
                    }
                }
            }
        }

        return far;
    }
}
