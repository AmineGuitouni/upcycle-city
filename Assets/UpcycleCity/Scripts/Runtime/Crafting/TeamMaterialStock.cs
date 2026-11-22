using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Host-authoritative shared team Material Stock &amp; Energy (NRG) manager.
    /// <para>
    /// Keeps all recyclable waste, catalyst, refined materials, and built item counts completely separate from
    /// <see cref="UpcycleCity.Inventory.PlayerInventory"/> (the gear hotbar). Replicates automatically to all
    /// co-op clients via <see cref="SyncedStateRegistry"/> (1 meta provider + 4-slot material buckets) with
    /// zero <c>Unity.Netcode</c> coupling, and subscribes to <see cref="SortingCrate.ItemSorted"/> so sorting
    /// street trash feeds the team's crafting stock authoritatively when enabled in <see cref="EcosystemBalance"/>.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Team Material Stock")]
    public sealed class TeamMaterialStock : MonoBehaviour, ISyncedStateProvider
    {
        public const string ActionGrantStarterKit = "crafting.stock.grant_starter_kit";
        public const string ActionAddEnergy = "crafting.stock.add_energy";
        public const string ActionClearStock = "crafting.stock.clear";
        public const string MetaSyncKey = "craft_stock:meta";

        [Header("Data")]
        [SerializeField] CraftingDatabase database;
        [SerializeField] CraftingStarterKit starterKit;

        [Header("Startup")]
        [Tooltip("Apply Starter Kit automatically on Start (authoritative side only) — enabled in CraftingWorkshop.")]
        [SerializeField] bool applyStarterKitOnStart;
        [SerializeField, Min(0)] int startingEnergy = 30;
        [SerializeField, Min(50)] int maxEnergy = 500;

        [Header("Sorting Integration")]
        [Tooltip("Fallback toggle when no EcosystemBalance.Active is present. In live scenes EcosystemBalance.sortingCratesCreditMaterialStock controls whether sorting crates also credit Material Stock.")]
        [SerializeField] bool creditFromSortingCrates = true;

        [Header("Debug")]
        [Tooltip("Enables dev-only crafting debug custom actions (grant starter kit, add NRG, clear stock, set machine level). Ignored in non-development release builds.")]
        [SerializeField] bool allowDebugActions;

        public static TeamMaterialStock Instance { get; private set; }

        MaterialStockLedger ledger;
        readonly List<StockBucketProvider> bucketProviders = new List<StockBucketProvider>(8);
        SyncedStateRegistryInstance registeredRegistry;
        bool debugHandlersRegistered;
        bool initialized;
        bool isIsolatedMirror;

        public CraftingDatabase Database => ResolveDatabase();
        public CraftingStarterKit StarterKit => starterKit;
        public bool AllowDebugActions => allowDebugActions && CraftingDebugGate.IsDevEnvironment;

        public MaterialStockLedger Ledger
        {
            get
            {
                EnsureInitialized();
                return ledger;
            }
        }

        public int Energy => Ledger.Energy;
        public int MaxEnergy => Ledger.MaxEnergy;
        public string SyncKey => MetaSyncKey;

        /// <summary>Raised whenever any material quantity or team energy changes (locally or via co-op replication).</summary>
        public event Action Changed;

        /// <summary>Raised on the authoritative side when trash is credited from a SortingCrate or hopper.</summary>
        public event Action<MaterialDefinition, float> MaterialCredited;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        /// <summary>Programmatic configuration helper for scene builders and tests.</summary>
        public void Configure(
            CraftingDatabase db,
            CraftingStarterKit kit = null,
            bool applyKitOnStart = false,
            int initialEnergy = 30,
            int energyCap = 500,
            bool? enableDebugActions = null)
        {
            if (db != null)
                database = db;
            starterKit = kit;
            applyStarterKitOnStart = applyKitOnStart;
            startingEnergy = Mathf.Max(0, initialEnergy);
            maxEnergy = Mathf.Max(50, energyCap);
            if (enableDebugActions.HasValue)
            {
                allowDebugActions = enableDebugActions.Value;
                if (!isIsolatedMirror && isActiveAndEnabled)
                    CraftingDebugGate.SetDebugActionsAllowed(allowDebugActions);
            }

            if (initialized && ledger != null)
            {
                ledger.SetEnergy(startingEnergy, maxEnergy, notify: true);
                RebuildBucketProviders();
            }
        }

        /// <summary>
        /// Configures this component on an inactive GameObject as an isolated client-side mirror bound to
        /// <paramref name="registry"/> (used by in-process host+client PlayMode tests without colliding with the host singleton).
        /// </summary>
        public void ConfigureAsIsolatedMirror(CraftingDatabase db, SyncedStateRegistryInstance registry)
        {
            isIsolatedMirror = true;
            if (db != null)
                database = db;
            startingEnergy = 0;
            creditFromSortingCrates = false;
            allowDebugActions = false;
            EnsureInitialized();
            RegisterWithRegistry(registry);
        }

        void Awake()
        {
            if (!isIsolatedMirror)
            {
                if (Instance != null && Instance != this)
                {
                    Destroy(gameObject);
                    return;
                }
                Instance = this;
            }
            EnsureInitialized();
        }

        void Start()
        {
            if (!isIsolatedMirror && applyStarterKitOnStart && GameAuthority.IsAuthoritative && starterKit != null)
                ApplyStarterKitAuthoritative();
        }

        void OnEnable()
        {
            EnsureInitialized();
            if (registeredRegistry == null)
                RegisterWithRegistry(SyncedStateRegistry.Default);
            if (!isIsolatedMirror)
            {
                CraftingDebugGate.SetDebugActionsAllowed(allowDebugActions);
                CraftingDebugGate.Changed += SyncDebugHandlerRegistration;
                SyncDebugHandlerRegistration();
                SortingCrate.ItemSorted += OnSortingCrateItemSorted;
            }
        }

        void OnDisable()
        {
            SortingCrate.ItemSorted -= OnSortingCrateItemSorted;
            CraftingDebugGate.Changed -= SyncDebugHandlerRegistration;
            UnregisterDebugHandlers();
            UnregisterFromRegistry();
        }

        void OnDestroy()
        {
            if (ledger != null)
                ledger.Changed -= HandleLedgerChanged;
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Ensures a <see cref="TeamMaterialStock"/> exists in the active scene (used by runtime-spawned machines
        /// in scenes that did not pre-place one).
        /// </summary>
        public static TeamMaterialStock EnsureInstance(CraftingDatabase fallbackDatabase = null)
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject("TeamMaterialStock");
            var stock = go.AddComponent<TeamMaterialStock>();
            if (fallbackDatabase != null)
                stock.Configure(fallbackDatabase);
            return stock;
        }

        void EnsureInitialized()
        {
            if (initialized)
                return;

            initialized = true;
            ResolveDatabase();
            ledger = new MaterialStockLedger(startingEnergy, maxEnergy);
            ledger.Changed += HandleLedgerChanged;
            RebuildBucketProviders();
        }

        CraftingDatabase ResolveDatabase()
        {
            if (database == null)
                database = CraftingDatabase.LoadDefault();
            return database;
        }

        void HandleLedgerChanged() => Changed?.Invoke();

        // ------------------------------------------------------------ Queries

        public float GetAmount(string materialId) => Ledger.GetAmount(materialId);
        public float GetAmount(MaterialDefinition material) => Ledger.GetAmount(material);

        // ------------------------------------------------------------ Authoritative Mutations

        /// <summary>Authoritatively credits a material quantity on host/solo.</summary>
        public bool TryAddMaterialAuthoritative(string materialId, float amount)
        {
            if (!GameAuthority.IsAuthoritative || string.IsNullOrWhiteSpace(materialId) || amount <= 0f)
                return false;

            Ledger.Add(materialId, amount);
            if (ResolveDatabase() != null && database.TryGetMaterial(materialId, out MaterialDefinition mat))
                MaterialCredited?.Invoke(mat, amount);
            GameAuthority.Current.FlushSyncedStates();
            return true;
        }

        /// <summary>Authoritatively consumes a set of materials + optional energy atomically on host/solo.</summary>
        public bool TryConsumeAuthoritative(MaterialAmount[] inputs, int energyCost = 0)
        {
            if (!GameAuthority.IsAuthoritative)
                return false;

            if (!Ledger.TryConsumeAll(inputs, energyCost))
                return false;

            GameAuthority.Current.FlushSyncedStates();
            return true;
        }

        /// <summary>Authoritatively adds team energy on host/solo.</summary>
        public bool TryAddEnergyAuthoritative(int delta)
        {
            if (!GameAuthority.IsAuthoritative || delta <= 0)
                return false;

            Ledger.AddEnergy(delta);
            GameAuthority.Current.FlushSyncedStates();
            return true;
        }

        /// <summary>Authoritatively resolves and credits a physical <see cref="TrashItem"/> into Material Stock.</summary>
        public bool TryCreditTrashItemAuthoritative(
            TrashItem item,
            out MaterialDefinition material,
            out float yieldAmount,
            float yieldScale = 1f)
        {
            material = null;
            yieldAmount = 0f;
            if (!GameAuthority.IsAuthoritative || item == null || yieldScale <= 0f)
                return false;

            CraftingDatabase db = ResolveDatabase();
            if (db == null || !db.TryResolveTrashMaterial(item, out material, out float baseYield))
                return false;

            yieldAmount = MaterialStockLedger.RoundQuantity(baseYield * yieldScale);
            if (yieldAmount <= 0f)
                return false;

            Ledger.Add(material.MaterialId, yieldAmount);
            MaterialCredited?.Invoke(material, yieldAmount);
            GameAuthority.Current.FlushSyncedStates();
            return true;
        }

        /// <summary>Authoritatively applies the configured <see cref="StarterKit"/> (materials + NRG + optional coins).</summary>
        public bool ApplyStarterKitAuthoritative()
        {
            if (!GameAuthority.IsAuthoritative)
                return false;

            if (starterKit != null)
            {
                starterKit.ApplyTo(Ledger);
                if (starterKit.BonusCoins > 0)
                    GameAuthority.Current.CreditTeamCoins(starterKit.BonusCoins);
                GameAuthority.Current.FlushSyncedStates();
                return true;
            }

            // Sensible fallback if no StarterKit asset was assigned
            Ledger.AddEnergy(150);
            CraftingDatabase db = ResolveDatabase();
            if (db != null)
            {
                for (int i = 0; i < db.Materials.Count; i++)
                {
                    MaterialDefinition mat = db.Materials[i];
                    if (mat == null)
                        continue;
                    if (mat.Category == MaterialCategory.Trash || mat.Category == MaterialCategory.Consumable)
                        Ledger.Add(mat.MaterialId, mat.Unit == MaterialUnit.Units ? 3f : 12f);
                }
            }
            GameAuthority.Current.FlushSyncedStates();
            return true;
        }

        /// <summary>Authoritatively clears all materials and energy.</summary>
        public void ClearAuthoritative()
        {
            if (!GameAuthority.IsAuthoritative)
                return;
            Ledger.Clear();
            GameAuthority.Current.FlushSyncedStates();
        }

        // ------------------------------------------------------------ Client-Safe Debug Requests (Dev-Gated)

        /// <summary>Requests granting the Starter Kit through <see cref="GameAuthority.Current"/> (dev/editor only when enabled).</summary>
        public void RequestGrantStarterKit(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionGrantStarterKit, MetaSyncKey, 0, 0f, onResult);

        /// <summary>Requests adding <paramref name="amount"/> team NRG through <see cref="GameAuthority.Current"/> (dev/editor only when enabled).</summary>
        public void RequestAddEnergy(int amount, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionAddEnergy, MetaSyncKey, amount, 0f, onResult);

        /// <summary>Requests clearing all stock and energy through <see cref="GameAuthority.Current"/> (dev/editor only when enabled).</summary>
        public void RequestClearStock(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionClearStock, MetaSyncKey, 0, 0f, onResult);

        // ------------------------------------------------------------ Custom Action Handlers (Dev-Gated)

        void SyncDebugHandlerRegistration()
        {
            if (isIsolatedMirror || !isActiveAndEnabled)
                return;

            if (CraftingDebugGate.IsDebugActionsAllowed)
                RegisterDebugHandlers();
            else
                UnregisterDebugHandlers();
        }

        void RegisterDebugHandlers()
        {
            if (debugHandlersRegistered || !CraftingDebugGate.IsDebugActionsAllowed)
                return;
            debugHandlersRegistered = true;
            GameAuthority.RegisterCustomActionHandler(ActionGrantStarterKit, MetaSyncKey, HandleGrantStarterKit);
            GameAuthority.RegisterCustomActionHandler(ActionAddEnergy, MetaSyncKey, HandleAddEnergy);
            GameAuthority.RegisterCustomActionHandler(ActionClearStock, MetaSyncKey, HandleClearStock);
        }

        void UnregisterDebugHandlers()
        {
            if (!debugHandlersRegistered)
                return;
            debugHandlersRegistered = false;
            GameAuthority.UnregisterCustomActionHandler(ActionGrantStarterKit, MetaSyncKey, HandleGrantStarterKit);
            GameAuthority.UnregisterCustomActionHandler(ActionAddEnergy, MetaSyncKey, HandleAddEnergy);
            GameAuthority.UnregisterCustomActionHandler(ActionClearStock, MetaSyncKey, HandleClearStock);
        }

        CustomGameActionResult HandleGrantStarterKit(in CustomGameActionContext context)
        {
            if (!CraftingDebugGate.IsDebugActionsAllowed)
                return CustomGameActionResult.Fail("Debug actions are disabled.");

            ApplyStarterKitAuthoritative();
            return CustomGameActionResult.Ok("Starter Kit granted to Team Material Stock.");
        }

        CustomGameActionResult HandleAddEnergy(in CustomGameActionContext context)
        {
            if (!CraftingDebugGate.IsDebugActionsAllowed)
                return CustomGameActionResult.Fail("Debug actions are disabled.");

            int delta = context.IntArg > 0 ? context.IntArg : 50;
            TryAddEnergyAuthoritative(delta);
            return CustomGameActionResult.Ok($"+{delta} NRG added (Total: {Energy} NRG).");
        }

        CustomGameActionResult HandleClearStock(in CustomGameActionContext context)
        {
            if (!CraftingDebugGate.IsDebugActionsAllowed)
                return CustomGameActionResult.Fail("Debug actions are disabled.");

            ClearAuthoritative();
            return CustomGameActionResult.Ok("Cleared Team Material Stock and Energy.");
        }

        // ------------------------------------------------------------ SortingCrate Hook

        void OnSortingCrateItemSorted(SortingCrate crate, TrashItem item, bool match, int payout)
        {
            EcosystemBalance balance = EcosystemBalance.Active;
            bool shouldCredit = balance != null ? balance.sortingCratesCreditMaterialStock : creditFromSortingCrates;
            if (!shouldCredit || !GameAuthority.IsAuthoritative || !match || item == null)
                return;

            float yieldScale = balance != null ? balance.sortingMaterialYieldScale : 1f;
            if (yieldScale <= 0f)
                return;

            TryCreditTrashItemAuthoritative(item, out _, out _, yieldScale);
        }

        // ------------------------------------------------------------ ISyncedStateProvider (Meta + Dynamic Buckets)

        /// <summary>Registers meta + bucket providers with a specific registry (defaults to <see cref="SyncedStateRegistry.Default"/>; tests can bind isolated registries).</summary>
        public void RegisterWithRegistry(SyncedStateRegistryInstance registry)
        {
            if (registry == null)
                return;
            if (ReferenceEquals(registeredRegistry, registry))
                return;

            UnregisterFromRegistry();
            registeredRegistry = registry;
            registeredRegistry.Register(this);
            for (int i = 0; i < bucketProviders.Count; i++)
                registeredRegistry.Register(bucketProviders[i]);
        }

        public void UnregisterFromRegistry()
        {
            if (registeredRegistry == null)
                return;

            registeredRegistry.Unregister(this);
            for (int i = 0; i < bucketProviders.Count; i++)
                registeredRegistry.Unregister(bucketProviders[i]);
            registeredRegistry = null;
        }

        void RebuildBucketProviders()
        {
            if (registeredRegistry != null)
            {
                for (int i = 0; i < bucketProviders.Count; i++)
                    registeredRegistry.Unregister(bucketProviders[i]);
            }

            bucketProviders.Clear();
            CraftingDatabase db = ResolveDatabase();
            int count = db != null ? db.Materials.Count : 0;
            int bucketCount = Mathf.CeilToInt(count / 4f);
            for (int b = 0; b < bucketCount; b++)
            {
                var provider = new StockBucketProvider(this, b);
                bucketProviders.Add(provider);
                registeredRegistry?.Register(provider);
            }
        }

        public SyncedStateRecord CaptureSyncState() => new SyncedStateRecord
        {
            i0 = Ledger.Energy,
            i1 = Ledger.MaxEnergy,
            v0 = Ledger.Revision,
            v1 = ResolveDatabase() != null ? database.Materials.Count : 0,
        };

        public void ApplySyncState(in SyncedStateRecord state)
        {
            EnsureInitialized();
            int replicatedMax = state.i1 > 0 ? state.i1 : maxEnergy;
            Ledger.SetEnergy(state.i0, replicatedMax, notify: true);
        }

        SyncedStateRecord CaptureBucketState(int bucketIndex)
        {
            CraftingDatabase db = ResolveDatabase();
            if (db == null)
                return default;

            int baseIdx = bucketIndex * 4;
            return new SyncedStateRecord
            {
                v0 = GetMaterialAmountByIndex(db, baseIdx),
                v1 = GetMaterialAmountByIndex(db, baseIdx + 1),
                v2 = GetMaterialAmountByIndex(db, baseIdx + 2),
                v3 = GetMaterialAmountByIndex(db, baseIdx + 3),
                i0 = baseIdx,
            };
        }

        void ApplyBucketState(int bucketIndex, in SyncedStateRecord state)
        {
            EnsureInitialized();
            CraftingDatabase db = ResolveDatabase();
            if (db == null)
                return;

            int baseIdx = bucketIndex * 4;
            SetMaterialAmountByIndex(db, baseIdx, state.v0);
            SetMaterialAmountByIndex(db, baseIdx + 1, state.v1);
            SetMaterialAmountByIndex(db, baseIdx + 2, state.v2);
            SetMaterialAmountByIndex(db, baseIdx + 3, state.v3);
            Ledger.RaiseChanged();
        }

        float GetMaterialAmountByIndex(CraftingDatabase db, int index)
        {
            MaterialDefinition mat = db.GetMaterialAt(index);
            return mat != null ? Ledger.GetAmount(mat.MaterialId) : 0f;
        }

        void SetMaterialAmountByIndex(CraftingDatabase db, int index, float value)
        {
            MaterialDefinition mat = db.GetMaterialAt(index);
            if (mat != null)
                Ledger.SetAmount(mat.MaterialId, value, notify: false);
        }

        sealed class StockBucketProvider : ISyncedStateProvider
        {
            readonly TeamMaterialStock owner;
            readonly int bucketIndex;

            public string SyncKey { get; }

            public StockBucketProvider(TeamMaterialStock owner, int bucketIndex)
            {
                this.owner = owner;
                this.bucketIndex = bucketIndex;
                SyncKey = $"craft_stock:b{bucketIndex}";
            }

            public SyncedStateRecord CaptureSyncState() => owner.CaptureBucketState(bucketIndex);

            public void ApplySyncState(in SyncedStateRecord state) => owner.ApplyBucketState(bucketIndex, in state);
        }
    }
}
