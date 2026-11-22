using System;
using UnityEditor;
using UnityEngine;

namespace UpcycleCity.Editor.Audio
{
    /// <summary>
    /// Resolved Unity <see cref="AudioImporter"/> configuration produced by <see cref="AudioImportRules"/>.
    /// </summary>
    public readonly struct AudioImportProfile
    {
        public readonly bool ShouldApply;
        public readonly bool ForceToMono;
        public readonly bool LoadInBackground;
        public readonly bool PreloadAudioData;
        public readonly AudioClipLoadType LoadType;
        public readonly AudioCompressionFormat CompressionFormat;
        public readonly float Quality;

        public AudioImportProfile(
            bool shouldApply,
            bool forceToMono,
            bool loadInBackground,
            bool preloadAudioData,
            AudioClipLoadType loadType,
            AudioCompressionFormat compressionFormat,
            float quality)
        {
            ShouldApply = shouldApply;
            ForceToMono = forceToMono;
            LoadInBackground = loadInBackground;
            PreloadAudioData = preloadAudioData;
            LoadType = loadType;
            CompressionFormat = compressionFormat;
            Quality = quality;
        }

        public static AudioImportProfile Ignored => new AudioImportProfile(
            shouldApply: false,
            forceToMono: false,
            loadInBackground: false,
            preloadAudioData: true,
            loadType: AudioClipLoadType.DecompressOnLoad,
            compressionFormat: AudioCompressionFormat.Vorbis,
            quality: 0.82f);
    }

    /// <summary>
    /// Automatically enforces optimal Unity audio import settings (Streaming stereo Vorbis for music/ambience,
    /// stereo DecompressOnLoad for 2D/UI stingers, and mono DecompressOnLoad for all 3D/positional SFX)
    /// whenever audio clips are imported under <c>Assets/UpcycleCity/Audio/</c>.
    /// </summary>
    public sealed class AudioImportRules : AssetPostprocessor
    {
        public const string AudioRoot = "Assets/UpcycleCity/Audio/";

        readonly struct FolderRule
        {
            public readonly string PathFragment;
            public readonly AudioImportProfile Profile;

            public FolderRule(string pathFragment, AudioImportProfile profile)
            {
                PathFragment = pathFragment;
                Profile = profile;
            }
        }

        static readonly AudioImportProfile MusicProfile = new AudioImportProfile(
            shouldApply: true,
            forceToMono: false,
            loadInBackground: true,
            preloadAudioData: false,
            loadType: AudioClipLoadType.Streaming,
            compressionFormat: AudioCompressionFormat.Vorbis,
            quality: 0.72f);

        static readonly AudioImportProfile AmbienceProfile = new AudioImportProfile(
            shouldApply: true,
            forceToMono: false,
            loadInBackground: true,
            preloadAudioData: false,
            loadType: AudioClipLoadType.Streaming,
            compressionFormat: AudioCompressionFormat.Vorbis,
            quality: 0.65f);

        static readonly AudioImportProfile Stereo2DSfxProfile = new AudioImportProfile(
            shouldApply: true,
            forceToMono: false,
            loadInBackground: false,
            preloadAudioData: true,
            loadType: AudioClipLoadType.DecompressOnLoad,
            compressionFormat: AudioCompressionFormat.Vorbis,
            quality: 0.82f);

        // 3D spatialized SFX benefit from mono (proper 3D panning + 50% RAM savings on mobile)
        static readonly AudioImportProfile PositionalMonoSfxProfile = new AudioImportProfile(
            shouldApply: true,
            forceToMono: true,
            loadInBackground: false,
            preloadAudioData: true,
            loadType: AudioClipLoadType.DecompressOnLoad,
            compressionFormat: AudioCompressionFormat.Vorbis,
            quality: 0.82f);

        // Explicit fallback for clips placed directly under Assets/UpcycleCity/Audio/ or future world SFX folders
        static readonly AudioImportProfile DefaultFallbackProfile = PositionalMonoSfxProfile;

        /// <summary>
        /// Ordered, data-driven folder rules evaluated top-to-bottom (most specific folder first).
        /// Keep music, ambience, and 2D UI/stinger folders stereo; force every 3D/positional SFX folder to mono.
        /// </summary>
        static readonly FolderRule[] FolderRules =
        {
            new FolderRule("/Music/", MusicProfile),
            new FolderRule("/Ambience/", AmbienceProfile),
            new FolderRule("/UI/", Stereo2DSfxProfile),
            new FolderRule("/SFX/UI/", Stereo2DSfxProfile),
            new FolderRule("/SFX/2D/", Stereo2DSfxProfile),
            new FolderRule("/SFX/Stingers/", Stereo2DSfxProfile),
            new FolderRule("/SFX/Economy/", Stereo2DSfxProfile),
            new FolderRule("/SFX/", PositionalMonoSfxProfile),
        };

        /// <summary>Returns true when <paramref name="assetPath"/> lives under <see cref="AudioRoot"/>.</summary>
        public static bool IsManagedAudioPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;
            string normalized = assetPath.Replace('\\', '/');
            return normalized.StartsWith(AudioRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Resolves the import profile for <paramref name="assetPath"/> using <see cref="FolderRules"/>.</summary>
        public static AudioImportProfile ResolveProfile(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return AudioImportProfile.Ignored;

            string normalized = assetPath.Replace('\\', '/');
            if (!normalized.StartsWith(AudioRoot, StringComparison.OrdinalIgnoreCase))
                return AudioImportProfile.Ignored;

            for (int i = 0; i < FolderRules.Length; i++)
            {
                if (normalized.IndexOf(FolderRules[i].PathFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return FolderRules[i].Profile;
            }

            return DefaultFallbackProfile;
        }

        /// <summary>Convenience helper returning whether <paramref name="assetPath"/> should be forced to mono.</summary>
        public static bool ShouldForceMono(string assetPath)
        {
            AudioImportProfile profile = ResolveProfile(assetPath);
            return profile.ShouldApply && profile.ForceToMono;
        }

        void OnPreprocessAudio()
        {
            AudioImportProfile profile = ResolveProfile(assetPath);
            if (!profile.ShouldApply)
                return;

            var importer = (AudioImporter)assetImporter;
            if (importer == null)
                return;

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            settings.loadType = profile.LoadType;
            settings.compressionFormat = profile.CompressionFormat;
            settings.quality = profile.Quality;
            settings.preloadAudioData = profile.PreloadAudioData;

            importer.forceToMono = profile.ForceToMono;
            importer.loadInBackground = profile.LoadInBackground;
            importer.defaultSampleSettings = settings;
        }
    }
}

