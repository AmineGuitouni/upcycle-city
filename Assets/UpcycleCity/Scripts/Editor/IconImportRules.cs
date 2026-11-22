using System;
using UnityEditor;
using UnityEngine;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Automatically imports UI icon textures under <c>Assets/UpcycleCity/Art/Icons/</c> as UI Sprites
    /// (Single, alpha is transparency, no mipmaps, Clamp, Bilinear, sRGB, max size 256, mobile ASTC on Android/iOS).
    /// </summary>
    sealed class IconImportRules : AssetPostprocessor
    {
        const string IconsArtRoot = "Assets/UpcycleCity/Art/Icons/";
        const string SourceSubfolder = "Assets/UpcycleCity/Art/Icons/_Source/";
        const int MaxIconSize = 256;

        public override uint GetVersion() => 1;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(IconsArtRoot, StringComparison.OrdinalIgnoreCase))
                return;

            if (assetPath.StartsWith(SourceSubfolder, StringComparison.OrdinalIgnoreCase))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.sRGBTexture = true;
            importer.maxTextureSize = MaxIconSize;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.crunchedCompression = false;

            ConfigureMobilePlatform(importer, "Android");
            ConfigureMobilePlatform(importer, "iPhone");
        }

        static void ConfigureMobilePlatform(TextureImporter importer, string platformName)
        {
            var settings = importer.GetPlatformTextureSettings(platformName);
            settings.name = platformName;
            settings.overridden = true;
            settings.maxTextureSize = MaxIconSize;
            settings.format = TextureImporterFormat.ASTC_6x6;
            settings.textureCompression = TextureImporterCompression.Compressed;
            settings.compressionQuality = 50;
            importer.SetPlatformTextureSettings(settings);
        }
    }
}
