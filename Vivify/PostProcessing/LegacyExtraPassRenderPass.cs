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
    private static readonly List<ShaderTagId> _shaderTags = [new("SRPDefaultUnlit"), new("UniversalForward"), new("UniversalForwardOnly"), new("Always"), new("ForwardBase")];

    private readonly List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)> _plainDraws = [];
    private readonly List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)> _grabDraws = [];
    private readonly List<int> _grabTextures = [];
    private readonly List<int> _grabTexelSizes = [];
    private readonly List<(uint Layer, LegacyExtraPasses.Draw Draw)> _plainParticles = [];
    private readonly List<(uint Layer, LegacyExtraPasses.Draw Draw)> _grabParticles = [];
    private readonly HashSet<(uint Layer, int Pass)> _particleKeys = [];
    private readonly Plane[] _frustum = new Plane[6];

    internal LegacyExtraPassRenderPass()
    {
        renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        if (LegacyExtraPasses.Active.Count == 0 || !CollectDraws(cameraData.camera))
        {
            return;
        }

        UniversalResourceData resources = frameData.Get<UniversalResourceData>();
        TextureHandle color = resources.activeColorTexture;
        TextureHandle depth = resources.activeDepthTexture;
        if (_plainDraws.Count > 0 || _plainParticles.Count > 0)
        {
            AddDrawPass(renderGraph, frameData, "Vivify legacy extra passes", color, depth, _plainDraws, _plainParticles, TextureHandle.nullHandle);
        }

        if (_grabDraws.Count == 0 && _grabParticles.Count == 0)
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

        AddDrawPass(renderGraph, frameData, "Vivify legacy grab extra passes", color, depth, _grabDraws, _grabParticles, grab, new Vector4(1f / desc.width, 1f / desc.height, desc.width, desc.height));
    }

    private bool CollectDraws(Camera camera)
    {
        _plainDraws.Clear();
        _grabDraws.Clear();
        _grabTextures.Clear();
        _grabTexelSizes.Clear();
        _plainParticles.Clear();
        _grabParticles.Clear();
        _particleKeys.Clear();
        GeometryUtility.CalculateFrustumPlanes(camera.cullingMatrix, _frustum);
        foreach (LegacyExtraPasses extra in LegacyExtraPasses.Active)
        {
            Renderer? renderer = extra == null ? null : extra.Renderer;
            if (renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.isVisible ||
                (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0 ||
                (!camera.stereoEnabled && !GeometryUtility.TestPlanesAABB(_frustum, renderer.bounds)))
            {
                continue;
            }

            foreach (LegacyExtraPasses.Draw draw in extra!.Draws)
            {
                uint particleLayer = extra.ParticleLayer;
                if (particleLayer != 0 && !_particleKeys.Add((particleLayer, draw.Pass)))
                {
                    continue;
                }

                if (draw.GrabTextures == null)
                {
                    if (particleLayer != 0)
                    {
                        _plainParticles.Add((particleLayer, draw));
                    }
                    else
                    {
                        _plainDraws.Add((renderer, draw));
                    }

                    continue;
                }

                if (particleLayer != 0)
                {
                    _grabParticles.Add((particleLayer, draw));
                }
                else
                {
                    _grabDraws.Add((renderer, draw));
                }

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

        return _plainDraws.Count > 0 || _grabDraws.Count > 0 || _plainParticles.Count > 0 || _grabParticles.Count > 0;
    }

    private void AddDrawPass(
        RenderGraph renderGraph,
        ContextContainer frameData,
        string name,
        TextureHandle color,
        TextureHandle depth,
        List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)> draws,
        List<(uint Layer, LegacyExtraPasses.Draw Draw)> particles,
        TextureHandle grab,
        Vector4 texelSize = default)
    {
        using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(name, out DrawData data);
        data.Draws = draws;
        data.Particles.Clear();
        foreach ((uint layer, LegacyExtraPasses.Draw draw) in particles)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            DrawingSettings settings = RenderingUtils.CreateDrawingSettings(
                _shaderTags,
                renderingData,
                cameraData,
                frameData.Get<UniversalLightData>(),
                draw.Material.renderQueue > 2500 ? SortingCriteria.CommonTransparent : cameraData.defaultOpaqueSortFlags);

            // Selecting the same shader's explicit pass keeps each source material's properties and
            // native particle instance buffers. DrawRenderer cannot submit those buffers.
            settings.overrideShader = draw.Material.shader;
            settings.overrideShaderPassIndex = draw.Pass;
            settings.enableInstancing = true;
            RendererListParams parameters = new(renderingData.cullResults, settings,
                new FilteringSettings(RenderQueueRange.all, cameraData.camera.cullingMask, layer));
            RendererListHandle list = renderGraph.CreateRendererList(parameters);
            data.Particles.Add(list);
            builder.UseRendererList(list);
        }

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

            foreach (RendererListHandle particles in drawData.Particles)
            {
                cmd.DrawRendererList(particles);
            }
        });
    }

    private sealed class CopyData
    {
        internal TextureHandle Source { get; set; }
    }

    private sealed class DrawData
    {
        internal List<RendererListHandle> Particles { get; } = [];

        internal List<(Renderer Renderer, LegacyExtraPasses.Draw Draw)>? Draws { get; set; }

        internal List<int>? TexelSizes { get; set; }

        internal Vector4 TexelSize { get; set; }
    }
}
#endif
