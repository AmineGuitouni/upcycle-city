using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Cause #2: Overflowing Street / Shop Bins.
    /// Fills over time from foot traffic &amp; citizen deposits. Once full (fillLevel >= 1),
    /// it visibly overflows by spilling physical TrashItems onto the sidewalk and adding stench/grime load.
    /// The player can service/empty it with [E] (<see cref="IInteractable"/>) to earn coins,
    /// stop the overflow, and boost local Resident Satisfaction.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Ecosystem/Overflowing Bin Source")]
    public sealed class OverflowingBinSource : PollutionSourceBase, IInteractable
    {
        [Header("Bin Fill & Overflow")]
        [SerializeField, Range(0f, 1f)] float fillLevel = 0.72f;

        [Tooltip("Passive fill per second from nearby shop/pedestrian foot traffic.")]
        [SerializeField, Min(0f)] float passiveFillPerSecond = 0.012f;

        [Tooltip("Seconds between physical trash spills while the bin is overflowing.")]
        [SerializeField, Min(1.5f)] float spillInterval = 7.5f;

        [Tooltip("Continuous pollution load contributed while overflowing (stench & grime).")]
        [SerializeField, Min(0f)] float overflowAirLoad = 3.2f;

        [Tooltip("Coins awarded to the player for emptying a full or overflowing street bin.")]
        [SerializeField, Min(1)] int serviceCoinReward = 14;

        [Header("Visuals & Spawn")]
        [SerializeField] Transform spillPoint;
        [SerializeField] Renderer fillIndicatorRenderer;
        [SerializeField] Transform fillMeterBar;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        MaterialPropertyBlock indicatorBlock;
        float spillTimer;

        public override WasteSourceKind SourceKind => WasteSourceKind.ShopOverflow;
        public float FillLevel => fillLevel;
        public bool IsOverflowing => fillLevel >= 0.99f;
        public override bool IsMitigated => !IsOverflowing;
        public override float AirPollutionLoad => IsOverflowing ? overflowAirLoad : 0f;

        public Transform Transform => transform;

        public string InteractionPrompt
        {
            get
            {
                int pct = Mathf.RoundToInt(fillLevel * 100f);
                if (IsOverflowing)
                    return $"[E] Empty OVERFLOWING {DisplayName} (+{serviceCoinReward} Coins)";
                if (fillLevel >= 0.15f)
                    return $"[E] Service {DisplayName} ({pct}% Full)";
                return $"{DisplayName} is Clean ({pct}% Full)";
            }
        }

        public void Configure(
            string id,
            string label,
            CityDistrict targetDistrict,
            WasteCatalog catalog,
            float startingFill,
            Transform spillAnchor,
            Renderer indicator,
            Transform meterBar = null)
        {
            sourceId = id;
            displayName = label;
            district = targetDistrict;
            catalogOverride = catalog;
            fillLevel = Mathf.Clamp01(startingFill);
            spillPoint = spillAnchor;
            fillIndicatorRenderer = indicator;
            fillMeterBar = meterBar;
            maxActiveTrash = 10;
        }

        protected override void Awake()
        {
            base.Awake();
            indicatorBlock = new MaterialPropertyBlock();
            if (gameObject.layer == GameLayers.Default)
                GameLayers.SetLayerRecursively(gameObject, GameLayers.Interactable);
            spillTimer = Random.Range(2f, spillInterval * 0.65f);
            RefreshIndicator();
        }

        void Update()
        {
            if (passiveFillPerSecond > 0f && fillLevel < 1f)
            {
                fillLevel = Mathf.Min(1f, fillLevel + passiveFillPerSecond * Time.deltaTime);
                RefreshIndicator();
            }

            if (IsOverflowing)
            {
                spillTimer -= Time.deltaTime;
                if (spillTimer <= 0f)
                {
                    spillTimer = spillInterval * Random.Range(0.85f, 1.25f);
                    SpillTrash();
                }
                RefreshIndicator();
            }
        }

        /// <summary>Called by walking citizens when they responsibly use this street bin.</summary>
        public void AddCitizenDeposit(float amount)
        {
            fillLevel = Mathf.Clamp01(fillLevel + Mathf.Max(0.01f, amount));
            RefreshIndicator();
        }

        void SpillTrash()
        {
            if (!CanEmitTrash())
                return;

            Vector3 origin = spillPoint != null
                ? spillPoint.position
                : transform.position + Vector3.up * 1.05f + transform.forward * 0.45f;

            Vector3 outward = spillPoint != null ? spillPoint.forward : transform.forward;
            Vector3 randomDir = (outward + new Vector3(Random.Range(-0.55f, 0.55f), 0f, Random.Range(-0.55f, 0.55f))).normalized;
            Vector3 vel = randomDir * Random.Range(1.2f, 2.3f) + Vector3.up * Random.Range(1.0f, 1.8f);

            EmitTrash(origin, vel);
        }

        public bool CanInteract(PlayerInteractor interactor)
        {
            return fillLevel >= 0.15f;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor))
                return;

            int payout = Mathf.Max(2, Mathf.RoundToInt(serviceCoinReward * fillLevel));
            bool wasOverflow = IsOverflowing;

            fillLevel = 0f;
            spillTimer = spillInterval;
            RefreshIndicator();

            interactor?.Inventory?.AddCoins(payout);
            district?.AddSatisfactionBoost(wasOverflow ? 0.12f : 0.06f);

            string msg = wasOverflow
                ? $"Cleared overflowing {DisplayName}! +{payout} Coins · Overflow stopped"
                : $"Serviced {DisplayName} · +{payout} Coins";
            Color color = new Color(0.18f, 0.72f, 0.42f);

            interactor?.NotifyFeedback(msg, color);
            PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.Interact, transform.position, 1f, msg, color);
        }

        void RefreshIndicator()
        {
            if (fillMeterBar != null)
            {
                Vector3 s = fillMeterBar.localScale;
                s.y = Mathf.Max(0.06f, fillLevel);
                fillMeterBar.localScale = s;
            }

            if (fillIndicatorRenderer == null || indicatorBlock == null)
                return;

            Color c;
            if (IsOverflowing)
            {
                float pulse = 0.65f + 0.35f * Mathf.Sin(Time.time * 6f);
                c = new Color(0.95f * pulse, 0.18f, 0.14f, 1f);
            }
            else if (fillLevel >= 0.65f)
            {
                c = new Color(0.95f, 0.68f, 0.12f, 1f);
            }
            else
            {
                c = new Color(0.18f, 0.72f, 0.42f, 1f);
            }

            fillIndicatorRenderer.GetPropertyBlock(indicatorBlock);
            indicatorBlock.SetColor(BaseColorId, c);
            indicatorBlock.SetColor(ColorId, c);
            fillIndicatorRenderer.SetPropertyBlock(indicatorBlock);
        }
    }
}
