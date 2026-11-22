using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Bridges the local first-person <see cref="PlayerController"/> prefab with Netcode for GameObjects:
    /// <list type="bullet">
    /// <item>Owner keeps camera, input, <see cref="FirstPersonArms"/>, <see cref="AudioListener"/>, and HUD,
    /// driving movement with owner-authoritative <see cref="NetworkTransform"/> so arcade Rigidbody feel is preserved.</item>
    /// <item>Remote proxies disable all local-only components and render a visible third-person
    /// <see cref="CharacterAvatar"/> with their replicated <see cref="CharacterAppearance"/> plus subtle
    /// procedural walk bob, forward lean, and crouch squash.</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkTransform))]
    [RequireComponent(typeof(PlayerController))]
    [AddComponentMenu("Upcycle City/Networking/Network Player Controller")]
    public sealed class NetworkPlayerController : NetworkBehaviour, ISessionPlayer
    {
        const float PingSampleInterval = 1f;

        // Host-measured round-trip time to this player's client (0 for the host's own player)
        readonly NetworkVariable<ushort> syncedPingMs =
            new NetworkVariable<ushort>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        float pingTimer;
        string cachedName = "Player";

        static readonly List<NetworkPlayerController> ActivePlayers = new List<NetworkPlayerController>(4);
        static readonly List<NetworkPlayerController> RemotePlayersList = new List<NetworkPlayerController>(4);

        readonly NetworkVariable<NetworkCharacterAppearance> syncedAppearance =
            new NetworkVariable<NetworkCharacterAppearance>(
                NetworkCharacterAppearance.From(CharacterAppearance.Default),
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Owner);

        readonly NetworkVariable<FixedString32Bytes> syncedName =
            new NetworkVariable<FixedString32Bytes>(
                new FixedString32Bytes("Cleaner"),
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        readonly NetworkVariable<byte> syncedSlotNumber =
            new NetworkVariable<byte>(
                1,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        readonly NetworkVariable<byte> syncedCrouching =
            new NetworkVariable<byte>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Owner);

        readonly NetworkVariable<Vector3> syncedSpawnPosition =
            new NetworkVariable<Vector3>(
                Vector3.zero,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        readonly NetworkVariable<Quaternion> syncedSpawnRotation =
            new NetworkVariable<Quaternion>(
                Quaternion.identity,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        readonly NetworkVariable<byte> syncedHasSpawnPose =
            new NetworkVariable<byte>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        readonly NetworkVariable<FixedString32Bytes> syncedEquippedItemId =
            new NetworkVariable<FixedString32Bytes>(
                default,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Owner);

        PlayerController playerController;
        PlayerInputReader inputReader;
        PlayerMotor motor;
        PlayerCameraController cameraController;
        PhysicsGrabController grabber;
        PlayerInteractor interactor;
        PlayerFeedbackHub feedbackHub;
        FirstPersonArms firstPersonArms;
        PlayerGymHUD gymHud;
        PlayerBodyShadow bodyShadow;
        CharacterAvatar bodyAvatar;
        Rigidbody rb;
        NetworkTransform networkTransform;

        Vector3 previousPosition;
        Vector3 avatarBaseLocalPos;
        float smoothedSpeed;
        float bobPhase;
        float crouchScale = 1f;
        bool remoteConfigured;
        bool spawnPoseApplied;

        /// <summary>All currently spawned co-op players (local + remote).</summary>
        public static IReadOnlyList<NetworkPlayerController> AllPlayers => ActivePlayers;

        /// <summary>Currently spawned remote co-op players (used by <see cref="CoopInGameHud"/> for overhead nameplates).</summary>
        public static IReadOnlyList<NetworkPlayerController> RemotePlayers => RemotePlayersList;

        /// <summary>Current replicated appearance struct.</summary>
        public CharacterAppearance CurrentAppearance => syncedAppearance.Value.ToAppearance();

        /// <summary>Item ID of the tool or item currently equipped in this player's hotbar (cached; zero per-frame allocations).</summary>
        public string EquippedItemId => cachedEquippedItemId;

        /// <summary>Underlying <see cref="PlayerController"/> component.</summary>
        public PlayerController LocalController => playerController;

        /// <summary>Player display label (e.g. "P2 · Maya", cached; zero per-frame allocations).</summary>
        public string DisplayLabel => cachedDisplayLabel;

        /// <summary>Short player slot tag (e.g. "P2", cached; zero per-frame allocations).</summary>
        public string SlotTag => cachedSlotTag;

        string cachedDisplayLabel = "P1 · Cleaner";
        string cachedSlotTag = "P1";
        string cachedEquippedItemId = string.Empty;

        /// <summary>World-space anchor slightly above the player's head for screen-space nameplates.</summary>
        public Vector3 NameplateWorldPosition => transform.position + Vector3.up * (syncedCrouching.Value != 0 ? 1.55f : 2.08f);

        /// <summary>Looks up a spawned player controller by owner client ID.</summary>
        public static bool TryGetPlayer(ulong clientId, out NetworkPlayerController player)
        {
            for (int i = 0; i < ActivePlayers.Count; i++)
            {
                NetworkPlayerController candidate = ActivePlayers[i];
                if (candidate != null && candidate.IsSpawned && candidate.OwnerClientId == clientId)
                {
                    player = candidate;
                    return true;
                }
            }
            player = null;
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            ActivePlayers.Clear();
            RemotePlayersList.Clear();
        }

        /// <summary>Server setup called right after spawning to assign slot index, display name, and authoritative spawn pose.</summary>
        public void InitializeServerIdentity(
            byte slotNumber,
            string playerName,
            NetworkCharacterAppearance initialAppearance,
            Vector3? spawnPosition = null,
            Quaternion? spawnRotation = null)
        {
            if (!IsServer)
                return;
            syncedSlotNumber.Value = slotNumber > 0 ? slotNumber : (byte)1;
            syncedName.Value = new FixedString32Bytes(string.IsNullOrWhiteSpace(playerName) ? $"Player {slotNumber}" : playerName);
            RebuildCachedIdentityLabels();
            if (IsOwner)
                syncedAppearance.Value = initialAppearance;

            if (spawnPosition.HasValue)
            {
                syncedSpawnPosition.Value = spawnPosition.Value;
                syncedSpawnRotation.Value = spawnRotation ?? Quaternion.identity;
                syncedHasSpawnPose.Value = 1;
                if (IsOwner)
                    ApplyOwnerSpawnPose();
            }
        }

        void Awake()
        {
            playerController = GetComponent<PlayerController>();
            inputReader = GetComponent<PlayerInputReader>();
            motor = GetComponent<PlayerMotor>();
            cameraController = GetComponent<PlayerCameraController>();
            grabber = GetComponent<PhysicsGrabController>();
            interactor = GetComponent<PlayerInteractor>();
            feedbackHub = GetComponent<PlayerFeedbackHub>();
            firstPersonArms = GetComponent<FirstPersonArms>();
            gymHud = GetComponent<PlayerGymHUD>();
            bodyShadow = GetComponentInChildren<PlayerBodyShadow>(true);
            bodyAvatar = GetComponentInChildren<CharacterAvatar>(true);
            rb = GetComponent<Rigidbody>();
            networkTransform = GetComponent<NetworkTransform>();

            if (networkTransform != null)
                networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;

            if (bodyAvatar != null)
                avatarBaseLocalPos = bodyAvatar.transform.localPosition;

            // Prevent remote instances from registering into PlayerContext before OnNetworkSpawn resolves IsOwner
            if (playerController != null)
                playerController.SuppressContextRegistration = true;

            RebuildCachedIdentityLabels();
            RebuildCachedEquippedItemId();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (!ActivePlayers.Contains(this))
                ActivePlayers.Add(this);
            GameSession.Register(this);

            syncedAppearance.OnValueChanged += OnAppearanceChanged;
            syncedHasSpawnPose.OnValueChanged += OnSpawnPoseReady;
            syncedSlotNumber.OnValueChanged += OnSlotNumberChanged;
            syncedName.OnValueChanged += OnSyncedNameChanged;
            syncedEquippedItemId.OnValueChanged += OnSyncedEquippedItemChanged;

            RebuildCachedIdentityLabels();
            RebuildCachedEquippedItemId();

            if (IsOwner)
            {
                ConfigureAsLocalPlayer();
            }
            else
            {
                ConfigureAsRemotePlayer();
            }
        }

        public override void OnNetworkDespawn()
        {
            syncedAppearance.OnValueChanged -= OnAppearanceChanged;
            syncedHasSpawnPose.OnValueChanged -= OnSpawnPoseReady;
            syncedSlotNumber.OnValueChanged -= OnSlotNumberChanged;
            syncedName.OnValueChanged -= OnSyncedNameChanged;
            syncedEquippedItemId.OnValueChanged -= OnSyncedEquippedItemChanged;
            CharacterAppearanceStore.Changed -= OnLocalAppearanceChanged;
            if (playerController != null && playerController.Inventory != null)
                playerController.Inventory.Changed -= SyncLocalEquippedItem;
            ActivePlayers.Remove(this);
            RemotePlayersList.Remove(this);
            GameSession.Unregister(this);
            base.OnNetworkDespawn();
        }

        void OnSlotNumberChanged(byte previous, byte current) => RebuildCachedIdentityLabels();

        void OnSyncedNameChanged(FixedString32Bytes previous, FixedString32Bytes current) => RebuildCachedIdentityLabels();

        void OnSyncedEquippedItemChanged(FixedString32Bytes previous, FixedString32Bytes current) => RebuildCachedEquippedItemId();

        void RebuildCachedIdentityLabels()
        {
            int slot = syncedSlotNumber.Value > 0 ? syncedSlotNumber.Value : 1;
            cachedSlotTag = $"P{slot}";
            string nameText = syncedName.Value.IsEmpty ? $"Player {slot}" : syncedName.Value.ToString();
            cachedName = nameText;
            cachedDisplayLabel = $"P{slot} · {nameText}";
        }

        void RebuildCachedEquippedItemId()
        {
            cachedEquippedItemId = syncedEquippedItemId.Value.IsEmpty ? string.Empty : syncedEquippedItemId.Value.ToString();
        }

        void ConfigureAsLocalPlayer()
        {
            remoteConfigured = false;
            RemotePlayersList.Remove(this);

            if (rb != null)
                rb.isKinematic = false;

            ApplyOwnerSpawnPose();

            if (playerController != null)
            {
                playerController.SuppressContextRegistration = false;
                playerController.enabled = true;
                PlayerContext.Register(playerController);
            }

            if (gymHud != null)
            {
                string activeScene = SceneManager.GetActiveScene().name;
                if (activeScene == "EcosystemSandbox")
                    gymHud.ConfigureDisplay("AVENUE HABIB BOURGUIBA · CO-OP", showControls: false);
                gymHud.enabled = true;
            }

            syncedAppearance.Value = NetworkCharacterAppearance.From(CharacterAppearanceStore.Current);
            CharacterAppearanceStore.Changed += OnLocalAppearanceChanged;

            if (playerController != null && playerController.Inventory != null)
            {
                playerController.Inventory.Changed -= SyncLocalEquippedItem;
                playerController.Inventory.Changed += SyncLocalEquippedItem;
                SyncLocalEquippedItem();
            }

            if (NetworkStateReplicator.Active != null && playerController != null && playerController.Inventory != null)
                playerController.Inventory.SetCoinsFromNetwork(NetworkStateReplicator.Active.TeamCoins);
        }

        void SyncLocalEquippedItem()
        {
            if (!IsSpawned || !IsOwner || playerController == null || playerController.Inventory == null)
                return;
            string itemId = playerController.Inventory.EquippedItem != null
                ? playerController.Inventory.EquippedItem.ItemId ?? string.Empty
                : string.Empty;
            var next = new FixedString32Bytes(itemId);
            if (!syncedEquippedItemId.Value.Equals(next))
            {
                syncedEquippedItemId.Value = next;
                cachedEquippedItemId = itemId;
            }
        }

        void OnSpawnPoseReady(byte previous, byte current)
        {
            if (current != 0 && IsOwner)
                ApplyOwnerSpawnPose();
        }

        void ApplyOwnerSpawnPose()
        {
            if (!IsOwner || spawnPoseApplied || syncedHasSpawnPose.Value == 0)
                return;

            spawnPoseApplied = true;
            Vector3 pos = syncedSpawnPosition.Value;
            Quaternion rot = syncedSpawnRotation.Value;

            transform.SetPositionAndRotation(pos, rot);
            if (rb != null)
            {
                rb.position = pos;
                rb.rotation = rot;
                if (!rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }

            if (networkTransform != null && IsSpawned)
                networkTransform.Teleport(pos, rot, transform.localScale);
        }

        void ConfigureAsRemotePlayer()
        {
            remoteConfigured = true;
            if (!RemotePlayersList.Contains(this))
                RemotePlayersList.Add(this);

            if (playerController != null)
            {
                playerController.SuppressContextRegistration = true;
                PlayerContext.Unregister(playerController);
                playerController.enabled = false;
            }

            if (inputReader != null) inputReader.enabled = false;
            if (motor != null) motor.enabled = false;
            if (cameraController != null) cameraController.enabled = false;
            if (grabber != null) grabber.enabled = false;
            if (interactor != null) interactor.enabled = false;
            if (feedbackHub != null) feedbackHub.enabled = false;
            if (firstPersonArms != null) firstPersonArms.enabled = false;
            if (gymHud != null) gymHud.enabled = false;
            if (bodyShadow != null) bodyShadow.enabled = false;

            // Disable cameras and audio listeners on remote player proxies
            Camera[] cams = GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null)
                    cams[i].enabled = false;
            }

            AudioListener[] listeners = GetComponentsInChildren<AudioListener>(true);
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null)
                    listeners[i].enabled = false;
            }

            if (rb != null)
                rb.isKinematic = true;

            // Show full third-person character avatar with replicated appearance
            if (bodyAvatar != null)
            {
                bodyAvatar.SetFollowSavedAppearance(false);
                bodyAvatar.SetShadowCastingMode(ShadowCastingMode.On);
                bodyAvatar.Apply(syncedAppearance.Value.ToAppearance());
                bodyAvatar.SetShadowCastingMode(ShadowCastingMode.On);
            }

            previousPosition = transform.position;
        }

        void OnLocalAppearanceChanged(CharacterAppearance next)
        {
            if (IsSpawned && IsOwner)
                syncedAppearance.Value = NetworkCharacterAppearance.From(next);
        }

        void OnAppearanceChanged(NetworkCharacterAppearance previous, NetworkCharacterAppearance current)
        {
            if (!IsOwner && bodyAvatar != null)
            {
                bodyAvatar.Apply(current.ToAppearance());
                bodyAvatar.SetShadowCastingMode(ShadowCastingMode.On);
            }
        }

        // ---------------------------------------------------------------- ISessionPlayer (pause menu)

        string ISessionPlayer.DisplayName => cachedName;
        int ISessionPlayer.Slot => syncedSlotNumber.Value > 0 ? syncedSlotNumber.Value : 1;
        bool ISessionPlayer.IsHost => IsSpawned && NetworkManager != null && OwnerClientId == NetworkManager.ServerClientId;
        bool ISessionPlayer.IsLocal => IsSpawned && IsOwner;

        int ISessionPlayer.PingMs
        {
            get
            {
                if (!IsSpawned || NetworkManager == null)
                    return -1;
                if (OwnerClientId == NetworkManager.ServerClientId)
                    return 0;
                // Our own ping is measured live on this client; other players' come from the host
                if (IsOwner && !IsServer && NetworkManager.NetworkConfig?.NetworkTransport != null)
                    return (int)NetworkManager.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.ServerClientId);
                return syncedPingMs.Value;
            }
        }

        // Host: sample each client's RTT from the transport about once a second (event-driven sync: only on change)
        void SamplePingOnServer()
        {
            pingTimer -= Time.unscaledDeltaTime;
            if (pingTimer > 0f)
                return;
            pingTimer = PingSampleInterval;
            ulong rtt = OwnerClientId == NetworkManager.ServerClientId || NetworkManager.NetworkConfig?.NetworkTransport == null
                ? 0UL
                : NetworkManager.NetworkConfig.NetworkTransport.GetCurrentRtt(OwnerClientId);
            ushort value = (ushort)Mathf.Min((float)rtt, ushort.MaxValue);
            if (syncedPingMs.Value != value)
                syncedPingMs.Value = value;
        }

        void Update()
        {
            if (!IsSpawned)
                return;

            if (IsServer)
                SamplePingOnServer();

            if (IsOwner)
            {
                byte crouching = motor != null && motor.IsCrouching ? (byte)1 : (byte)0;
                if (syncedCrouching.Value != crouching)
                    syncedCrouching.Value = crouching;
                return;
            }

            if (!remoteConfigured || bodyAvatar == null)
                return;

            // Procedural walk bob, forward lean, and crouch squash (zero allocations)
            float dt = Time.deltaTime;
            if (dt <= 0.0001f)
                return;

            Vector3 pos = transform.position;
            Vector3 delta = pos - previousPosition;
            previousPosition = pos;
            delta.y = 0f;
            float speed = delta.magnitude / dt;
            smoothedSpeed = Mathf.Lerp(smoothedSpeed, Mathf.Min(speed, 9f), 1f - Mathf.Exp(-10f * dt));

            bool moving = smoothedSpeed > 0.25f;
            if (moving)
                bobPhase += dt * Mathf.Lerp(7f, 12.5f, Mathf.Clamp01(smoothedSpeed / 6f));

            float bobAmp = moving ? Mathf.Clamp01(smoothedSpeed / 5f) * 0.045f : 0.004f;
            float bobY = Mathf.Abs(Mathf.Sin(bobPhase)) * bobAmp;
            float swayRoll = moving ? Mathf.Sin(bobPhase) * Mathf.Clamp01(smoothedSpeed / 5f) * 3.2f : 0f;
            float forwardLean = moving ? Mathf.Clamp01(smoothedSpeed / 6f) * 6.5f : 0f;

            float targetCrouch = syncedCrouching.Value != 0 ? 0.68f : 1f;
            crouchScale = Mathf.Lerp(crouchScale, targetCrouch, 1f - Mathf.Exp(-12f * dt));

            Transform avatarTr = bodyAvatar.transform;
            avatarTr.localPosition = new Vector3(avatarBaseLocalPos.x, avatarBaseLocalPos.y + bobY, avatarBaseLocalPos.z);
            avatarTr.localRotation = Quaternion.Euler(forwardLean, 0f, swayRoll);
            avatarTr.localScale = new Vector3(1f, crouchScale, 1f);
        }
    }
}
