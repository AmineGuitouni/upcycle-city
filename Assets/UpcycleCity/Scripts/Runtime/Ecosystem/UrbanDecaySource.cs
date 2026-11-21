using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Cause #4: Urban Decay / Neglected Alley Dumping.
    /// When the enclosing <see cref="CityDistrict"/> stays dirty above <see cref="decayThreshold"/>,
    /// this neglected lot periodically accumulates bulky dumped items (tyres, broken TVs, old washers, boxes).
    /// Once the player cleans the district below <see cref="decayThreshold"/>, dumping halts automatically!
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Ecosystem/Urban Decay Source")]
    public sealed class UrbanDecaySource : PollutionSourceBase
    {
        [Header("Decay Conditions")]
        [Tooltip("District pollution level above which neglect causes bulky dumping.")]
        [SerializeField, Range(0.15f, 0.85f)] float decayThreshold = 0.42f;

        [Tooltip("Seconds between bulky decay dumps while the district is neglected.")]
        [SerializeField, Min(4f)] float dumpInterval = 18f;

        [Tooltip("Spawn spread radius around this lot's center.")]
        [SerializeField, Min(0.5f)] float dumpRadius = 2.6f;

        [Tooltip("Continuous visual/neglect load contributed while actively decaying.")]
        [SerializeField, Min(0f)] float neglectPollutionLoad = 1.6f;

        float dumpTimer;

        public override WasteSourceKind SourceKind => WasteSourceKind.UrbanDecay;

        public bool IsDecaying => district == null || district.CurrentPollution >= DecayThreshold;

        // Live values: the city's EcosystemBalance when one is active, else this source's own settings
        float DecayThreshold => EcosystemBalance.Active != null ? EcosystemBalance.Active.decayThreshold : decayThreshold;
        float DumpInterval => EcosystemBalance.Active != null ? EcosystemBalance.Active.decayDumpInterval : dumpInterval;
        public override bool IsMitigated => !IsDecaying;
        public override float AirPollutionLoad => IsDecaying ? neglectPollutionLoad : 0f;

        public void Configure(
            string id,
            string label,
            CityDistrict targetDistrict,
            WasteCatalog catalog,
            float threshold = 0.42f,
            float interval = 18f,
            float radius = 2.6f,
            int maxTrash = 6)
        {
            sourceId = id;
            displayName = label;
            district = targetDistrict;
            catalogOverride = catalog;
            decayThreshold = Mathf.Clamp(threshold, 0.15f, 0.85f);
            dumpInterval = Mathf.Max(4f, interval);
            dumpRadius = Mathf.Max(0.5f, radius);
            maxActiveTrash = Mathf.Clamp(maxTrash, 1, 20);
        }

        protected override void Awake()
        {
            base.Awake();
            dumpTimer = Random.Range(6f, DumpInterval * 0.7f);
        }

        void Update()
        {
            if (!IsDecaying)
                return;

            dumpTimer -= Time.deltaTime;
            if (dumpTimer > 0f)
                return;

            dumpTimer = DumpInterval * Random.Range(0.85f, 1.25f);
            DumpBulkyWaste();
        }

        void DumpBulkyWaste()
        {
            if (!CanEmitTrash())
                return;

            Vector2 offset2D = Random.insideUnitCircle * dumpRadius;
            Vector3 spawnPos = transform.position + new Vector3(offset2D.x, 1.15f, offset2D.y);
            Vector3 vel = new Vector3(Random.Range(-0.5f, 0.5f), 0.35f, Random.Range(-0.5f, 0.5f));
            EmitTrash(spawnPos, vel);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = IsDecaying ? new Color(0.82f, 0.36f, 0.18f, 0.65f) : new Color(0.2f, 0.8f, 0.45f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, dumpRadius);
        }
    }
}
