using UnityEngine;

namespace Vivify.PostProcessing;

// SetTargetBuffers for some reason causes OnRenderImage to recieve a null source.
// This class allows anyone to apply effects to any render texture
internal class MainEffectRenderer
{
    internal MainEffectRenderer(MainEffectController mainEffectController) { }

    internal void Render(RenderTexture src, RenderTexture dest)
    {
        Graphics.Blit(src, dest);
    }
}
