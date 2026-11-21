using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// Grows a pollution zone outwards from its centre: a wave that cleans the city as it passes,
    /// dissolving litter and bringing the colour back.
    /// </summary>
    [RequireComponent(typeof(PollutionZone))]
    [AddComponentMenu("Upcycle City/Cleanup Wave")]
    public sealed class CleanupWave : MonoBehaviour
    {
        [Tooltip("Radius the wave reaches, in metres.")]
        [SerializeField, Min(0f)] float maxRadius = 160f;

        [Tooltip("Seconds the wave takes to reach its full radius.")]
        [SerializeField, Min(0.01f)] float duration = 6f;

        [SerializeField] AnimationCurve growth = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [SerializeField] bool playOnStart;

        PollutionZone zone;
        float elapsed = -1f;

        public bool IsPlaying => elapsed >= 0f;

        void Awake() => zone = GetComponent<PollutionZone>();

        void Start()
        {
            if (playOnStart)
                Play();
        }

        /// <summary>Restarts the wave from its centre.</summary>
        [ContextMenu("Play")]
        public void Play()
        {
            if (zone == null)
                zone = GetComponent<PollutionZone>();
            elapsed = 0f;
            zone.Radius = 0f;
        }

        /// <summary>Starts the wave at a world position, e.g. where the player used a clean-up tool.</summary>
        public void PlayAt(Vector3 position)
        {
            transform.position = position;
            Play();
        }

        void Update()
        {
            if (!IsPlaying)
                return;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            zone.Radius = growth.Evaluate(t) * maxRadius;
            if (t >= 1f)
                elapsed = -1f;
        }
    }
}
