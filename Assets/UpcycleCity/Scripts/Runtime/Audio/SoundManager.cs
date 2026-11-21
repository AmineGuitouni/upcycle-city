using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UpcycleCity.Ecosystem;
using UpcycleCity.Shops;

namespace UpcycleCity.Audio
{
    /// <summary>
    /// Central, persistent audio manager for Upcycle City.
    /// <para>
    /// Automatically bootstraps before gameplay from <c>Resources/UpcycleCitySoundLibrary</c> so every scene
    /// gets pooled 2D/3D SFX, randomized pitch/clip variations, crossfading background music, adaptive city
    /// ambience, and PlayerPrefs-persisted bus volumes with zero per-scene boilerplate.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-60)]
    [AddComponentMenu("Upcycle City/Audio/Sound Manager")]
    public sealed class SoundManager : MonoBehaviour
    {
        const string VolumePrefPrefix = "UpcycleCity.Audio.Vol.";
        const string MutePrefPrefix = "UpcycleCity.Audio.Mute.";

        [Tooltip("Central library of SoundCues, music playlist, ambience, and voice-pool budgets.")]
        [SerializeField] SoundLibrary library;

        [Tooltip("Keep this SoundManager alive across scene transitions.")]
        [SerializeField] bool persistAcrossScenes = true;

        [Tooltip("Automatically start the background music playlist and city ambience on load.")]
        [SerializeField] bool autoPlayBackgroundAudio = true;

        public static SoundManager Instance { get; private set; }
        public static bool HasInstance => Instance != null;

        public SoundLibrary Library => library;
        public int ActiveVoiceCount => voicePool != null ? voicePool.ActiveVoiceCount : 0;
        public int AllocatedVoiceCount => voicePool != null ? voicePool.AllocatedVoiceCount : 0;

        AudioVoicePool voicePool;
        readonly float[] categoryVolumes = { 1f, 0.9f, 0.75f, 0.8f, 0.85f };
        readonly bool[] categoryMuted = new bool[5];
        Func<SoundCategory, float> cachedBusVolumeGetter;

        // Dual-source crossfading music player
        AudioSource musicSourceA;
        AudioSource musicSourceB;
        bool musicUsingA = true;
        int[] musicOrder;
        int musicCursor;
        int currentTrackIndex = -1;
        float musicFadeProgress = 1f;
        float musicFadeDuration = 2f;
        float musicDuckMultiplier = 1f;
        bool musicPaused;

        // Ambient bed source
        AudioSource ambienceSource;
        float ambienceSmogMultiplier = 1f;
        float ambienceFadeIn;

        // Fallback AudioListener used only if a scene has none
        AudioListener fallbackListener;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            if (Instance != null)
                return;

            SoundManager existing = FindAnyObjectByType<SoundManager>();
            if (existing != null)
                return;

            SoundLibrary defaultLib = Resources.Load<SoundLibrary>(SoundLibrary.DefaultResourcePath);
            if (defaultLib == null)
            {
                Debug.LogWarning($"[UpcycleCity.Audio] No SoundLibrary found at Resources/{SoundLibrary.DefaultResourcePath}. Run 'Upcycle City → Audio → Build Sound Library & Cues'.");
                return;
            }

            var root = new GameObject("UpcycleCity_SoundManager");
            var mgr = root.AddComponent<SoundManager>();
            mgr.library = defaultLib;
            mgr.InitializeInternal();

            if (root.GetComponent<GameAudioRouter>() == null)
                root.AddComponent<GameAudioRouter>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (persistAcrossScenes)
                DontDestroyOnLoad(gameObject);

            if (library == null)
                library = Resources.Load<SoundLibrary>(SoundLibrary.DefaultResourcePath);

            InitializeInternal();

            if (GetComponent<GameAudioRouter>() == null)
                gameObject.AddComponent<GameAudioRouter>();
        }

        void InitializeInternal()
        {
            if (voicePool != null)
                return;

            cachedBusVolumeGetter = GetEffectiveBusVolume;
            LoadSavedBusSettings();

            var poolRoot = new GameObject("VoicePool").transform;
            poolRoot.SetParent(transform, false);
            int initSize = library != null ? library.InitialPoolSize : 24;
            int maxSize = library != null ? library.MaxPoolSize : 48;
            voicePool = new AudioVoicePool(poolRoot, initSize, maxSize);

            musicSourceA = CreateBackgroundSource("Music_A", loop: false);
            musicSourceB = CreateBackgroundSource("Music_B", loop: false);
            ambienceSource = CreateBackgroundSource("Ambience_City", loop: true);

            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureAudioListenerExists();

            if (autoPlayBackgroundAudio)
                StartBackgroundAudio();
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Instance == this)
                Instance = null;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureAudioListenerExists();
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float now = Time.unscaledTime;

            voicePool?.Tick(now, cachedBusVolumeGetter);
            TickMusic(dt);
            TickAmbience(dt);
        }

        // ---------------------------------------------------------------- Static Playback API

        /// <summary>Plays a 2D/UI or non-positional sound by <see cref="SoundId"/>.</summary>
        public static SoundHandle Play(SoundId id, float volumeScale = 1f, float pitchScale = 1f)
        {
            return Instance != null ? Instance.PlayInternal(id, null, null, volumeScale, pitchScale) : SoundHandle.Invalid;
        }

        /// <summary>Plays a 3D spatialized sound by <see cref="SoundId"/> at <paramref name="worldPosition"/>.</summary>
        public static SoundHandle PlayAt(SoundId id, Vector3 worldPosition, float volumeScale = 1f, float pitchScale = 1f)
        {
            return Instance != null ? Instance.PlayInternal(id, worldPosition, null, volumeScale, pitchScale) : SoundHandle.Invalid;
        }

        /// <summary>Plays a 3D sound attached to a moving <paramref name="target"/> transform.</summary>
        public static SoundHandle PlayAttached(SoundId id, Transform target, float volumeScale = 1f, float pitchScale = 1f)
        {
            return Instance != null ? Instance.PlayInternal(id, target != null ? target.position : (Vector3?)null, target, volumeScale, pitchScale) : SoundHandle.Invalid;
        }

        /// <summary>Plays any <see cref="SoundCue"/> directly (works for brand-new designer cues not in SoundId).</summary>
        public static SoundHandle Play(SoundCue cue, Vector3? worldPosition = null, float volumeScale = 1f, float pitchScale = 1f)
        {
            return Instance != null ? Instance.PlayCueInternal(cue, worldPosition, null, volumeScale, pitchScale) : SoundHandle.Invalid;
        }

        /// <summary>Plays a cue by string key (matches SoundId name, cue asset name, or custom key in SoundLibrary).</summary>
        public static SoundHandle Play(string key, Vector3? worldPosition = null, float volumeScale = 1f, float pitchScale = 1f)
        {
            if (Instance == null || Instance.library == null)
                return SoundHandle.Invalid;
            return Instance.PlayCueInternal(Instance.library.GetCue(key), worldPosition, null, volumeScale, pitchScale);
        }

        public static bool Stop(SoundHandle handle, float fadeOutSeconds = 0f)
        {
            return Instance != null && Instance.voicePool != null && Instance.voicePool.Stop(handle, fadeOutSeconds);
        }

        public static void StopAll(SoundCategory? category = null)
        {
            Instance?.voicePool?.StopAll(category);
        }

        public static bool IsPlaying(SoundHandle handle)
        {
            return Instance != null && Instance.voicePool != null && Instance.voicePool.IsPlaying(handle);
        }

        // ---------------------------------------------------------------- Bus Volumes & Mute

        public static float GetCategoryVolume(SoundCategory category)
        {
            int idx = (int)category;
            if (Instance == null || idx < 0 || idx >= Instance.categoryVolumes.Length)
                return 1f;
            return Instance.categoryVolumes[idx];
        }

        public static void SetCategoryVolume(SoundCategory category, float volume, bool save = true)
        {
            if (Instance == null)
                return;
            int idx = (int)category;
            if (idx < 0 || idx >= Instance.categoryVolumes.Length)
                return;

            float clamped = Mathf.Clamp01(volume);
            Instance.categoryVolumes[idx] = clamped;
            if (save)
                PlayerPrefs.SetFloat(VolumePrefPrefix + category, clamped);

            Instance.voicePool?.RefreshCategoryVolumes(Instance.cachedBusVolumeGetter);
        }

        public static bool IsCategoryMuted(SoundCategory category)
        {
            int idx = (int)category;
            if (Instance == null || idx < 0 || idx >= Instance.categoryMuted.Length)
                return false;
            return Instance.categoryMuted[idx];
        }

        public static void SetCategoryMuted(SoundCategory category, bool muted, bool save = true)
        {
            if (Instance == null)
                return;
            int idx = (int)category;
            if (idx < 0 || idx >= Instance.categoryMuted.Length)
                return;

            Instance.categoryMuted[idx] = muted;
            if (save)
                PlayerPrefs.SetInt(MutePrefPrefix + category, muted ? 1 : 0);

            Instance.voicePool?.RefreshCategoryVolumes(Instance.cachedBusVolumeGetter);
        }

        public static void SetMuted(bool muted, bool save = true) => SetCategoryMuted(SoundCategory.Master, muted, save);

        // ---------------------------------------------------------------- Music Controls

        public static void NextMusicTrack(float fadeSeconds = -1f) => Instance?.AdvanceMusicTrack(fadeSeconds);

        public static void PlayMusicTrack(int trackIndex, float fadeSeconds = -1f) => Instance?.CrossfadeToMusicTrack(trackIndex, fadeSeconds);

        public static void SetMusicPaused(bool paused)
        {
            if (Instance == null)
                return;
            Instance.musicPaused = paused;
            if (paused)
            {
                Instance.musicSourceA?.Pause();
                Instance.musicSourceB?.Pause();
            }
            else
            {
                Instance.musicSourceA?.UnPause();
                Instance.musicSourceB?.UnPause();
            }
        }

        // ---------------------------------------------------------------- Internals

        SoundHandle PlayInternal(SoundId id, Vector3? pos, Transform follow, float volumeScale, float pitchScale)
        {
            if (id == SoundId.None || library == null)
                return SoundHandle.Invalid;
            return PlayCueInternal(library.GetCue(id), pos, follow, volumeScale, pitchScale);
        }

        SoundHandle PlayCueInternal(SoundCue cue, Vector3? pos, Transform follow, float volumeScale, float pitchScale)
        {
            if (cue == null || voicePool == null)
                return SoundHandle.Invalid;

            float busVol = GetEffectiveBusVolume(cue.Category);
            return voicePool.Play(cue, pos, follow, volumeScale, pitchScale, busVol);
        }

        float GetEffectiveBusVolume(SoundCategory category)
        {
            if (categoryMuted[(int)SoundCategory.Master])
                return 0f;

            float master = categoryVolumes[(int)SoundCategory.Master];
            if (category == SoundCategory.Master)
                return master;

            int idx = (int)category;
            if (idx < 0 || idx >= categoryVolumes.Length || categoryMuted[idx])
                return 0f;

            return master * categoryVolumes[idx];
        }

        void LoadSavedBusSettings()
        {
            for (int i = 0; i < categoryVolumes.Length; i++)
            {
                var cat = (SoundCategory)i;
                float defVol = library != null ? library.GetDefaultCategoryVolume(cat) : categoryVolumes[i];
                categoryVolumes[i] = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumePrefPrefix + cat, defVol));
                categoryMuted[i] = PlayerPrefs.GetInt(MutePrefPrefix + cat, 0) != 0;
            }
        }

        AudioSource CreateBackgroundSource(string goName, bool loop)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loop;
            src.spatialBlend = 0f;
            src.dopplerLevel = 0f;
            src.priority = 16;
            src.volume = 0f;
            return src;
        }

        void StartBackgroundAudio()
        {
            if (library == null)
                return;

            // Start ambient city bed
            if (library.AmbienceLoop != null && ambienceSource != null)
            {
                ambienceSource.clip = library.AmbienceLoop;
                ambienceSource.loop = true;
                ambienceSource.volume = 0f;
                ambienceSource.Play();
                ambienceFadeIn = 0f;
            }

            // Start music playlist
            AudioClip[] tracks = library.MusicTracks;
            if (tracks != null && tracks.Length > 0)
            {
                BuildMusicOrder(tracks.Length);
                AdvanceMusicTrack(fadeSeconds: 1.5f);
            }
        }

        void BuildMusicOrder(int count)
        {
            musicOrder = new int[count];
            for (int i = 0; i < count; i++)
                musicOrder[i] = i;

            if (library != null && library.ShuffleMusicPlaylist && count > 1)
            {
                for (int i = count - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    int tmp = musicOrder[i];
                    musicOrder[i] = musicOrder[j];
                    musicOrder[j] = tmp;
                }
                if (musicOrder[0] == currentTrackIndex && count > 1)
                {
                    int swap = UnityEngine.Random.Range(1, count);
                    int tmp = musicOrder[0];
                    musicOrder[0] = musicOrder[swap];
                    musicOrder[swap] = tmp;
                }
            }
            musicCursor = 0;
        }

        void AdvanceMusicTrack(float fadeSeconds)
        {
            if (library == null || library.MusicTracks == null || library.MusicTracks.Length == 0)
                return;

            int count = library.MusicTracks.Length;
            if (musicOrder == null || musicOrder.Length != count || musicCursor >= count)
                BuildMusicOrder(count);

            int nextIdx = musicOrder[musicCursor++];
            CrossfadeToMusicTrack(nextIdx, fadeSeconds);
        }

        void CrossfadeToMusicTrack(int trackIndex, float fadeSeconds)
        {
            if (library == null || library.MusicTracks == null || trackIndex < 0 || trackIndex >= library.MusicTracks.Length)
                return;

            AudioClip clip = library.MusicTracks[trackIndex];
            if (clip == null)
                return;

            currentTrackIndex = trackIndex;
            musicUsingA = !musicUsingA;

            AudioSource incoming = musicUsingA ? musicSourceA : musicSourceB;
            incoming.clip = clip;
            incoming.loop = library.MusicTracks.Length == 1;
            incoming.volume = 0f;
            incoming.Play();

            musicFadeDuration = fadeSeconds >= 0f ? Mathf.Max(0.05f, fadeSeconds) : library.MusicCrossfadeSeconds;
            musicFadeProgress = 0f;
        }

        void TickMusic(float dt)
        {
            if (library == null || musicPaused)
                return;

            AudioSource active = musicUsingA ? musicSourceA : musicSourceB;
            AudioSource outgoing = musicUsingA ? musicSourceB : musicSourceA;
            if (active == null)
                return;

            // Gently duck music when ShopUI is open so the UI feels intimate
            bool shopOpen = ShopUI.Instance != null && ShopUI.Instance.IsOpen;
            float targetDuck = shopOpen ? library.ShopOpenMusicDuck : 1f;
            musicDuckMultiplier = Mathf.MoveTowards(musicDuckMultiplier, targetDuck, dt * 1.8f);

            if (musicFadeProgress < 1f)
            {
                musicFadeProgress = Mathf.Min(1f, musicFadeProgress + dt / Mathf.Max(0.05f, musicFadeDuration));
                if (musicFadeProgress >= 1f && outgoing != null && outgoing.isPlaying)
                {
                    outgoing.Stop();
                    outgoing.clip = null;
                }
            }

            float targetVol = library.MusicBaseVolume * GetEffectiveBusVolume(SoundCategory.Music) * musicDuckMultiplier;
            // Smooth equal-power-ish curve
            float inGain = Mathf.Sin(musicFadeProgress * Mathf.PI * 0.5f);
            float outGain = Mathf.Cos(musicFadeProgress * Mathf.PI * 0.5f);

            active.volume = targetVol * inGain;
            if (outgoing != null && outgoing.isPlaying)
                outgoing.volume = targetVol * outGain;

            // Trigger smooth crossfade slightly before the current track finishes
            if (library.MusicTracks != null && library.MusicTracks.Length > 1 && active.clip != null && musicFadeProgress >= 1f)
            {
                float crossfadeLead = Mathf.Min(library.MusicCrossfadeSeconds, active.clip.length * 0.25f);
                if (!active.isPlaying || active.time >= active.clip.length - crossfadeLead)
                    AdvanceMusicTrack(library.MusicCrossfadeSeconds);
            }
        }

        void TickAmbience(float dt)
        {
            if (library == null || ambienceSource == null || !ambienceSource.isPlaying)
                return;

            ambienceFadeIn = Mathf.Min(1f, ambienceFadeIn + dt * 0.5f);

            // Living-city feedback: as city pollution drops, pleasant ambient soundscape gently blooms
            float targetCleanMult = 1f;
            CityEcosystemManager eco = CityEcosystemManager.Active;
            if (eco != null)
            {
                float cleanliness = 1f - Mathf.Clamp01(eco.CityPollution);
                targetCleanMult = Mathf.Lerp(library.AmbienceSmogMinMultiplier, 1f, cleanliness);
            }
            ambienceSmogMultiplier = Mathf.MoveTowards(ambienceSmogMultiplier, targetCleanMult, dt * 0.35f);

            ambienceSource.volume = library.AmbienceBaseVolume
                * GetEffectiveBusVolume(SoundCategory.Ambience)
                * ambienceFadeIn
                * ambienceSmogMultiplier;
        }

        void EnsureAudioListenerExists()
        {
            // Keep our fallback listener disabled whenever the scene already provides an active AudioListener
            AudioListener[] listeners = FindObjectsByType<AudioListener>();
            bool hasExternalListener = false;
            for (int i = 0; i < listeners.Length; i++)
            {
                AudioListener l = listeners[i];
                if (l != null && l != fallbackListener && l.isActiveAndEnabled)
                {
                    hasExternalListener = true;
                    break;
                }
            }

            if (hasExternalListener)
            {
                if (fallbackListener != null)
                    fallbackListener.enabled = false;
            }
            else
            {
                if (fallbackListener == null)
                    fallbackListener = gameObject.AddComponent<AudioListener>();
                fallbackListener.enabled = true;
            }
        }
    }
}
