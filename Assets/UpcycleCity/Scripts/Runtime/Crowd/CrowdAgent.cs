using UnityEngine;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// One NPC of the crowd. It only describes the NPC; the <see cref="CrowdSystem"/> moves and animates
    /// every agent from one place, so a crowd costs no per-NPC Update calls.
    /// In the editor, and whenever it is not one of the nearest NPCs, the agent shows a cheap posed
    /// mesh (its static visual); near the camera the system swaps in the animated rig.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crowd/Crowd Agent")]
    public sealed class CrowdAgent : MonoBehaviour
    {
        [SerializeField] CrowdActivity activity = CrowdActivity.Idle;

        [Tooltip("Rigged Humanoid version of this character, animated near the camera.")]
        [SerializeField] GameObject animatedPrefab;

        [Tooltip("Posed mesh shown in the editor and whenever the character is not animated.")]
        [SerializeField] GameObject staticVisual;

        [Header("Walking")]
        [Tooltip("Route to walk. The agent starts from the point of the path nearest to where it is placed, facing the way it faces.")]
        [SerializeField] CrowdPath path;

        [Tooltip("Walking speed, in m/s.")]
        [SerializeField, Min(0.1f)] float walkSpeed = 1.3f;

        public CrowdActivity Activity => activity;
        public GameObject AnimatedPrefab => animatedPrefab;
        public GameObject StaticVisual => staticVisual;
        public CrowdPath Path => path;
        public float WalkSpeed => walkSpeed;

        /// <summary>Walks only when it has a path; otherwise a walker just stands.</summary>
        public bool IsWalker => activity == CrowdActivity.Walk && path != null && path.SegmentCount > 0;

        /// <summary>Sets the agent up from code, e.g. in <see cref="CrowdSpawner"/>. Call while the GameObject is inactive.</summary>
        public void Configure(CrowdActivity newActivity, GameObject animated, GameObject visual, CrowdPath route = null, float speed = 1.3f)
        {
            activity = newActivity;
            animatedPrefab = animated;
            staticVisual = visual;
            path = route;
            walkSpeed = Mathf.Max(0.1f, speed);
        }

        void OnEnable() => CrowdSystem.Register(this);

        void OnDisable() => CrowdSystem.Unregister(this);
    }
}
