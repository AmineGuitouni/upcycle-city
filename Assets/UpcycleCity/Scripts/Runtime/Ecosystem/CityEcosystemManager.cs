using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Player;
using UpcycleCity.Pollution;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Central Phase 2 simulation coordinator for the Living Cause-and-Effect Pollution Ecosystem.
    /// Tracks every physical TrashItem (via events, zero FindObjectsByType), updates all CityDistricts,
    /// drives PollutionController's global atmosphere/sky/smog, enforces mobile-safe trash caps,
    /// and applies a keyed "SmogZone" movement penalty via PlayerModifiers when the player walks into thick smog.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-40)]
    [AddComponentMenu("Upcycle City/Ecosystem/City Ecosystem Manager")]
    public sealed class CityEcosystemManager : MonoBehaviour
    {
        public const string SmogModifierSourceId = "SmogZone";

        [Header("Simulation")]
        [Tooltip("Seconds between ecosystem aggregation ticks (4 Hz keeps mobile CPU cost near zero).")]
        [SerializeField, Min(0.05f)] float tickInterval = 0.25f;

        [Tooltip("Hard cap on active physical TrashItems across the whole scene for mobile performance.")]
        [SerializeField, Range(15, 200)] int maxGlobalTrash = 65;

        [Tooltip("Catalog of physical litter/bulky trash archetypes shared by all pollution sources.")]
        [SerializeField] WasteCatalog defaultCatalog;

        [Header("Player Smog Exposure")]
        [Tooltip("Local pollution threshold above which heavy smog slows an unprotected player.")]
        [SerializeField, Range(0.2f, 0.95f)] float smogSlowThreshold = 0.58f;

        [Tooltip("Minimum move speed multiplier at 100% toxic smog (ignored when SmogImmune flag is active).")]
        [SerializeField, Range(0.5f, 1f)] float maxSmogSpeedMultiplier = 0.78f;

        static readonly List<CityDistrict> Districts = new List<CityDistrict>(8);
        static readonly List<IPollutionSource> Sources = new List<IPollutionSource>(32);
        static readonly List<OverflowingBinSource> StreetBins = new List<OverflowingBinSource>(16);

        readonly HashSet<TrashItem> liveTrash = new HashSet<TrashItem>();
        readonly List<TrashItem> scratchTrash = new List<TrashItem>(64);
        readonly int[] countsBySource = new int[6];

        float tickTimer;
        bool initializedSnap;

        public static CityEcosystemManager Active { get; private set; }

        public event Action<CityEcosystemManager> Updated;

        public WasteCatalog DefaultCatalog => defaultCatalog;
        public int MaxGlobalTrash => maxGlobalTrash;
        public int TotalActiveTrash => liveTrash.Count;
        public bool CanSpawnMoreTrash => liveTrash.Count < maxGlobalTrash;
        public float CityPollution { get; private set; }
        public float CitySatisfaction { get; private set; } = 0.5f;
        public float PlayerSmogExposure { get; private set; }
        public bool IsPlayerSlowedBySmog { get; private set; }
        public bool IsPlayerSmogProtected { get; private set; }
        public CityDistrict PlayerDistrict { get; private set; }

        public IReadOnlyList<CityDistrict> AllDistricts => Districts;
        public IReadOnlyList<IPollutionSource> AllSources => Sources;
        public IReadOnlyList<OverflowingBinSource> AllStreetBins => StreetBins;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
            Districts.Clear();
            Sources.Clear();
            StreetBins.Clear();
        }

        public void Configure(WasteCatalog catalog, int globalTrashCap = 65)
        {
            defaultCatalog = catalog;
            maxGlobalTrash = Mathf.Clamp(globalTrashCap, 15, 200);
        }

        public int GetActiveTrashCount(WasteSourceKind kind)
        {
            int idx = (int)kind;
            return idx >= 0 && idx < countsBySource.Length ? countsBySource[idx] : 0;
        }

        internal static void RegisterDistrict(CityDistrict district)
        {
            if (district == null || Districts.Contains(district))
                return;
            Districts.Add(district);
            BindUnassignedSources();
        }

        internal static void UnregisterDistrict(CityDistrict district)
        {
            Districts.Remove(district);
        }

        public static void RegisterSource(IPollutionSource source)
        {
            if (source == null || Sources.Contains(source))
                return;
            Sources.Add(source);

            if (source is OverflowingBinSource bin && !StreetBins.Contains(bin))
                StreetBins.Add(bin);

            if (source.District != null)
            {
                source.District.RegisterSource(source);
            }
            else
            {
                CityDistrict nearest = FindDistrictForPosition(source.WorldPosition);
                if (nearest != null)
                    nearest.RegisterSource(source);
            }
        }

        public static void UnregisterSource(IPollutionSource source)
        {
            if (source == null)
                return;
            Sources.Remove(source);
            if (source is OverflowingBinSource bin)
                StreetBins.Remove(bin);
            source.District?.UnregisterSource(source);
        }

        static void BindUnassignedSources()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                IPollutionSource s = Sources[i];
                if (s != null && s.District == null)
                {
                    CityDistrict nearest = FindDistrictForPosition(s.WorldPosition);
                    if (nearest != null)
                        nearest.RegisterSource(s);
                }
            }
        }

        /// <summary>Returns the enclosing CityDistrict for a world point, or the closest district if outside all radii.</summary>
        public static CityDistrict FindDistrictForPosition(Vector3 worldPos)
        {
            CityDistrict enclosing = null;
            float bestEnclosingSqr = float.MaxValue;
            CityDistrict closest = null;
            float bestClosestSqr = float.MaxValue;

            for (int i = 0; i < Districts.Count; i++)
            {
                CityDistrict d = Districts[i];
                if (d == null)
                    continue;

                float sqr = d.FlatSqrDistance(worldPos);
                if (sqr < bestClosestSqr)
                {
                    bestClosestSqr = sqr;
                    closest = d;
                }

                float r = d.Radius;
                if (sqr <= r * r && sqr < bestEnclosingSqr)
                {
                    bestEnclosingSqr = sqr;
                    enclosing = d;
                }
            }

            return enclosing != null ? enclosing : closest;
        }

        /// <summary>Finds the nearest non-overflowing street bin within <paramref name="maxRadius"/> metres.</summary>
        public static OverflowingBinSource FindNearestCleanBin(Vector3 worldPos, float maxRadius)
        {
            OverflowingBinSource best = null;
            float bestSqr = maxRadius * maxRadius;

            for (int i = 0; i < StreetBins.Count; i++)
            {
                OverflowingBinSource bin = StreetBins[i];
                if (bin == null || !bin.isActiveAndEnabled || bin.IsOverflowing)
                    continue;

                Vector3 delta = bin.WorldPosition - worldPos;
                float sqr = delta.x * delta.x + delta.z * delta.z;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = bin;
                }
            }

            return best;
        }

        void OnEnable()
        {
            Active = this;
            TrashItem.Spawned += OnTrashSpawned;
            TrashItem.Removed += OnTrashRemoved;

            // Collect any pre-placed TrashItems already awake in the scene
            TrashItem[] existing = FindObjectsByType<TrashItem>();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] != null && !existing[i].IsDisposed)
                    liveTrash.Add(existing[i]);
            }

            BindUnassignedSources();
            initializedSnap = false;
        }

        void OnDisable()
        {
            TrashItem.Spawned -= OnTrashSpawned;
            TrashItem.Removed -= OnTrashRemoved;
            PlayerContext.ActiveModifiers?.RemoveAllFromSource(SmogModifierSourceId);

            if (Active == this)
                Active = null;
        }

        void OnTrashSpawned(TrashItem item)
        {
            if (item != null && !item.IsDisposed)
                liveTrash.Add(item);
        }

        void OnTrashRemoved(TrashItem item)
        {
            if (item != null)
                liveTrash.Remove(item);
        }

        void Start()
        {
            // Snap initial atmosphere to match real physical world state right away
            StepSimulation(tickInterval, snapImmediate: true);
            initializedSnap = true;
        }

        void Update()
        {
            tickTimer += Time.deltaTime;
            if (tickTimer < tickInterval)
                return;

            float dt = tickTimer;
            tickTimer = 0f;
            StepSimulation(dt, snapImmediate: !initializedSnap);
            initializedSnap = true;
        }

        void StepSimulation(float dt, bool snapImmediate)
        {
            Array.Clear(countsBySource, 0, countsBySource.Length);

            for (int i = 0; i < Districts.Count; i++)
                Districts[i]?.BeginAccumulation();

            // Copy liveTrash into scratch list to safely purge any destroyed references without GC
            scratchTrash.Clear();
            foreach (TrashItem item in liveTrash)
                scratchTrash.Add(item);

            for (int i = 0; i < scratchTrash.Count; i++)
            {
                TrashItem item = scratchTrash[i];
                if (item == null || item.IsDisposed)
                {
                    liveTrash.Remove(item);
                    continue;
                }

                int srcIdx = (int)item.Source;
                if (srcIdx >= 0 && srcIdx < countsBySource.Length)
                    countsBySource[srcIdx]++;

                CityDistrict d = FindDistrictForPosition(item.transform.position);
                if (d != null)
                    d.AccumulateTrash(item.PollutionWeight);
            }

            float weightedPollution = 0f;
            float weightedSatisfaction = 0f;
            float totalWeight = 0f;

            for (int i = 0; i < Districts.Count; i++)
            {
                CityDistrict d = Districts[i];
                if (d == null)
                    continue;

                d.FinalizeTick(dt, snapImmediate);
                weightedPollution += d.CurrentPollution * d.CityWeight;
                weightedSatisfaction += d.CurrentSatisfaction * d.CityWeight;
                totalWeight += d.CityWeight;
            }

            if (totalWeight > 0f)
            {
                CityPollution = Mathf.Clamp01(weightedPollution / totalWeight);
                CitySatisfaction = Mathf.Clamp01(weightedSatisfaction / totalWeight);
            }
            else
            {
                CityPollution = 0f;
                CitySatisfaction = 1f;
            }

            if (PollutionController.Active != null)
                PollutionController.Active.SetPollution(CityPollution, instant: snapImmediate);

            UpdatePlayerSmogExposure();
            Updated?.Invoke(this);
        }

        void UpdatePlayerSmogExposure()
        {
            PlayerController player = PlayerContext.Active;
            if (player == null)
            {
                PlayerSmogExposure = 0f;
                IsPlayerSlowedBySmog = false;
                IsPlayerSmogProtected = false;
                PlayerDistrict = null;
                return;
            }

            Vector3 pos = player.transform.position;
            PlayerDistrict = FindDistrictForPosition(pos);
            PlayerSmogExposure = PollutionController.Active != null
                ? PollutionController.Active.SampleAt(pos)
                : (PlayerDistrict != null ? PlayerDistrict.CurrentPollution : CityPollution);

            PlayerModifiers mods = player.Modifiers;
            if (mods == null)
                return;

            IsPlayerSmogProtected = mods.HasFlag(PlayerModifierFlag.SmogImmune);
            if (!IsPlayerSmogProtected && PlayerSmogExposure >= smogSlowThreshold)
            {
                float t = Mathf.InverseLerp(smogSlowThreshold, 1f, PlayerSmogExposure);
                float speedMult = Mathf.Lerp(0.94f, maxSmogSpeedMultiplier, t);
                mods.SetMultiplier(SmogModifierSourceId, PlayerStat.MoveSpeedMultiplier, speedMult);
                IsPlayerSlowedBySmog = true;
            }
            else
            {
                mods.RemoveAllFromSource(SmogModifierSourceId);
                IsPlayerSlowedBySmog = false;
            }
        }
    }
}
