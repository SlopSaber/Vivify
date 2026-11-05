#if BEAT_SABER_1453
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vivify.PostProcessing;

// Draws are recorded after the skybox so that passes following a GrabPass can sample a copy of
// everything rendered so far, which is what the built-in pipeline captured for opaque materials.
internal sealed class LegacyExtraPassRenderPass : ScriptableRenderPass
{
    private static readonly Dictionary<string, (int Texture, int TexelSize)> _propertyIds = new();

    private readonly List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)> _plainDraws = [];
    private readonly List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)> _grabDraws = [];
    private readonly List<int> _grabTextures = [];
    private readonly List<int> _grabTexelSizes = [];

    internal LegacyExtraPassRenderPass()
    {
        renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        if (LegacyExtraPasses.Active.Count == 0 || !CollectDraws())
        {
            return;
        }

        UniversalResourceData resources = frameData.Get<UniversalResourceData>();
        TextureHandle color = resources.activeColorTexture;
        TextureHandle depth = resources.activeDepthTexture;
        if (_plainDraws.Count > 0)
        {
            AddDrawPass(renderGraph, "Vivify legacy extra passes", color, depth, _plainDraws, TextureHandle.nullHandle);
        }

        if (_grabDraws.Count == 0)
        {
            return;
        }

        TextureDesc desc = renderGraph.GetTextureDesc(color);
        desc.name = "_VivifyGrabTexture";
        desc.msaaSamples = MSAASamples.None;
        desc.bindTextureMS = false;
        desc.depthBufferBits = DepthBits.None;
        desc.clearBuffer = false;
        TextureHandle grab = renderGraph.CreateTexture(desc);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Vivify legacy grab pass", out CopyData data))
        {
            data.Source = color;
            builder.UseTexture(color);
            builder.SetRenderAttachment(grab, 0);
            foreach (int texture in _grabTextures)
            {
                builder.SetGlobalTextureAfterPass(grab, texture);
            }

            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (CopyData copyData, RasterGraphContext context) =>
                Blitter.BlitTexture(context.cmd, copyData.Source, new Vector4(1, 1, 0, 0), 0, false));
        }

        AddDrawPass(renderGraph, "Vivify legacy grab extra passes", color, depth, _grabDraws, grab, new Vector4(1f / desc.width, 1f / desc.height, desc.width, desc.height));
    }

    private bool CollectDraws()
    {
        _plainDraws.Clear();
        _grabDraws.Clear();
        _grabTextures.Clear();
        _grabTexelSizes.Clear();
        foreach (LegacyExtraPasses extra in LegacyExtraPasses.Active)
        {
            Renderer? renderer = extra == null ? null : extra.Renderer;
            if (renderer == null || !renderer.enabled || !renderer.isVisible)
            {
                continue;
            }

            foreach (LegacyExtraPasses.Draw draw in extra!.Draws)
            {
                if (draw.GrabTextures == null)
                {
                    _plainDraws.Add((renderer, draw));
                    continue;
                }

                _grabDraws.Add((renderer, draw));
                foreach (string name in draw.GrabTextures)
                {
                    if (!_propertyIds.TryGetValue(name, out (int Texture, int TexelSize) ids))
                    {
                        ids = (Shader.PropertyToID(name), Shader.PropertyToID(name + "_TexelSize"));
                        _propertyIds[name] = ids;
                    }

                    if (!_grabTextures.Contains(ids.Texture))
                    {
                        _grabTextures.Add(ids.Texture);
                        _grabTexelSizes.Add(ids.TexelSize);
                    }
                }
            }
        }

        return _plainDraws.Count > 0 || _grabDraws.Count > 0;
    }

    private void AddDrawPass(
        RenderGraph renderGraph,
        string name,
        TextureHandle color,
        TextureHandle depth,
        List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)> draws,
        TextureHandle grab,
        Vector4 texelSize = default)
    {
        using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(name, out DrawData data);
        data.Draws = draws;
        data.TexelSizes = grab.IsValid() ? _grabTexelSizes : null;
        data.TexelSize = texelSize;
        builder.SetRenderAttachment(color, 0);
        builder.SetRenderAttachmentDepth(depth);
        if (grab.IsValid())
        {
            builder.UseTexture(grab);
            builder.AllowGlobalStateModification(true);
        }

        builder.AllowPassCulling(false);
        builder.SetRenderFunc(static (DrawData drawData, RasterGraphContext context) =>
        {
            RasterCommandBuffer cmd = context.cmd;
            if (drawData.TexelSizes != null)
            {
                foreach (int texelSize in drawData.TexelSizes)
                {
                    cmd.SetGlobalVector(texelSize, drawData.TexelSize);
                }
            }

            foreach ((Renderer renderer, LegacyExtraPasses.Draw draw) in drawData.Draws!)
            {
                if (renderer != null)
                {
                    cmd.DrawRenderer(renderer, draw.Material, draw.Submesh, draw.Pass);
                }
            }
        });
    }

    private sealed class CopyData
    {
        internal TextureHandle Source { get; set; }
    }

    private sealed class DrawData
    {
        internal List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)>? Draws { get; set; }

        internal List<int>? TexelSizes { get; set; }

        internal Vector4 TexelSize { get; set; }
    }
}
#endif
