using System;
using UnityEngine;

namespace UpcycleCity.Audio
{
    /// <summary>How a <see cref="SoundCue"/> chooses among multiple clip variations.</summary>
    public enum ClipSelectionMode
    {
        /// <summary>Shuffled deck: plays every clip once before reshuffling and avoids back-to-back repeats.</summary>
        ShuffleBag = 0,
        /// <summary>Uniform random selection avoiding immediate repeat when 2+ clips are present.</summary>
        RandomNoRepeat = 1,
        /// <summary>Plays clips in strict array order, wrapping around.</summary>
        Sequential = 2,
        /// <summary>Pure random selection.</summary>
        Random = 3,
    }

    /// <summary>
    /// Data-driven audio cue asset supporting clip variation, pitch/volume randomization,
    /// spam cooldowns, polyphony caps, and 2D/3D spatialization.
    /// Create new cues via <b>Assets → Create → Upcycle City → Audio → Sound Cue</b>.
    /// </summary>
    [CreateAssetMenu(fileName = "SFX_NewCue", menuName = "Upcycle City/Audio/Sound Cue", order = 10)]
    public sealed class SoundCue : ScriptableObject
    {
        [Header("Clips & Variation")]
        [Tooltip("One or more audio clips to vary across repeated triggers.")]
        [SerializeField] AudioClip[] clips = Array.Empty<AudioClip>();

        [Tooltip("How clips are selected when multiple variations exist.")]
        [SerializeField] ClipSelectionMode selectionMode = ClipSelectionMode.ShuffleBag;

        [Header("Bus & Gain")]
        [SerializeField] SoundCategory category = SoundCategory.SFX;

        [Tooltip("Randomized base volume range (min..max) per trigger.")]
        [SerializeField] Vector2 volumeRange = new Vector2(0.85f, 1.0f);

        [Tooltip("Randomized pitch range (min..max) per trigger for natural variation.")]
        [SerializeField] Vector2 pitchRange = new Vector2(0.95f, 1.05f);

        [Tooltip("When enabled, consecutive triggers nudge away from the previous pitch so single-clip cues stay fresh.")]
        [SerializeField] bool avoidSimilarPitch = true;

        [Header("Playback & Budget")]
        [Tooltip("Loop until explicitly stopped via SoundHandle.")]
        [SerializeField] bool loop;

        [Tooltip("Minimum seconds between consecutive triggers of this cue (prevents rapid audio spam).")]
        [SerializeField, Min(0f)] float minInterval = 0.04f;

        [Tooltip("Maximum simultaneous voices allowed for this cue (0 = unlimited).")]
        [SerializeField, Range(0, 16)] int maxConcurrent = 4;

        [Tooltip("Optional cutoff duration in seconds (0 = play full clip). Useful for trimming long tails.")]
        [SerializeField, Min(0f)] float maxDuration;

        [Tooltip("Voice stealing priority (0 = highest importance, 256 = lowest).")]
        [SerializeField, Range(0, 256)] int priority = 128;

        [Header("Spatialization")]
        [Tooltip("When true and a world position is provided, plays in 3D space with distance attenuation.")]
        [SerializeField] bool spatialize = true;

        [SerializeField, Range(0f, 1f)] float spatialBlend = 0.85f;
        [SerializeField, Min(0.1f)] float minDistance = 2.0f;
        [SerializeField, Min(1f)] float maxDistance = 28f;
        [SerializeField] AudioRolloffMode rolloffMode = AudioRolloffMode.Logarithmic;
        [SerializeField, Range(0f, 5f)] float dopplerLevel = 0f;

        public AudioClip[] Clips => clips;
        public ClipSelectionMode SelectionMode => selectionMode;
        public SoundCategory Category => category;
        public Vector2 VolumeRange => volumeRange;
        public Vector2 PitchRange => pitchRange;
        public bool AvoidSimilarPitch => avoidSimilarPitch;
        public bool Loop => loop;
        public float MinInterval => minInterval;
        public int MaxConcurrent => maxConcurrent;
        public float MaxDuration => maxDuration;
        public int Priority => priority;
        public bool Spatialize => spatialize;
        public float SpatialBlend => spatialBlend;
        public float MinDistance => minDistance;
        public float MaxDistance => maxDistance;
        public AudioRolloffMode RolloffMode => rolloffMode;
        public float DopplerLevel => dopplerLevel;

        public bool HasValidClip
        {
            get
            {
                if (clips == null || clips.Length == 0)
                    return false;
                for (int i = 0; i < clips.Length; i++)
                {
                    if (clips[i] != null)
                        return true;
                }
                return false;
            }
        }

        public float SampleVolume()
        {
            float lo = Mathf.Clamp01(Mathf.Min(volumeRange.x, volumeRange.y));
            float hi = Mathf.Clamp01(Mathf.Max(volumeRange.x, volumeRange.y));
            return Mathf.Approximately(lo, hi) ? lo : UnityEngine.Random.Range(lo, hi);
        }

        public float SamplePitch(float lastPitch)
        {
            float lo = Mathf.Clamp(Mathf.Min(pitchRange.x, pitchRange.y), 0.15f, 3f);
            float hi = Mathf.Clamp(Mathf.Max(pitchRange.x, pitchRange.y), 0.15f, 3f);
            float span = hi - lo;
            if (span < 0.005f)
                return lo;

            float p = UnityEngine.Random.Range(lo, hi);
            if (avoidSimilarPitch && lastPitch > 0f && Mathf.Abs(p - lastPitch) < span * 0.25f)
            {
                // Push away from the last pitch inside [lo, hi] for clearly audible variation
                float shifted = p + (p >= lastPitch ? span * 0.35f : -span * 0.35f);
                if (shifted > hi) shifted = lo + (shifted - hi);
                else if (shifted < lo) shifted = hi - (lo - shifted);
                p = Mathf.Clamp(shifted, lo, hi);
            }
            return p;
        }

        /// <summary>Editor/builder helper to configure a cue programmatically.</summary>
        public void Configure(
            AudioClip[] audioClips,
            SoundCategory soundCategory,
            Vector2 volume,
            Vector2 pitch,
            bool isSpatial = true,
            float blend = 0.85f,
            float minDist = 2f,
            float maxDist = 28f,
            float cooldown = 0.04f,
            int concurrentCap = 4,
            int voicePriority = 128,
            ClipSelectionMode mode = ClipSelectionMode.ShuffleBag,
            bool isLoop = false,
            float cutoffDuration = 0f)
        {
            clips = audioClips ?? Array.Empty<AudioClip>();
            category = soundCategory;
            volumeRange = volume;
            pitchRange = pitch;
            spatialize = isSpatial;
            spatialBlend = Mathf.Clamp01(blend);
            minDistance = Mathf.Max(0.1f, minDist);
            maxDistance = Mathf.Max(minDistance + 0.5f, maxDist);
            minInterval = Mathf.Max(0f, cooldown);
            maxConcurrent = Mathf.Clamp(concurrentCap, 0, 16);
            priority = Mathf.Clamp(voicePriority, 0, 256);
            selectionMode = mode;
            loop = isLoop;
            maxDuration = Mathf.Max(0f, cutoffDuration);
            avoidSimilarPitch = true;
            dopplerLevel = 0f;
            rolloffMode = AudioRolloffMode.Logarithmic;
        }

        void OnValidate()
        {
            volumeRange.x = Mathf.Clamp01(volumeRange.x);
            volumeRange.y = Mathf.Clamp01(volumeRange.y);
            if (volumeRange.y < volumeRange.x)
                volumeRange.y = volumeRange.x;

            pitchRange.x = Mathf.Clamp(pitchRange.x, 0.15f, 3f);
            pitchRange.y = Mathf.Clamp(pitchRange.y, 0.15f, 3f);
            if (pitchRange.y < pitchRange.x)
                pitchRange.y = pitchRange.x;

            minDistance = Mathf.Max(0.1f, minDistance);
            maxDistance = Mathf.Max(minDistance + 0.5f, maxDistance);
            minInterval = Mathf.Max(0f, minInterval);
            maxDuration = Mathf.Max(0f, maxDuration);
        }
    }
}
