using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>Scales a particle system's emission with pollution, e.g. factory chimney smoke.</summary>
    [RequireComponent(typeof(ParticleSystem))]
    [AddComponentMenu("Upcycle City/Pollution Particles")]
    public sealed class PollutionParticles : PollutionResponder
    {
        [Tooltip("Emission multiplier over pollution, from clean (0) to polluted (1).")]
        [SerializeField] AnimationCurve emissionByPollution = AnimationCurve.EaseInOut(0.15f, 0f, 1f, 1f);

        ParticleSystem system;
        float baseRate;

        void Awake()
        {
            system = GetComponent<ParticleSystem>();
            baseRate = system.emission.rateOverTimeMultiplier;
        }

        protected override void OnPollutionChanged(float pollution)
        {
            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTimeMultiplier = baseRate * Mathf.Max(0f, emissionByPollution.Evaluate(pollution));
        }
    }
}
