using System;
using UnityEditor;
using UnityEngine;

namespace UpcycleCity.Editor.Audio
{
    /// <summary>
    /// Automatically enforces optimal Unity audio import settings (Streaming Vorbis for music/ambience,
    /// DecompressOnLoad mono for 3D SFX) whenever audio clips are added under <c>Assets/UpcycleCity/Audio/</c>.
    /// </summary>
    public sealed class AudioImportRules : AssetPostprocessor
    {
        const string AudioRoot = "Assets/UpcycleCity/Audio/";

        void OnPreprocessAudio()
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith(AudioRoot, StringComparison.OrdinalIgnoreCase))
                return;

            var importer = (AudioImporter)assetImporter;
            if (importer == null)
                return;

            bool isMusic = assetPath.IndexOf("/Music/", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isAmbience = assetPath.IndexOf("/Ambience/", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isFootstepOrMaterial = assetPath.IndexOf("/SFX/Footsteps/", StringComparison.OrdinalIgnoreCase) >= 0
                || assetPath.IndexOf("/SFX/Materials/", StringComparison.OrdinalIgnoreCase) >= 0;

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;

            if (isMusic || isAmbience)
            {
                importer.forceToMono = false;
                importer.loadInBackground = true;
                settings.loadType = AudioClipLoadType.Streaming;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = isMusic ? 0.72f : 0.65f;
                settings.preloadAudioData = false;
            }
            else
            {
                // 3D spatialized SFX benefit from mono (proper 3D panning + 50% RAM savings)
                importer.forceToMono = isFootstepOrMaterial;
                importer.loadInBackground = false;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.82f;
                settings.preloadAudioData = true;
            }

            importer.defaultSampleSettings = settings;
        }
    }
}
