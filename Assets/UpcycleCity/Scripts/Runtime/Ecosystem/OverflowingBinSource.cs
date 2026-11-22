using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;
using UpcycleCity.Waste;
using Random = UnityEngine.Random;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Cause #2: Overflowing Street / Shop Bins.
    /// Fills over time from foot traffic &amp; citizen deposits. Once full (fillLevel >= 1),
    /// it visibly overflows by spilling physical TrashItems onto the sidewalk and adding stench/grime load.
    /// <para>
    /// [E] opens the bin like an inventory (<see cref="BinSearchUI"/>): the player rummages through it and finds turn up
    /// one by one, common things first and rare ones last (<see cref="BinContents"/>, <see cref="BinLootTable"/>). Each
    /// find taken goes into the player's <see cref="RecyclingBag"/> and lowers the fill; emptying the bin completely
    /// pays the team, stops the overflow and boosts local Resident Satisfaction.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Ecosystem/Overflowing Bin Source")]
    public sealed class OverflowingBinSource : PollutionSourceBase, IInteractable, ISyncedStateProvider
    {
        [Header("Bin Fill & Overflow")]
        [SerializeField, Range(0f, 1f)] float fillLevel = 0.72f;

        [Tooltip("Passive fill per second from nearby shop/pedestrian foot traffic.")]
        [SerializeField, Min(0f)] float passiveFillPerSecond = 0.012f;

        [Tooltip("Bin capacity: citizen deposits are scaled by this (lower = a bigger bin that fills more slowly). " +
                 "Lets a street with many bins and walkers keep a demo-friendly pace.")]
        [SerializeField, Range(0.1f, 2f)] float depositScale = 1f;

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
        string syncKey;

        readonly BinContents contents = new BinContents();
        readonly HashSet<ulong> searchers = new HashSet<ulong>();
        System.Random lootRandom;

        /// <summary>What's inside and how far the search has got (replicated).</summary>
        public BinContents Contents => contents;

        /// <summary>Raised when the contents or search state change (host or replicated).</summary>
        public event Action ContentsChanged;

        /// <summary>Shared clock the search runs on (server time in co-op).</summary>
        public static float SearchClock => (float)GameAuthority.Current.ServerTime;

        public override WasteSourceKind SourceKind => WasteSourceKind.ShopOverflow;
        public float FillLevel => fillLevel;
        public bool IsOverflowing => fillLevel >= 0.99f;
        public override bool IsMitigated => !IsOverflowing;
        public override float AirPollutionLoad => IsOverflowing ? OverflowAirLoad : 0f;
        public string SyncKey => syncKey ??= SyncedStateRegistry.BuildSpatialKey("bin", SourceId, transform.position);

        // Live values: the city's EcosystemBalance when one is active, else this bin's own settings
        static EcosystemBalance Balance => EcosystemBalance.Active;
        float PassiveFillPerSecond => Balance != null ? Balance.binPassiveFillPerSecond : passiveFillPerSecond;
        float DepositScale => Balance != null ? Balance.binDepositScale : depositScale;
        float SpillInterval => Balance != null ? Balance.binSpillInterval : spillInterval;
        float OverflowAirLoad => Balance != null ? Balance.binOverflowAirLoad : overflowAirLoad;
        int ServiceCoinReward => Balance != null ? Balance.binServiceReward : serviceCoinReward;

        public Transform Transform => transform;

        public string InteractionPrompt
        {
            get
            {
                if (contents.Count == 0)
                    return $"{DisplayName} is empty";
                return IsOverflowing
                    ? $"[E] Search OVERFLOWING {DisplayName}"
                    : $"[E] Search {DisplayName} ({Mathf.RoundToInt(fillLevel * 100f)}% full)";
            }
        }

        public void Configure(
            string id,
            string label,
            CityDistrict targetDistrict,
            WasteCatalog catalog,
            float startingFill,
            Transform spillAnchor,
            Renderer indicator = null,
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
            spillTimer = Random.Range(2f, SpillInterval * 0.65f);
            RefreshIndicator();
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
                if (IsOverflowing)
                    RefreshIndicator();
                return;
            }

            float passiveFill = PassiveFillPerSecond;
            if (passiveFill > 0f && fillLevel < 1f)
            {
                fillLevel = Mathf.Min(1f, fillLevel + passiveFill * Time.deltaTime);
                RefreshIndicator();
            }
            FillContentsToLevel();

            if (IsOverflowing)
            {
                spillTimer -= Time.deltaTime;
                if (spillTimer <= 0f)
                {
                    spillTimer = SpillInterval * Random.Range(0.85f, 1.25f);
                    SpillTrash();
                }
                RefreshIndicator();
            }
        }

        /// <summary>Called by walking citizens when they responsibly use this street bin.</summary>
        public void AddCitizenDeposit(float amount)
        {
            if (!GameAuthority.IsAuthoritative)
                return;
            fillLevel = Mathf.Clamp01(fillLevel + Mathf.Max(0.01f, amount) * DepositScale);
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
            return contents.Count > 0 && (BinSearchUI.Instance == null || !BinSearchUI.Instance.IsOpen);
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor))
                return;
            BinSearchUI.EnsureInstance().Open(this);
        }

        // ------------------------------------------------------------ searching (host)

        // A new find only appears once the fill has reached a whole slot, so taking one (which drops the fill to
        // exactly what's left) isn't instantly undone by the next tick of passive fill
        static int SlotsFor(float fill, BinLootTable table) =>
            Mathf.Clamp(Mathf.FloorToInt(fill * table.BinSlots + 0.0001f), 0, table.BinSlots);

        // The bin holds one find per slot of fill: as it fills up (foot traffic, citizens) new finds are rolled in
        void FillContentsToLevel()
        {
            BinLootTable table = BinLootTable.Active;
            if (table == null)
                return;
            int target = SlotsFor(fillLevel, table);
            if (contents.Count >= target)
                return;

            lootRandom ??= new System.Random(unchecked((int)SyncedStateRegistry.ComputeKeyHash(SyncKey) ^ Environment.TickCount));
            float now = SearchClock;
            bool changed = false;
            while (contents.Count < target)
            {
                int entry = table.Roll(lootRandom);
                if (entry < 0 || !contents.Add(entry, now, table))
                    break;
                changed = true;
            }
            if (changed)
                ContentsChanged?.Invoke();
        }

        /// <summary>Host: a player started (or stopped) searching this bin; the search runs while anyone is at it.</summary>
        public void AuthoritativeSetSearcher(ulong clientId, bool searching)
        {
            BinLootTable table = BinLootTable.Active;
            if (table == null)
                return;
            bool wasSearching = searchers.Count > 0;
            if (searching) searchers.Add(clientId);
            else searchers.Remove(clientId);
            bool isSearching = searchers.Count > 0;
            if (wasSearching == isSearching)
                return;
            FillContentsToLevel();
            contents.SetSearching(isSearching, SearchClock, table);
            ContentsChanged?.Invoke();
        }

        /// <summary>
        /// Host: takes the find in <paramref name="slot"/> if it has turned up and is still <paramref name="expectedEntry"/>.
        /// Lowers the fill; emptying the bin completely pays the team. Returns the loot entry, or -1.
        /// </summary>
        public int AuthoritativeTake(int slot, int expectedEntry, ulong takerClientId)
        {
            BinLootTable table = BinLootTable.Active;
            if (table == null || slot < 0 || slot >= contents.Count || contents[slot] != expectedEntry)
                return -1;
            int entry = contents.TakeAt(slot, SearchClock, table);
            if (entry < 0)
                return -1;

            bool wasOverflow = IsOverflowing;
            fillLevel = Mathf.Min(fillLevel, contents.Count / (float)table.BinSlots);
            if (contents.Count == 0)
            {
                fillLevel = 0f;
                spillTimer = SpillInterval;
                int payout = Mathf.Max(2, ServiceCoinReward / 2);
                GameAuthority.Current.CreditTeamCoins(payout);
                district?.AddSatisfactionBoost(wasOverflow ? 0.12f : 0.06f);
                string message = $"Emptied {DisplayName}! +{payout} Coins";
                GameAuthority.Current.SendTargetedFeedback(takerClientId, message, new Color(0.18f, 0.72f, 0.42f),
                    PlayerFeedbackEvent.BinServiced, transform.position);
            }
            RefreshIndicator();
            ContentsChanged?.Invoke();
            return entry;
        }

        /// <summary>Executes authoritative bin emptying (invoked directly in solo or on the host in co-op).</summary>
        public bool ExecuteAuthoritativeService(
            PlayerInteractor interactor,
            out int payout,
            out string message,
            out Color color,
            bool creditLocalInventory = true,
            bool emitLocalFeedback = true)
        {
            payout = 0;
            message = string.Empty;
            color = new Color(0.18f, 0.72f, 0.42f);

            if (fillLevel < 0.15f)
                return false;

            payout = Mathf.Max(2, Mathf.RoundToInt(ServiceCoinReward * fillLevel));
            bool wasOverflow = IsOverflowing;

            fillLevel = 0f;
            spillTimer = SpillInterval;
            contents.Clear();
            ContentsChanged?.Invoke();
            RefreshIndicator();

            if (creditLocalInventory)
                interactor?.Inventory?.AddCoins(payout);
            district?.AddSatisfactionBoost(wasOverflow ? 0.12f : 0.06f);

            message = wasOverflow
                ? $"Cleared overflowing {DisplayName}! +{payout} Coins · Overflow stopped"
                : $"Serviced {DisplayName} · +{payout} Coins";

            if (emitLocalFeedback)
            {
                interactor?.NotifyFeedback(message, color);
                PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.BinServiced, transform.position, 1f, message, color);
            }
            return true;
        }

        /// <summary>Convenience overload used by <see cref="LocalGameAuthority"/>.</summary>
        public void ExecuteAuthoritativeService(PlayerInteractor interactor)
        {
            ExecuteAuthoritativeService(interactor, out _, out _, out _, creditLocalInventory: true, emitLocalFeedback: true);
        }

        public SyncedStateRecord CaptureSyncState()
        {
            contents.Capture(out float progressBase, out float searchStart, out int i0, out int i1);
            return new SyncedStateRecord
            {
                v0 = fillLevel,
                v1 = progressBase,
                v2 = searchStart,
                i0 = i0,
                i1 = i1,
            };
        }

        public void ApplySyncState(in SyncedStateRecord state)
        {
            fillLevel = Mathf.Clamp01(state.v0);
            contents.Capture(out float b, out float s, out int i0, out int i1);
            contents.Apply(state.v1, state.v2, state.i0, state.i1);
            if (i0 != state.i0 || i1 != state.i1 || !Mathf.Approximately(b, state.v1) || !Mathf.Approximately(s, state.v2))
                ContentsChanged?.Invoke();
            RefreshIndicator();
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
