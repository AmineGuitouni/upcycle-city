using UnityEngine;

namespace UpcycleCity.Audio
{
    /// <summary>
    /// Drop-in designer component for playing any <see cref="SoundCue"/> or <see cref="SoundId"/>
    /// from props, machines, triggers, collisions, or UnityEvents without writing code.
    /// </summary>
    [AddComponentMenu("Upcycle City/Audio/Sound Emitter")]
    public sealed class SoundEmitter : MonoBehaviour
    {
        [Header("Sound Selection (Cue overrides SoundId if assigned)")]
        [SerializeField] SoundCue cueOverride;
        [SerializeField] SoundId soundId = SoundId.None;

        [Header("Playback")]
        [SerializeField, Range(0f, 2f)] float volumeScale = 1f;
        [SerializeField, Range(0.25f, 2f)] float pitchScale = 1f;
        [SerializeField] bool attachToTransform = true;
        [SerializeField] bool playOnEnable;
        [SerializeField] bool stopOnDisable = true;
        [SerializeField, Min(0f)] float stopFadeSeconds = 0.15f;

        SoundHandle activeHandle = SoundHandle.Invalid;

        void OnEnable()
        {
            if (playOnEnable)
                Play();
        }

        void OnDisable()
        {
            if (stopOnDisable && activeHandle.IsValid)
            {
                SoundManager.Stop(activeHandle, stopFadeSeconds);
                activeHandle = SoundHandle.Invalid;
            }
        }

        public void Play()
        {
            if (cueOverride != null)
            {
                activeHandle = SoundManager.Play(cueOverride, transform.position, volumeScale, pitchScale);
                return;
            }

            if (soundId == SoundId.None)
                return;

            activeHandle = attachToTransform
                ? SoundManager.PlayAttached(soundId, transform, volumeScale, pitchScale)
                : SoundManager.PlayAt(soundId, transform.position, volumeScale, pitchScale);
        }

        public void Stop()
        {
            if (!activeHandle.IsValid)
                return;
            SoundManager.Stop(activeHandle, stopFadeSeconds);
            activeHandle = SoundHandle.Invalid;
        }
    }
}
