#if BEAT_SABER_1453
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vivify.PostProcessing;

internal sealed class LegacyLightingPass : ScriptableRenderPass, IDisposable
{
    private static readonly int _lightColor = Shader.PropertyToID("_LightColor0");
    private static readonly int _lightPosition = Shader.PropertyToID("_WorldSpaceLightPos0");

    private readonly Vector4 _originalColor = Shader.GetGlobalVector(_lightColor);
    private readonly Vector4 _originalPosition = Shader.GetGlobalVector(_lightPosition);
    private readonly bool _originalDirectional = Shader.IsKeywordEnabled("DIRECTIONAL");

    private bool _disposed;

    internal LegacyLightingPass()
    {
        // Mirrors can render at BeforeRendering, before the ordinary opaque pass.
        renderPassEvent = RenderPassEvent.BeforeRendering;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (_disposed)
        {
            return;
        }

        UniversalLightData lightData = frameData.Get<UniversalLightData>();
        using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PassData>("Vivify legacy lighting", out PassData data);
        data.Color = Vector4.zero;
        data.Position = new Vector4(0, 1, 0, 0);
        int mainLight = lightData.mainLightIndex;
        if (mainLight >= 0 && mainLight < lightData.visibleLights.Length)
        {
            VisibleLight light = lightData.visibleLights[mainLight];
            if (light.lightType == LightType.Directional)
            {
                data.Color = light.finalColor;
                Vector4 forward = light.localToWorldMatrix.GetColumn(2);
                data.Position = new Vector4(-forward.x, -forward.y, -forward.z, 0);
            }
        }

        builder.AllowGlobalStateModification(true);
        builder.AllowPassCulling(false);
        builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
        {
            context.cmd.SetGlobalVector(_lightColor, pass.Color);
            context.cmd.SetGlobalVector(_lightPosition, pass.Position);
            context.cmd.EnableShaderKeyword("DIRECTIONAL");
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shader.SetGlobalVector(_lightColor, _originalColor);
        Shader.SetGlobalVector(_lightPosition, _originalPosition);
        if (_originalDirectional)
        {
            Shader.EnableKeyword("DIRECTIONAL");
        }
        else
        {
            Shader.DisableKeyword("DIRECTIONAL");
        }
    }

    private sealed class PassData
    {
        internal Vector4 Color { get; set; }

        internal Vector4 Position { get; set; }
    }
}
#endif
