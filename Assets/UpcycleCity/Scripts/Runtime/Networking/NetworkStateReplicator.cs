using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Shops;
using UpcycleCity.Waste;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Compact replicated entry stored in <see cref="NetworkStateReplicator"/>'s generic state list.
    /// Keyed by deterministic FNV-1a hash of <see cref="ISyncedStateProvider.SyncKey"/>.
    /// </summary>
    public struct ReplicatedStateEntry : INetworkSerializable, IEquatable<ReplicatedStateEntry>
    {
        public uint keyHash;
        public float v0;
        public float v1;
        public float v2;
        public float v3;
        public int i0;
        public int i1;
        public byte flags;

        public SyncedStateRecord ToRecord() => new SyncedStateRecord
        {
            v0 = v0,
            v1 = v1,
            v2 = v2,
            v3 = v3,
            i0 = i0,
            i1 = i1,
            flags = flags,
        };

        public static ReplicatedStateEntry From(uint hash, in SyncedStateRecord rec) => new ReplicatedStateEntry
        {
            keyHash = hash,
            v0 = rec.v0,
            v1 = rec.v1,
            v2 = rec.v2,
            v3 = rec.v3,
            i0 = rec.i0,
            i1 = rec.i1,
            flags = rec.flags,
        };

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref keyHash);
            serializer.SerializeValue(ref v0);
            serializer.SerializeValue(ref v1);
            serializer.SerializeValue(ref v2);
            serializer.SerializeValue(ref v3);
            serializer.SerializeValue(ref i0);
            serializer.SerializeValue(ref i1);
            serializer.SerializeValue(ref flags);
        }

        public bool Equals(ReplicatedStateEntry o) =>
            keyHash == o.keyHash
            && Mathf.Abs(v0 - o.v0) < 0.0001f
            && Mathf.Abs(v1 - o.v1) < 0.0001f
            && Mathf.Abs(v2 - o.v2) < 0.0001f
            && Mathf.Abs(v3 - o.v3) < 0.0001f
            && i0 == o.i0
            && i1 == o.i1
            && flags == o.flags;

        public override bool Equals(object obj) => obj is ReplicatedStateEntry o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(keyHash, i0, i1, flags);
    }

    /// <summary>
    /// Host-authoritative co-op <see cref="IGameAuthority"/> and generic data-driven state replicator.
    /// <para>
    /// Installed automatically as <see cref="GameAuthority.Current"/> when spawned in a co-op session,
    /// and restored to <see cref="LocalGameAuthority.Instance"/> on despawn. Owns:
    /// </para>
    /// <list type="bullet">
    /// <item>The <see cref="SharedTeamWallet"/> replicated to every player's <see cref="PlayerInventory"/>.</item>
    /// <item>Delta-only replication of every registered <see cref="ISyncedStateProvider"/> (districts, bins,
    /// factories, sorting crates, buy-back scales, city ecosystem, and future machines).</item>
    /// <item>Host-validated RPCs for sorting, buy-back, bin servicing, factory repairs, catalog-resolved shop
    /// purchases, host-only networked prefab spawning/despawning, and extensible custom actions.</item>
    /// <item>Server-timestamped city event warnings &amp; countdowns (zero per-frame NetworkVariable churn).</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [AddComponentMenu("Upcycle City/Networking/Network State Replicator")]
    public sealed class NetworkStateReplicator : NetworkBehaviour, IGameAuthority
    {
        const float MaxInteractRange = 7.5f;
        const float MaxFactoryRepairRange = 8.5f;
        const float MaxShopRange = 8.5f;
        const float MaxFreeItemStationRange = 4.5f;

        [SerializeField, Range(0.1f, 1f)] float syncIntervalSeconds = 0.25f;
        [SerializeField, Min(0)] int startingTeamCoins = 0;
        [SerializeField] GameObject networkTrashPrefab;

        readonly NetworkVariable<int> teamCoins = new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        readonly NetworkVariable<FixedString64Bytes> activeEventTitle = new NetworkVariable<FixedString64Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        readonly NetworkVariable<double> activeEventEndServerTime = new NetworkVariable<double>(
            0d,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        readonly NetworkVariable<byte> activeEventSeverity = new NetworkVariable<byte>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        NetworkList<ReplicatedStateEntry> replicatedStates;

        readonly Dictionary<uint, int> serverStateIndexByHash = new Dictionary<uint, int>(64);
        readonly List<uint> scratchStaleHashes = new List<uint>(8);
        readonly Dictionary<int, Action<ShopPurchaseResult>> pendingShopCallbacks = new Dictionary<int, Action<ShopPurchaseResult>>(8);
        readonly Dictionary<int, Action<bool, string>> pendingCustomActionCallbacks = new Dictionary<int, Action<bool, string>>(8);

        SyncedStateRegistryInstance stateRegistry;
        SharedTeamWallet wallet;
        string cachedActiveEventTitle = string.Empty;
        float syncTimer;
        int nextPurchaseRequestId = 1;
        int nextCustomActionRequestId = 1;

        public static NetworkStateReplicator Active { get; private set; }

        /// <summary>The state registry instance synchronized by this replicator (defaults to <see cref="SyncedStateRegistry.Default"/>).</summary>
        public SyncedStateRegistryInstance StateRegistry => stateRegistry ?? SyncedStateRegistry.Default;

        public bool IsAuthoritative => !IsSpawned || IsServer;
        public bool IsMultiplayer => IsSpawned;
        public double ServerTime => IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.unscaledTimeAsDouble;
        public int TeamCoins => teamCoins.Value;
        public string ActiveEventTitle => cachedActiveEventTitle;
        public byte ActiveEventSeverity => activeEventSeverity.Value;
        public SharedTeamWallet Wallet => wallet;

        public float ActiveEventTimeRemaining
        {
            get
            {
                double endTime = activeEventEndServerTime.Value;
                if (!IsSpawned || endTime <= 0d || NetworkManager == null)
                    return 0f;
                return Mathf.Max(0f, (float)(endTime - NetworkManager.ServerTime.Time));
            }
        }

        public event Action<int> TeamCoinsChanged;

        /// <summary>Raised on this replicator instance whenever targeted or broadcast player feedback is delivered to it.</summary>
        public event Action<string, Color, byte> LocalFeedbackReceived;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
        }

        /// <summary>Configures runtime references before or after spawning (used by <see cref="CoopSessionManager"/> and tests).</summary>
        public void Configure(GameObject trashPrefab, float syncInterval = 0.25f, int initialCoins = 0)
        {
            if (trashPrefab != null)
                networkTrashPrefab = trashPrefab;
            syncIntervalSeconds = Mathf.Clamp(syncInterval, 0.1f, 1f);
            startingTeamCoins = Mathf.Max(0, initialCoins);
        }

        /// <summary>
        /// Binds a specific <see cref="SyncedStateRegistryInstance"/> to this replicator (primarily for
        /// isolated host/client registries in multi-instance tests). Immediately resyncs providers if spawned.
        /// </summary>
        public void SetStateRegistry(SyncedStateRegistryInstance registry)
        {
            SyncedStateRegistryInstance previous = StateRegistry;
            SyncedStateRegistryInstance next = registry ?? SyncedStateRegistry.Default;
            if (ReferenceEquals(previous, next))
                return;

            if (IsSpawned)
            {
                if (IsServer)
                    previous.ProviderUnregistered -= OnServerProviderUnregistered;
                else
                    previous.Changed -= ApplyAllReplicatedStatesToClient;
            }

            stateRegistry = next;

            if (!IsSpawned)
                return;

            if (IsServer)
            {
                next.ProviderUnregistered += OnServerProviderUnregistered;
                serverStateIndexByHash.Clear();
                replicatedStates?.Clear();
                PushAllProviderStatesToNetwork(forceAll: true);
            }
            else
            {
                next.Changed += ApplyAllReplicatedStatesToClient;
                ApplyAllReplicatedStatesToClient();
            }
        }

        void Awake()
        {
            replicatedStates = new NetworkList<ReplicatedStateEntry>(
                null,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);
        }

        public int ReplicatedStateCount => replicatedStates != null ? replicatedStates.Count : 0;

        /// <summary>Looks up a replicated state record by its FNV-1a key hash.</summary>
        public bool TryGetReplicatedState(uint keyHash, out SyncedStateRecord record)
        {
            if (replicatedStates != null)
            {
                for (int i = 0; i < replicatedStates.Count; i++)
                {
                    if (replicatedStates[i].keyHash == keyHash)
                    {
                        record = replicatedStates[i].ToRecord();
                        return true;
                    }
                }
            }
            record = default;
            return false;
        }

        ulong ResolveLocalClientId() => NetworkManager != null && NetworkManager.IsListening ? NetworkManager.LocalClientId : 0UL;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (Active == null || !Active.IsSpawned || IsServer)
            {
                Active = this;
                GameAuthority.SetAuthority(this);
                GameAuthority.LocalClientIdResolver = ResolveLocalClientId;
            }

            if (networkTrashPrefab == null && CoopSessionManager.Instance != null && CoopSessionManager.Instance.Settings != null)
                networkTrashPrefab = CoopSessionManager.Instance.Settings.NetworkTrashPrefab;

            if (CoopSessionManager.Instance != null && CoopSessionManager.Instance.Settings != null)
                syncIntervalSeconds = CoopSessionManager.Instance.Settings.StateSyncIntervalSeconds;

            teamCoins.OnValueChanged += OnTeamCoinsChanged;
            activeEventTitle.OnValueChanged += OnActiveEventTitleChanged;
            cachedActiveEventTitle = activeEventTitle.Value.IsEmpty ? string.Empty : activeEventTitle.Value.ToString();
            PlayerContext.PlayerRegistered += OnLocalPlayerRegistered;

            if (IsServer)
            {
                wallet = new SharedTeamWallet(startingTeamCoins);
                wallet.BalanceChanged += OnServerWalletChanged;
                teamCoins.Value = wallet.Balance;
                serverStateIndexByHash.Clear();
                replicatedStates?.Clear();
                StateRegistry.ProviderUnregistered += OnServerProviderUnregistered;
                PushAllProviderStatesToNetwork(forceAll: true);
            }
            else
            {
                replicatedStates.OnListChanged += OnClientStateListChanged;
                StateRegistry.Changed += ApplyAllReplicatedStatesToClient;
                ApplyAllReplicatedStatesToClient();
            }

            if (ReferenceEquals(Active, this))
                ApplyCoinsToLocalInventory(teamCoins.Value);
        }

        public override void OnNetworkDespawn()
        {
            teamCoins.OnValueChanged -= OnTeamCoinsChanged;
            activeEventTitle.OnValueChanged -= OnActiveEventTitleChanged;
            cachedActiveEventTitle = string.Empty;
            PlayerContext.PlayerRegistered -= OnLocalPlayerRegistered;

            if (wallet != null)
                wallet.BalanceChanged -= OnServerWalletChanged;

            if (replicatedStates != null)
                replicatedStates.OnListChanged -= OnClientStateListChanged;

            StateRegistry.ProviderUnregistered -= OnServerProviderUnregistered;
            StateRegistry.Changed -= ApplyAllReplicatedStatesToClient;
            FailPendingCallbacks("Disconnected from the co-op session.");

            if (Active == this)
                Active = null;

            if (ReferenceEquals(GameAuthority.Current, this))
            {
                GameAuthority.ResetToLocal();
                GameAuthority.LocalClientIdResolver = null;
            }

            base.OnNetworkDespawn();
        }

        /// <summary>Completes every in-flight request with a failure so UIs waiting on a reply never hang after a disconnect.</summary>
        void FailPendingCallbacks(string reason)
        {
            if (pendingShopCallbacks.Count > 0)
            {
                var shop = new List<Action<ShopPurchaseResult>>(pendingShopCallbacks.Values);
                pendingShopCallbacks.Clear();
                for (int i = 0; i < shop.Count; i++)
                    shop[i]?.Invoke(new ShopPurchaseResult(false, string.Empty, 0, 0, 0, reason));
            }

            if (pendingCustomActionCallbacks.Count > 0)
            {
                var custom = new List<Action<bool, string>>(pendingCustomActionCallbacks.Values);
                pendingCustomActionCallbacks.Clear();
                for (int i = 0; i < custom.Count; i++)
                    custom[i]?.Invoke(false, reason);
            }
        }

        void OnActiveEventTitleChanged(FixedString64Bytes previous, FixedString64Bytes current)
        {
            cachedActiveEventTitle = current.IsEmpty ? string.Empty : current.ToString();
        }

        void Update()
        {
            if (!IsSpawned || !IsServer)
                return;

            // Only write once on expiration — no per-frame NetworkVariable churn
            double endTime = activeEventEndServerTime.Value;
            if (endTime > 0d && NetworkManager != null && NetworkManager.ServerTime.Time >= endTime)
            {
                activeEventEndServerTime.Value = 0d;
                activeEventTitle.Value = default;
                cachedActiveEventTitle = string.Empty;
                activeEventSeverity.Value = 0;
            }

            syncTimer += Time.deltaTime;
            if (syncTimer >= syncIntervalSeconds)
            {
                syncTimer = 0f;
                PushAllProviderStatesToNetwork(forceAll: false);
            }
        }

        /// <summary>Host-only API to broadcast an advance-warned city event + server-clock countdown banner to all co-op players.</summary>
        public void TriggerCityEvent(string title, float durationSeconds, byte severity = 1)
        {
            if (!IsSpawned || !IsServer || NetworkManager == null)
                return;

            if (durationSeconds <= 0f || string.IsNullOrWhiteSpace(title))
            {
                activeEventTitle.Value = default;
                cachedActiveEventTitle = string.Empty;
                activeEventEndServerTime.Value = 0d;
                activeEventSeverity.Value = 0;
                return;
            }

            var fixedTitle = new FixedString64Bytes(title);
            activeEventTitle.Value = fixedTitle;
            cachedActiveEventTitle = fixedTitle.ToString();
            activeEventEndServerTime.Value = NetworkManager.ServerTime.Time + durationSeconds;
            activeEventSeverity.Value = severity;
        }

        // ---------------------------------------------------------------- IGameAuthority

        public bool TrySpendTeamCoins(int amount)
        {
            if (amount <= 0)
                return true;
            if (!IsSpawned)
                return LocalGameAuthority.Instance.TrySpendTeamCoins(amount);
            if (!IsServer || wallet == null)
                return false;
            return wallet.TrySpend(amount);
        }

        public void CreditTeamCoins(int amount)
        {
            if (amount <= 0)
                return;
            if (!IsSpawned)
            {
                LocalGameAuthority.Instance.CreditTeamCoins(amount);
                return;
            }
            if (IsServer && wallet != null)
                wallet.Credit(amount);
        }

        public void FlushSyncedStates()
        {
            if (IsSpawned && IsServer)
                PushAllProviderStatesToNetwork(forceAll: false);
        }

        public void SendTargetedFeedback(
            ulong targetClientId,
            string message,
            Color color,
            PlayerFeedbackEvent feedbackEvent,
            Vector3 worldPos)
        {
            if (string.IsNullOrEmpty(message))
                return;
            if (!IsSpawned)
            {
                LocalGameAuthority.Instance.SendTargetedFeedback(targetClientId, message, color, feedbackEvent, worldPos);
                return;
            }
            if (!IsServer)
                return;

            SendFeedbackToClient(targetClientId, message, color, (byte)feedbackEvent, worldPos);
        }

        public void RequestSortTrash(SortingCrate crate, TrashItem item, PlayerInteractor interactor)
        {
            if (!IsSpawned || crate == null || item == null)
                return;

            uint crateHash = SyncedStateRegistry.ComputeKeyHash(crate.SyncKey);
            bool isPassiveTrigger = interactor == null;
            if (!TryGetNetworkObjectId(item.gameObject, out ulong trashNetId))
            {
                if (IsServer)
                    ExecuteSortOnServer(crate, item, NetworkManager.ServerClientId, isPassiveTrigger);
                return;
            }

            if (IsServer)
                ExecuteSortByIdOnServer(crateHash, trashNetId, NetworkManager.ServerClientId, isPassiveTrigger);
            else
                SortTrashServerRpc(crateHash, trashNetId);
        }

        public void RequestDepositTrash(ITrashHopper hopper, TrashItem item, PlayerInteractor interactor)
        {
            if (!IsSpawned || hopper == null || item == null)
                return;

            uint hopperHash = SyncedStateRegistry.ComputeKeyHash(hopper.HopperKey);
            bool isPassiveTrigger = interactor == null;
            if (!TryGetNetworkObjectId(item.gameObject, out ulong trashNetId))
            {
                if (IsServer)
                    ExecuteDepositOnServer(hopper, item, NetworkManager.ServerClientId, isPassiveTrigger);
                return;
            }

            if (IsServer)
                ExecuteDepositByIdOnServer(hopperHash, trashNetId, NetworkManager.ServerClientId, isPassiveTrigger);
            else
                DepositTrashServerRpc(hopperHash, trashNetId);
        }

        public void RequestSellTrash(RecyclingBuyBack buyBack, TrashItem item, PlayerInteractor interactor)
        {
            if (!IsSpawned || buyBack == null || item == null)
                return;

            uint buyBackHash = SyncedStateRegistry.ComputeKeyHash(buyBack.SyncKey);
            bool isPassiveTrigger = interactor == null;
            if (!TryGetNetworkObjectId(item.gameObject, out ulong trashNetId))
            {
                if (IsServer)
                    ExecuteSellOnServer(buyBack, item, NetworkManager.ServerClientId, isPassiveTrigger);
                return;
            }

            if (IsServer)
                ExecuteSellByIdOnServer(buyBackHash, trashNetId, NetworkManager.ServerClientId, isPassiveTrigger);
            else
                SellTrashServerRpc(buyBackHash, trashNetId);
        }

        public void RequestServiceBin(OverflowingBinSource bin, PlayerInteractor interactor)
        {
            if (!IsSpawned || bin == null)
                return;

            uint binHash = SyncedStateRegistry.ComputeKeyHash(bin.SyncKey);
            if (IsServer)
                ExecuteServiceBinOnServer(binHash, NetworkManager.ServerClientId);
            else
                ServiceBinServerRpc(binHash);
        }

        public void RequestRepairFactory(FactorySmokestackSource factory, PlayerInteractor interactor)
        {
            if (!IsSpawned || factory == null)
                return;

            uint factoryHash = SyncedStateRegistry.ComputeKeyHash(factory.SyncKey);
            if (IsServer)
                ExecuteRepairFactoryOnServer(factoryHash, NetworkManager.ServerClientId);
            else
                RepairFactoryServerRpc(factoryHash);
        }

        public void RequestShopPurchase(ShopPurchaseRequest request, Action<ShopPurchaseResult> onResult)
        {
            if (!IsSpawned)
            {
                LocalGameAuthority.Instance.RequestShopPurchase(request, onResult);
                return;
            }

            if (IsServer)
            {
                ShopPurchaseResult result = ResolveAndExecuteShopPurchaseOnServer(
                    NetworkManager.ServerClientId,
                    request.ShopKey,
                    request.ItemId,
                    request.Quantity,
                    request.Catalog);
                onResult?.Invoke(result);
                return;
            }

            int reqId = nextPurchaseRequestId++;
            if (onResult != null)
                pendingShopCallbacks[reqId] = onResult;

            ShopPurchaseServerRpc(
                reqId,
                new FixedString64Bytes(request.ShopKey ?? string.Empty),
                new FixedString64Bytes(request.ItemId ?? string.Empty),
                request.Quantity);
        }

        public bool RequestCustomAction(
            string actionId,
            string targetId = "",
            int intArg = 0,
            float floatArg = 0f,
            Action<bool, string> onResult = null)
        {
            if (!IsSpawned)
            {
                CustomGameActionResult localResult = GameAuthority.InvokeCustomActionLocally(
                    0UL, actionId, targetId, intArg, floatArg, senderPosition: null);
                onResult?.Invoke(localResult.Success, localResult.Message);
                return localResult.Success;
            }

            if (IsServer)
            {
                Vector3? senderPos = TryGetSenderPosition(NetworkManager.ServerClientId, out Vector3 hostPos)
                    ? hostPos
                    : (PlayerContext.Active != null ? PlayerContext.Active.transform.position : (Vector3?)null);
                CustomGameActionResult hostResult = GameAuthority.InvokeCustomActionLocally(
                    NetworkManager.ServerClientId, actionId, targetId, intArg, floatArg, senderPos);
                onResult?.Invoke(hostResult.Success, hostResult.Message);
                return hostResult.Success;
            }

            int reqId = 0;
            if (onResult != null)
            {
                reqId = nextCustomActionRequestId++;
                if (nextCustomActionRequestId <= 0)
                    nextCustomActionRequestId = 1;
                pendingCustomActionCallbacks[reqId] = onResult;
            }

            CustomActionServerRpc(
                reqId,
                new FixedString64Bytes(actionId ?? string.Empty),
                new FixedString64Bytes(targetId ?? string.Empty),
                intArg,
                floatArg);
            return true;
        }

        public TrashItem SpawnTrash(
            WasteCatalog catalog,
            WasteCatalog.Entry entry,
            WasteSourceKind sourceKind,
            Vector3 worldPosition,
            Vector3 initialVelocity,
            Transform parent = null)
        {
            // Clients never spawn authoritative trash locally
            if (!IsSpawned || !IsServer)
                return null;

            if (networkTrashPrefab == null)
            {
                Debug.LogWarning("[Upcycle City] NetworkStateReplicator has no networkTrashPrefab assigned; cannot spawn networked trash.");
                return null;
            }

            short entryIndex = catalog != null ? (short)catalog.FindEntryIndex(entry) : (short)-1;
            NetworkTrashDescriptor desc = BuildDescriptor(entry, sourceKind, entryIndex);

            Quaternion rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            GameObject go = Instantiate(networkTrashPrefab, worldPosition, rot);
            NetworkTrashArchetype archetype = go.GetComponent<NetworkTrashArchetype>();
            if (archetype != null)
                archetype.InitializeDescriptor(desc);

            NetworkObject netObj = go.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Destroy(go);
                return null;
            }

            netObj.Spawn(destroyWithScene: true);

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = initialVelocity;
                rb.angularVelocity = UnityEngine.Random.insideUnitSphere * (desc.isBulky != 0 ? 1.2f : 3.5f);
            }

            return go.GetComponent<TrashItem>();
        }

        public GameObject SpawnNetworkedPrefab(
            GameObject prefab,
            Vector3 worldPosition,
            Quaternion rotation,
            Transform parent = null) =>
            SpawnNetworkedPrefab(prefab, worldPosition, rotation, beforeSpawn: null, parent: parent);

        public GameObject SpawnNetworkedPrefab(
            GameObject prefab,
            Vector3 worldPosition,
            Quaternion rotation,
            Action<GameObject> beforeSpawn,
            Transform parent = null)
        {
            if (prefab == null)
                return null;

            if (!IsSpawned)
            {
                GameObject localGo = Instantiate(prefab, worldPosition, rotation, parent);
                beforeSpawn?.Invoke(localGo);
                return localGo;
            }

            if (!IsServer)
            {
                Debug.LogError(
                    $"[Upcycle City] SpawnNetworkedPrefab('{prefab.name}') is host-only in co-op. " +
                    "Clients must request spawns via GameAuthority.Current.RequestCustomAction so the host validates and spawns.");
                return null;
            }

            GameObject go = Instantiate(prefab, worldPosition, rotation);
            beforeSpawn?.Invoke(go);
            NetworkObject netObj = go.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn(destroyWithScene: true);
                if (parent != null)
                {
                    if (parent.TryGetComponent(out NetworkObject parentNetObj) && parentNetObj.IsSpawned)
                    {
                        if (!netObj.TrySetParent(parentNetObj, worldPositionStays: true))
                        {
                            Debug.LogWarning(
                                $"[Upcycle City] SpawnNetworkedPrefab('{prefab.name}'): TrySetParent under '{parent.name}' was rejected by Netcode; object left at scene root.");
                        }
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[Upcycle City] SpawnNetworkedPrefab('{prefab.name}'): parent '{parent.name}' is not a spawned NetworkObject. " +
                            "NGO only replicates parenting under a spawned NetworkObject; object was spawned at scene root.");
                    }
                }
            }
            else
            {
                if (parent != null)
                    go.transform.SetParent(parent, worldPositionStays: true);
                Debug.LogWarning($"[Upcycle City] SpawnNetworkedPrefab called on '{prefab.name}' which has no NetworkObject.");
            }
            return go;
        }

        public bool DespawnNetworkedObject(GameObject instance)
        {
            if (instance == null)
                return false;

            if (!IsSpawned)
            {
                Destroy(instance);
                return true;
            }

            if (!IsServer)
            {
                Debug.LogError(
                    $"[Upcycle City] DespawnNetworkedObject('{instance.name}') is host-only in co-op. " +
                    "Clients must request despawns via a validated host action.");
                return false;
            }

            NetworkObject netObj = instance.GetComponent<NetworkObject>();
            if (netObj != null && netObj.IsSpawned)
                netObj.Despawn(true);
            else
                Destroy(instance);
            return true;
        }

        /// <summary>Server helper: converts a pre-placed offline scene TrashItem into a replicated NetworkTrashItem.</summary>
        public TrashItem ConvertOfflineTrashToNetworked(TrashItem offlineTrash, WasteCatalog catalog)
        {
            if (!IsSpawned || !IsServer || offlineTrash == null || networkTrashPrefab == null)
                return null;

            short idx = catalog != null
                ? (short)catalog.FindEntryIndex(offlineTrash.DisplayName, offlineTrash.Stream)
                : (short)-1;

            WasteCatalog.Entry entry = idx >= 0 && catalog != null ? catalog.Entries[idx] : null;
            NetworkTrashDescriptor desc = entry != null
                ? BuildDescriptor(entry, offlineTrash.Source, idx)
                : NetworkTrashDescriptor.CreateFallback(
                    offlineTrash.DisplayName,
                    offlineTrash.Stream,
                    offlineTrash.Source,
                    offlineTrash.PollutionWeight,
                    offlineTrash.BaseCoinValue,
                    offlineTrash.Body != null ? offlineTrash.Body.mass : 0.5f,
                    offlineTrash.IsBulky,
                    offlineTrash.CarrySpeedMultiplier,
                    offlineTrash.HoldDistanceOffset,
                    idx,
                    offlineTrash.ArchetypeId);

            GameObject go = Instantiate(networkTrashPrefab, offlineTrash.transform.position, offlineTrash.transform.rotation);
            NetworkTrashArchetype archetype = go.GetComponent<NetworkTrashArchetype>();
            if (archetype != null)
                archetype.InitializeDescriptor(desc);

            NetworkObject netObj = go.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Destroy(go);
                return null;
            }

            netObj.Spawn(destroyWithScene: true);
            return go.GetComponent<TrashItem>();
        }

        // ---------------------------------------------------------------- Authoritative Server Execution & RPCs

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SortTrashServerRpc(uint crateKeyHash, ulong trashNetworkObjectId, RpcParams rpcParams = default)
        {
            ExecuteSortByIdOnServer(crateKeyHash, trashNetworkObjectId, rpcParams.Receive.SenderClientId, isPassiveTrigger: false);
        }

        void ExecuteSortByIdOnServer(uint crateKeyHash, ulong trashNetworkObjectId, ulong senderClientId, bool isPassiveTrigger = false)
        {
            if (!StateRegistry.TryGet(crateKeyHash, out ISyncedStateProvider provider) || provider is not SortingCrate crate)
                return;
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(trashNetworkObjectId, out NetworkObject trashNetObj))
                return;
            TrashItem item = trashNetObj.GetComponent<TrashItem>();
            if (item == null || item.IsDisposed)
                return;

            if (!ValidateTrashStationAction(senderClientId, item, crate.transform.position))
                return;

            ExecuteSortOnServer(crate, item, senderClientId, isPassiveTrigger);
        }

        void ExecuteSortOnServer(SortingCrate crate, TrashItem item, ulong senderClientId, bool isPassiveTrigger = false)
        {
            // Capture feedback recipient & material stream BEFORE item.Dispose() resets them
            ulong feedbackRecipient = ResolveTrashActionFeedbackRecipient(item, senderClientId, isPassiveTrigger);
            byte materialStream = FeedbackSignal.EncodeMaterial(item.Stream, item.IsBulky);

            bool executed = crate.ExecuteAuthoritativeSort(
                item,
                inventory: null,
                interactor: null,
                out bool match,
                out int payout,
                out string message,
                out Color color,
                creditLocalInventory: false,
                emitLocalFeedback: false);

            if (!executed)
                return;

            if (payout > 0)
                wallet.Credit(payout);

            PushProviderStateNow(crate);
            SendFeedbackToClient(
                feedbackRecipient,
                message,
                color,
                (byte)(match ? PlayerFeedbackEvent.SortCorrect : PlayerFeedbackEvent.SortWrong),
                crate.transform.position,
                materialStream);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void DepositTrashServerRpc(uint hopperKeyHash, ulong trashNetworkObjectId, RpcParams rpcParams = default)
        {
            ExecuteDepositByIdOnServer(hopperKeyHash, trashNetworkObjectId, rpcParams.Receive.SenderClientId, isPassiveTrigger: false);
        }

        void ExecuteDepositByIdOnServer(uint hopperKeyHash, ulong trashNetworkObjectId, ulong senderClientId, bool isPassiveTrigger = false)
        {
            if (!TrashHopperRegistry.TryGet(hopperKeyHash, out ITrashHopper hopper))
                return;
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(trashNetworkObjectId, out NetworkObject trashNetObj))
                return;
            TrashItem item = trashNetObj.GetComponent<TrashItem>();
            if (item == null || item.IsDisposed)
                return;

            if (!ValidateHopperDepositAction(senderClientId, item, hopper.Transform.position, isPassiveTrigger))
                return;

            ExecuteDepositOnServer(hopper, item, senderClientId, isPassiveTrigger);
        }

        bool ValidateHopperDepositAction(ulong senderClientId, TrashItem item, Vector3 stationPosition, bool isPassiveTrigger)
        {
            NetworkedPhysicsItem netItem = item.GetComponent<NetworkedPhysicsItem>();
            if (netItem != null && netItem.IsHeldByAnyone)
            {
                if (netItem.HolderClientId != senderClientId)
                    return false;

                return ValidateSenderRangeStrict(senderClientId, stationPosition, MaxInteractRange);
            }

            if (Vector3.Distance(item.transform.position, stationPosition) > MaxFreeItemStationRange)
                return false;

            if (!isPassiveTrigger && !ValidateSenderRangeStrict(senderClientId, stationPosition, MaxInteractRange + 1.5f))
                return false;

            return true;
        }

        bool ValidateSenderRangeStrict(ulong senderClientId, Vector3 targetPosition, float maxRange)
        {
            if (TryGetSenderPosition(senderClientId, out Vector3 senderPos))
                return Vector3.Distance(senderPos, targetPosition) <= maxRange;

            if (senderClientId == NetworkManager.ServerClientId && PlayerContext.Active != null)
                return Vector3.Distance(PlayerContext.Active.transform.position, targetPosition) <= maxRange;

            // In co-op, reject hopper actions whose sender has no known player position
            return false;
        }

        void ExecuteDepositOnServer(ITrashHopper hopper, TrashItem item, ulong senderClientId, bool isPassiveTrigger = false)
        {
            ulong feedbackRecipient = ResolveTrashActionFeedbackRecipient(item, senderClientId, isPassiveTrigger);

            bool executed = hopper.ExecuteAuthoritativeDeposit(
                item,
                interactor: null,
                out string message,
                out Color color,
                emitLocalFeedback: false);

            if (!executed)
            {
                if (!string.IsNullOrEmpty(message))
                {
                    SendFeedbackToClient(
                        feedbackRecipient,
                        message,
                        color,
                        (byte)PlayerFeedbackEvent.SortWrong,
                        hopper.Transform.position);
                }
                return;
            }

            PushAllProviderStatesToNetwork(forceAll: false);
            SendFeedbackToClient(
                feedbackRecipient,
                message,
                color,
                (byte)PlayerFeedbackEvent.SortCorrect,
                hopper.Transform.position);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SellTrashServerRpc(uint buyBackKeyHash, ulong trashNetworkObjectId, RpcParams rpcParams = default)
        {
            ExecuteSellByIdOnServer(buyBackKeyHash, trashNetworkObjectId, rpcParams.Receive.SenderClientId, isPassiveTrigger: false);
        }

        void ExecuteSellByIdOnServer(uint buyBackKeyHash, ulong trashNetworkObjectId, ulong senderClientId, bool isPassiveTrigger = false)
        {
            if (!StateRegistry.TryGet(buyBackKeyHash, out ISyncedStateProvider provider) || provider is not RecyclingBuyBack buyBack)
                return;
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(trashNetworkObjectId, out NetworkObject trashNetObj))
                return;
            TrashItem item = trashNetObj.GetComponent<TrashItem>();
            if (item == null || item.IsDisposed)
                return;

            if (!ValidateTrashStationAction(senderClientId, item, buyBack.transform.position))
                return;

            ExecuteSellOnServer(buyBack, item, senderClientId, isPassiveTrigger);
        }

        void ExecuteSellOnServer(RecyclingBuyBack buyBack, TrashItem item, ulong senderClientId, bool isPassiveTrigger = false)
        {
            // Capture feedback recipient & material stream BEFORE item.Dispose() resets them
            ulong feedbackRecipient = ResolveTrashActionFeedbackRecipient(item, senderClientId, isPassiveTrigger);
            byte materialStream = FeedbackSignal.EncodeMaterial(item.Stream, item.IsBulky);

            bool executed = buyBack.ExecuteAuthoritativeBuy(
                item,
                interactor: null,
                out int price,
                out string message,
                out Color colour,
                creditLocalInventory: false,
                emitLocalFeedback: false);

            if (!executed)
                return;

            if (price > 0)
                wallet.Credit(price);

            PushProviderStateNow(buyBack);
            SendFeedbackToClient(
                feedbackRecipient,
                message,
                colour,
                (byte)PlayerFeedbackEvent.SellTrash,
                buyBack.transform.position,
                materialStream);
        }

        ulong ResolveTrashActionFeedbackRecipient(TrashItem item, ulong fallbackClientId, bool isPassiveTrigger)
        {
            if (!isPassiveTrigger || item == null)
                return fallbackClientId;

            NetworkedPhysicsItem netItem = item.GetComponent<NetworkedPhysicsItem>();
            if (netItem == null)
                return fallbackClientId;

            ulong actorId = netItem.IsHeldByAnyone ? netItem.HolderClientId : netItem.LastHolderClientId;
            if (actorId == ulong.MaxValue)
                return fallbackClientId;

            if (NetworkManager != null && NetworkManager.ConnectedClients.ContainsKey(actorId))
                return actorId;

            return fallbackClientId;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ServiceBinServerRpc(uint binKeyHash, RpcParams rpcParams = default)
        {
            ExecuteServiceBinOnServer(binKeyHash, rpcParams.Receive.SenderClientId);
        }

        void ExecuteServiceBinOnServer(uint binKeyHash, ulong senderClientId)
        {
            if (!StateRegistry.TryGet(binKeyHash, out ISyncedStateProvider provider) || provider is not OverflowingBinSource bin)
                return;

            if (!ValidateSenderRange(senderClientId, bin.transform.position, MaxInteractRange))
                return;

            if (!bin.ExecuteAuthoritativeService(null, out int payout, out string message, out Color color, creditLocalInventory: false, emitLocalFeedback: false))
                return;

            if (payout > 0)
                wallet.Credit(payout);

            PushProviderStateNow(bin);
            SendFeedbackToClient(
                senderClientId,
                message,
                color,
                (byte)PlayerFeedbackEvent.BinServiced,
                bin.transform.position);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RepairFactoryServerRpc(uint factoryKeyHash, RpcParams rpcParams = default)
        {
            ExecuteRepairFactoryOnServer(factoryKeyHash, rpcParams.Receive.SenderClientId);
        }

        void ExecuteRepairFactoryOnServer(uint factoryKeyHash, ulong senderClientId)
        {
            if (!StateRegistry.TryGet(factoryKeyHash, out ISyncedStateProvider provider) || provider is not FactorySmokestackSource factory)
                return;

            if (!ValidateSenderRange(senderClientId, factory.transform.position, MaxFactoryRepairRange))
                return;

            if (!ValidateSenderHasRequiredTool(senderClientId, factory.RequiredToolId))
                return;

            if (!factory.ExecuteAuthoritativeRepair(null, out int payout, out string message, out Color color, creditLocalInventory: false, emitLocalFeedback: false))
                return;

            if (payout > 0)
                wallet.Credit(payout);

            PushProviderStateNow(factory);
            // Factory repair is a team-wide district milestone, broadcast once to everyone
            BroadcastFeedbackClientRpc(
                ToFixed128(message),
                color,
                (byte)PlayerFeedbackEvent.FactoryRepaired,
                factory.transform.position,
                FeedbackSignal.NoMaterialStream);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ShopPurchaseServerRpc(
            int requestId,
            FixedString64Bytes shopKey,
            FixedString64Bytes itemId,
            int quantity,
            RpcParams rpcParams = default)
        {
            ulong senderId = rpcParams.Receive.SenderClientId;
            ShopPurchaseResult result = ResolveAndExecuteShopPurchaseOnServer(
                senderId,
                shopKey.ToString(),
                itemId.ToString(),
                quantity,
                hostCatalogFallback: null);

            ShopPurchaseResponseClientRpc(
                requestId,
                result.Approved,
                new FixedString64Bytes(result.ItemId ?? string.Empty),
                result.Quantity,
                result.Price,
                result.RemainingCoins,
                ToFixed128(result.RejectionReason),
                RpcTarget.Single(senderId, RpcTargetUse.Temp));
        }

        ShopPurchaseResult ResolveAndExecuteShopPurchaseOnServer(
            ulong senderClientId,
            string shopKey,
            string itemId,
            int quantity,
            ShopCatalog hostCatalogFallback)
        {
            if (quantity <= 0 || string.IsNullOrWhiteSpace(itemId))
                return new ShopPurchaseResult(false, itemId, quantity, 0, wallet.Balance, "Invalid shop request.");

            ShopCounter counter = null;
            ShopCatalog.Offer offer = null;
            bool found = ShopCounter.TryResolveOffer(shopKey, itemId, quantity, out counter, out offer);
            if (!found && hostCatalogFallback != null && senderClientId == NetworkManager.ServerClientId)
                found = hostCatalogFallback.TryFindOffer(itemId, quantity, out offer);

            if (!found || offer == null || offer.price <= 0)
                return new ShopPurchaseResult(false, itemId, quantity, 0, wallet.Balance, "Unknown shop offer.");

            if (counter != null && !ValidateSenderRange(senderClientId, counter.transform.position, MaxShopRange))
                return new ShopPurchaseResult(false, itemId, quantity, offer.price, wallet.Balance, "Move closer to the shop counter.");

            int resolvedPrice = offer.price;
            if (!wallet.TrySpend(resolvedPrice))
            {
                return new ShopPurchaseResult(
                    false,
                    itemId,
                    quantity,
                    resolvedPrice,
                    wallet.Balance,
                    $"Not enough team coins — costs {resolvedPrice}.");
            }

            if (offer.item != null && offer.item.UseBehavior is IMaterialStockCreditBehavior stockCredit && stockCredit.CreditOnPurchase)
            {
                stockCredit.CreditAuthoritativeStock(offer.quantity, out _);
                PushAllProviderStatesToNetwork(forceAll: false);
            }

            return new ShopPurchaseResult(true, itemId, quantity, resolvedPrice, wallet.Balance);
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void ShopPurchaseResponseClientRpc(
            int requestId,
            bool approved,
            FixedString64Bytes itemId,
            int quantity,
            int price,
            int remainingCoins,
            FixedString128Bytes reason,
            RpcParams rpcParams = default)
        {
            if (pendingShopCallbacks.TryGetValue(requestId, out Action<ShopPurchaseResult> cb))
            {
                pendingShopCallbacks.Remove(requestId);
                cb?.Invoke(new ShopPurchaseResult(
                    approved,
                    itemId.ToString(),
                    quantity,
                    price,
                    remainingCoins,
                    reason.ToString()));
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void CustomActionServerRpc(
            int requestId,
            FixedString64Bytes actionId,
            FixedString64Bytes targetId,
            int intArg,
            float floatArg,
            RpcParams rpcParams = default)
        {
            ulong senderId = rpcParams.Receive.SenderClientId;
            Vector3? senderPos = TryGetSenderPosition(senderId, out Vector3 pos) ? pos : (Vector3?)null;
            CustomGameActionResult result = GameAuthority.InvokeCustomActionLocally(
                senderId,
                actionId.ToString(),
                targetId.ToString(),
                intArg,
                floatArg,
                senderPos);

            PushAllProviderStatesToNetwork(forceAll: false);

            if (requestId > 0)
            {
                var msg = new FixedString512Bytes();
                msg.CopyFromTruncated(result.Message ?? string.Empty);

                CustomActionResponseClientRpc(
                    requestId,
                    result.Success,
                    msg,
                    RpcTarget.Single(senderId, RpcTargetUse.Temp));
            }
        }

        /// <summary>
        /// Converts a player-facing message to a <see cref="FixedString128Bytes"/>, truncating by UTF-8 byte count
        /// (accents and symbols like "→" take several bytes) so long card/shop messages never throw.
        /// </summary>
        static FixedString128Bytes ToFixed128(string text)
        {
            var fixedText = new FixedString128Bytes();
            if (!string.IsNullOrEmpty(text))
                fixedText.CopyFromTruncated(text);
            return fixedText;
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void CustomActionResponseClientRpc(
            int requestId,
            bool success,
            FixedString512Bytes message,
            RpcParams rpcParams = default)
        {
            if (pendingCustomActionCallbacks.TryGetValue(requestId, out Action<bool, string> cb))
            {
                pendingCustomActionCallbacks.Remove(requestId);
                cb?.Invoke(success, message.ToString());
            }
        }

        void SendFeedbackToClient(
            ulong targetClientId,
            string message,
            Color color,
            byte feedbackEvent,
            Vector3 worldPos,
            byte materialStream = FeedbackSignal.NoMaterialStream)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (targetClientId == NetworkManager.LocalClientId)
            {
                EmitLocalFeedback(message, color, (PlayerFeedbackEvent)feedbackEvent, worldPos, materialStream);
                return;
            }

            TargetedFeedbackClientRpc(
                ToFixed128(message),
                color,
                feedbackEvent,
                worldPos,
                materialStream,
                RpcTarget.Single(targetClientId, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void TargetedFeedbackClientRpc(
            FixedString128Bytes message,
            Color color,
            byte feedbackEvent,
            Vector3 worldPos,
            byte materialStream,
            RpcParams rpcParams = default)
        {
            EmitLocalFeedback(message.ToString(), color, (PlayerFeedbackEvent)feedbackEvent, worldPos, materialStream);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void BroadcastFeedbackClientRpc(
            FixedString128Bytes message,
            Color color,
            byte feedbackEvent,
            Vector3 worldPos,
            byte materialStream)
        {
            EmitLocalFeedback(message.ToString(), color, (PlayerFeedbackEvent)feedbackEvent, worldPos, materialStream);
        }

        void EmitLocalFeedback(
            string text,
            Color color,
            PlayerFeedbackEvent evt,
            Vector3 worldPos,
            byte materialStream = FeedbackSignal.NoMaterialStream)
        {
            PlayerContext.ActiveInteractor?.NotifyFeedback(text, color);
            PlayerContext.ActiveFeedback?.Emit(evt, worldPos, 1f, text, color, materialStream);
            LocalFeedbackReceived?.Invoke(text, color, (byte)evt);
        }

        // ---------------------------------------------------------------- Server Validation Helpers

        bool ValidateTrashStationAction(ulong senderClientId, TrashItem item, Vector3 stationPosition)
        {
            NetworkedPhysicsItem netItem = item.GetComponent<NetworkedPhysicsItem>();
            if (netItem != null && netItem.IsHeldByAnyone)
            {
                // Never allow sorting/selling an item held by a different player
                if (netItem.HolderClientId != senderClientId)
                    return false;

                // Holder must be within interaction range of the station
                return ValidateSenderRange(senderClientId, stationPosition, MaxInteractRange);
            }

            // Free item: must actually be near the station (e.g. tossed into trigger or dropped next to it)
            if (Vector3.Distance(item.transform.position, stationPosition) > MaxFreeItemStationRange)
                return false;

            // Remote clients cannot trigger free items from across the map
            if (senderClientId != NetworkManager.ServerClientId
                && !ValidateSenderRange(senderClientId, stationPosition, MaxInteractRange + 1.5f))
                return false;

            return true;
        }

        bool ValidateSenderRange(ulong senderClientId, Vector3 targetPosition, float maxRange)
        {
            if (TryGetSenderPosition(senderClientId, out Vector3 senderPos))
                return Vector3.Distance(senderPos, targetPosition) <= maxRange;

            // If no player object exists (e.g. host physics trigger or headless unit test), allow
            return true;
        }

        bool ValidateSenderHasRequiredTool(ulong senderClientId, string requiredToolId)
        {
            if (string.IsNullOrEmpty(requiredToolId))
                return true;

            if (TryGetSenderPlayer(senderClientId, out NetworkPlayerController npc))
            {
                if (string.Equals(npc.EquippedItemId, requiredToolId, StringComparison.Ordinal))
                    return true;
                if (senderClientId == NetworkManager.ServerClientId
                    && npc.LocalController != null
                    && npc.LocalController.Inventory != null
                    && npc.LocalController.Inventory.EquippedItem != null
                    && string.Equals(npc.LocalController.Inventory.EquippedItem.ItemId, requiredToolId, StringComparison.Ordinal))
                    return true;
                return false;
            }

            if (senderClientId == NetworkManager.ServerClientId
                && PlayerContext.ActiveInventory != null
                && PlayerContext.ActiveInventory.EquippedItem != null
                && string.Equals(PlayerContext.ActiveInventory.EquippedItem.ItemId, requiredToolId, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        bool TryGetSenderPlayer(ulong senderClientId, out NetworkPlayerController player)
        {
            if (NetworkManager != null
                && NetworkManager.ConnectedClients.TryGetValue(senderClientId, out NetworkClient client)
                && client != null
                && client.PlayerObject != null)
            {
                player = client.PlayerObject.GetComponent<NetworkPlayerController>();
                if (player != null)
                    return true;
            }
            return NetworkPlayerController.TryGetPlayer(senderClientId, out player);
        }

        bool TryGetSenderPosition(ulong senderClientId, out Vector3 position)
        {
            if (NetworkManager != null
                && NetworkManager.ConnectedClients.TryGetValue(senderClientId, out NetworkClient client)
                && client != null
                && client.PlayerObject != null)
            {
                position = client.PlayerObject.transform.position;
                return true;
            }

            if (NetworkPlayerController.TryGetPlayer(senderClientId, out NetworkPlayerController npc))
            {
                position = npc.transform.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        // ---------------------------------------------------------------- Generic State Registry Sync

        void OnServerProviderUnregistered(uint keyHash)
        {
            if (!IsSpawned || !IsServer || replicatedStates == null)
                return;

            // Leaving a session: Shutdown disposes the NetworkList before scene providers (machines, bins)
            // unregister in OnDisable while the gameplay scene unloads; nothing is left to replicate to.
            if (NetworkManager == null || !NetworkManager.IsListening || NetworkManager.ShutdownInProgress)
                return;

            // If another provider immediately took ownership of the same hash, keep the slot.
            if (StateRegistry.TryGet(keyHash, out _))
                return;

            try
            {
                RemoveServerStateEntry(keyHash);
            }
            catch (ObjectDisposedException)
            {
                // Same teardown race when the list is disposed before NGO reports the shutdown
            }
        }

        void RemoveServerStateEntry(uint keyHash)
        {
            if (!serverStateIndexByHash.TryGetValue(keyHash, out int idx))
                return;

            serverStateIndexByHash.Remove(keyHash);
            if (replicatedStates == null || idx < 0 || idx >= replicatedStates.Count)
                return;

            replicatedStates.RemoveAt(idx);
            // Rebuild indices for shifted elements so every live hash maps to its exact NetworkList index.
            for (int i = idx; i < replicatedStates.Count; i++)
                serverStateIndexByHash[replicatedStates[i].keyHash] = i;
        }

        void PruneUnregisteredServerStates()
        {
            if (serverStateIndexByHash.Count == 0 || replicatedStates == null)
                return;

            scratchStaleHashes.Clear();
            foreach (KeyValuePair<uint, int> kv in serverStateIndexByHash)
            {
                if (!StateRegistry.TryGet(kv.Key, out _))
                    scratchStaleHashes.Add(kv.Key);
            }

            for (int i = 0; i < scratchStaleHashes.Count; i++)
                RemoveServerStateEntry(scratchStaleHashes[i]);
            scratchStaleHashes.Clear();
        }

        void PushAllProviderStatesToNetwork(bool forceAll)
        {
            PruneUnregisteredServerStates();

            IReadOnlyList<ISyncedStateProvider> providers = StateRegistry.AllProviders;
            for (int i = 0; i < providers.Count; i++)
            {
                ISyncedStateProvider provider = providers[i];
                if (provider == null)
                    continue;
                PushProviderState(provider, forceAll);
            }
        }

        void PushProviderStateNow(ISyncedStateProvider provider)
        {
            if (IsSpawned && IsServer && provider != null)
                PushProviderState(provider, force: true);
        }

        void PushProviderState(ISyncedStateProvider provider, bool force)
        {
            uint hash = SyncedStateRegistry.ComputeKeyHash(provider.SyncKey);
            SyncedStateRecord captured = provider.CaptureSyncState();

            if (serverStateIndexByHash.TryGetValue(hash, out int idx) && idx >= 0 && idx < replicatedStates.Count)
            {
                ReplicatedStateEntry existing = replicatedStates[idx];
                SyncedStateRecord existingRec = existing.ToRecord();
                if (!force && existingRec.ApproximatelyEquals(in captured))
                    return;

                replicatedStates[idx] = ReplicatedStateEntry.From(hash, in captured);
            }
            else
            {
                int newIdx = replicatedStates.Count;
                replicatedStates.Add(ReplicatedStateEntry.From(hash, in captured));
                serverStateIndexByHash[hash] = newIdx;
            }
        }

        void OnClientStateListChanged(NetworkListEvent<ReplicatedStateEntry> changeEvent)
        {
            if (changeEvent.Type == NetworkListEvent<ReplicatedStateEntry>.EventType.Add
                || changeEvent.Type == NetworkListEvent<ReplicatedStateEntry>.EventType.Insert
                || changeEvent.Type == NetworkListEvent<ReplicatedStateEntry>.EventType.Value)
            {
                ReplicatedStateEntry entry = changeEvent.Value;
                SyncedStateRecord rec = entry.ToRecord();
                StateRegistry.TryApply(entry.keyHash, in rec);
            }
            else if (changeEvent.Type == NetworkListEvent<ReplicatedStateEntry>.EventType.Full)
            {
                ApplyAllReplicatedStatesToClient();
            }
            // Remove / RemoveAt / Clear automatically drop entries from replicatedStates so
            // TryGetReplicatedState and ReplicatedStateCount reflect removals immediately on clients.
        }

        void ApplyAllReplicatedStatesToClient()
        {
            if (replicatedStates == null)
                return;
            for (int i = 0; i < replicatedStates.Count; i++)
            {
                ReplicatedStateEntry entry = replicatedStates[i];
                SyncedStateRecord rec = entry.ToRecord();
                StateRegistry.TryApply(entry.keyHash, in rec);
            }
        }

        // ---------------------------------------------------------------- Wallet Sync

        void OnServerWalletChanged(int newBalance, int delta)
        {
            if (IsSpawned && IsServer)
                teamCoins.Value = newBalance;
        }

        void OnTeamCoinsChanged(int previous, int current)
        {
            if (ReferenceEquals(Active, this))
                ApplyCoinsToLocalInventory(current);
            TeamCoinsChanged?.Invoke(current);
        }

        void OnLocalPlayerRegistered(PlayerController player)
        {
            if (ReferenceEquals(Active, this) && player != null && player.Inventory != null)
                player.Inventory.SetCoinsFromNetwork(teamCoins.Value);
        }

        static void ApplyCoinsToLocalInventory(int coins)
        {
            if (PlayerContext.ActiveInventory != null)
                PlayerContext.ActiveInventory.SetCoinsFromNetwork(coins);
        }

        static bool TryGetNetworkObjectId(GameObject go, out ulong netId)
        {
            netId = 0UL;
            if (go == null)
                return false;
            NetworkObject netObj = go.GetComponent<NetworkObject>();
            if (netObj == null || !netObj.IsSpawned)
                return false;
            netId = netObj.NetworkObjectId;
            return true;
        }

        static NetworkTrashDescriptor BuildDescriptor(WasteCatalog.Entry entry, WasteSourceKind sourceKind, short entryIndex)
        {
            if (entry == null)
                return NetworkTrashDescriptor.CreateFallback("Litter", WasteStream.Plastic, sourceKind, entryIndex: entryIndex);

            return NetworkTrashDescriptor.CreateFallback(
                entry.displayName,
                entry.stream,
                sourceKind,
                entry.pollutionWeight,
                entry.baseCoinValue,
                entry.mass,
                entry.isBulky,
                entry.carrySpeedMultiplier,
                entry.holdDistanceOffset,
                entryIndex,
                entry.ResolvedArchetypeId);
        }
    }
}
