using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Crowd;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Cause #1: Walking Citizens.
    /// Coordinates real walking <see cref="CrowdAgent"/>s (under a CrowdSpawner or assigned roots)
    /// without running dozens of per-NPC Update loops.
    /// When a citizen wants to discard a cup/wrapper/can:
    /// - If a clean <see cref="OverflowingBinSource"/> is nearby and local Resident Satisfaction is decent,
    ///   they responsibly deposit it into that bin (filling the bin realistically!).
    /// - Otherwise, they visibly toss a physical <see cref="TrashItem"/> onto the sidewalk!
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Ecosystem/Citizen Litter Source")]
    public sealed class CitizenLitterSource : PollutionSourceBase
    {
        [Header("Citizen Tracking")]
        [Tooltip("Parent transform containing walking CrowdAgents (e.g. a CrowdSpawner).")]
        [SerializeField] Transform crowdContainer;

        [Tooltip("Seconds between citizen litter/bin-use checks (scales inversely with Resident Satisfaction).")]
        [SerializeField, Min(1f)] float baseDecisionInterval = 6.5f;

        [Tooltip("How close a clean street bin must be for a citizen to use it instead of littering.")]
        [SerializeField, Min(1f)] float binSearchRadius = 9.5f;

        [Tooltip("How much a single responsible citizen deposit fills a nearby street bin (0..1).")]
        [SerializeField, Range(0.02f, 0.35f)] float binDepositAmount = 0.14f;

        [Tooltip("Above this Resident Satisfaction level, citizens strongly prefer using bins and rarely litter.")]
        [SerializeField, Range(0.5f, 0.98f)] float civicPrideThreshold = 0.82f;

        readonly List<CrowdAgent> cachedAgents = new List<CrowdAgent>(32);
        float decisionTimer = 3f;
        int responsibleBinDeposits;

        public override WasteSourceKind SourceKind => WasteSourceKind.Citizen;
        public override bool IsMitigated => district != null && district.CurrentSatisfaction >= civicPrideThreshold;
        public int ResponsibleBinDeposits => responsibleBinDeposits;

        public void Configure(string id, string label, Transform container, CityDistrict targetDistrict, WasteCatalog catalog, int maxTrash = 12, float interval = 6.5f)
        {
            sourceId = id;
            displayName = label;
            crowdContainer = container;
            district = targetDistrict;
            catalogOverride = catalog;
            maxActiveTrash = Mathf.Clamp(maxTrash, 1, 30);
            baseDecisionInterval = Mathf.Max(1.5f, interval);
        }

        void Start()
        {
            RefreshAgentCache();
            decisionTimer = Random.Range(2f, baseDecisionInterval * 0.7f);
        }

        public void RefreshAgentCache()
        {
            cachedAgents.Clear();
            Transform searchRoot = crowdContainer != null ? crowdContainer : transform;
            CrowdAgent[] found = searchRoot.GetComponentsInChildren<CrowdAgent>(true);
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null)
                    cachedAgents.Add(found[i]);
            }
        }

        void Update()
        {
            if (cachedAgents.Count == 0 && crowdContainer != null)
                RefreshAgentCache();

            if (cachedAgents.Count == 0)
                return;

            float rateMultiplier = district != null ? district.LitterRateMultiplier : 1f;
            decisionTimer -= Time.deltaTime * Mathf.Max(0.2f, rateMultiplier);
            if (decisionTimer > 0f)
                return;

            decisionTimer = baseDecisionInterval * Random.Range(0.75f, 1.3f);
            PerformCitizenDecision();
        }

        void PerformCitizenDecision()
        {
            // Pick a live, active walker from the crowd
            CrowdAgent chosen = PickActiveWalker();
            if (chosen == null)
                return;

            Vector3 citizenPos = chosen.transform.position;
            float satisfaction = district != null ? district.CurrentSatisfaction : 0.4f;

            // Check if a clean street bin is nearby
            OverflowingBinSource cleanBin = CityEcosystemManager.FindNearestCleanBin(citizenPos, binSearchRadius);
            if (cleanBin != null)
            {
                // Higher satisfaction means citizens walk the extra steps to use the clean bin!
                float useBinChance = Mathf.Lerp(0.35f, 0.96f, satisfaction);
                if (Random.value <= useBinChance)
                {
                    cleanBin.AddCitizenDeposit(binDepositAmount);
                    responsibleBinDeposits++;
                    return;
                }
            }

            // If satisfaction is very high and no trash needed, citizens hold onto their wrapper
            if (satisfaction >= civicPrideThreshold && Random.value < 0.85f)
                return;

            if (!CanEmitTrash())
                return;

            // Toss a real physical piece of litter from the walking citizen's hand
            Vector3 forward = chosen.transform.forward;
            Vector3 side = chosen.transform.right * (Random.value < 0.5f ? -1f : 1f);
            Vector3 spawnPos = citizenPos + Vector3.up * 1.05f + side * 0.42f + forward * 0.25f;
            Vector3 tossVel = (side * Random.Range(0.7f, 1.6f)) + (forward * Random.Range(0.3f, 1.1f)) + Vector3.up * Random.Range(0.8f, 1.5f);

            EmitTrash(spawnPos, tossVel);
        }

        CrowdAgent PickActiveWalker()
        {
            int count = cachedAgents.Count;
            if (count == 0)
                return null;

            int start = Random.Range(0, count);
            for (int i = 0; i < count; i++)
            {
                CrowdAgent agent = cachedAgents[(start + i) % count];
                if (agent == null || !agent.gameObject.activeInHierarchy)
                    continue;
                if (district != null && !district.ContainsPoint(agent.transform.position))
                    continue;
                return agent;
            }
            return null;
        }
    }
}
