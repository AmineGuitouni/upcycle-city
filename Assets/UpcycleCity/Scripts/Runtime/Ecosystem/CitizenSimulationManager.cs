using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Crowd;
using UpcycleCity.Player;
using UpcycleCity.Pollution;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Scalable 2-Tier Hybrid LOD Citizen Simulation implementing <see cref="IPollutionSource"/>:
    /// <list type="bullet">
    /// <item><b>Tier 1 (Near-Player Visible Citizens):</b> Real animated <see cref="CrowdAgent"/>s with
    /// distinct personalities (<see cref="CitizenPersonality"/>), visible pausing at shops/fountains/benches,
    /// smog reactions, turning to deposit waste in clean street bins, and visibly tossing physical
    /// <see cref="TrashItem"/>s when dissatisfied or when bins are overflowing.</item>
    /// <item><b>Tier 2 (Large-Scale Background Batch Population):</b> Flat struct array processing
    /// hundreds/thousands of distant residents in tiny time-sliced batches (&lt;0.1 ms on mobile, zero GC,
    /// zero GameObjects). Distant citizens still genuinely fill distant street bins and accumulate
    /// district litter load that seamlessly materializes into physical trash when the player approaches!</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Ecosystem/Citizen Simulation Manager")]
    public sealed class CitizenSimulationManager : PollutionSourceBase
    {
        [Header("Tier 1 · Visible Active Citizens")]
        [Tooltip("Parent transforms containing spawned CrowdAgents (e.g. CrowdSpawners).")]
        [SerializeField] Transform[] crowdContainers;

        [Tooltip("Citizens within this radius of the player run full visible personality actions & animations.")]
        [SerializeField, Min(10f)] float activeVisualRadius = 34f;

        [Tooltip("Seconds between Tier-1 behavior AI updates (5 Hz is ultra-light and responsive).")]
        [SerializeField, Min(0.05f)] float visibleTickInterval = 0.2f;

        [Header("Tier 2 · Background Batch Population")]
        [Tooltip("Number of distant/off-screen city residents simulated purely in flat batched structs.")]
        [SerializeField, Range(0, 2000)] int backgroundPopulationCount = 240;

        [Tooltip("How many background citizen structs to process per time-sliced batch tick.")]
        [SerializeField, Range(8, 256)] int batchSliceSize = 48;

        [Tooltip("Seconds between Tier-2 background batch ticks.")]
        [SerializeField, Min(0.05f)] float backgroundTickInterval = 0.25f;

        [Tooltip("Distance at which a district's accumulated background litter smoothly materializes as real TrashItems.")]
        [SerializeField, Min(8f)] float materializationRadius = 30f;

        [Header("Action Balance")]
        [SerializeField, Range(0.02f, 0.30f)] float visibleBinDepositAmount = 0.14f;
        [SerializeField, Range(0.005f, 0.08f)] float backgroundBinDepositAmount = 0.022f;
        [SerializeField, Range(0.5f, 0.98f)] float civicPrideThreshold = 0.80f;

        sealed class VisibleCitizen
        {
            public CrowdAgent Agent;
            public CitizenPersonality Personality;
            public CitizenActionState State;
            public float StateTimer;
            public float ActionCooldown;
            public bool IsNearPlayer;
            public bool ActionExecuted;
            public OverflowingBinSource TargetBin;
            public CitizenPointOfInterest TargetPoi;
        }

        struct BackgroundCitizenRecord
        {
            public int DistrictIndex;
            public CitizenPersonality Personality;
            public float DecisionTimer;
            public float Mood;
        }

        readonly List<VisibleCitizen> visibleCitizens = new List<VisibleCitizen>(32);
        readonly HashSet<CrowdAgent> knownAgents = new HashSet<CrowdAgent>();
        readonly List<CitizenReactionCue> activeCues = new List<CitizenReactionCue>(8);

        BackgroundCitizenRecord[] backgroundPopulation = System.Array.Empty<BackgroundCitizenRecord>();
        int backgroundCursor;

        float visibleTickTimer;
        float backgroundTickTimer;
        float agentScanTimer;
        float materializeTimer;

        int activeTier1Count;
        int demotedVisibleCount;
        int visibleBinDeposits;
        int backgroundBinDeposits;
        int backgroundLitterEvents;
        int materializedTrashCount;

        public static CitizenSimulationManager Active { get; private set; }

        public override WasteSourceKind SourceKind => WasteSourceKind.Citizen;
        public override bool IsMitigated => CityEcosystemManager.Active != null && CityEcosystemManager.Active.CitySatisfaction >= civicPrideThreshold;

        public int ActiveTier1Count => activeTier1Count;
        public int TotalVisibleAgents => visibleCitizens.Count;
        public int BackgroundPopulationCount => backgroundPopulation.Length + demotedVisibleCount;
        public int TotalSimulatedPopulation => visibleCitizens.Count + backgroundPopulation.Length;
        public int VisibleBinDeposits => visibleBinDeposits;
        public int BackgroundBinDeposits => backgroundBinDeposits;
        public int BackgroundLitterEvents => backgroundLitterEvents;
        public int MaterializedTrashCount => materializedTrashCount;
        public IReadOnlyList<CitizenReactionCue> ActiveCues => activeCues;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
        }

        public void Configure(
            string id,
            string label,
            Transform[] containers,
            CityDistrict primaryDistrict,
            WasteCatalog catalog,
            int bgPopulation = 240,
            int maxPhysicalTrash = 18)
        {
            sourceId = id;
            displayName = label;
            crowdContainers = containers;
            district = primaryDistrict;
            catalogOverride = catalog;
            backgroundPopulationCount = Mathf.Clamp(bgPopulation, 0, 2000);
            maxActiveTrash = Mathf.Clamp(maxPhysicalTrash, 4, 30);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Active = this;
        }

        protected override void OnDisable()
        {
            if (Active == this)
                Active = null;
            base.OnDisable();
        }

        void Start()
        {
            InitializeBackgroundPopulation();
            ScanForCrowdAgents();
        }

        void Update()
        {
            float dt = Time.deltaTime;

            // Decay active floating reaction cues
            for (int i = activeCues.Count - 1; i >= 0; i--)
            {
                CitizenReactionCue cue = activeCues[i];
                cue.Remaining -= dt;
                if (cue.Remaining <= 0f || cue.Anchor == null)
                    activeCues.RemoveAt(i);
                else
                    activeCues[i] = cue;
            }

            // Pick up any newly spawned CrowdAgents (CrowdSpawner runs in Start)
            agentScanTimer -= dt;
            if (agentScanTimer <= 0f)
            {
                agentScanTimer = 1.5f;
                ScanForCrowdAgents();
            }

            // Tier 1: Visible near-player personality & activity simulation
            visibleTickTimer += dt;
            if (visibleTickTimer >= visibleTickInterval)
            {
                float step = visibleTickTimer;
                visibleTickTimer = 0f;
                TickTier1VisibleCitizens(step);
            }

            // Tier 2: Time-sliced batch background population simulation
            backgroundTickTimer += dt;
            if (backgroundTickTimer >= backgroundTickInterval)
            {
                float step = backgroundTickTimer;
                backgroundTickTimer = 0f;
                TickTier2BackgroundBatch(step);
            }

            // Seamless materialization of off-screen background litter when player nears a district
            materializeTimer -= dt;
            if (materializeTimer <= 0f)
            {
                materializeTimer = 0.85f;
                MaterializeNearbyBackgroundLitter();
            }
        }

        void InitializeBackgroundPopulation()
        {
            int count = Mathf.Clamp(backgroundPopulationCount, 0, 2000);
            backgroundPopulation = new BackgroundCitizenRecord[count];

            int districtCount = CityEcosystemManager.Active != null
                ? Mathf.Max(1, CityEcosystemManager.Active.AllDistricts.Count)
                : 1;

            for (int i = 0; i < count; i++)
            {
                backgroundPopulation[i] = new BackgroundCitizenRecord
                {
                    DistrictIndex = i % districtCount,
                    Personality = PickWeightedPersonality(i),
                    DecisionTimer = Random.Range(1.5f, 18f),
                    Mood = 0.5f,
                };
            }
        }

        void ScanForCrowdAgents()
        {
            if (crowdContainers != null && crowdContainers.Length > 0)
            {
                for (int c = 0; c < crowdContainers.Length; c++)
                {
                    if (crowdContainers[c] == null)
                        continue;
                    CrowdAgent[] agents = crowdContainers[c].GetComponentsInChildren<CrowdAgent>(true);
                    RegisterAgents(agents);
                }
            }
            else
            {
                CrowdAgent[] agents = FindObjectsByType<CrowdAgent>();
                RegisterAgents(agents);
            }
        }

        void RegisterAgents(CrowdAgent[] agents)
        {
            for (int i = 0; i < agents.Length; i++)
            {
                CrowdAgent agent = agents[i];
                if (agent == null || !knownAgents.Add(agent))
                    continue;

                CitizenPersonality personality = PickWeightedPersonality(visibleCitizens.Count + 7);
                CitizenPersonalityProfile profile = CitizenPersonalityProfile.For(personality);

                var vc = new VisibleCitizen
                {
                    Agent = agent,
                    Personality = personality,
                    State = CitizenActionState.Walking,
                    StateTimer = 0f,
                    ActionCooldown = Random.Range(2.5f, 8.5f),
                    IsNearPlayer = true,
                };
                visibleCitizens.Add(vc);

                // Apply personality walk speed right away
                CrowdSystem.Active?.SetAgentBehavior(agent, CrowdActivity.Walk, paused: false, speedMultiplier: profile.WalkSpeedMultiplier);
            }
        }

        // ------------------------------------------------------------ TIER 1 (VISIBLE FULL SIMULATION)

        void TickTier1VisibleCitizens(float dt)
        {
            Vector3 playerPos = PlayerContext.HasActivePlayer
                ? PlayerContext.Active.transform.position
                : (Camera.main != null ? Camera.main.transform.position : transform.position);

            float activeSqrRadius = activeVisualRadius * activeVisualRadius;
            activeTier1Count = 0;
            demotedVisibleCount = 0;

            for (int i = visibleCitizens.Count - 1; i >= 0; i--)
            {
                VisibleCitizen vc = visibleCitizens[i];
                if (vc.Agent == null)
                {
                    visibleCitizens.RemoveAt(i);
                    continue;
                }

                if (!vc.Agent.gameObject.activeInHierarchy)
                    continue;

                Vector3 pos = vc.Agent.transform.position;
                Vector3 delta = pos - playerPos;
                float sqrDist = delta.x * delta.x + delta.z * delta.z;
                bool isNear = sqrDist <= activeSqrRadius;

                if (!isNear)
                {
                    // Demote distant CrowdAgent to cheap background batch simulation
                    if (vc.IsNearPlayer && vc.State != CitizenActionState.Walking)
                    {
                        vc.State = CitizenActionState.Walking;
                        vc.TargetBin = null;
                        vc.TargetPoi = null;
                        CitizenPersonalityProfile p = CitizenPersonalityProfile.For(vc.Personality);
                        CrowdSystem.Active?.SetAgentBehavior(vc.Agent, CrowdActivity.Walk, paused: false, speedMultiplier: p.WalkSpeedMultiplier);
                    }
                    vc.IsNearPlayer = false;
                    demotedVisibleCount++;

                    // Still ticks at low cost so distant agents contribute to their district!
                    vc.ActionCooldown -= dt * 0.5f;
                    if (vc.ActionCooldown <= 0f)
                    {
                        vc.ActionCooldown = Random.Range(10f, 18f);
                        CityDistrict d = CityEcosystemManager.FindDistrictForPosition(pos);
                        ExecuteBackgroundEffectForDistrict(d, vc.Personality, isDemotedAgent: true);
                    }
                    continue;
                }

                vc.IsNearPlayer = true;
                activeTier1Count++;
                StepVisibleCitizen(vc, pos, sqrDist, dt);
            }
        }

        void StepVisibleCitizen(VisibleCitizen vc, Vector3 pos, float sqrDistToPlayer, float dt)
        {
            CitizenPersonalityProfile profile = CitizenPersonalityProfile.For(vc.Personality);

            if (vc.State != CitizenActionState.Walking)
            {
                vc.StateTimer -= dt;

                // Execute physical world effect midway through the visible pause
                if (!vc.ActionExecuted && vc.StateTimer <= 0.85f)
                {
                    vc.ActionExecuted = true;
                    ExecuteMidActionEffect(vc, pos, sqrDistToPlayer);
                }

                if (vc.StateTimer <= 0f)
                {
                    vc.State = CitizenActionState.Walking;
                    vc.TargetBin = null;
                    vc.TargetPoi = null;
                    vc.ActionExecuted = false;
                    vc.ActionCooldown = Random.Range(5.5f, 12.0f);

                    float localPol = PollutionController.Active != null ? PollutionController.Active.SampleAt(pos) : 0f;
                    float smogSlow = localPol > 0.65f ? Mathf.Lerp(1f, 0.82f, profile.SmogSensitivity) : 1f;
                    CrowdSystem.Active?.SetAgentBehavior(vc.Agent, CrowdActivity.Walk, paused: false, speedMultiplier: profile.WalkSpeedMultiplier * smogSlow);
                }
                return;
            }

            vc.ActionCooldown -= dt;
            if (vc.ActionCooldown > 0f)
                return;

            ChooseVisibleActivity(vc, pos, sqrDistToPlayer, profile);
        }

        void ChooseVisibleActivity(VisibleCitizen vc, Vector3 pos, float sqrDistToPlayer, in CitizenPersonalityProfile profile)
        {
            CityDistrict localDistrict = CityEcosystemManager.FindDistrictForPosition(pos);
            float satisfaction = localDistrict != null ? localDistrict.CurrentSatisfaction : 0.5f;
            float localPollution = PollutionController.Active != null
                ? PollutionController.Active.SampleAt(pos)
                : (localDistrict != null ? localDistrict.CurrentPollution : 0.4f);

            // 1. Smog reaction when passing through heavy pollution
            if (localPollution >= 0.66f && Random.value < 0.28f * profile.SmogSensitivity)
            {
                BeginVisibleAction(vc, CitizenActionState.ReactingToSmog, CrowdActivity.Idle, Random.Range(1.4f, 2.2f), null);
                TryPushCue(vc.Agent.transform, "*Cough* Thick smog!", new Color(0.86f, 0.42f, 0.18f), sqrDistToPlayer);
                return;
            }

            // 2. Waste disposal decision (either use a clean nearby bin OR visibly litter!)
            float wasteDecisionChance = Mathf.Lerp(0.55f, 0.26f, satisfaction);
            if (Random.value < wasteDecisionChance)
            {
                OverflowingBinSource cleanBin = CityEcosystemManager.FindNearestCleanBin(pos, profile.BinSearchRadius);
                float useBinChance = Mathf.Clamp01(profile.BaseBinPreference * Mathf.Lerp(0.65f, 1.25f, satisfaction));

                if (cleanBin != null && Random.value <= useBinChance)
                {
                    vc.TargetBin = cleanBin;
                    BeginVisibleAction(vc, CitizenActionState.UsingBin, CrowdActivity.Idle, Random.Range(1.4f, 2.0f), cleanBin.WorldPosition);
                    return;
                }

                // Decide whether to litter based on personality + satisfaction
                float litterProb = profile.BaseLitterChance * Mathf.Lerp(1.35f, 0.12f, satisfaction);
                if (satisfaction >= civicPrideThreshold)
                    litterProb *= 0.2f;

                if (Random.value < litterProb && CanEmitTrash())
                {
                    BeginVisibleAction(vc, CitizenActionState.Littering, CrowdActivity.Idle, Random.Range(1.2f, 1.7f), null);
                    return;
                }
            }

            // 3. Pause at a real nearby landmark (shop window, fountain, bench, cafe)
            CitizenPointOfInterest poi = CitizenPointOfInterest.FindNearest(pos, 4.8f);
            if (poi != null && Random.value < profile.LandmarkPauseChance)
            {
                vc.TargetPoi = poi;
                CrowdActivity anim = poi.Kind == CitizenPoiKind.Bench && Random.value < 0.45f
                    ? CrowdActivity.Sit
                    : CrowdActivity.Idle;
                CitizenActionState state = anim == CrowdActivity.Sit ? CitizenActionState.Resting : CitizenActionState.VisitingLandmark;
                BeginVisibleAction(vc, state, anim, Random.Range(2.2f, 4.2f), poi.Position);

                string cueText = BuildLandmarkCue(poi, localPollution);
                Color cueColor = localPollution < 0.35f
                    ? new Color(0.20f, 0.72f, 0.44f)
                    : new Color(0.76f, 0.78f, 0.82f);
                TryPushCue(vc.Agent.transform, cueText, cueColor, sqrDistToPlayer);
                return;
            }

            // 4. Celebrate clean neighborhood if satisfaction is high
            if (localPollution <= 0.25f && vc.Personality == CitizenPersonality.EcoConscious && Random.value < 0.25f)
            {
                BeginVisibleAction(vc, CitizenActionState.CelebratingClean, CrowdActivity.Idle, Random.Range(1.5f, 2.2f), null);
                TryPushCue(vc.Agent.transform, "So clean here!", new Color(0.18f, 0.78f, 0.46f), sqrDistToPlayer);
                return;
            }

            // Keep walking smoothly
            vc.ActionCooldown = Random.Range(3.5f, 7.5f);
        }

        void BeginVisibleAction(VisibleCitizen vc, CitizenActionState state, CrowdActivity anim, float duration, Vector3? lookAt)
        {
            vc.State = state;
            vc.StateTimer = duration;
            vc.ActionExecuted = false;
            CrowdSystem.Active?.SetAgentBehavior(vc.Agent, anim, paused: true, speedMultiplier: 1f, lookAtWorldPos: lookAt);
        }

        void ExecuteMidActionEffect(VisibleCitizen vc, Vector3 pos, float sqrDistToPlayer)
        {
            if (vc.State == CitizenActionState.UsingBin)
            {
                if (vc.TargetBin != null && vc.TargetBin.isActiveAndEnabled)
                {
                    vc.TargetBin.AddCitizenDeposit(visibleBinDepositAmount);
                    visibleBinDeposits++;
                    TryPushCue(vc.Agent.transform, "Used Bin!", new Color(0.18f, 0.74f, 0.45f), sqrDistToPlayer);
                }
            }
            else if (vc.State == CitizenActionState.Littering)
            {
                Vector3 forward = vc.Agent.transform.forward;
                Vector3 side = vc.Agent.transform.right * (Random.value < 0.5f ? -1f : 1f);
                Vector3 spawnPos = pos + Vector3.up * 1.05f + side * 0.40f + forward * 0.30f;
                Vector3 tossVel = (side * Random.Range(0.7f, 1.5f)) + (forward * Random.Range(0.5f, 1.3f)) + Vector3.up * Random.Range(0.9f, 1.6f);

                TrashItem item = EmitTrash(spawnPos, tossVel);
                if (item != null)
                    TryPushCue(vc.Agent.transform, $"Littered ({item.DisplayName})", new Color(0.90f, 0.34f, 0.22f), sqrDistToPlayer);
            }
        }

        // ------------------------------------------------------------ TIER 2 (TIME-SLICED BACKGROUND BATCH)

        void TickTier2BackgroundBatch(float dt)
        {
            int total = backgroundPopulation.Length;
            if (total == 0 || CityEcosystemManager.Active == null)
                return;

            IReadOnlyList<CityDistrict> districts = CityEcosystemManager.Active.AllDistricts;
            int districtCount = districts.Count;
            if (districtCount == 0)
                return;

            int slice = Mathf.Min(batchSliceSize, total);
            // Scale timer advancement so each record advances in real time across the full batch cycle
            float effectiveDt = dt * ((float)total / slice);

            for (int i = 0; i < slice; i++)
            {
                int idx = backgroundCursor;
                backgroundCursor = (backgroundCursor + 1) % total;

                BackgroundCitizenRecord rec = backgroundPopulation[idx];
                rec.DecisionTimer -= effectiveDt;
                if (rec.DecisionTimer > 0f)
                {
                    backgroundPopulation[idx] = rec;
                    continue;
                }

                // Periodically commute between districts
                if (districtCount > 1 && Random.value < 0.15f)
                    rec.DistrictIndex = (rec.DistrictIndex + 1) % districtCount;

                CityDistrict d = districts[rec.DistrictIndex % districtCount];
                rec.DecisionTimer = Random.Range(14f, 28f);

                if (d != null)
                {
                    rec.Mood = Mathf.Lerp(rec.Mood, d.CurrentSatisfaction, 0.35f);
                    ExecuteBackgroundEffectForDistrict(d, rec.Personality, isDemotedAgent: false);
                }

                backgroundPopulation[idx] = rec;
            }
        }

        void ExecuteBackgroundEffectForDistrict(CityDistrict d, CitizenPersonality personality, bool isDemotedAgent)
        {
            if (d == null)
                return;

            CitizenPersonalityProfile profile = CitizenPersonalityProfile.For(personality);
            float satisfaction = d.CurrentSatisfaction;

            // Check if this district has any clean street bin
            OverflowingBinSource cleanBin = FindCleanBinInDistrict(d);
            float useBinChance = Mathf.Clamp01(profile.BaseBinPreference * Mathf.Lerp(0.60f, 1.25f, satisfaction));

            if (cleanBin != null && Random.value <= useBinChance)
            {
                cleanBin.AddCitizenDeposit(isDemotedAgent ? visibleBinDepositAmount * 0.6f : backgroundBinDepositAmount);
                backgroundBinDeposits++;
                return;
            }

            // Off-screen littering still genuinely raises district pollution!
            float litterProb = profile.BaseLitterChance * Mathf.Lerp(1.25f, 0.10f, satisfaction) * 0.18f;
            if (satisfaction >= civicPrideThreshold)
                litterProb *= 0.2f;

            if (Random.value < litterProb)
            {
                d.AddBackgroundLitter(weight: 0.9f, maxPending: 8);
                backgroundLitterEvents++;
            }
        }

        void MaterializeNearbyBackgroundLitter()
        {
            if (CityEcosystemManager.Active == null || !CanEmitTrash())
                return;

            Vector3 playerPos = PlayerContext.HasActivePlayer
                ? PlayerContext.Active.transform.position
                : (Camera.main != null ? Camera.main.transform.position : transform.position);

            float sqrMatRadius = materializationRadius * materializationRadius;
            IReadOnlyList<CityDistrict> districts = CityEcosystemManager.Active.AllDistricts;

            for (int i = 0; i < districts.Count; i++)
            {
                CityDistrict d = districts[i];
                if (d == null || d.PendingBackgroundTrash <= 0)
                    continue;

                if (d.FlatSqrDistance(playerPos) > sqrMatRadius)
                    continue;

                // Materialize 1 physical item per tick smoothly so there's zero frame spike
                if (d.TryConsumeBackgroundLitter(out _))
                {
                    Vector2 offset = Random.insideUnitCircle * Mathf.Min(8f, d.Radius * 0.45f);
                    Vector3 spawnPos = d.transform.position + new Vector3(offset.x, 0.55f, offset.y);
                    TrashItem spawned = EmitTrash(spawnPos, Vector3.zero);
                    if (spawned != null)
                        materializedTrashCount++;
                    break;
                }
            }
        }

        static OverflowingBinSource FindCleanBinInDistrict(CityDistrict d)
        {
            IReadOnlyList<IPollutionSource> sources = d.Sources;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] is OverflowingBinSource bin && bin.isActiveAndEnabled && !bin.IsOverflowing)
                    return bin;
            }
            return null;
        }

        void TryPushCue(Transform anchor, string text, Color color, float sqrDistToPlayer)
        {
            // Only show subtle world cues for very close visible citizens (within 16m, max 4 at once)
            if (anchor == null || sqrDistToPlayer > 16f * 16f)
                return;

            for (int i = 0; i < activeCues.Count; i++)
            {
                if (activeCues[i].Anchor == anchor)
                {
                    activeCues[i] = new CitizenReactionCue { Anchor = anchor, Text = text, AccentColor = color, Remaining = 2.1f, Duration = 2.1f };
                    return;
                }
            }

            if (activeCues.Count >= 4)
                activeCues.RemoveAt(0);

            activeCues.Add(new CitizenReactionCue
            {
                Anchor = anchor,
                Text = text,
                AccentColor = color,
                Remaining = 2.1f,
                Duration = 2.1f,
            });
        }

        static string BuildLandmarkCue(CitizenPointOfInterest poi, float localPollution)
        {
            switch (poi.Kind)
            {
                case CitizenPoiKind.Fountain:
                    return localPollution < 0.40f ? "Love the fountain!" : "Fountain's dry...";
                case CitizenPoiKind.Bench:
                    return localPollution < 0.45f ? "Taking a breather" : "Too dusty here";
                case CitizenPoiKind.Cafe:
                    return "Grabbing coffee";
                default:
                    return $"Checking {poi.Label}";
            }
        }

        static CitizenPersonality PickWeightedPersonality(int seedIndex)
        {
            int bucket = Mathf.Abs(seedIndex * 37 + 13) % 10;
            if (bucket < 3) return CitizenPersonality.EcoConscious;   // 30%
            if (bucket < 6) return CitizenPersonality.CasualStroller; // 30%
            if (bucket < 8) return CitizenPersonality.RushedCommuter; // 20%
            return CitizenPersonality.CarelessLitterer;               // 20%
        }
    }
}
