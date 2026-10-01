#nullable enable

using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The see-through materials highlight and show_preview xray draw with. Meshes: the T-Ray SPU's own material
/// (SPUMesonScanner._material, which the scanner draws pipes, cables and chutes through walls with each frame by
/// Graphics.DrawMeshInstanced, per-instance _Color; the Clear Lenses mod tints things the same way), copied and, when
/// its shader has a depth-test switch, set to always pass. Without that prefab or field, and for lines: Unity's
/// built-in Hidden/Internal-Colored with depth test Always, no depth write, alpha blended, both faces.
/// </summary>
internal static class XRay
{
    private const int Overlay = 4000;

    private static Material? _mesh;
    private static Material? _lines;
    private static string _meshSource = "none";

    /// <summary>The meshes' material, or null when neither the T-Ray material nor a built-in shader is there.</summary>
    internal static Material? MeshMaterial
    {
        get
        {
            if (_mesh == null)
            {
                _mesh = FromTRay();
                if (_mesh == null)
                {
                    _mesh = BuiltIn("Hidden/Internal-Colored");
                    _meshSource = _mesh != null ? "built-in" : "none";
                }
            }

            return _mesh;
        }
    }

    /// <summary>The lines' material (show_preview xray), or null when the built-in shader is missing.</summary>
    internal static Material? LineMaterial
    {
        get
        {
            if (_lines == null)
            {
                _lines = BuiltIn("Hidden/Internal-Colored");
            }

            return _lines;
        }
    }

    /// <summary>Which material the meshes use: "t-ray (shader name)" or "built-in", for the reply.</summary>
    internal static string MeshSource
    {
        get
        {
            _ = MeshMaterial;
            return _meshSource;
        }
    }

    /// <summary>Whether the mesh material takes a per-instance colour array, as the T-Ray's does.</summary>
    internal static bool Instanced { get; private set; }

    private static Material? FromTRay()
    {
        if (!GameMembers.MesonScannerMaterial.TryResolve())
        {
            return null;
        }

        foreach (Thing prefab in Prefab.AllPrefabs)
        {
            if (prefab is SPUMesonScanner scanner &&
                GameMembers.MesonScannerMaterial.GetValue(scanner) is Material material && material != null)
            {
                Material copy = new Material(material) { renderQueue = Overlay, enableInstancing = true };
                if (copy.HasProperty("_ZTest"))
                {
                    copy.SetInt("_ZTest", (int)CompareFunction.Always);
                }

                Instanced = true;
                _meshSource = $"t-ray ({material.shader.name})";
                return copy;
            }
        }

        return null;
    }

    private static Material? BuiltIn(string name)
    {
        Shader shader = Shader.Find(name);
        if (shader == null)
        {
            return null;
        }

        Material material = new Material(shader) { renderQueue = Overlay };
        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_Cull", (int)CullMode.Off);
        material.SetInt("_ZWrite", 0);
        material.SetInt("_ZTest", (int)CompareFunction.Always);
        return material;
    }
}
