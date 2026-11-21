using UnityEngine;
using UpcycleCity.Pollution;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Smoothly brings a city fountain's water jets and basin water to life as local pollution drops,
    /// replacing sci-fi dissolve effects with a natural physical transition.
    /// </summary>
    [AddComponentMenu("Upcycle City/Ecosystem/Fountain Water Responder")]
    public sealed class FountainWaterResponder : PollutionResponder
    {
        [SerializeField] ParticleSystem waterSpray;
        [SerializeField] Transform waterSurface;
        [SerializeField, Range(0.1f, 0.9f)] float dryPollutionThreshold = 0.62f;
        [SerializeField, Min(1f)] float fullSprayRate = 28f;
        [SerializeField] float dryYOffset = -0.18f;

        Vector3 fullScale;
        Vector3 fullLocalPos;
        bool initialized;

        public void Configure(ParticleSystem spray, Transform surface, float threshold = 0.62f, float maxRate = 28f)
        {
            waterSpray = spray;
            waterSurface = surface;
            dryPollutionThreshold = Mathf.Clamp(threshold, 0.1f, 0.9f);
            fullSprayRate = Mathf.Max(1f, maxRate);
            CacheWaterRestTransform();
        }

        void Awake()
        {
            CacheWaterRestTransform();
        }

        void CacheWaterRestTransform()
        {
            if (initialized || waterSurface == null)
                return;
            fullScale = waterSurface.localScale;
            fullLocalPos = waterSurface.localPosition;
            initialized = true;
        }

        protected override void OnPollutionChanged(float pollution)
        {
            CacheWaterRestTransform();
            float cleanliness = 1f - Mathf.Clamp01(pollution / Mathf.Max(0.05f, dryPollutionThreshold));

            if (waterSpray != null)
            {
                ParticleSystem.EmissionModule emission = waterSpray.emission;
                emission.rateOverTimeMultiplier = fullSprayRate * cleanliness;

                ParticleSystem.MainModule main = waterSpray.main;
                main.startSpeedMultiplier = Mathf.Lerp(0.4f, 2.2f, cleanliness);
            }

            if (waterSurface != null)
            {
                bool hasWater = cleanliness > 0.04f;
                waterSurface.gameObject.SetActive(hasWater);
                if (hasWater)
                {
                    waterSurface.localPosition = fullLocalPos + new Vector3(0f, Mathf.Lerp(dryYOffset, 0f, cleanliness), 0f);
                    waterSurface.localScale = new Vector3(fullScale.x, Mathf.Max(0.005f, fullScale.y * cleanliness), fullScale.z);
                }
            }
        }
    }
}
