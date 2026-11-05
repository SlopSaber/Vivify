#if BEAT_SABER_1453
using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using SiraUtil.Affinity;
using SiraUtil.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using Vivify.Managers;
using Vivify.PostProcessing;

namespace Vivify.HarmonyPatches;

internal sealed class LegacyShaderRendering : IAffinity, IDisposable
{
    private static readonly ShaderTagId _forwardBase = new("ForwardBase");
    private static readonly ShaderTagId _universalForward = new("UniversalForward");
    private static readonly ShaderTagId _universalForwardOnly = new("UniversalForwardOnly");
    private static readonly ShaderTagId _lightMode = new("LightMode");

    private readonly LegacyLightingPass? _lightingPass;
    private readonly LegacyGrabTexturePass? _grabPass;
    private readonly LegacyExtraPassRenderPass? _extraPass;
    private readonly SiraLog _log;

    private bool _reportedFallback;
    private volatile bool _grabPassRequested;
    private bool _reportedGrabPass;

    [UsedImplicitly]
    private LegacyShaderRendering(AssetBundleManager assetBundleManager, SiraLog log)
    {
        _log = log;
        if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset)
        {
            return;
        }

        // A GrabPass is only reported by Unity when it is drawn, from a render job thread.
        _grabPass = new LegacyGrabTexturePass();
        Application.logMessageReceivedThreaded += OnLogMessage;

        HashSet<Shader> shaders = [];
        List<Material> materials = [];
        foreach (UnityEngine.Object asset in assetBundleManager.Assets)
        {
            if (asset is Material material)
            {
                shaders.Add(material.shader);
            }
            else if (asset is GameObject prefab)
            {
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.GetSharedMaterials(materials);
                    bool extraPasses = false;
                    foreach (Material sharedMaterial in materials)
                    {
                        if (sharedMaterial != null)
                        {
                            shaders.Add(sharedMaterial.shader);
                            extraPasses |= LegacyExtraPasses.HasExtraPasses(sharedMaterial.shader);
                        }
                    }

                    if (extraPasses && !renderer.TryGetComponent(out LegacyExtraPasses _))
                    {
                        renderer.gameObject.AddComponent<LegacyExtraPasses>();
                        _extraPass ??= new LegacyExtraPassRenderPass();
                    }
                }
            }
        }

        if (_extraPass != null)
        {
            log.Info("Enabled additional untagged shader passes for bundled renderers.");
        }

        foreach (Shader shader in shaders)
        {
            if (shader == null || !HasForwardBase(shader))
            {
                continue;
            }

            _lightingPass = new LegacyLightingPass();
            log.Info("Enabled legacy ForwardBase rendering for bundled shaders.");
            return;
        }
    }

    public void Dispose()
    {
        Application.logMessageReceivedThreaded -= OnLogMessage;
        _lightingPass?.Dispose();
    }

    private static bool HasForwardBase(Shader shader)
    {
        for (int subshader = 0; subshader < shader.subshaderCount; subshader++)
        {
            int count = shader.GetPassCountInSubshader(subshader);
            for (int pass = 0; pass < count; pass++)
            {
                string mode = shader.FindPassTagValue(subshader, pass, _lightMode).name;
                if (string.Equals(mode, "ForwardBase", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void OnLogMessage(string condition, string stackTrace, LogType type)
    {
        if (!_grabPassRequested && type == LogType.Error && condition.StartsWith("ShaderLab::GrabPasses", StringComparison.Ordinal))
        {
            _grabPassRequested = true;
        }
    }

    [AffinityPrefix]
    [AffinityPatch(typeof(ScriptableRenderer), "AddRenderPasses")]
    private void AddLightingPass(ScriptableRenderer __instance)
    {
        if (_lightingPass != null)
        {
            __instance.EnqueuePass(_lightingPass);
        }

        if (_extraPass != null)
        {
            __instance.EnqueuePass(_extraPass);
        }

        if (_grabPass != null && _grabPassRequested)
        {
            __instance.EnqueuePass(_grabPass);
            if (!_reportedGrabPass)
            {
                _reportedGrabPass = true;
                _log.Info("Bundled shaders use GrabPass; providing the camera opaque texture as _GrabTexture.");
            }
        }
    }

    [AffinityPostfix]
    [AffinityPatch(
        typeof(RenderingUtils),
        nameof(RenderingUtils.CreateDrawingSettings),
        AffinityMethodType.Normal,
        new[] { typeof(List<ShaderTagId>), typeof(UniversalRenderingData), typeof(UniversalCameraData), typeof(UniversalLightData), typeof(SortingCriteria) })]
    private void AddLegacyFallback(List<ShaderTagId> shaderTagIdList, UniversalCameraData cameraData, ref DrawingSettings __result)
    {
        if (_lightingPass == null || shaderTagIdList == null || shaderTagIdList.Count == 0 ||
            shaderTagIdList.Count >= DrawingSettings.maxShaderPasses ||
            shaderTagIdList.Contains(_forwardBase) ||
            (!shaderTagIdList.Contains(_universalForward) && !shaderTagIdList.Contains(_universalForwardOnly)))
        {
            return;
        }

        // Appending a fallback preserves native pass priority, sorting and per-object data.
        __result.SetShaderPassName(shaderTagIdList.Count, _forwardBase);
        if (!_reportedFallback)
        {
            _reportedFallback = true;
            _log.Debug($"Added legacy ForwardBase fallback for [{cameraData.camera.name}].");
        }
    }

    [AffinityPostfix]
    [AffinityPatch(typeof(DrawObjectsPass), "CanDisableZWrite")]
    private void KeepLegacyDepthWrites(ref bool __result)
    {
        if (_lightingPass != null)
        {
            // Legacy materials can lack the depth prepass required by depth priming's Equal test.
            __result = false;
        }
    }
}
#endif
