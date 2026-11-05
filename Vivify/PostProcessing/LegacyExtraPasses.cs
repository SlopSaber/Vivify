#if BEAT_SABER_1453
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vivify.PostProcessing;

// URP maps every untagged pass to SRPDefaultUnlit and draws only the first one, while the built-in
// pipeline drew all of them in order. Renderers whose shaders rely on further untagged passes (such as
// outlines) register here so those passes can be drawn explicitly.
internal sealed class LegacyExtraPasses : MonoBehaviour
{
    private static readonly ShaderTagId _lightMode = new("LightMode");
    private static readonly Dictionary<Shader, int[]> _extraPasses = new();
    private static readonly List<Material> _materials = [];

    private readonly List<Draw> _draws = [];
    private Renderer? _renderer;

    internal static HashSet<LegacyExtraPasses> Active { get; } = [];

    internal Renderer? Renderer => _renderer;

    internal IReadOnlyList<Draw> Draws => _draws;

    internal static bool HasExtraPasses(Shader? shader) => shader != null && GetExtraPasses(shader).Length > 0;

    private static int[] GetExtraPasses(Shader shader)
    {
        if (_extraPasses.TryGetValue(shader, out int[] passes))
        {
            return passes;
        }

        List<int> untagged = [];
        int count = shader.subshaderCount > 0 ? shader.GetPassCountInSubshader(0) : 0;
        for (int pass = 0; pass < count; pass++)
        {
            string mode = shader.FindPassTagValue(0, pass, _lightMode).name;
            if (string.IsNullOrEmpty(mode) || string.Equals(mode, "Always", StringComparison.OrdinalIgnoreCase))
            {
                untagged.Add(pass);
            }
        }

        passes = untagged.Count > 1 ? untagged.GetRange(1, untagged.Count - 1).ToArray() : [];
        _extraPasses[shader] = passes;
        return passes;
    }

    private void OnEnable()
    {
        _renderer = GetComponent<Renderer>();
        _draws.Clear();
        if (_renderer == null)
        {
            return;
        }

        int submeshes = _renderer switch
        {
            MeshRenderer => GetComponent<MeshFilter>()?.sharedMesh?.subMeshCount ?? 1,
            SkinnedMeshRenderer skinned => skinned.sharedMesh?.subMeshCount ?? 1,
            _ => 1,
        };
        _renderer.GetSharedMaterials(_materials);
        for (int i = 0; i < _materials.Count; i++)
        {
            Material material = _materials[i];
            if (material == null)
            {
                continue;
            }

            foreach (int pass in GetExtraPasses(material.shader))
            {
                _draws.Add(new Draw(material, Math.Min(i, Math.Max(submeshes - 1, 0)), pass));
            }
        }

        _materials.Clear();
        if (_draws.Count > 0)
        {
            Active.Add(this);
        }
    }

    private void OnDisable()
    {
        Active.Remove(this);
    }

    internal readonly struct Draw
    {
        internal readonly Material Material;
        internal readonly int Submesh;
        internal readonly int Pass;

        internal Draw(Material material, int submesh, int pass)
        {
            Material = material;
            Submesh = submesh;
            Pass = pass;
        }
    }
}
#endif
