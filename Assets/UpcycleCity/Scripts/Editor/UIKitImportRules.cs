using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Automatically imports UI Art Kit textures under <c>Assets/UpcycleCity/Art/UI/</c> as crisp 2D/UI Sprites
    /// (Single, alpha is transparency, no mipmaps, Clamp, Bilinear, sRGB, max size 2048 for logos/loading screen
    /// and 1024 for chrome, uncompressed on desktop + high-quality ASTC 4x4 on mobile, plus exact 9-slice
    /// <see cref="TextureImporter.spriteBorder"/> borders keyed by file name).
    /// </summary>
    sealed class UIKitImportRules : AssetPostprocessor
    {
        const string UiArtRoot = "Assets/UpcycleCity/Art/UI/";
        const string LogoSubfolder = "Assets/UpcycleCity/Art/UI/Logo/";
        const int MaxLargeSize = 2048;
        const int MaxChromeSize = 1024;

        /// <summary>
        /// 9-slice sprite borders keyed by asset file name (without extension).
        /// Unity's <see cref="TextureImporter.spriteBorder"/> uses <c>Vector4(left, bottom, right, top)</c> in texture pixels.
        /// Note: USS uses <c>-unity-slice-left / -unity-slice-top / -unity-slice-right / -unity-slice-bottom</c>.
        /// </summary>
        static readonly Dictionary<string, Vector4> SliceBorders =
            new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            {
                // Logo
                { "logo_badge_stage", new Vector4(44, 32, 44, 28) },

                // Panels
                { "panel_card", new Vector4(48, 48, 48, 48) },
                { "panel_card_header", new Vector4(32, 8, 32, 32) },
                { "panel_tooltip", new Vector4(36, 36, 36, 36) },
                { "panel_toast", new Vector4(44, 46, 44, 40) },
                { "panel_tab_bar", new Vector4(32, 8, 32, 32) },
                { "tab_selected", new Vector4(12, 12, 12, 12) },
                { "divider", new Vector4(36, 0, 36, 0) },

                // Buttons
                { "btn_primary_orange", new Vector4(36, 40, 36, 36) },
                { "btn_primary_orange_pressed", new Vector4(36, 40, 36, 36) },
                { "btn_primary_orange_disabled", new Vector4(36, 40, 36, 36) },
                { "btn_secondary_dark", new Vector4(36, 40, 36, 36) },
                { "btn_secondary_dark_pressed", new Vector4(36, 40, 36, 36) },
                { "btn_secondary_dark_disabled", new Vector4(36, 40, 36, 36) },
                { "btn_success_green", new Vector4(36, 40, 36, 36) },
                { "btn_success_green_pressed", new Vector4(36, 40, 36, 36) },
                { "btn_success_green_disabled", new Vector4(36, 40, 36, 36) },
                { "btn_danger_red", new Vector4(36, 40, 36, 36) },
                { "btn_danger_red_pressed", new Vector4(36, 40, 36, 36) },
                { "btn_danger_red_disabled", new Vector4(36, 40, 36, 36) },
                { "btn_pill_chip", new Vector4(36, 28, 36, 24) },
                { "swatch_ring_selected", new Vector4(28, 28, 28, 28) },
                { "slider_track", new Vector4(12, 0, 12, 0) },

                // HUD
                { "hud_chip", new Vector4(40, 40, 40, 34) },
                { "hud_bar_bg", new Vector4(22, 12, 22, 12) },
                { "hud_bar_fill", new Vector4(18, 10, 18, 10) },
                { "hotbar_slot", new Vector4(36, 38, 36, 34) },
                { "hotbar_slot_selected", new Vector4(36, 38, 36, 34) },
                { "hud_prompt_key", new Vector4(26, 30, 26, 24) },
                { "hud_minibanner", new Vector4(38, 40, 38, 34) },
            };

        public override uint GetVersion() => 1;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(UiArtRoot, StringComparison.OrdinalIgnoreCase))
                return;

            string key = Path.GetFileNameWithoutExtension(assetPath);
            bool isLarge = assetPath.StartsWith(LogoSubfolder, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(key, "loading_screen", StringComparison.OrdinalIgnoreCase);
            int maxSize = isLarge ? MaxLargeSize : MaxChromeSize;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.sRGBTexture = true;
            importer.maxTextureSize = maxSize;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;

            importer.spriteBorder = SliceBorders.TryGetValue(key, out Vector4 border)
                ? border
                : Vector4.zero;

            ConfigureMobilePlatform(importer, "Android", maxSize);
            ConfigureMobilePlatform(importer, "iPhone", maxSize);
        }

        static void ConfigureMobilePlatform(TextureImporter importer, string platformName, int maxSize)
        {
            var settings = importer.GetPlatformTextureSettings(platformName);
            settings.name = platformName;
            settings.overridden = true;
            settings.maxTextureSize = maxSize;
            settings.format = TextureImporterFormat.ASTC_4x4;
            settings.textureCompression = TextureImporterCompression.CompressedHQ;
            settings.compressionQuality = 100;
            importer.SetPlatformTextureSettings(settings);
        }
    }
}
