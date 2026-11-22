using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Put on a scene-placed workshop machine that must be bought before use. While locked the machine is hidden
    /// (renderers, lights and colliders off, so no interaction or hopper intake) and its authoritative handlers refuse
    /// every request. The GameObject itself stays active so its in-scene <c>NetworkObject</c>, placement ID and synced
    /// state keep working for late joiners. Driven by <see cref="WorkshopProperty"/> from replicated owned-state.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Workshop Machine Lock")]
    public sealed class WorkshopMachineLock : MonoBehaviour
    {
        [Tooltip("Locked until the owning WorkshopProperty says it was bought.")]
        [SerializeField] bool startLocked = true;

        Renderer[] renderers;
        Collider[] colliders;
        Light[] lights;
        bool[] colliderWasEnabled;
        bool cached;
        bool locked;
        bool applied;

        /// <summary>True while the machine has not been bought (hidden and unusable).</summary>
        public bool IsLocked => locked;

        void Awake()
        {
            // The owning WorkshopProperty may already have set the real state (its Awake can run first)
            if (applied)
                return;
            locked = startLocked;
            Apply();
        }

        /// <summary>Locks or unlocks this machine (idempotent; safe before Awake).</summary>
        public void SetLocked(bool value)
        {
            if (applied && locked == value)
                return;
            locked = value;
            Apply();
        }

        void Cache()
        {
            if (cached)
                return;
            cached = true;
            renderers = GetComponentsInChildren<Renderer>(true);
            colliders = GetComponentsInChildren<Collider>(true);
            lights = GetComponentsInChildren<Light>(true);
            colliderWasEnabled = new bool[colliders.Length];
            for (int i = 0; i < colliders.Length; i++)
                colliderWasEnabled[i] = colliders[i] != null && colliders[i].enabled;
        }

        void Apply()
        {
            Cache();
            applied = true;
            bool visible = !locked;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = visible;
            }
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null)
                    lights[i].enabled = visible;
            }
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    colliders[i].enabled = visible && colliderWasEnabled[i];
            }
        }

        /// <summary>True when <paramref name="target"/> carries a lock that is currently locked.</summary>
        public static bool IsMachineLocked(WorkshopMachineLock target) => target != null && target.locked;
    }
}
