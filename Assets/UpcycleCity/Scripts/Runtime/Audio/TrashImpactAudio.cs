using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Audio
{
    /// <summary>
    /// Lightweight physics collision audio attached automatically to <see cref="TrashItem"/>s.
    /// Plays 3D spatialized material-matched impacts scaled by collision speed with spawn-grace
    /// and per-item cooldown to keep piles of physics litter crisp and performance-safe.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Audio/Trash Impact Audio")]
    public sealed class TrashImpactAudio : MonoBehaviour
    {
        const float MinImpactSpeed = 1.35f;
        const float MaxImpactSpeed = 8.5f;
        const float SpawnGraceSeconds = 0.40f;
        const float PerItemCooldown = 0.18f;

        TrashItem trashItem;
        float lastImpactTime = -10f;
        float readyTime;

        void Awake()
        {
            trashItem = GetComponent<TrashItem>();
            readyTime = Time.unscaledTime + SpawnGraceSeconds;
        }

        void OnEnable()
        {
            readyTime = Time.unscaledTime + SpawnGraceSeconds;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (trashItem == null || trashItem.IsDisposed || trashItem.IsHeld)
                return;

            float now = Time.unscaledTime;
            if (now < readyTime || (now - lastImpactTime) < PerItemCooldown)
                return;

            float speed = collision.relativeVelocity.magnitude;
            if (speed < MinImpactSpeed)
                return;

            lastImpactTime = now;
            float t = Mathf.InverseLerp(MinImpactSpeed, MaxImpactSpeed, speed);
            float volumeScale = Mathf.Lerp(0.28f, 1.0f, t);
            float pitchScale = Mathf.Lerp(0.92f, 1.12f, t);

            Vector3 contactPos = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
            SoundManager.PlayAt(ImpactSoundForStream(trashItem.Stream, trashItem.IsBulky), contactPos, volumeScale, pitchScale);
        }

        public static SoundId ImpactSoundForStream(WasteStream stream, bool isBulky = false)
        {
            if (isBulky || stream == WasteStream.BulkyUpcyclable)
                return SoundId.ImpactBulky;

            switch (stream)
            {
                case WasteStream.Paper: return SoundId.ImpactPaper;
                case WasteStream.Plastic: return SoundId.ImpactPlastic;
                case WasteStream.Glass: return SoundId.ImpactGlass;
                case WasteStream.Cans: return SoundId.ImpactMetal;
                default: return SoundId.ImpactBulky;
            }
        }

        public static SoundId GrabSoundForStream(WasteStream stream, bool isBulky = false)
        {
            if (isBulky || stream == WasteStream.BulkyUpcyclable)
                return SoundId.GrabBulky;

            switch (stream)
            {
                case WasteStream.Paper: return SoundId.GrabPaper;
                case WasteStream.Plastic: return SoundId.GrabPlastic;
                case WasteStream.Glass: return SoundId.GrabGlass;
                case WasteStream.Cans: return SoundId.GrabMetal;
                default: return SoundId.GrabBulky;
            }
        }
    }
}
