using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Cause #3: Industrial Factory Smokestack &amp; Scrap Chute.
    /// Emits heavy continuous air pollution (driving local &amp; city smog) plus periodic industrial scrap.
    /// Implements <see cref="IToolTarget"/> AND <see cref="IInteractable"/>:
    /// equip the Wrench (<c>tool_wrench</c>) and press [F] (or [E]) at the Scrubber Control Panel
    /// to install/repair the Scrubber Filter, slashing air emissions by ~85% and watching the sky &amp; grime clear!
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Ecosystem/Factory Smokestack Source")]
    public sealed class FactorySmokestackSource : PollutionSourceBase, IToolTarget, IInteractable, ISyncedStateProvider
    {
        [Header("Emissions")]
        [Tooltip("Air pollution load while the smokestack scrubber is broken/unfiltered.")]
        [SerializeField, Min(0.5f)] float unfilteredAirLoad = 16f;

        [Tooltip("Residual air pollution load after the scrubber filter is repaired.")]
        [SerializeField, Min(0f)] float filteredAirLoad = 1.8f;

        [Tooltip("Seconds between industrial scrap ejections while unfiltered.")]
        [SerializeField, Min(2f)] float scrapInterval = 9.5f;

        [Tooltip("If true, the scrubber slowly degrades over time and needs periodic maintenance.")]
        [SerializeField] bool degradeOverTime;

        [SerializeField, Min(30f)] float filterDurationSeconds = 180f;

        [Header("Repair Tool")]
        [SerializeField] string requiredToolId = "tool_wrench";
        [SerializeField, Min(5)] int repairCoinReward = 35;

        [Header("Visuals & Anchors")]
        [SerializeField] ParticleSystem smokeParticles;
        [SerializeField] Transform scrapChutePoint;
        [SerializeField] Renderer statusBeaconRenderer;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        MaterialPropertyBlock beaconBlock;
        bool scrubberActive;
        float filterRemaining;
        float scrapTimer;
        float baseSmokeRate = 18f;
        string syncKey;

        public override WasteSourceKind SourceKind => WasteSourceKind.FactoryScrap;
        public bool IsScrubberActive => scrubberActive;
        public override bool IsMitigated => scrubberActive;
        public override float AirPollutionLoad => scrubberActive ? FilteredAirLoad : UnfilteredAirLoad;
        public string SyncKey => syncKey ??= SyncedStateRegistry.BuildSpatialKey("factory", SourceId, transform.position);

        // Live values: the city's EcosystemBalance when one is active, else this factory's own settings
        static EcosystemBalance Balance => EcosystemBalance.Active;
        float UnfilteredAirLoad => Balance != null ? Balance.factoryUnfilteredLoad : unfilteredAirLoad;
        float FilteredAirLoad => Balance != null ? Balance.factoryFilteredLoad : filteredAirLoad;
        float ScrapInterval => Balance != null ? Balance.factoryScrapInterval : scrapInterval;
        int RepairCoinReward => Balance != null ? Balance.factoryRepairReward : repairCoinReward;

        public Transform Transform => transform;

        public string InteractionPrompt
        {
            get
            {
                if (scrubberActive)
                    return $"{DisplayName} Scrubber ACTIVE (-88% Emissions)";
                return $"[F with Wrench] Repair {DisplayName} Scrubber (+{RepairCoinReward} Coins)";
            }
        }

        public void Configure(
            string id,
            string label,
            CityDistrict targetDistrict,
            WasteCatalog catalog,
            ParticleSystem smoke,
            Transform chute,
            Renderer beacon,
            float unfilteredLoad = 16f,
            float filteredLoad = 1.8f)
        {
            sourceId = id;
            displayName = label;
            district = targetDistrict;
            catalogOverride = catalog;
            smokeParticles = smoke;
            scrapChutePoint = chute;
            statusBeaconRenderer = beacon;
            unfilteredAirLoad = Mathf.Max(1f, unfilteredLoad);
            filteredAirLoad = Mathf.Max(0f, filteredLoad);
            maxActiveTrash = 10;
        }

        protected override void Awake()
        {
            base.Awake();
            beaconBlock = new MaterialPropertyBlock();
            if (gameObject.layer == GameLayers.Default)
                GameLayers.SetLayerRecursively(gameObject, GameLayers.Interactable);

            if (smokeParticles != null)
                baseSmokeRate = Mathf.Max(4f, smokeParticles.emission.rateOverTimeMultiplier);

            scrapTimer = Random.Range(3f, ScrapInterval * 0.7f);
            RefreshVisuals();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            SyncedStateRegistry.Register(this);
        }

        protected override void OnDisable()
        {
            SyncedStateRegistry.Unregister(this);
            base.OnDisable();
        }

        void Update()
        {
            if (!GameAuthority.IsAuthoritative)
            {
                if (!scrubberActive)
                    RefreshBeaconPulse();
                return;
            }

            if (scrubberActive)
            {
                if (degradeOverTime)
                {
                    filterRemaining -= Time.deltaTime;
                    if (filterRemaining <= 0f)
                    {
                        scrubberActive = false;
                        RefreshVisuals();
                        PlayerContext.ActiveInteractor?.NotifyFeedback(
                            $"{DisplayName} scrubber clogged! Repair with Wrench.",
                            new Color(0.92f, 0.45f, 0.16f));
                    }
                }
                return;
            }

            // Unfiltered factory periodically ejects physical industrial scrap from its chute
            scrapTimer -= Time.deltaTime;
            if (scrapTimer <= 0f)
            {
                scrapTimer = ScrapInterval * Random.Range(0.85f, 1.25f);
                EjectIndustrialScrap();
            }

            RefreshBeaconPulse();
        }

        void EjectIndustrialScrap()
        {
            if (!CanEmitTrash())
                return;

            Vector3 origin = scrapChutePoint != null
                ? scrapChutePoint.position
                : transform.position + transform.forward * 1.4f + Vector3.up * 1.4f;
            Vector3 dir = scrapChutePoint != null ? scrapChutePoint.forward : transform.forward;
            Vector3 vel = (dir + new Vector3(Random.Range(-0.25f, 0.25f), 0f, Random.Range(-0.25f, 0.25f))).normalized * Random.Range(2.0f, 3.4f)
                        + Vector3.up * Random.Range(0.8f, 1.5f);

            EmitTrash(origin, vel);
        }

        public string GetToolPrompt(IInventoryItem equippedItem)
        {
            if (scrubberActive)
                return $"{DisplayName} Scrubber Filtered";
            if (IsMatchingTool(equippedItem))
                return $"[F] Repair {DisplayName} Scrubber with {equippedItem.DisplayName}";
            return $"Requires Wrench — equip Wrench and press [F]";
        }

        public bool CanUseTool(PlayerInteractor interactor, IInventoryItem equippedItem)
        {
            return !scrubberActive && IsMatchingTool(equippedItem);
        }

        public bool UseTool(PlayerInteractor interactor, IInventoryItem equippedItem)
        {
            if (!CanUseTool(interactor, equippedItem))
                return false;

            GameAuthority.Current.RequestRepairFactory(this, interactor);
            return true;
        }

        public bool CanInteract(PlayerInteractor interactor) => !scrubberActive;

        public string RequiredToolId => requiredToolId;

        public void Interact(PlayerInteractor interactor)
        {
            if (scrubberActive)
                return;

            IInventoryItem equipped = interactor?.Inventory?.EquippedItem;
            if (IsMatchingTool(equipped))
            {
                GameAuthority.Current.RequestRepairFactory(this, interactor);
            }
            else
            {
                interactor?.NotifyFeedback(
                    $"Needs Wrench! Pick up & select Wrench, then press [F] or [E].",
                    new Color(0.95f, 0.71f, 0.11f));
            }
        }

        bool IsMatchingTool(IInventoryItem item)
        {
            return item != null && string.Equals(item.ItemId, requiredToolId, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Executes authoritative scrubber repair (invoked directly in solo or on the host in co-op).</summary>
        public bool ExecuteAuthoritativeRepair(
            PlayerInteractor interactor,
            out int payout,
            out string message,
            out Color color,
            bool creditLocalInventory = true,
            bool emitLocalFeedback = true)
        {
            payout = 0;
            message = string.Empty;
            color = new Color(0.18f, 0.78f, 0.45f);

            if (scrubberActive)
                return false;

            scrubberActive = true;
            filterRemaining = filterDurationSeconds;
            RefreshVisuals();

            payout = RepairCoinReward;
            if (creditLocalInventory)
                interactor?.Inventory?.AddCoins(payout);
            district?.AddSatisfactionBoost(0.18f);

            message = $"Scrubber Repaired! {DisplayName} emissions slashed · +{payout} Coins";
            if (emitLocalFeedback)
            {
                interactor?.NotifyFeedback(message, color);
                PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.FactoryRepaired, transform.position, 1f, message, color);
            }
            return true;
        }

        /// <summary>Convenience overload used by <see cref="LocalGameAuthority"/>.</summary>
        public void ExecuteAuthoritativeRepair(PlayerInteractor interactor)
        {
            ExecuteAuthoritativeRepair(interactor, out _, out _, out _, creditLocalInventory: true, emitLocalFeedback: true);
        }

        public SyncedStateRecord CaptureSyncState() => new SyncedStateRecord
        {
            v0 = filterRemaining,
            flags = scrubberActive ? (byte)1 : (byte)0,
        };

        public void ApplySyncState(in SyncedStateRecord state)
        {
            bool nextActive = (state.flags & 1) != 0;
            filterRemaining = Mathf.Max(0f, state.v0);
            if (scrubberActive != nextActive)
            {
                scrubberActive = nextActive;
                RefreshVisuals();
            }
        }

        void RefreshVisuals()
        {
            if (smokeParticles != null)
            {
                ParticleSystem.EmissionModule emission = smokeParticles.emission;
                emission.rateOverTimeMultiplier = scrubberActive ? baseSmokeRate * 0.16f : baseSmokeRate;

                ParticleSystem.MainModule main = smokeParticles.main;
                main.startColor = scrubberActive
                    ? new ParticleSystem.MinMaxGradient(new Color(0.86f, 0.90f, 0.92f, 0.35f))
                    : new ParticleSystem.MinMaxGradient(new Color(0.18f, 0.17f, 0.15f, 0.85f));
            }

            RefreshBeaconPulse();
        }

        void RefreshBeaconPulse()
        {
            if (statusBeaconRenderer == null || beaconBlock == null)
                return;

            Color c = scrubberActive
                ? new Color(0.16f, 0.82f, 0.46f, 1f)
                : new Color(0.96f * (0.6f + 0.4f * Mathf.Sin(Time.time * 5.5f)), 0.18f, 0.12f, 1f);

            statusBeaconRenderer.GetPropertyBlock(beaconBlock);
            beaconBlock.SetColor(BaseColorId, c);
            beaconBlock.SetColor(ColorId, c);
            statusBeaconRenderer.SetPropertyBlock(beaconBlock);
        }
    }
}
