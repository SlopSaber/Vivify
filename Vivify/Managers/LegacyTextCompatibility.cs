#if BEAT_SABER_1453
using System.Collections.Generic;
using JetBrains.Annotations;
using SiraUtil.Logging;
using TMPro;
using UnityEngine;

namespace Vivify.Managers;

// TextMeshPro dereferences both the text's shared material and its font's material while
// building geometry. Bundles made with older TextMeshPro versions can lose either one,
// which throws on every canvas rebuild, so missing materials are recreated from the atlas.
internal sealed class LegacyTextCompatibility
{
    private const string DistanceFieldShader = "TextMeshPro/Distance Field";

    [UsedImplicitly]
    private LegacyTextCompatibility(AssetBundleManager assetBundleManager, SiraLog log)
    {
        int repaired = 0;
        List<TMP_Text> texts = [];
        foreach (Object asset in assetBundleManager.Assets)
        {
            switch (asset)
            {
                case TMP_FontAsset font:
                    if (EnsureFontMaterial(font))
                    {
                        repaired++;
                    }

                    break;

                case GameObject prefab:
                    prefab.GetComponentsInChildren(true, texts);
                    foreach (TMP_Text text in texts)
                    {
                        if (EnsureTextMaterial(text))
                        {
                            repaired++;
                        }
                    }

                    break;
            }
        }

        if (repaired > 0)
        {
            log.Info($"Restored {repaired} missing TextMeshPro material(s) in bundled assets.");
        }
    }

    private static bool EnsureTextMaterial(TMP_Text text)
    {
        TMP_FontAsset? font = text.font;
        if (font == null)
        {
            return false;
        }

        bool repaired = EnsureFontMaterial(font);
        if (text.fontSharedMaterial == null && font.material != null)
        {
            text.fontSharedMaterial = font.material;
            repaired = true;
        }

        return repaired;
    }

    private static bool EnsureFontMaterial(TMP_FontAsset font)
    {
        if (font.material != null)
        {
            return false;
        }

        Shader? shader = Shader.Find(DistanceFieldShader);
        Texture2D? atlas = font.atlasTexture;
        if (shader == null || atlas == null)
        {
            return false;
        }

        Material material = new(shader) { name = $"{font.name} Material" };
        material.SetTexture(ShaderUtilities.ID_MainTex, atlas);
        material.SetFloat(ShaderUtilities.ID_TextureWidth, atlas.width);
        material.SetFloat(ShaderUtilities.ID_TextureHeight, atlas.height);
        material.SetFloat(ShaderUtilities.ID_GradientScale, font.atlasPadding + 1);
        material.SetFloat(ShaderUtilities.ID_WeightNormal, font.normalStyle);
        material.SetFloat(ShaderUtilities.ID_WeightBold, font.boldStyle);
        font.material = material;
        return true;
    }
}
#endif
