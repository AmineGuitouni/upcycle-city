using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Hand- or pedal-powered manual energy generator that produces team NRG for <see cref="TeamMaterialStock"/>.
    /// Uses a stable <see cref="PlacementId"/> (never position-based) for future workshop build-mode placement,
    /// shares <see cref="MachineUpgradeHelper"/> with <see cref="CraftingMachine"/>, and opens the
    /// generator panel in <see cref="CraftingUI"/> so players on both phone and desktop can pedal-crank and upgrade.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Manual Energy Generator")]
    public sealed class ManualEnergyGenerator : MonoBehaviour, IInteractable, ISyncedStateProvider, IMachineIdentityReceiver
    {
        public const string ActionCrank = "crafting.generator.crank";
        public const string ActionUpgrade = "crafting.generator.upgrade";

        [Header("Identity")]
        [Tooltip("Stable unique placement ID (auto-generated for scene-placed generators, host-assigned for runtime-spawned generators). Must remain empty on prefab assets.")]
        [SerializeField] string placementId = string.Empty;

        [Header("Configuration")]
        [SerializeField] MachineDefinition definition;
        [SerializeField] CraftingDatabase database;
        [SerializeField, Min(1)] int energyPerCrankLevel0 = 15;
        [SerializeField, Min(1)] int energyPerCrankLevel1 = 25;
        [SerializeField, Min(0.25f)] float crankCooldownSeconds = 1.25f;
        [SerializeField, Min(1.5f)] float interactRange = 5.5f;

        CraftingMachineVisuals visuals;
        WorkshopMachineLock workshopLock;
        string syncKey;
        string registeredKey;
        SyncedStateRegistryInstance registeredRegistry;
        bool handlersRegistered;
        bool isClientReplica;

        int level;
        int totalEnergyGenerated;
        double lastCrankServerTime = -100d;

        public event Action<ManualEnergyGenerator> StateChanged;

        public MachineDefinition Definition => definition;
        public CraftingDatabase Database => database ?? CraftingDatabase.LoadDefault();
        public int Level => level;
        public int MaxLevel => definition != null ? definition.MaxLevel : 1;
        public int EnergyPerCrank => level >= 1 ? energyPerCrankLevel1 : energyPerCrankLevel0;
        public int EnergyPerCrankNextLevel => level < MaxLevel ? energyPerCrankLevel1 : EnergyPerCrank;
        public int TotalEnergyGenerated => totalEnergyGenerated;
        public float CrankCooldownSeconds => crankCooldownSeconds;
        public float CrankCooldownRemaining => Mathf.Max(0f, (float)(crankCooldownSeconds - (GameAuthority.Current.ServerTime - lastCrankServerTime)));
        public string DisplayName => definition != null ? definition.DisplayName : "Pedal Energy Generator";
        public string PlacementId => placementId ?? string.Empty;

        public Transform Transform => transform;

        public string SyncKey
        {
            get
            {
                if (string.IsNullOrEmpty(syncKey))
                    syncKey = BuildStableSyncKey();
                return syncKey;
            }
        }

        public string InteractionPrompt =>
            $"[E] Use {DisplayName} (+{EnergyPerCrank} NRG · Lv {level})";

        public void Configure(
            MachineDefinition def,
            CraftingDatabase db = null,
            int nrgLvl0 = 15,
            int nrgLvl1 = 25,
            float cooldown = 1.25f,
            string explicitPlacementId = null)
        {
            definition = def;
            if (db != null)
                database = db;
            energyPerCrankLevel0 = Mathf.Max(1, nrgLvl0);
            energyPerCrankLevel1 = Mathf.Max(energyPerCrankLevel0, nrgLvl1);
            crankCooldownSeconds = Mathf.Max(0.25f, cooldown);

            if (!string.IsNullOrWhiteSpace(explicitPlacementId))
                ApplyPlacementId(explicitPlacementId);
        }

        /// <summary>Assigns a new stable placement ID, re-registering handlers and registry entries cleanly.</summary>
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

                if (!isClientReplica)
                {
                    RegisterHandlers();
                    if (GameAuthority.IsAuthoritative)
                        GameAuthority.Current.FlushSyncedStates();
                }
            }
        }

        /// <summary>Marks this instance as a non-authoritative client replica of a network-spawned generator.</summary>
        public void SetClientReplicaMode(bool replica)
        {
            if (isClientReplica == replica)
                return;

            isClientReplica = replica;
            if (isClientReplica)
                UnregisterHandlers(registeredKey ?? syncKey);
            else if (isActiveAndEnabled && !ShouldDeferRegistration())
                RegisterHandlers();
        }

        /// <summary>Captures a serializable persistence snapshot for workshop build-mode save/load.</summary>
        public MachineSaveState CaptureSaveState() => new MachineSaveState
        {
            schemaVersion = MachineSaveState.CurrentSchemaVersion,
            placementId = EnsurePlacementId(),
            machineId = definition != null ? definition.MachineId : string.Empty,
            level = level,
            position = transform.position,
            rotation = transform.rotation,
            activeCardId = string.Empty,
            remainingProcessingSeconds = 0f,
        };

        /// <summary>Restores identity, transform, and level from a persisted <see cref="MachineSaveState"/>.</summary>
        public void RestoreSaveState(in MachineSaveState state)
        {
            if (!string.IsNullOrWhiteSpace(state.placementId))
                ApplyPlacementId(state.placementId);

            transform.SetPositionAndRotation(state.position, state.rotation);
            level = Mathf.Clamp(state.level, 0, MaxLevel);
            visuals?.SetMachineLevel(level);
            StateChanged?.Invoke(this);
            if (!isClientReplica && GameAuthority.IsAuthoritative)
                GameAuthority.Current.FlushSyncedStates();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            EnsureValidScenePlacementIdInEditor();
        }

        /// <summary>
        /// Ensures scene-placed generators have a unique placement ID (regenerating on Ctrl+D duplicates)
        /// while keeping prefab assets and Prefab Mode contents strictly empty.
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

            ManualEnergyGenerator[] generators = FindObjectsByType<ManualEnergyGenerator>(FindObjectsInactive.Include);
            for (int i = 0; i < generators.Length; i++)
            {
                ManualEnergyGenerator other = generators[i];
                if (other == null || ReferenceEquals(other, this))
                    continue;
                if (other.gameObject.scene != gameObject.scene || UnityEditor.PrefabUtility.IsPartOfPrefabAsset(other.gameObject))
                    continue;
                if (string.Equals(other.placementId, candidate, StringComparison.Ordinal))
                    return true;
            }

            CraftingMachine[] machines = FindObjectsByType<CraftingMachine>(FindObjectsInactive.Include);
            for (int i = 0; i < machines.Length; i++)
            {
                CraftingMachine other = machines[i];
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
            workshopLock = GetComponent<WorkshopMachineLock>();
            visuals.BindChildren();
            visuals.SetMachineLevel(level);

            if (string.IsNullOrWhiteSpace(placementId) && !ShouldDeferRegistration())
                placementId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        void OnEnable()
        {
            visuals?.SetMachineLevel(level);

            if (ShouldDeferRegistration())
                return;

            if (registeredRegistry == null)
                RegisterWithRegistry(SyncedStateRegistry.Default);
            if (!isClientReplica)
                RegisterHandlers();
        }

        void OnDisable()
        {
            UnregisterHandlers(registeredKey ?? syncKey);
            UnregisterFromRegistry();
        }

        /// <summary>True while this generator sits on an unbought workshop stand (hidden; every request is refused).</summary>
        public bool IsWorkshopLocked => WorkshopMachineLock.IsMachineLocked(workshopLock);

        public bool CanInteract(PlayerInteractor interactor) => interactor != null && !IsWorkshopLocked;

        public void Interact(PlayerInteractor interactor)
        {
            if (interactor == null || IsWorkshopLocked)
                return;

            // Open the phone-friendly Generator mode in CraftingUI so players can both crank NRG and upgrade the generator
            CraftingUI ui = CraftingUI.EnsureInstance();
            if (ui != null)
            {
                ui.OpenGenerator(this, interactor);
                return;
            }

            // Fallback if no UI is available: crank directly
            RequestCrank((success, message) =>
            {
                Color color = success ? new Color(0.95f, 0.76f, 0.11f) : new Color(0.85f, 0.30f, 0.24f);
                interactor.NotifyFeedback(message, color);
                if (success)
                {
                    visuals?.TriggerActivityBurst(1.1f);
                    PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.Interact, transform.position, 0.8f, message, color);
                }
            });
        }

        /// <summary>Requests one manual crank cycle through <see cref="GameAuthority.Current"/>.</summary>
        public bool RequestCrank(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionCrank, SyncKey, 0, 0f, onResult);

        /// <summary>Requests upgrading this generator to the next level using shared coins &amp; materials.</summary>
        public bool RequestUpgrade(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionUpgrade, SyncKey, level + 1, 0f, onResult);

        void RegisterHandlers()
        {
            if (handlersRegistered)
                return;
            handlersRegistered = true;
            registeredKey = SyncKey;
            GameAuthority.RegisterCustomActionHandler(ActionCrank, registeredKey, HandleCrank);
            GameAuthority.RegisterCustomActionHandler(ActionUpgrade, registeredKey, HandleUpgrade);
        }

        void UnregisterHandlers(string key)
        {
            if (!handlersRegistered || string.IsNullOrEmpty(key))
                return;
            handlersRegistered = false;
            GameAuthority.UnregisterCustomActionHandler(ActionCrank, key, HandleCrank);
            GameAuthority.UnregisterCustomActionHandler(ActionUpgrade, key, HandleUpgrade);
        }

        CustomGameActionResult HandleCrank(in CustomGameActionContext context)
        {
            if (IsWorkshopLocked)
                return CustomGameActionResult.Fail($"Buy the {DisplayName} first.");
            float range = definition != null ? definition.InteractRange : interactRange;
            if (!MachineUpgradeHelper.ValidateRange(range, DisplayName, transform.position, in context, out string rangeError))
                return CustomGameActionResult.Fail(rangeError);

            double now = GameAuthority.Current.ServerTime;
            if (now - lastCrankServerTime < crankCooldownSeconds)
            {
                float wait = Mathf.Max(0.1f, (float)(crankCooldownSeconds - (now - lastCrankServerTime)));
                return CustomGameActionResult.Fail($"Flywheel spinning — wait {wait:0.#}s.");
            }

            lastCrankServerTime = now;
            int gain = EnergyPerCrank;
            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance(database ?? CraftingDatabase.LoadDefault());
            int total = stock.Ledger.AddEnergy(gain);
            totalEnergyGenerated += gain;

            visuals?.TriggerActivityBurst(1.25f);
            StateChanged?.Invoke(this);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok($"+{gain} NRG generated (Team Energy: {total} NRG)");
        }

        CustomGameActionResult HandleUpgrade(in CustomGameActionContext context)
        {
            if (IsWorkshopLocked)
                return CustomGameActionResult.Fail($"Buy the {DisplayName} first.");
            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance(database ?? CraftingDatabase.LoadDefault());
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

            level = upgradedLevel;
            visuals?.SetMachineLevel(level);
            visuals?.TriggerActivityBurst(0.8f);
            StateChanged?.Invoke(this);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok($"Upgraded {DisplayName} to Level {level} (+{EnergyPerCrank} NRG/crank)!");
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
            return $"generator:{idPart}:{EnsurePlacementId()}";
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

        public SyncedStateRecord CaptureSyncState() => new SyncedStateRecord
        {
            i0 = level,
            i1 = totalEnergyGenerated,
        };

        public void ApplySyncState(in SyncedStateRecord state)
        {
            int nextLevel = Mathf.Clamp(state.i0, 0, MaxLevel);
            bool cranked = state.i1 > totalEnergyGenerated;
            bool changed = nextLevel != level || state.i1 != totalEnergyGenerated;

            level = nextLevel;
            totalEnergyGenerated = Mathf.Max(0, state.i1);
            visuals?.SetMachineLevel(level);
            if (cranked)
                visuals?.TriggerActivityBurst(1.1f);
            if (changed)
                StateChanged?.Invoke(this);
        }
    }
}
