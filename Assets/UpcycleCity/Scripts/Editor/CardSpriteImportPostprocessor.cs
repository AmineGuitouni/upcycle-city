using System;
using UnityEditor;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Automatically imports card textures under <c>Assets/UpcycleCity/Art/Cards/</c> as UI Sprites
    /// with mobile-friendly compression (1024 max size, no mipmaps, single sprite).
    /// Dropping new Refining or Building card PNGs into that folder requires zero manual import tweaks.
    /// </summary>
    sealed class CardSpriteImportPostprocessor : AssetPostprocessor
    {
        const string CardsArtRoot = "Assets/UpcycleCity/Art/Cards/";

        public override uint GetVersion() => 1;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(CardsArtRoot, StringComparison.OrdinalIgnoreCase))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.maxTextureSize = 1024;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.crunchedCompression = false;
            importer.filterMode = UnityEngine.FilterMode.Bilinear;
            importer.wrapMode = UnityEngine.TextureWrapMode.Clamp;
        }
    }
}
