using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Plays a splash (droplets and a spreading ripple ring) whenever the tracked object breaks the water
    /// surface going up or down, like the turtle surfacing to breathe.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Splash On Surface")]
    public sealed class SplashOnSurface : MonoBehaviour
    {
        [SerializeField] Transform tracked;
        [Tooltip("Height of the tracked point that counts as breaking the surface.")]
        [SerializeField] float surfaceHeight = 0.12f;
        [Tooltip("Offset from the tracked pivot to the point tested (e.g. the top of the shell).")]
        [SerializeField] float probeHeight = 0.2f;
        [SerializeField] ParticleSystem droplets;
        [SerializeField] ParticleSystem ripples;
        [SerializeField, Min(0f)] float cooldown = 0.8f;

        bool wasAbove;
        float lastSplash = -10f;

        void OnEnable()
        {
            wasAbove = tracked != null && tracked.position.y + probeHeight > surfaceHeight;
        }

        void Update()
        {
            if (tracked == null)
                return;
            bool above = tracked.position.y + probeHeight > surfaceHeight;
            if (above != wasAbove && Time.time - lastSplash > cooldown)
            {
                Vector3 at = new Vector3(tracked.position.x, surfaceHeight + 0.02f, tracked.position.z);
                Emit(droplets, at, above ? 26 : 14);
                Emit(ripples, at, 2);
                lastSplash = Time.time;
            }
            wasAbove = above;
        }

        static void Emit(ParticleSystem system, Vector3 at, int count)
        {
            if (system == null)
                return;
            system.transform.position = at;
            system.Emit(count);
        }
    }
}
