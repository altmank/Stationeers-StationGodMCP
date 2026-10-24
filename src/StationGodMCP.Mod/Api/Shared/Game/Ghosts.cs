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
/// Ghosts in this game: show preview's (each for its seconds) and the ghost board's (GhostBoardStore: placed by
/// players or agents, kept until taken off or built). A ghost is a structure's own meshes, from its prefab at a build
/// state (GhostMeshes), drawn tinted where it would stand, every frame by Tick (the mod's Update) with
/// Graphics.DrawMesh, as the highlights are: no game object, nothing in the world. Two passes: the game's
/// construction-cursor shader (CursorManager.CursorShader, the look of a structure in the hand), hidden by what stands
/// in front as a solid thing is; and with xray a fainter pass in XRay's see-through material on top of everything, so
/// a ghost behind walls, frames or terrain shows dimly and the parts in view brighter. The board's ghosts can be hidden
/// (BoardVisible) and one of them picked under the crosshair (Pick) and drawn brighter (Picked). The ghost under the
/// crosshair (Hover) is what the overlay's tooltip names: its label, its structure and who placed it. Clear removes the
/// previews, ClearBoard the board's, ClearAll both (when the mod unloads or the world is left).
/// </summary>
internal static class Ghosts
{
    /// <summary>How much fainter the see-through pass is than the ghost's own alpha.</summary>
    private const float SeeThroughShare = 0.5f;

    /// <summary>How far off the board's ghosts can be picked, in metres.</summary>
    private const float PickReachM = 12f;

    private static readonly int OffsetProperty = Shader.PropertyToID("_Offset");
    private static readonly List<GhostMark> Shown = new List<GhostMark>();
    private static readonly Dictionary<long, GhostMark> Board = new Dictionary<long, GhostMark>();
    private static readonly MeshDraws Solid = new MeshDraws();
    private static readonly MeshDraws SeeThrough = new MeshDraws();
    private static Material? _solid;
    private static string _source = "none";

    /// <summary>The previews shown now.</summary>
    internal static int Count => Shown.Count;

    /// <summary>The board's ghosts this game draws.</summary>
    internal static int BoardCount => Board.Count;

    /// <summary>Whether the board's ghosts are drawn (a player's toggle; previews always are).</summary>
    internal static bool BoardVisible { get; set; } = true;

    /// <summary>
    /// Whether Lattice draws the board's ghosts (its render module runs): this game then keeps them for hover and
    /// picking only, and draws previews alone.
    /// </summary>
    internal static bool BoardDrawnElsewhere { get; set; }

    /// <summary>The board ghost drawn brighter (the one under the crosshair in ghost mode); 0 for none.</summary>
    internal static long Picked { get; set; }

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
    /// Shows each preview ghost; the number drawn: a ghost whose prefab this game does not have, or that has no
    /// meshes, is left out.
    /// </summary>
    internal static int Show(IReadOnlyList<GhostSpec> ghosts)
    {
        float now = Time.realtimeSinceStartup;
        int drawn = 0;
        foreach (GhostSpec ghost in ghosts)
        {
            GhostMark? mark = MarkOf(ghost.PrefabHash, ghost.BuildState, ghost.Position, ghost.Euler, ghost.Tint,
                ghost.XRay, now + ghost.Seconds, 0, ghost.Label, null);
            if (mark != null)
            {
                Shown.Add(mark);
                drawn++;
            }
        }

        return drawn;
    }

    /// <summary>Draws a board ghost (replacing the one of the same id), always with the see-through pass.</summary>
    internal static void ShowBoard(BoardGhost ghost)
    {
        GhostMark? mark = MarkOf(ghost.PrefabHash, ghost.BuildState, ghost.Position, ghost.Euler, ghost.Tint, true,
            float.PositiveInfinity, ghost.Id, ghost.Label, ghost.Owner);
        if (mark != null)
        {
            Board[ghost.Id] = mark;
        }
        else
        {
            Board.Remove(ghost.Id);
        }
    }

    internal static void RemoveBoard(long id)
    {
        Board.Remove(id);
        if (Picked == id)
        {
            Picked = 0;
        }
    }

    /// <summary>The ghost the ray meets first within reach (a preview, or a shown board ghost); null for none.</summary>
    internal static GhostMark? Hover(Ray ray)
    {
        GhostMark? best = null;
        float nearest = PickReachM;
        foreach (GhostMark mark in Shown)
        {
            if (mark.Bounds.IntersectRay(ray, out float distance) && distance < nearest)
            {
                nearest = distance;
                best = mark;
            }
        }

        if (BoardVisible)
        {
            foreach (KeyValuePair<long, GhostMark> entry in Board)
            {
                if (entry.Value.Bounds.IntersectRay(ray, out float distance) && distance < nearest)
                {
                    nearest = distance;
                    best = entry.Value;
                }
            }
        }

        return best;
    }

    /// <summary>The board ghost the ray meets first within reach, by its meshes' boxes; 0 for none.</summary>
    internal static long Pick(Ray ray)
    {
        long best = 0;
        float nearest = PickReachM;
        foreach (KeyValuePair<long, GhostMark> entry in Board)
        {
            if (entry.Value.Bounds.IntersectRay(ray, out float distance) && distance < nearest)
            {
                nearest = distance;
                best = entry.Key;
            }
        }

        return best;
    }

    /// <summary>Drops previews whose time is up and draws every ghost; every frame.</summary>
    internal static void Tick()
    {
        bool drawsBoard = BoardVisible && !BoardDrawnElsewhere;
        if (Shown.Count == 0 && (Board.Count == 0 || !drawsBoard))
        {
            return;
        }

        if (Expiry.RemoveExpired(Shown, Time.realtimeSinceStartup) > 0 && Shown.Count == 0 &&
            (Board.Count == 0 || !drawsBoard))
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
            anySeeThrough |= Draw(mark, seeThrough, picked: false);
        }

        if (drawsBoard)
        {
            foreach (KeyValuePair<long, GhostMark> entry in Board)
            {
                anySeeThrough |= Draw(entry.Value, seeThrough, entry.Key == Picked);
            }
        }

        Solid.Flush(solid, instanced: false);
        if (anySeeThrough)
        {
            SeeThrough.Flush(seeThrough!, XRay.Instanced);
        }
    }

    /// <summary>Removes the previews; how many there were.</summary>
    internal static int Clear()
    {
        int count = Shown.Count;
        Shown.Clear();
        if (Board.Count == 0)
        {
            Idle();
        }

        return count;
    }

    /// <summary>Removes the board's ghosts from this game's drawing (the board itself is GhostBoardStore's).</summary>
    internal static void ClearBoard()
    {
        Board.Clear();
        Picked = 0;
        if (Shown.Count == 0)
        {
            Idle();
        }
    }

    internal static void ClearAll()
    {
        Shown.Clear();
        ClearBoard();
        Idle();
    }

    // One mark's passes; whether it drew the see-through one. A picked ghost is drawn brighter in both.
    private static bool Draw(GhostMark mark, Material? seeThrough, bool picked)
    {
        mark.Draw(Solid, picked ? mark.PickedColor : mark.SolidColor);
        if (!mark.XRay || seeThrough == null)
        {
            return false;
        }

        mark.Draw(SeeThrough, picked ? mark.PickedColor : mark.SeeThroughColor);
        return true;
    }

    private static GhostMark? MarkOf(int prefabHash, int buildState, Vec3 position, Vec3 euler, Rgba tint, bool xray,
        float until, long boardId, string? label, string? owner)
    {
        if (!(Prefab.Find(prefabHash) is Structure prefab) || prefab == null)
        {
            OnceLog.Warning($"ghost_prefab:{prefabHash}",
                $"No structure prefab with hash {prefabHash} in this game: its ghost is not drawn.");
            return null;
        }

        List<GhostPart> parts = GhostMeshes.Of(prefab, buildState);
        if (parts.Count == 0)
        {
            return null;
        }

        Matrix4x4 pose = Matrix4x4.TRS(Bodies.U(position), Quaternion.Euler(Bodies.U(euler)),
            prefab.transform.localScale);
        return new GhostMark(parts, pose, tint, xray, until, boardId)
        {
            Label = label,
            Title = prefab.DisplayName,
            Owner = owner
        };
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

/// <summary>
/// A ghost as drawn: its meshes placed in the world and the box around them, its colours, its time (infinite for the
/// board's) and its board id (0 for a preview).
/// </summary>
internal sealed class GhostMark : IExpiring
{
    private readonly Matrix4x4[] _matrices;
    private readonly Mesh[] _meshes;

    internal GhostMark(List<GhostPart> parts, Matrix4x4 pose, Rgba tint, bool xray, float until, long boardId)
    {
        _meshes = new Mesh[parts.Count];
        _matrices = new Matrix4x4[parts.Count];
        Bounds bounds = new Bounds(pose.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
        for (int index = 0; index < parts.Count; index++)
        {
            _meshes[index] = parts[index].Mesh;
            _matrices[index] = pose * parts[index].Local;
            if (_meshes[index] != null)
            {
                Encapsulate(ref bounds, _meshes[index].bounds, _matrices[index]);
            }
        }

        Bounds = bounds;
        XRay = xray;
        Until = until;
        BoardId = boardId;
        SolidColor = new Color(tint.Red, tint.Green, tint.Blue, tint.Alpha);
        SeeThroughColor = new Color(tint.Red, tint.Green, tint.Blue, Ghosts.SeeThroughAlpha(tint.Alpha));
        PickedColor = new Color(tint.Red, tint.Green, tint.Blue, Mathf.Min(1f, tint.Alpha + 0.4f));
    }

    public float Until { get; }

    internal long BoardId { get; }

    /// <summary>What it is, as the agent or player wrote it; null for none.</summary>
    internal string? Label { get; set; }

    /// <summary>The structure's shown name.</summary>
    internal string Title { get; set; } = string.Empty;

    /// <summary>Who placed a board ghost; null for a preview.</summary>
    internal string? Owner { get; set; }

    internal bool XRay { get; }

    /// <summary>The world box around the ghost's meshes.</summary>
    internal Bounds Bounds { get; }

    internal Color SolidColor { get; }

    internal Color SeeThroughColor { get; }

    internal Color PickedColor { get; }

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

    // The mesh box's eight corners, placed, into the world box.
    private static void Encapsulate(ref Bounds bounds, Bounds local, Matrix4x4 matrix)
    {
        Vector3 min = local.min;
        Vector3 max = local.max;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new Vector3((corner & 1) == 0 ? min.x : max.x, (corner & 2) == 0 ? min.y : max.y,
                (corner & 4) == 0 ? min.z : max.z);
            bounds.Encapsulate(matrix.MultiplyPoint3x4(point));
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
