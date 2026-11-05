#if BEAT_SABER_1453
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vivify.PostProcessing;

internal sealed class LegacyExtraPassRenderPass : ScriptableRenderPass
{
    private readonly List<LegacyExtraPasses> _snapshot = [];

    internal LegacyExtraPassRenderPass()
    {
        renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (LegacyExtraPasses.Active.Count == 0)
        {
            return;
        }

        UniversalResourceData resources = frameData.Get<UniversalResourceData>();
        using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Vivify legacy extra passes", out PassData data);
        data.Pass = this;
        builder.SetRenderAttachment(resources.activeColorTexture, 0);
        builder.SetRenderAttachmentDepth(resources.activeDepthTexture);
        builder.AllowPassCulling(false);
        builder.SetRenderFunc(static (PassData passData, RasterGraphContext context) => passData.Pass!.Execute(context.cmd));
    }

    private void Execute(RasterCommandBuffer cmd)
    {
        _snapshot.Clear();
        _snapshot.AddRange(LegacyExtraPasses.Active);
        foreach (LegacyExtraPasses extra in _snapshot)
        {
            Renderer? renderer = extra.Renderer;
            if (extra == null || renderer == null || !renderer.enabled || !renderer.isVisible)
            {
                continue;
            }

            foreach (LegacyExtraPasses.Draw draw in extra.Draws)
            {
                cmd.DrawRenderer(renderer, draw.Material, draw.Submesh, draw.Pass);
            }
        }
    }

    private sealed class PassData
    {
        internal LegacyExtraPassRenderPass? Pass { get; set; }
    }
}
#endif
