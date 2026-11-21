using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Pollution;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// A named spatial city district bound to a <see cref="PollutionZone"/>.
    /// Aggregates real physical trash weight + active <see cref="IPollutionSource"/> air emissions
    /// inside its radius, drives live building grime &amp; foliage shaders via <see cref="PollutionZone"/>,
    /// and computes Resident Satisfaction which feeds back into citizen littering rates.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PollutionZone))]
    [AddComponentMenu("Upcycle City/Ecosystem/City District")]
    public sealed class CityDistrict : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] string districtName = "Neighborhood";
        [SerializeField, Range(0.25f, 3f)] float cityWeight = 1f;

        [Header("Pollution Capacity")]
        [Tooltip("Total trash + air pollution load that corresponds to 100% district pollution.")]
        [SerializeField, Min(2f)] float pollutionCapacity = 22f;

        [Tooltip("How fast district pollution blends toward the real physical load (units/sec).")]
        [SerializeField, Min(0.05f)] float pollutionSmoothing = 0.28f;

        [Header("Satisfaction Feedback")]
        [Tooltip("How fast resident satisfaction responds to cleanliness and bin upkeep (units/sec).")]
        [SerializeField, Min(0.05f)] float satisfactionSmoothing = 0.22f;

        [Tooltip("Bonus satisfaction impulse when the player empties a bin or fixes a pollution source here.")]
        [SerializeField, Range(0f, 0.3f)] float serviceSatisfactionPulse = 0.10f;

        PollutionZone zone;
        readonly List<IPollutionSource> sources = new List<IPollutionSource>(8);

        float currentPollution;
        float targetPollution;
        float currentSatisfaction = 0.5f;
        float targetSatisfaction = 0.5f;
        float satisfactionBonus;

        int activeTrashCount;
        float trashPollutionLoad;
        float backgroundLitterLoad;
        int pendingBackgroundTrash;
        float airPollutionLoad;
        int totalBins;
        int cleanBins;
        int mitigatedSources;

        bool wasCleanNotified;

        public event Action<CityDistrict> StateChanged;

        public string DistrictName => string.IsNullOrWhiteSpace(districtName) ? gameObject.name : districtName;
        public float CityWeight => cityWeight;
        public PollutionZone Zone => zone;
        public float Radius => zone != null ? zone.Radius : 25f;
        public float CurrentPollution => currentPollution;
        public float TargetPollution => targetPollution;
        public float CurrentSatisfaction => currentSatisfaction;
        public int ActiveTrashCount => activeTrashCount;
        public int PendingBackgroundTrash => pendingBackgroundTrash;
        public float TrashPollutionLoad => trashPollutionLoad + backgroundLitterLoad;
        public float BackgroundLitterLoad => backgroundLitterLoad;
        public float AirPollutionLoad => airPollutionLoad;
        public int TotalBins => totalBins;
        public int CleanBins => cleanBins;
        public int SourceCount => sources.Count;
        public int MitigatedSourceCount => mitigatedSources;
        public IReadOnlyList<IPollutionSource> Sources => sources;

        /// <summary>
        /// Multiplier (0.08..1.65) for how likely citizens are to litter here.
        /// Clean districts with serviced bins nearly eliminate littering; dirty neglected ones accelerate it.
        /// </summary>
        public float LitterRateMultiplier => Mathf.Lerp(1.65f, 0.08f, currentSatisfaction);

        public void Configure(string nameLabel, float radius, float falloff, float capacity, float weight = 1f)
        {
            districtName = nameLabel;
            pollutionCapacity = Mathf.Max(2f, capacity);
            cityWeight = Mathf.Clamp(weight, 0.25f, 3f);

            if (zone == null)
                zone = GetComponent<PollutionZone>();
            if (zone != null)
            {
                zone.Radius = Mathf.Max(4f, radius);
                zone.Falloff = Mathf.Clamp(falloff, 1f, radius * 0.8f);
                zone.Weight = 1f;
            }
        }

        void Awake()
        {
            zone = GetComponent<PollutionZone>();
            currentPollution = zone != null ? zone.Pollution : 0.5f;
            targetPollution = currentPollution;
            currentSatisfaction = Mathf.Clamp01(1f - currentPollution);
            targetSatisfaction = currentSatisfaction;
        }

        void OnEnable()
        {
            CityEcosystemManager.RegisterDistrict(this);
        }

        void OnDisable()
        {
            CityEcosystemManager.UnregisterDistrict(this);
        }

        public bool ContainsPoint(Vector3 worldPos)
        {
            Vector3 c = transform.position;
            float dx = worldPos.x - c.x;
            float dz = worldPos.z - c.z;
            float r = Radius;
            return dx * dx + dz * dz <= r * r;
        }

        public float FlatSqrDistance(Vector3 worldPos)
        {
            Vector3 c = transform.position;
            float dx = worldPos.x - c.x;
            float dz = worldPos.z - c.z;
            return dx * dx + dz * dz;
        }

        public void RegisterSource(IPollutionSource source)
        {
            if (source == null || sources.Contains(source))
                return;
            sources.Add(source);
            source.BindDistrict(this);
        }

        public void UnregisterSource(IPollutionSource source)
        {
            sources.Remove(source);
        }

        /// <summary>Called when the player actively services a bin or repairs a factory scrubber in this district.</summary>
        public void AddSatisfactionBoost(float amount = -1f)
        {
            float boost = amount > 0f ? amount : serviceSatisfactionPulse;
            satisfactionBonus = Mathf.Clamp(satisfactionBonus + boost, 0f, 0.25f);
        }

        /// <summary>
        /// Adds virtual litter from distant Tier-2 background citizens so off-screen populations
        /// genuinely affect district pollution before the player arrives and the litter materializes physically.
        /// </summary>
        public void AddBackgroundLitter(float weight, int maxPending = 8)
        {
            if (pendingBackgroundTrash >= maxPending)
                return;
            pendingBackgroundTrash++;
            backgroundLitterLoad = Mathf.Min(backgroundLitterLoad + Mathf.Max(0.2f, weight), pollutionCapacity * 0.55f);
        }

        /// <summary>
        /// Transfers one pending virtual litter piece into real physical trash when the player enters this district.
        /// </summary>
        public bool TryConsumeBackgroundLitter(out float averageWeight)
        {
            if (pendingBackgroundTrash <= 0)
            {
                averageWeight = 0f;
                return false;
            }

            averageWeight = backgroundLitterLoad / pendingBackgroundTrash;
            pendingBackgroundTrash--;
            backgroundLitterLoad = pendingBackgroundTrash > 0 ? Mathf.Max(0f, backgroundLitterLoad - averageWeight) : 0f;
            return true;
        }

        internal void BeginAccumulation()
        {
            activeTrashCount = 0;
            trashPollutionLoad = 0f;
            airPollutionLoad = 0f;
            totalBins = 0;
            cleanBins = 0;
            mitigatedSources = 0;
        }

        internal void AccumulateTrash(float pollutionWeight)
        {
            activeTrashCount++;
            trashPollutionLoad += Mathf.Max(0.05f, pollutionWeight);
        }

        internal void FinalizeTick(float dt, bool snapImmediate = false)
        {
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                IPollutionSource s = sources[i];
                if (s == null)
                {
                    sources.RemoveAt(i);
                    continue;
                }

                if (s.IsActive)
                    airPollutionLoad += Mathf.Max(0f, s.AirPollutionLoad);

                if (s.IsMitigated)
                    mitigatedSources++;

                if (s is OverflowingBinSource bin)
                {
                    totalBins++;
                    if (!bin.IsOverflowing)
                        cleanBins++;
                }
            }

            float totalLoad = trashPollutionLoad + backgroundLitterLoad + airPollutionLoad;
            targetPollution = Mathf.Clamp01(totalLoad / Mathf.Max(1f, pollutionCapacity));

            // Resident satisfaction reflects cleanliness (65%), clean bin availability (20%), and air quality (15%)
            float cleanlinessScore = 1f - currentPollution;
            float binScore = totalBins > 0 ? (float)cleanBins / totalBins : 1f;
            float airScore = 1f - Mathf.Clamp01(airPollutionLoad / Mathf.Max(1f, pollutionCapacity * 0.65f));
            targetSatisfaction = Mathf.Clamp01(
                cleanlinessScore * 0.65f +
                binScore * 0.20f +
                airScore * 0.15f +
                satisfactionBonus);

            if (snapImmediate)
            {
                currentPollution = targetPollution;
                currentSatisfaction = targetSatisfaction;
            }
            else
            {
                currentPollution = Mathf.MoveTowards(currentPollution, targetPollution, pollutionSmoothing * dt);
                currentSatisfaction = Mathf.MoveTowards(currentSatisfaction, targetSatisfaction, satisfactionSmoothing * dt);
                satisfactionBonus = Mathf.MoveTowards(satisfactionBonus, 0f, 0.025f * dt);
            }

            if (zone != null)
            {
                zone.Weight = 1f;
                if (!Mathf.Approximately(zone.Pollution, currentPollution))
                    zone.Pollution = currentPollution;
            }

            // Milestone toast when a district flips to clean
            if (!wasCleanNotified && currentPollution <= 0.22f && Time.time > 3f)
            {
                wasCleanNotified = true;
                Player.PlayerContext.ActiveInteractor?.NotifyFeedback(
                    $"{DistrictName} is CLEAN! Residents are thriving (+Satisfaction)",
                    new Color(0.18f, 0.72f, 0.42f));
            }
            else if (wasCleanNotified && currentPollution >= 0.48f)
            {
                wasCleanNotified = false;
            }

            StateChanged?.Invoke(this);
        }
    }
}
