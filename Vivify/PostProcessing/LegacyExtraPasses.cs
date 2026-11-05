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
    private static readonly Dictionary<Shader, PassPlan[]> _extraPasses = new();
    private static readonly List<Material> _materials = [];

    private readonly List<Draw> _draws = [];
    private Renderer? _renderer;

    internal static HashSet<LegacyExtraPasses> Active { get; } = [];

    internal Renderer? Renderer => _renderer;

    internal IReadOnlyList<Draw> Draws => _draws;

    internal static bool HasExtraPasses(Shader? shader) => shader != null && GetExtraPasses(shader).Length > 0;

    internal static void ClearCache() => _extraPasses.Clear();

    // URP already draws the first untagged pass. GrabPass passes are never drawn; the passes after one
    // sample the texture it would have captured.
    private static PassPlan[] GetExtraPasses(Shader shader)
    {
        if (_extraPasses.TryGetValue(shader, out PassPlan[] passes))
        {
            return passes;
        }

        List<PassPlan> plans = [];
        List<string> grabs = [];
        bool first = true;
        int count = shader.subshaderCount > 0 ? shader.GetPassCountInSubshader(0) : 0;
        for (int pass = 0; pass < count; pass++)
        {
            string mode = shader.FindPassTagValue(0, pass, _lightMode).name;
            if (!string.IsNullOrEmpty(mode) && !string.Equals(mode, "Always", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (LegacyGrabPasses.TryGetTextureName(shader, pass, out string grabTexture))
            {
                if (!grabs.Contains(grabTexture))
                {
                    grabs.Add(grabTexture);
                }
            }
            else if (!first)
            {
                plans.Add(new PassPlan(pass, grabs.Count > 0 ? grabs.ToArray() : null));
            }

            first = false;
        }

        passes = plans.ToArray();
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

            foreach (PassPlan plan in GetExtraPasses(material.shader))
            {
                _draws.Add(new Draw(material, Math.Min(i, Math.Max(submeshes - 1, 0)), plan.Pass, plan.GrabTextures));
            }
        }

        _materials.Clear();
        if (_draws.Count > 0)
        {
            // DrawRenderer only receives the particle system's combined mesh, which instanced mesh
            // particles never build.
            if (_renderer is ParticleSystemRenderer particles)
            {
                particles.enableGPUInstancing = false;
            }

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
        internal readonly string[]? GrabTextures;

        internal Draw(Material material, int submesh, int pass, string[]? grabTextures)
        {
            Material = material;
            Submesh = submesh;
            Pass = pass;
            GrabTextures = grabTextures;
        }
    }

    private readonly struct PassPlan
    {
        internal readonly int Pass;
        internal readonly string[]? GrabTextures;

        internal PassPlan(int pass, string[]? grabTextures)
        {
            Pass = pass;
            GrabTextures = grabTextures;
        }
    }
}
#endif
