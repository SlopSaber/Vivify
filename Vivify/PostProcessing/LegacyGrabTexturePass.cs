#if BEAT_SABER_1453
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vivify.PostProcessing;

// URP cannot execute GrabPass. Shaders written for it sample _GrabTexture, which the
// camera's opaque colour copy approximates (it lacks earlier transparent objects).
internal sealed class LegacyGrabTexturePass : ScriptableRenderPass
{
    private static readonly int _grabTexture = Shader.PropertyToID("_GrabTexture");

    internal LegacyGrabTexturePass()
    {
        renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        ConfigureInput(ScriptableRenderPassInput.Color);
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalResourceData resources = frameData.Get<UniversalResourceData>();
        TextureHandle opaque = resources.cameraOpaqueTexture;
        if (!opaque.IsValid())
        {
            return;
        }

        using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Vivify legacy grab texture", out PassData _);
        builder.UseTexture(opaque);
        builder.SetGlobalTextureAfterPass(opaque, _grabTexture);
        builder.AllowPassCulling(false);
        builder.SetRenderFunc((PassData _, UnsafeGraphContext _) => { });
    }

    private sealed class PassData
    {
    }
}
#endif
