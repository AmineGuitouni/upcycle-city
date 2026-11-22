using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Audio
{
    /// <summary>
    /// Central registry mapping <see cref="SoundId"/>s and extensible string keys to <see cref="SoundCue"/> assets,
    /// plus music playlist, ambience bed, bus defaults, and voice-pool budgets.
    /// <para>
    /// Place the default library at <c>Assets/UpcycleCity/Resources/UpcycleCitySoundLibrary.asset</c>
    /// so <see cref="SoundManager"/> auto-bootstraps in every scene without manual scene edits.
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "UpcycleCitySoundLibrary", menuName = "Upcycle City/Audio/Sound Library", order = 1)]
    public sealed class SoundLibrary : ScriptableObject
    {
        public const string DefaultResourcePath = "UpcycleCitySoundLibrary";

        [Serializable]
        public struct CueBinding
        {
            public SoundId id;
            public SoundCue cue;
        }

        [Serializable]
        public struct NamedCueBinding
        {
            [Tooltip("Case-insensitive string key for SoundManager.Play(\"key\"). Defaults to cue.name if empty.")]
            public string key;
            public SoundCue cue;
        }

        [Header("Core Sound Bindings")]
        [SerializeField] CueBinding[] bindings = Array.Empty<CueBinding>();

        [Header("Additional Named Cues (Scalable Extension)")]
        [Tooltip("Drop extra SoundCues here to play them via SoundManager.Play(\"key\") without editing SoundId.")]
        [SerializeField] NamedCueBinding[] namedCues = Array.Empty<NamedCueBinding>();

        [Header("Background Music Playlist")]
        [SerializeField] AudioClip[] musicTracks = Array.Empty<AudioClip>();
        [SerializeField] bool shuffleMusicPlaylist = true;
        [SerializeField, Range(0f, 1f)] float musicBaseVolume = 0.24f;
        [SerializeField, Range(0.2f, 8f)] float musicCrossfadeSeconds = 2.4f;
        [SerializeField, Range(0.2f, 1f)] float shopOpenMusicDuck = 0.58f;

        [Header("City Ambience Bed")]
        [SerializeField] AudioClip ambienceLoop;
        [SerializeField, Range(0f, 1f)] float ambienceBaseVolume = 0.22f;
        [Tooltip("Ambience multiplier in heavy smog (clean city blooms smoothly up to 1.0).")]
        [SerializeField, Range(0.2f, 1f)] float ambienceSmogMinMultiplier = 0.55f;
        [Tooltip("Ambience multiplier while a shop UI is open (0 = silent inside shops).")]
        [SerializeField, Range(0f, 1f)] float shopOpenAmbienceDuck = 0f;
        [Tooltip("Scene names where outdoor city ambience must stay silent (e.g. CharacterCreator).")]
        [SerializeField] string[] ambienceSilentScenes = { "CharacterCreator", "MainMenu" };

        [Header("Default Bus Volumes")]
        [SerializeField, Range(0f, 1f)] float masterVolume = 1.0f;
        [SerializeField, Range(0f, 1f)] float sfxVolume = 0.90f;
        [SerializeField, Range(0f, 1f)] float musicVolume = 0.75f;
        [SerializeField, Range(0f, 1f)] float ambienceVolume = 0.80f;
        [SerializeField, Range(0f, 1f)] float uiVolume = 0.85f;

        [Header("Voice Pool Optimization")]
        [SerializeField, Range(8, 64)] int initialPoolSize = 24;
        [SerializeField, Range(16, 96)] int maxPoolSize = 48;

        SoundCue[] idLookup;
        Dictionary<string, SoundCue> keyLookup;
        bool lookupBuilt;

        public AudioClip[] MusicTracks => musicTracks;
        public bool ShuffleMusicPlaylist => shuffleMusicPlaylist;
        public float MusicBaseVolume => musicBaseVolume;
        public float MusicCrossfadeSeconds => musicCrossfadeSeconds;
        public float ShopOpenMusicDuck => shopOpenMusicDuck;

        public AudioClip AmbienceLoop => ambienceLoop;
        public float AmbienceBaseVolume => ambienceBaseVolume;
        public float AmbienceSmogMinMultiplier => ambienceSmogMinMultiplier;
        public float ShopOpenAmbienceDuck => shopOpenAmbienceDuck;
        public IReadOnlyList<string> AmbienceSilentScenes => ambienceSilentScenes ?? Array.Empty<string>();

        public int InitialPoolSize => initialPoolSize;
        public int MaxPoolSize => maxPoolSize;

        /// <summary>
        /// Returns true if outdoor city ambience should play in <paramref name="sceneName"/>.
        /// Menu / Character Creator scenes listed in <see cref="AmbienceSilentScenes"/> return false.
        /// </summary>
        public bool ShouldPlayAmbienceInScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                return false;

            string trimmed = sceneName.Trim();
            string[] silent = ambienceSilentScenes;
            if (silent != null)
            {
                for (int i = 0; i < silent.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(silent[i])
                        && string.Equals(trimmed, silent[i].Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public float GetDefaultCategoryVolume(SoundCategory category)
        {
            switch (category)
            {
                case SoundCategory.Master: return masterVolume;
                case SoundCategory.SFX: return sfxVolume;
                case SoundCategory.Music: return musicVolume;
                case SoundCategory.Ambience: return ambienceVolume;
                case SoundCategory.UI: return uiVolume;
                default: return 1f;
            }
        }

        public void RebuildLookup()
        {
            int maxId = 0;
            if (bindings != null)
            {
                for (int i = 0; i < bindings.Length; i++)
                {
                    int val = (int)bindings[i].id;
                    if (val > maxId)
                        maxId = val;
                }
            }

            idLookup = new SoundCue[maxId + 1];
            keyLookup = new Dictionary<string, SoundCue>(StringComparer.OrdinalIgnoreCase);

            if (bindings != null)
            {
                for (int i = 0; i < bindings.Length; i++)
                {
                    CueBinding b = bindings[i];
                    if (b.cue == null)
                        continue;

                    int idx = (int)b.id;
                    if (idx > 0 && idx < idLookup.Length)
                        idLookup[idx] = b.cue;

                    keyLookup[b.id.ToString()] = b.cue;
                    if (!string.IsNullOrEmpty(b.cue.name))
                        keyLookup[b.cue.name] = b.cue;
                }
            }

            if (namedCues != null)
            {
                for (int i = 0; i < namedCues.Length; i++)
                {
                    NamedCueBinding n = namedCues[i];
                    if (n.cue == null)
                        continue;
                    string k = string.IsNullOrWhiteSpace(n.key) ? n.cue.name : n.key.Trim();
                    if (!string.IsNullOrEmpty(k))
                        keyLookup[k] = n.cue;
                }
            }

            lookupBuilt = true;
        }

        public SoundCue GetCue(SoundId id)
        {
            if (!lookupBuilt)
                RebuildLookup();

            int idx = (int)id;
            if (idLookup == null || idx <= 0 || idx >= idLookup.Length)
                return null;
            return idLookup[idx];
        }

        public SoundCue GetCue(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            if (!lookupBuilt)
                RebuildLookup();
            return keyLookup != null && keyLookup.TryGetValue(key, out SoundCue cue) ? cue : null;
        }

        /// <summary>Editor/builder helper to populate the library asset cleanly.</summary>
        public void Configure(
            CueBinding[] cueBindings,
            AudioClip[] playlist,
            AudioClip ambience,
            NamedCueBinding[] extraNamedCues = null)
        {
            bindings = cueBindings ?? Array.Empty<CueBinding>();
            musicTracks = playlist ?? Array.Empty<AudioClip>();
            ambienceLoop = ambience;
            if (extraNamedCues != null)
                namedCues = extraNamedCues;
            lookupBuilt = false;
        }

        void OnEnable() => lookupBuilt = false;
        void OnValidate() => lookupBuilt = false;
    }
}
