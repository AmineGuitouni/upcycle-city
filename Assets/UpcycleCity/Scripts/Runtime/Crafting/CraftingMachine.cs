using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Interactive manual crafting/refining/building machine placed in the world or spawned at runtime via
    /// <see cref="IGameAuthority.SpawnNetworkedPrefab"/>.
    /// <para>
    /// Uses a stable placement ID (<see cref="PlacementId"/>) — never position-based — so machines can be
    /// moved, rotated, picked up, persisted, or spawned at runtime in workshop build mode without breaking
    /// custom-action routing or co-op state sync. Replicates start server timestamp + duration ONCE per job
    /// (zero per-tick sync churn) and predicts progress locally on both host and clients.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Crafting Machine")]
    public sealed class CraftingMachine : MonoBehaviour, IInteractable, ITriggerReceiver, ITrashHopper, ISyncedStateProvider, IMachineIdentityReceiver
    {
        public const string ActionExecuteCard = "crafting.machine.execute";
        public const string ActionUpgrade = "crafting.machine.upgrade";
        public const string ActionSetLevel = "crafting.machine.set_level";

        [Header("Identity")]
        [Tooltip("Stable unique placement ID (auto-generated for scene-placed machines, host-assigned for runtime-spawned machines). Must remain empty on prefab assets.")]
        [SerializeField] string placementId = string.Empty;

        [Header("Configuration")]
        [SerializeField] MachineDefinition definition;
        [SerializeField] CraftingDatabase database;
        [SerializeField, Min(0)] int initialLevel;
        [SerializeField, Min(1.5f)] float interactRangeOverride;
        [SerializeField] bool allowHopperIntake = true;

        CraftingMachineVisuals visuals;
        string syncKey;
        string registeredKey;
        SyncedStateRegistryInstance registeredRegistry;
        bool coreHandlersRegistered;
        bool debugHandlerRegistered;
        bool hopperRegistered;
        bool isIsolatedMirror;
        bool isClientReplica;
        bool hasReceivedSyncState;

        bool SuppressAuthoritativeSide => isIsolatedMirror || isClientReplica;

        int level;
        bool isProcessing;
        int activeCardIndex = -1;
        double processStartServerTime;
        float processDuration;
        ulong activeRequesterClientId;
        int completionSequence;
        int lastCompletedCardIndex = -1;

        /// <summary>Raised ONLY on discrete state changes (level, start/stop processing, active card, completion) — never per frame.</summary>
        public event Action<CraftingMachine> StateChanged;

        /// <summary>Raised on the authoritative side when a card finishes processing and outputs are granted/spawned.</summary>
        public event Action<CraftingMachine, CraftingCard, GameObject> CardCompleted;

        /// <summary>Raised on both host and nearby clients when a card completes (drives UI completion banners on clients).</summary>
        public event Action<CraftingMachine, CraftingCard> CompletionSynced;

        public MachineDefinition Definition => definition;
        public CraftingDatabase Database => ResolveDatabase();
        public int Level => level;
        public int MaxLevel => definition != null ? definition.MaxLevel : 1;
        public bool IsProcessing => isProcessing;
        public float ProcessElapsed => isProcessing ? Mathf.Clamp((float)(GameAuthority.Current.ServerTime - processStartServerTime), 0f, processDuration) : 0f;
        public float ProcessProgress => isProcessing && processDuration > 0.001f ? Mathf.Clamp01(ProcessElapsed / processDuration) : 0f;
        public float ProcessRemainingSeconds => isProcessing ? Mathf.Max(0f, processDuration - ProcessElapsed) : 0f;
        public CraftingCard ActiveCard => isProcessing && activeCardIndex >= 0 ? ResolveDatabase()?.GetCardAt(activeCardIndex) : null;
        public float InteractRange => interactRangeOverride > 0.1f ? interactRangeOverride : (definition != null ? definition.InteractRange : 6.5f);
        public string DisplayName => definition != null ? definition.DisplayName : gameObject.name;

        public Transform Transform => transform;
        public string PlacementId => placementId ?? string.Empty;

        public string SyncKey
        {
            get
            {
                if (string.IsNullOrEmpty(syncKey))
                    syncKey = BuildStableSyncKey();
                return syncKey;
            }
        }

        /// <summary>Independent stable hopper identifier used by <see cref="TrashHopperRegistry"/>.</summary>
        public string HopperKey => SyncKey;

        public string InteractionPrompt
        {
            get
            {
                PlayerInteractor activeInteractor = PlayerContext.ActiveInteractor;
                if (allowHopperIntake
                    && activeInteractor != null
                    && activeInteractor.Grabber != null
                    && activeInteractor.Grabber.IsHolding
                    && activeInteractor.Grabber.HeldObject is TrashItem heldTrash)
                {
                    return $"[E] Feed {heldTrash.DisplayName} into {DisplayName}";
                }

                if (isProcessing)
                {
                    int pct = Mathf.RoundToInt(ProcessProgress * 100f);
                    return $"[E] {DisplayName} (Lv {level} · Processing {pct}%)";
                }

                return $"[E] Use {DisplayName} (Lv {level})";
            }
        }

        /// <summary>Programmatic configuration helper for builders, prefabs, and tests.</summary>
        public void Configure(
            MachineDefinition def,
            CraftingDatabase db = null,
            int startLevel = 0,
            bool hopperEnabled = true,
            string explicitPlacementId = null)
        {
            definition = def;
            if (db != null)
                database = db;
            initialLevel = Mathf.Max(0, startLevel);
            allowHopperIntake = hopperEnabled;
            level = Mathf.Clamp(initialLevel, 0, MaxLevel);
            visuals = GetComponent<CraftingMachineVisuals>();
            visuals?.SetMachineLevel(level);

            if (!string.IsNullOrWhiteSpace(explicitPlacementId))
                ApplyPlacementId(explicitPlacementId);
        }

        /// <summary>Configures an inactive machine as an isolated client mirror bound to <paramref name="registry"/> for PlayMode tests.</summary>
        public void ConfigureAsIsolatedMirror(MachineDefinition def, CraftingDatabase db, string explicitSyncKey, SyncedStateRegistryInstance registry)
        {
            isIsolatedMirror = true;
            syncKey = explicitSyncKey;
            Configure(def, db, startLevel: 0, hopperEnabled: false);
            RegisterWithRegistry(registry);
        }

        /// <summary>Assigns a new stable placement ID (e.g. from host spawn replication or save restore), re-registering handlers and registries cleanly.</summary>
        public void ApplyPlacementId(string newPlacementId)
        {
            if (string.IsNullOrWhiteSpace(newPlacementId))
                return;

            string trimmed = newPlacementId.Trim();
            bool alreadyRegisteredWithSameId = string.Equals(placementId, trimmed, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(syncKey)
                && (!isActiveAndEnabled || registeredRegistry != null);
            if (alreadyRegisteredWithSameId)
                return;

            string oldKey = registeredKey ?? syncKey;
            bool wasActive = isActiveAndEnabled;
            SyncedStateRegistryInstance prevRegistry = registeredRegistry;

            if (wasActive && !string.IsNullOrEmpty(oldKey))
            {
                UnregisterHandlers(oldKey);
                UnregisterHopper(oldKey);
                if (prevRegistry != null)
                {
                    prevRegistry.Unregister(this, oldKey);
                    registeredRegistry = null;
                }
            }

            placementId = trimmed;
            syncKey = BuildStableSyncKey();

            if (wasActive)
            {
                RegisterWithRegistry(prevRegistry ?? SyncedStateRegistry.Default);

                if (!SuppressAuthoritativeSide)
                {
                    RegisterHandlers();
                    RegisterHopper();
                    CraftingDebugGate.Changed -= SyncDebugHandlerRegistration;
                    CraftingDebugGate.Changed += SyncDebugHandlerRegistration;
                }

                if (!SuppressAuthoritativeSide && GameAuthority.IsAuthoritative)
                    GameAuthority.Current.FlushSyncedStates();
            }
        }

        /// <summary>Marks this instance as a non-authoritative client replica of a network-spawned machine.</summary>
        public void SetClientReplicaMode(bool replica)
        {
            if (isClientReplica == replica)
                return;

            isClientReplica = replica;
            if (isClientReplica)
            {
                CraftingDebugGate.Changed -= SyncDebugHandlerRegistration;
                UnregisterHandlers(registeredKey ?? syncKey);
                UnregisterHopper(registeredKey ?? syncKey);
            }
            else if (isActiveAndEnabled && !isIsolatedMirror && !ShouldDeferRegistration())
            {
                RegisterHandlers();
                RegisterHopper();
                CraftingDebugGate.Changed += SyncDebugHandlerRegistration;
            }
        }

        /// <summary>Captures a serializable persistence snapshot (including any in-progress job) for workshop build-mode save/load.</summary>
        public MachineSaveState CaptureSaveState() => new MachineSaveState
        {
            schemaVersion = MachineSaveState.CurrentSchemaVersion,
            placementId = EnsurePlacementId(),
            machineId = definition != null ? definition.MachineId : string.Empty,
            level = level,
            position = transform.position,
            rotation = transform.rotation,
            activeCardId = isProcessing && ActiveCard != null ? ActiveCard.CardId : string.Empty,
            remainingProcessingSeconds = isProcessing ? ProcessRemainingSeconds : 0f,
        };

        /// <summary>Restores identity, transform, level, and any in-progress job from a persisted <see cref="MachineSaveState"/>.</summary>
        public void RestoreSaveState(in MachineSaveState state)
        {
            if (!string.IsNullOrWhiteSpace(state.placementId))
                ApplyPlacementId(state.placementId);

            transform.SetPositionAndRotation(state.position, state.rotation);
            level = Mathf.Clamp(state.level, 0, MaxLevel);

            isProcessing = false;
            activeCardIndex = -1;
            processStartServerTime = 0d;
            processDuration = 0f;

            if (!string.IsNullOrWhiteSpace(state.activeCardId) && state.remainingProcessingSeconds > 0.01f)
            {
                CraftingDatabase db = ResolveDatabase();
                int idx = db != null ? db.GetCardIndex(state.activeCardId) : -1;
                CraftingCard card = idx >= 0 ? db.GetCardAt(idx) : null;
                if (card != null)
                {
                    float duration = Mathf.Max(0.05f, card.ProcessingSeconds);
                    float remaining = Mathf.Clamp(state.remainingProcessingSeconds, 0.05f, duration);
                    isProcessing = true;
                    activeCardIndex = idx;
                    processDuration = duration;
                    processStartServerTime = GameAuthority.Current.ServerTime - (duration - remaining);
                }
            }

            visuals?.SetMachineLevel(level);
            visuals?.SetProcessing(isProcessing);
            StateChanged?.Invoke(this);
            if (!SuppressAuthoritativeSide && GameAuthority.IsAuthoritative)
                GameAuthority.Current.FlushSyncedStates();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            EnsureValidScenePlacementIdInEditor();
        }

        /// <summary>
        /// Ensures scene-placed machines have a unique placement ID (regenerating on Ctrl+D duplicates)
        /// while keeping prefab assets and Prefab Mode contents strictly empty so runtime spawns never
        /// inherit a baked ID.
        /// </summary>
        public void EnsureValidScenePlacementIdInEditor()
        {
            if (Application.isPlaying)
                return;

            var currentPrefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            bool isPrefabAssetOrStage = UnityEditor.EditorUtility.IsPersistent(this)
                || UnityEditor.PrefabUtility.IsPartOfPrefabAsset(gameObject)
                || UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject) != null
                || (currentPrefabStage != null && currentPrefabStage.IsPartOfPrefabContents(gameObject));

            if (isPrefabAssetOrStage)
            {
                if (!string.IsNullOrEmpty(placementId))
                {
                    placementId = string.Empty;
                    UnityEditor.EditorUtility.SetDirty(this);
                }
                return;
            }

            if (!gameObject.scene.IsValid())
                return;

            if (string.IsNullOrWhiteSpace(placementId) || HasDuplicatePlacementIdInScene(placementId))
            {
                placementId = GenerateUniqueScenePlacementId();
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }

        bool HasDuplicatePlacementIdInScene(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            CraftingMachine[] machines = FindObjectsByType<CraftingMachine>(FindObjectsInactive.Include);
            for (int i = 0; i < machines.Length; i++)
            {
                CraftingMachine other = machines[i];
                if (other == null || ReferenceEquals(other, this))
                    continue;
                if (other.gameObject.scene != gameObject.scene || UnityEditor.PrefabUtility.IsPartOfPrefabAsset(other.gameObject))
                    continue;
                if (string.Equals(other.placementId, candidate, StringComparison.Ordinal))
                    return true;
            }

            ManualEnergyGenerator[] generators = FindObjectsByType<ManualEnergyGenerator>(FindObjectsInactive.Include);
            for (int i = 0; i < generators.Length; i++)
            {
                ManualEnergyGenerator other = generators[i];
                if (other == null || other.gameObject.scene != gameObject.scene || UnityEditor.PrefabUtility.IsPartOfPrefabAsset(other.gameObject))
                    continue;
                if (string.Equals(other.PlacementId, candidate, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        string GenerateUniqueScenePlacementId()
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                string candidate = Guid.NewGuid().ToString("N").Substring(0, 12);
                if (!HasDuplicatePlacementIdInScene(candidate))
                    return candidate;
            }
            return Guid.NewGuid().ToString("N");
        }
#endif

        bool ShouldDeferRegistration()
        {
            if (!string.IsNullOrWhiteSpace(placementId) || !string.IsNullOrEmpty(syncKey))
                return false;

            if (GameAuthority.IsMultiplayer && !GameAuthority.IsAuthoritative)
                return true;

            if (TryGetComponent(out INetworkMachineIdentityProvider identityProvider) && identityProvider.IsAwaitingNetworkSpawn)
                return true;

            return false;
        }

        void Awake()
        {
            visuals = GetComponent<CraftingMachineVisuals>();
            if (visuals == null)
                visuals = gameObject.AddComponent<CraftingMachineVisuals>();

            int defStart = definition != null ? Mathf.Max(initialLevel, definition.DefaultLevel) : initialLevel;
            level = Mathf.Clamp(defStart, 0, MaxLevel);
            visuals.BindChildren();
            visuals.SetMachineLevel(level);

            if (allowHopperIntake)
                TriggerRelay.AttachToChildTriggers(this);

            if (string.IsNullOrWhiteSpace(placementId) && !ShouldDeferRegistration())
                placementId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        void OnEnable()
        {
            visuals?.SetMachineLevel(level);
            visuals?.SetProcessing(isProcessing);

            // In co-op, runtime-spawned machines defer registration until beforeSpawn / OnNetworkSpawn supplies the final ID.
            if (ShouldDeferRegistration())
                return;

            if (registeredRegistry == null)
                RegisterWithRegistry(SyncedStateRegistry.Default);
            if (!SuppressAuthoritativeSide)
            {
                RegisterHandlers();
                RegisterHopper();
                CraftingDebugGate.Changed += SyncDebugHandlerRegistration;
            }
        }

        void OnDisable()
        {
            CraftingDebugGate.Changed -= SyncDebugHandlerRegistration;
            UnregisterHandlers(registeredKey ?? syncKey);
            UnregisterHopper(registeredKey ?? syncKey);
            UnregisterFromRegistry();
        }

        void Update()
        {
            if (!isProcessing || SuppressAuthoritativeSide)
                return;

            // Progress is derived locally from ServerTime - processStartServerTime (zero per-frame StateChanged or sync churn).
            if (GameAuthority.IsAuthoritative
                && GameAuthority.Current.ServerTime - processStartServerTime >= processDuration)
            {
                CompleteActiveCardAuthoritative();
            }
        }

        // ------------------------------------------------------------ IInteractable

        public bool CanInteract(PlayerInteractor interactor) => interactor != null;

        public void Interact(PlayerInteractor interactor)
        {
            if (interactor == null)
                return;

            // If holding a physical TrashItem, E deposits it into the intake hopper -> TeamMaterialStock
            if (allowHopperIntake
                && interactor.Grabber != null
                && interactor.Grabber.IsHolding
                && interactor.Grabber.HeldObject is TrashItem)
            {
                TrashItem item = interactor.Grabber.ReleaseForDeposit() as TrashItem;
                if (item != null)
                    GameAuthority.Current.RequestDepositTrash(this, item, interactor);
                return;
            }

            // Otherwise open the phone-friendly CraftingUI for this machine
            CraftingUI ui = CraftingUI.EnsureInstance();
            ui?.Open(this, interactor);
        }

        // ------------------------------------------------------------ Hopper Intake (ITriggerReceiver + ITrashHopper)

        public void OnRelayedTriggerEnter(Collider other) => OnTriggerEnter(other);

        void OnTriggerEnter(Collider other)
        {
            if (!allowHopperIntake || !GameAuthority.IsAuthoritative)
                return;

            TrashItem item = other.GetComponentInParent<TrashItem>();
            if (item == null || item.IsDisposed || item.IsHeld || item.IsContained)
                return;

            GameAuthority.Current.RequestDepositTrash(this, item, null);
        }

        public bool ExecuteAuthoritativeDeposit(
            TrashItem item,
            PlayerInteractor interactor,
            out string message,
            out Color feedbackColor,
            bool emitLocalFeedback = true)
        {
            message = string.Empty;
            feedbackColor = new Color(0.18f, 0.56f, 0.54f);

            if (!allowHopperIntake || item == null || item.IsDisposed)
                return false;

            CraftingDatabase db = ResolveDatabase();
            if (db == null || !db.TryResolveTrashMaterial(item, out MaterialDefinition material, out float yieldAmount))
            {
                message = $"{item.DisplayName} cannot be processed in this hopper.";
                feedbackColor = new Color(0.85f, 0.30f, 0.24f);
                if (emitLocalFeedback)
                    interactor?.NotifyFeedback(message, feedbackColor);
                return false;
            }

            // Per-machine hopper stream/material filtering — reject incompatible trash while keeping the physical item intact
            if (definition != null && !definition.AcceptsHopperDeposit(material, item.Stream))
            {
                message = $"{DisplayName} only accepts {definition.DescribeAcceptedHopperInputs()}, not {item.DisplayName}.";
                feedbackColor = new Color(0.85f, 0.30f, 0.24f);
                if (emitLocalFeedback)
                    interactor?.NotifyFeedback(message, feedbackColor);
                return false;
            }

            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance(db);
            if (!stock.TryAddMaterialAuthoritative(material.MaterialId, yieldAmount))
            {
                message = $"Could not credit {material.DisplayName} to Team Stock.";
                feedbackColor = new Color(0.85f, 0.30f, 0.24f);
                if (emitLocalFeedback)
                    interactor?.NotifyFeedback(message, feedbackColor);
                return false;
            }

            feedbackColor = material.AccentColor;
            message = $"+{material.FormatAmount(yieldAmount)} {material.DisplayName} → Team Stock";
            visuals?.TriggerActivityBurst(0.65f);

            if (emitLocalFeedback)
            {
                interactor?.NotifyFeedback(message, feedbackColor);
                PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.SortCorrect, transform.position, 0.85f, message, feedbackColor);
            }

            item.Dispose();
            return true;
        }

        // ------------------------------------------------------------ Client-Safe Action Requests

        /// <summary>Requests executing <paramref name="card"/> at this machine through <see cref="GameAuthority.Current"/>.</summary>
        public bool RequestExecuteCard(CraftingCard card, Action<bool, string> onResult = null)
        {
            CraftingDatabase db = ResolveDatabase();
            int cardIdx = card != null && db != null ? db.GetCardIndex(card.CardId) : -1;
            if (cardIdx < 0)
            {
                onResult?.Invoke(false, card == null ? "No card selected." : $"Card '{card.DisplayName}' is not registered in CraftingDatabase.");
                return false;
            }
            return GameAuthority.Current.RequestCustomAction(ActionExecuteCard, SyncKey, cardIdx, 0f, onResult);
        }

        /// <summary>Requests upgrading this machine to the next level using shared coins &amp; materials.</summary>
        public bool RequestUpgrade(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionUpgrade, SyncKey, level + 1, 0f, onResult);

        /// <summary>Debug/workshop request to set this machine's level directly (dev/editor only when enabled).</summary>
        public bool RequestDebugSetLevel(int targetLevel, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionSetLevel, SyncKey, targetLevel, 0f, onResult);

        // ------------------------------------------------------------ Authoritative Custom Action Handlers

        void RegisterHandlers()
        {
            string key = SyncKey;
            registeredKey = key;
            if (!coreHandlersRegistered)
            {
                coreHandlersRegistered = true;
                GameAuthority.RegisterCustomActionHandler(ActionExecuteCard, key, HandleExecuteCard);
                GameAuthority.RegisterCustomActionHandler(ActionUpgrade, key, HandleUpgrade);
            }
            SyncDebugHandlerRegistration();
        }

        void SyncDebugHandlerRegistration()
        {
            if (SuppressAuthoritativeSide || !isActiveAndEnabled)
                return;

            string key = registeredKey ?? SyncKey;
            if (CraftingDebugGate.IsDebugActionsAllowed)
            {
                if (!debugHandlerRegistered)
                {
                    debugHandlerRegistered = true;
                    GameAuthority.RegisterCustomActionHandler(ActionSetLevel, key, HandleSetLevel);
                }
            }
            else if (debugHandlerRegistered)
            {
                debugHandlerRegistered = false;
                GameAuthority.UnregisterCustomActionHandler(ActionSetLevel, key, HandleSetLevel);
            }
        }

        void UnregisterHandlers(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (coreHandlersRegistered)
            {
                coreHandlersRegistered = false;
                GameAuthority.UnregisterCustomActionHandler(ActionExecuteCard, key, HandleExecuteCard);
                GameAuthority.UnregisterCustomActionHandler(ActionUpgrade, key, HandleUpgrade);
            }

            if (debugHandlerRegistered)
            {
                debugHandlerRegistered = false;
                GameAuthority.UnregisterCustomActionHandler(ActionSetLevel, key, HandleSetLevel);
            }
        }

        void RegisterHopper()
        {
            if (!allowHopperIntake || hopperRegistered)
                return;
            hopperRegistered = true;
            TrashHopperRegistry.Register(this);
        }

        void UnregisterHopper(string previousKey)
        {
            if (!hopperRegistered)
                return;
            hopperRegistered = false;
            TrashHopperRegistry.Unregister(this, previousKey);
        }

        CustomGameActionResult HandleExecuteCard(in CustomGameActionContext context)
        {
            CraftingDatabase db = ResolveDatabase();
            CraftingCard card = db != null ? db.GetCardAt(context.IntArg) : null;
            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance(db);

            bool inRange = MachineUpgradeHelper.ValidateRange(InteractRange, DisplayName, transform.position, in context, out _);
            bool instant = card != null && card.ProcessingSeconds <= 0.01f;

            CraftingExecutionOutcome outcome = CraftingRules.TryExecute(
                card,
                definition,
                level,
                isProcessing,
                stock.Ledger,
                isPlayerInRange: inRange,
                grantOutputsImmediately: instant);

            if (!outcome.Succeeded)
                return CustomGameActionResult.Fail(outcome.SummaryMessage);

            if (instant)
            {
                GameObject spawned = SpawnCardOutputPrefabAuthoritative(card);
                lastCompletedCardIndex = context.IntArg;
                completionSequence++;
                visuals?.TriggerActivityBurst(1.1f);
                CardCompleted?.Invoke(this, card, spawned);
                CompletionSynced?.Invoke(this, card);
                StateChanged?.Invoke(this);
                GameAuthority.Current.FlushSyncedStates();
                return CustomGameActionResult.Ok(outcome.SummaryMessage);
            }

            isProcessing = true;
            activeCardIndex = context.IntArg;
            processStartServerTime = GameAuthority.Current.ServerTime;
            processDuration = card.ProcessingSeconds;
            activeRequesterClientId = context.SenderClientId;

            visuals?.SetProcessing(true);
            StateChanged?.Invoke(this);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok(outcome.SummaryMessage);
        }

        CustomGameActionResult HandleUpgrade(in CustomGameActionContext context)
        {
            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance(ResolveDatabase());
            CustomGameActionResult result = MachineUpgradeHelper.TryExecuteUpgrade(
                definition,
                level,
                DisplayName,
                transform.position,
                in context,
                stock,
                out int upgradedLevel);

            if (!result.Success)
                return result;

            SetLevelAuthoritative(upgradedLevel);
            return result;
        }

        CustomGameActionResult HandleSetLevel(in CustomGameActionContext context)
        {
            if (!CraftingDebugGate.IsDebugActionsAllowed)
                return CustomGameActionResult.Fail("Debug actions are disabled.");

            int clamped = Mathf.Clamp(context.IntArg, 0, MaxLevel);
            SetLevelAuthoritative(clamped);
            return CustomGameActionResult.Ok($"{DisplayName} set to Level {level}.");
        }

        void SetLevelAuthoritative(int newLevel)
        {
            level = Mathf.Clamp(newLevel, 0, MaxLevel);
            visuals?.SetMachineLevel(level);
            visuals?.TriggerActivityBurst(0.75f);
            StateChanged?.Invoke(this);
            GameAuthority.Current.FlushSyncedStates();
        }

        void CompleteActiveCardAuthoritative()
        {
            int completedIdx = activeCardIndex;
            CraftingCard card = ActiveCard;
            ulong requesterId = activeRequesterClientId;

            isProcessing = false;
            activeCardIndex = -1;
            processStartServerTime = 0d;
            processDuration = 0f;
            lastCompletedCardIndex = completedIdx;
            completionSequence++;

            visuals?.SetProcessing(false);
            visuals?.TriggerActivityBurst(0.6f);

            GameObject spawned = null;
            if (card != null)
            {
                TeamMaterialStock stock = TeamMaterialStock.EnsureInstance(ResolveDatabase());
                CraftingRules.GrantOutputs(card, stock.Ledger);
                spawned = SpawnCardOutputPrefabAuthoritative(card);
                CardCompleted?.Invoke(this, card, spawned);
                CompletionSynced?.Invoke(this, card);

                string summary = CraftingRules.BuildSuccessMessage(card, startedOnly: false);
                GameAuthority.Current.SendTargetedFeedback(
                    requesterId,
                    summary,
                    new Color(0.18f, 0.55f, 0.34f),
                    PlayerFeedbackEvent.CraftCompleted,
                    GetOutputSpawnPosition());
            }

            StateChanged?.Invoke(this);
            GameAuthority.Current.FlushSyncedStates();
        }

        GameObject SpawnCardOutputPrefabAuthoritative(CraftingCard card)
        {
            if (card == null || card.OutputPrefab == null || !GameAuthority.IsAuthoritative)
                return null;

            Vector3 spawnPos = GetOutputSpawnPosition();
            Quaternion spawnRot = transform.rotation;
            return GameAuthority.Current.SpawnNetworkedPrefab(card.OutputPrefab, spawnPos, spawnRot);
        }

        Vector3 GetOutputSpawnPosition()
        {
            if (visuals != null && visuals.OutputPoint != null)
            {
                Vector3 p = visuals.OutputPoint.position;
                Vector3 forward = (p - transform.position);
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f)
                    forward = -transform.forward;
                forward.Normalize();
                return new Vector3(p.x, transform.position.y, p.z) + forward * 0.9f;
            }
            return transform.position + transform.forward * 1.6f;
        }

        CraftingDatabase ResolveDatabase()
        {
            if (database == null)
                database = CraftingDatabase.LoadDefault();
            return database;
        }

        string EnsurePlacementId()
        {
            if (string.IsNullOrWhiteSpace(placementId))
                placementId = Guid.NewGuid().ToString("N").Substring(0, 12);
            return placementId;
        }

        string BuildStableSyncKey()
        {
            string idPart = definition != null && !string.IsNullOrWhiteSpace(definition.MachineId)
                ? definition.MachineId
                : gameObject.name;
            return $"machine:{idPart}:{EnsurePlacementId()}";
        }

        // ------------------------------------------------------------ ISyncedStateProvider

        public void RegisterWithRegistry(SyncedStateRegistryInstance registry)
        {
            if (registry == null || ReferenceEquals(registeredRegistry, registry))
                return;
            UnregisterFromRegistry();
            registeredRegistry = registry;
            registeredRegistry.Register(this);
        }

        public void UnregisterFromRegistry()
        {
            if (registeredRegistry == null)
                return;
            registeredRegistry.Unregister(this, registeredKey ?? syncKey);
            registeredRegistry = null;
        }

        public SyncedStateRecord CaptureSyncState()
        {
            // Split ServerTime into integer seconds (v0) + fractional seconds (v3) so values stay
            // completely constant while processing (replicated ONCE on start and ONCE on completion)
            // while maintaining sub-millisecond accuracy over long sessions.
            double whole = isProcessing ? Math.Floor(processStartServerTime) : 0d;
            double frac = isProcessing ? processStartServerTime - whole : 0d;

            return new SyncedStateRecord
            {
                i0 = level,
                i1 = isProcessing ? activeCardIndex : lastCompletedCardIndex,
                v0 = (float)whole,
                v1 = isProcessing ? processDuration : 0f,
                v2 = completionSequence,
                v3 = (float)frac,
                flags = isProcessing ? (byte)1 : (byte)0,
            };
        }

        public void ApplySyncState(in SyncedStateRecord state)
        {
            int nextLevel = Mathf.Clamp(state.i0, 0, MaxLevel);
            bool nextBusy = state.flags != 0 && state.i1 >= 0;
            int nextActiveCard = nextBusy ? state.i1 : -1;
            double nextStartTime = nextBusy ? (double)state.v0 + (double)state.v3 : 0d;
            float nextDuration = nextBusy ? Mathf.Max(0f, state.v1) : 0f;
            int nextCompletionSeq = Mathf.Max(0, Mathf.RoundToInt(state.v2));

            bool completedJustNow = hasReceivedSyncState && nextCompletionSeq > completionSequence;
            bool changed = nextLevel != level
                || nextBusy != isProcessing
                || nextActiveCard != activeCardIndex
                || completedJustNow
                || Math.Abs(nextStartTime - processStartServerTime) > 0.01d
                || Mathf.Abs(nextDuration - processDuration) > 0.01f;

            level = nextLevel;
            isProcessing = nextBusy;
            activeCardIndex = nextActiveCard;
            processStartServerTime = nextStartTime;
            processDuration = nextDuration;
            completionSequence = nextCompletionSeq;
            if (!nextBusy && state.i1 >= 0)
                lastCompletedCardIndex = state.i1;
            hasReceivedSyncState = true;

            visuals?.SetMachineLevel(level);
            visuals?.SetProcessing(isProcessing);

            if (completedJustNow)
            {
                visuals?.TriggerActivityBurst(0.6f);
                CraftingCard completedCard = lastCompletedCardIndex >= 0
                    ? ResolveDatabase()?.GetCardAt(lastCompletedCardIndex)
                    : null;
                CompletionSynced?.Invoke(this, completedCard);
            }

            if (changed)
                StateChanged?.Invoke(this);
        }
    }
}
