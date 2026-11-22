using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Player;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// A buyable workshop lot (the Upcycle Garage). The team first buys the lot at its <see cref="WorkshopSaleSign"/>,
    /// then buys each machine at its <see cref="WorkshopMachineStand"/>; a bought machine appears on its fixed spot and
    /// works as usual. Host-authoritative through <see cref="GameAuthority"/> custom actions (same path in solo and co-op),
    /// replicated with one <see cref="ISyncedStateProvider"/> record (<see cref="WorkshopPropertyState"/>), so every peer
    /// and late joiner shows the same sign, stands and machines.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Workshop Property")]
    public sealed class WorkshopProperty : MonoBehaviour, ISyncedStateProvider
    {
        public const string ActionBuyProperty = "workshop.buy_property";
        public const string ActionBuyMachine = "workshop.buy_machine";

        static readonly Color CelebrateColor = new Color(0.18f, 0.55f, 0.34f);
        static readonly Color RefuseColor = new Color(0.85f, 0.30f, 0.24f);

        [SerializeField] WorkshopConfig config;
        [SerializeField] WorkshopSaleSign saleSign;
        [SerializeField] List<WorkshopMachineStand> stands = new List<WorkshopMachineStand>();
        [Tooltip("Shown only while the lot is for sale (cordon tape, closed gate…).")]
        [SerializeField] List<GameObject> lockedOnlyObjects = new List<GameObject>();
        [Tooltip("Shown only once the lot is owned (welcome plaque…).")]
        [SerializeField] List<GameObject> ownedOnlyObjects = new List<GameObject>();

        [Header("Dev")]
        [Tooltip("Dev/test scenes: the team owns the lot from the start.")]
        [SerializeField] bool devOwnedAtStart;
        [Tooltip("Dev/test scenes: every machine is bought from the start.")]
        [SerializeField] bool devAllMachinesAtStart;

        static readonly List<WorkshopProperty> active = new List<WorkshopProperty>(2);

        WorkshopPropertyState state;
        SyncedStateRegistryInstance registeredRegistry;
        string registeredKey;
        bool handlersRegistered;
        bool isIsolatedMirror;
        bool hasReceivedSyncState;
        bool initialized;

        /// <summary>Raised on every peer whenever the owned flag or machine mask changes.</summary>
        public event Action<WorkshopProperty> StateChanged;

        /// <summary>Workshop lots currently enabled in the scene.</summary>
        public static IReadOnlyList<WorkshopProperty> Active => active;

        public WorkshopConfig Config => config;
        public WorkshopPropertyState State => state;
        public bool IsOwned => state.Owned;
        public string DisplayName => config != null ? config.DisplayName : name;
        public int PropertyPrice => config != null ? config.PropertyPrice : 0;
        public int MachineCount => config != null ? config.MachineCount : 0;
        public IReadOnlyList<WorkshopMachineStand> Stands => stands;
        public WorkshopSaleSign SaleSign => saleSign;
        public string SyncKey => $"workshop:{(config != null ? config.PropertyId : name)}";

        public bool IsMachineOwned(int index) => state.HasMachine(index);
        public int GetMachinePrice(int index) => config != null && config.GetMachine(index) != null ? Mathf.Max(0, config.GetMachine(index).price) : 0;
        public string GetMachineName(int index) => config != null && config.GetMachine(index) != null ? config.GetMachine(index).DisplayName : $"Machine {index + 1}";

        /// <summary>Builder/test wiring.</summary>
        public void Configure(
            WorkshopConfig workshopConfig,
            WorkshopSaleSign sign,
            IList<WorkshopMachineStand> machineStands,
            IList<GameObject> lockedOnly = null,
            IList<GameObject> ownedOnly = null,
            bool ownedAtStart = false,
            bool allMachinesAtStart = false)
        {
            config = workshopConfig;
            saleSign = sign;
            stands = machineStands != null ? new List<WorkshopMachineStand>(machineStands) : new List<WorkshopMachineStand>();
            lockedOnlyObjects = lockedOnly != null ? new List<GameObject>(lockedOnly) : new List<GameObject>();
            ownedOnlyObjects = ownedOnly != null ? new List<GameObject>(ownedOnly) : new List<GameObject>();
            devOwnedAtStart = ownedAtStart;
            devAllMachinesAtStart = allMachinesAtStart;
            for (int i = 0; i < stands.Count; i++)
                stands[i]?.Bind(this);
            saleSign?.Bind(this);
            ResetToStartState();
        }

        /// <summary>Configures an inactive instance as an isolated client mirror bound to <paramref name="registry"/> (PlayMode tests).</summary>
        public void ConfigureAsIsolatedMirror(SyncedStateRegistryInstance registry)
        {
            isIsolatedMirror = true;
            initialized = true;
            UnregisterHandlers();
            UnregisterFromRegistry();
            RegisterWithRegistry(registry);
        }

        void Awake()
        {
            // Configure() (builders/tests) or an early replicated record may already have set the state
            if (!initialized)
                ResetToStartState();
        }

        void ResetToStartState()
        {
            initialized = true;
            bool owned = devOwnedAtStart || devAllMachinesAtStart;
            int mask = devAllMachinesAtStart ? WorkshopPropertyState.MaskFor(MachineCount) : 0;
            state = new WorkshopPropertyState(owned, mask);
            ApplyPresentation(default, celebrate: false);
        }

        void OnEnable()
        {
            if (!initialized)
                ResetToStartState();
            if (!active.Contains(this))
                active.Add(this);
            for (int i = 0; i < stands.Count; i++)
                stands[i]?.Bind(this);
            saleSign?.Bind(this);

            if (isIsolatedMirror)
                return;
            if (registeredRegistry == null)
                RegisterWithRegistry(SyncedStateRegistry.Default);
            RegisterHandlers();
        }

        void OnDisable()
        {
            active.Remove(this);
            UnregisterHandlers();
            UnregisterFromRegistry();
        }

        // ------------------------------------------------------------ requests (client-safe)

        /// <summary>Asks the authority to buy the lot with team coins.</summary>
        public bool RequestBuyProperty(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionBuyProperty, SyncKey, 0, 0f, onResult);

        /// <summary>Asks the authority to buy machine stand <paramref name="index"/> with team coins.</summary>
        public bool RequestBuyMachine(int index, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionBuyMachine, SyncKey, index, 0f, onResult);

        /// <summary>Local-player refusal cue for a failed request (toast + error sound).</summary>
        public static void NotifyRefused(string message, Vector3 worldPos)
        {
            if (string.IsNullOrEmpty(message))
                return;
            PlayerContext.ActiveInteractor?.NotifyFeedback(message, RefuseColor);
            PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.ShopPurchaseFailed, worldPos, 0.8f, message, RefuseColor);
        }

        /// <summary>Shared prompt suffix: "Not enough coins (need N more)" or empty when affordable.</summary>
        public static string ShortfallText(int price)
        {
            int missing = price - GameAuthority.Current.TeamCoins;
            return missing > 0 ? $"Not enough coins (need {missing} more)" : string.Empty;
        }

        // ------------------------------------------------------------ authoritative handlers

        void RegisterHandlers()
        {
            if (handlersRegistered)
                return;
            handlersRegistered = true;
            registeredKey = SyncKey;
            GameAuthority.RegisterCustomActionHandler(ActionBuyProperty, registeredKey, HandleBuyProperty);
            GameAuthority.RegisterCustomActionHandler(ActionBuyMachine, registeredKey, HandleBuyMachine);
        }

        void UnregisterHandlers()
        {
            if (!handlersRegistered)
                return;
            handlersRegistered = false;
            GameAuthority.UnregisterCustomActionHandler(ActionBuyProperty, registeredKey, HandleBuyProperty);
            GameAuthority.UnregisterCustomActionHandler(ActionBuyMachine, registeredKey, HandleBuyMachine);
        }

        CustomGameActionResult HandleBuyProperty(in CustomGameActionContext context)
        {
            if (config == null)
                return CustomGameActionResult.Fail("This lot is not for sale.");
            if (state.Owned)
                return CustomGameActionResult.Fail($"The {DisplayName} is already yours.");

            Vector3 signPos = saleSign != null ? saleSign.transform.position : transform.position;
            if (!MachineUpgradeHelper.ValidateRange(config.PurchaseRange, $"{DisplayName} sign", signPos, in context, out string rangeError))
                return CustomGameActionResult.Fail(rangeError);

            int price = config.PropertyPrice;
            IGameAuthority authority = GameAuthority.Current;
            if (!authority.TrySpendTeamCoins(price))
                return CustomGameActionResult.Fail($"Not enough coins (need {Mathf.Max(1, price - authority.TeamCoins)} more).");

            SetStateAuthoritative(state.WithOwned(true));
            return CustomGameActionResult.Ok($"Bought the {DisplayName} for {price} coins!");
        }

        CustomGameActionResult HandleBuyMachine(in CustomGameActionContext context)
        {
            int index = context.IntArg;
            WorkshopConfig.MachineOffer offer = config != null ? config.GetMachine(index) : null;
            if (offer == null)
                return CustomGameActionResult.Fail("Unknown machine stand.");
            string machineName = offer.DisplayName;
            if (!state.Owned)
                return CustomGameActionResult.Fail($"Buy the {DisplayName} first.");
            if (state.HasMachine(index))
                return CustomGameActionResult.Fail($"The {machineName} is already installed.");

            WorkshopMachineStand stand = FindStand(index);
            Vector3 standPos = stand != null ? stand.transform.position : transform.position;
            if (!MachineUpgradeHelper.ValidateRange(config.PurchaseRange, $"{machineName} stand", standPos, in context, out string rangeError))
                return CustomGameActionResult.Fail(rangeError);

            int price = Mathf.Max(0, offer.price);
            IGameAuthority authority = GameAuthority.Current;
            if (!authority.TrySpendTeamCoins(price))
                return CustomGameActionResult.Fail($"Not enough coins (need {Mathf.Max(1, price - authority.TeamCoins)} more).");

            SetStateAuthoritative(state.WithMachine(index));
            return CustomGameActionResult.Ok($"Bought the {machineName} for {price} coins!");
        }

        void SetStateAuthoritative(WorkshopPropertyState next)
        {
            WorkshopPropertyState previous = state;
            state = next;
            ApplyPresentation(previous, celebrate: true);
            GameAuthority.Current.FlushSyncedStates();
        }

        /// <summary>Dev/test helper on the authority: sets the state directly (no coins).</summary>
        public void SetStateForDev(bool owned, int machineMask)
        {
            if (!GameAuthority.IsAuthoritative)
                return;
            SetStateAuthoritative(new WorkshopPropertyState(owned, machineMask));
        }

        // ------------------------------------------------------------ ISyncedStateProvider

        public SyncedStateRecord CaptureSyncState() => state.Encode();

        public void ApplySyncState(in SyncedStateRecord record)
        {
            initialized = true;
            WorkshopPropertyState next = WorkshopPropertyState.Decode(in record);
            bool celebrate = hasReceivedSyncState;
            hasReceivedSyncState = true;
            if (next.Equals(state))
                return;
            WorkshopPropertyState previous = state;
            state = next;
            // The first record (join / late join) is a baseline: no toasts for purchases made before we arrived
            ApplyPresentation(previous, celebrate);
        }

        void RegisterWithRegistry(SyncedStateRegistryInstance registry)
        {
            if (registry == null)
                return;
            registeredRegistry = registry;
            registry.Register(this);
        }

        void UnregisterFromRegistry()
        {
            if (registeredRegistry == null)
                return;
            registeredRegistry.Unregister(this);
            registeredRegistry = null;
        }

        // ------------------------------------------------------------ presentation

        WorkshopMachineStand FindStand(int index)
        {
            for (int i = 0; i < stands.Count; i++)
            {
                if (stands[i] != null && stands[i].OfferIndex == index)
                    return stands[i];
            }
            return null;
        }

        void ApplyPresentation(WorkshopPropertyState previous, bool celebrate)
        {
            bool owned = state.Owned;
            for (int i = 0; i < lockedOnlyObjects.Count; i++)
            {
                if (lockedOnlyObjects[i] != null)
                    lockedOnlyObjects[i].SetActive(!owned);
            }
            for (int i = 0; i < ownedOnlyObjects.Count; i++)
            {
                if (ownedOnlyObjects[i] != null)
                    ownedOnlyObjects[i].SetActive(owned);
            }

            saleSign?.Refresh();
            for (int i = 0; i < stands.Count; i++)
                stands[i]?.Refresh();

            StateChanged?.Invoke(this);

            if (!celebrate)
                return;

            if (owned && !previous.Owned)
            {
                Vector3 pos = saleSign != null ? saleSign.transform.position : transform.position;
                Celebrate($"The {DisplayName} is yours! Buy machines on the stands inside.", pos, PlayerFeedbackEvent.ShopPurchase);
            }

            for (int i = 0; i < stands.Count; i++)
            {
                WorkshopMachineStand stand = stands[i];
                if (stand == null)
                    continue;
                int index = stand.OfferIndex;
                if (state.HasMachine(index) && !previous.HasMachine(index))
                {
                    stand.PlayInstalledBurst();
                    Celebrate($"{GetMachineName(index)} installed — [E] to use it!", stand.transform.position, PlayerFeedbackEvent.MachineUpgraded);
                }
            }
        }

        static void Celebrate(string message, Vector3 worldPos, PlayerFeedbackEvent feedbackEvent)
        {
            PlayerContext.ActiveInteractor?.NotifyFeedback(message, CelebrateColor);
            PlayerContext.ActiveFeedback?.Emit(feedbackEvent, worldPos, 1f, message, CelebrateColor);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => active.Clear();
    }
}
