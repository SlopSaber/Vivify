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
    private static readonly Dictionary<(Shader Shader, bool Transparent), uint> _particleLayers = new();

    private static uint _availableParticleLayers;
    private static bool _reportedParticleFallback;

    private readonly List<Draw> _draws = [];
    private Renderer? _renderer;
    private ParticleSystemRenderer? _particles;
    private bool _restoreInstancing;

    internal static HashSet<LegacyExtraPasses> Active { get; } = [];

    internal Renderer? Renderer => _renderer;

    internal IReadOnlyList<Draw> Draws => _draws;

    internal uint ParticleLayer { get; private set; }

    internal static bool HasExtraPasses(Shader? shader) => shader != null && GetExtraPasses(shader).Length > 0;

    internal static void ClearCache()
    {
        _extraPasses.Clear();
        _particleLayers.Clear();
        _reportedParticleFallback = false;
        uint used = 0;
        foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            used |= renderer.renderingLayerMask;
        }

        foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
        {
            // An all-layers light already affects every renderer with a nonzero mask.
            if (light.renderingLayerMask != -1)
            {
                used |= unchecked((uint)light.renderingLayerMask);
            }
        }

        _availableParticleLayers = ~used;
    }

    private static uint GetParticleLayer(Material material)
    {
        (Shader Shader, bool Transparent) key = (material.shader, material.renderQueue > 2500);
        if (_particleLayers.TryGetValue(key, out uint layer))
        {
            return layer;
        }

        if (_availableParticleLayers == 0)
        {
            if (!_reportedParticleFallback)
            {
                _reportedParticleFallback = true;
                Plugin.Log.Warn("No unused rendering layer for instanced legacy particle passes; using combined meshes.");
            }

            return 0;
        }

        layer = 1u << 31;
        while ((_availableParticleLayers & layer) == 0)
        {
            layer >>= 1;
        }

        _availableParticleLayers &= ~layer;
        _particleLayers.Add(key, layer);
        Plugin.Log.Debug($"Using native instanced legacy passes for [{material.shader.name}].");
        return layer;
    }

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

        if (_draws.Count > 0)
        {
            if (_renderer is ParticleSystemRenderer particles && particles.enableGPUInstancing)
            {
                _particles = particles;
                if (particles.renderMode == ParticleSystemRenderMode.Mesh && _materials.Count == 1 &&
                    particles.renderingLayerMask != 0)
                {
                    ParticleLayer = GetParticleLayer(_materials[0]);
                }

                if (ParticleLayer != 0)
                {
                    particles.renderingLayerMask |= ParticleLayer;
                }
                else
                {
                    _restoreInstancing = true;
                    particles.enableGPUInstancing = false;
                }
            }

            Active.Add(this);
        }

        _materials.Clear();
    }

    private void OnDisable()
    {
        Active.Remove(this);
        if (_particles != null)
        {
            _particles.renderingLayerMask &= ~ParticleLayer;
            if (_restoreInstancing)
            {
                _particles.enableGPUInstancing = true;
            }
        }

        ParticleLayer = 0;
        _particles = null;
        _restoreInstancing = false;
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
