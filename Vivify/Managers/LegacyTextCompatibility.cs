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

    // Older font assets are upgraded on first use, so instances are checked again after
    // forcing that upgrade. Text that still has no usable material is disabled instead of
    // throwing on every rebuild.
    internal static int RepairInstance(GameObject instance, bool awake)
    {
        int disabled = 0;
        foreach (TMP_Text text in instance.GetComponentsInChildren<TMP_Text>(true))
        {
            TMP_FontAsset? font = text.font;
            if (font != null)
            {
                font.ReadFontAssetDefinition();
            }

            EnsureTextMaterial(text);
            bool unusable = awake && text.gameObject.activeInHierarchy
                ? text.font == null || text.font.material == null || text.fontSharedMaterial == null
                : text.font != null && text.font.material == null;
            if (unusable && text.enabled)
            {
                text.enabled = false;
                disabled++;
            }
        }

        return disabled;
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

        Shader? shader = Shader.Find(DistanceFieldShader) ?? FindLoadedTextShader();
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

    private static Shader? FindLoadedTextShader()
    {
        foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font != null && font.material != null && font.material.shader != null && font.material.shader.isSupported)
            {
                return font.material.shader;
            }
        }

        return null;
    }
}
#endif
