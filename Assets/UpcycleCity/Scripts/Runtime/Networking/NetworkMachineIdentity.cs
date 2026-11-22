using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Replicates a machine's stable placement ID across host and clients (including late joiners
    /// and runtime-spawned workshop build-mode machines). Keeps custom-action handlers,
    /// <see cref="SyncedStateRegistry"/> keys, and <see cref="Interaction.ITrashHopper"/> keys
    /// completely independent of world position so machines can be moved, rotated, picked up, or spawned
    /// dynamically without breaking sync.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [AddComponentMenu("Upcycle City/Networking/Network Machine Identity")]
    public sealed class NetworkMachineIdentity : NetworkBehaviour, INetworkMachineIdentityProvider
    {
        readonly NetworkVariable<FixedString64Bytes> replicatedPlacementId = new NetworkVariable<FixedString64Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        bool hasPendingId;
        string pendingPlacementId = string.Empty;

        /// <summary>True when in multiplayer and this NetworkObject has not yet completed <see cref="OnNetworkSpawn"/>.</summary>
        public bool IsAwaitingNetworkSpawn => GameAuthority.IsMultiplayer && !IsSpawned;

        /// <summary>Current replicated placement ID (empty until spawned or pre-configured).</summary>
        public string ReplicatedPlacementId =>
            replicatedPlacementId.Value.IsEmpty ? pendingPlacementId : replicatedPlacementId.Value.ToString();

        /// <summary>
        /// Host-only setter for assigning an explicit placement ID (e.g. when restoring from workshop save data
        /// or spawning in build mode). Safe to call either before or after <see cref="NetworkObject.Spawn"/>.
        /// </summary>
        public void SetAuthoritativePlacementId(string placementId)
        {
            string clean = string.IsNullOrWhiteSpace(placementId) ? string.Empty : placementId.Trim();
            if (IsSpawned)
            {
                if (!IsServer)
                    return;
                replicatedPlacementId.Value = new FixedString64Bytes(clean);
                ApplyToReceivers(clean);
                return;
            }

            pendingPlacementId = clean;
            hasPendingId = !string.IsNullOrEmpty(clean);
            if (hasPendingId)
                ApplyToReceivers(clean);
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            SetReplicaModeOnReceivers(!IsServer);

            if (IsServer)
            {
                string id = hasPendingId ? pendingPlacementId : ReadExistingReceiverId();
                hasPendingId = false;

                if (string.IsNullOrWhiteSpace(id))
                    id = Guid.NewGuid().ToString("N").Substring(0, 12);

                replicatedPlacementId.Value = new FixedString64Bytes(id);
            }

            replicatedPlacementId.OnValueChanged += OnPlacementIdChanged;
            if (!replicatedPlacementId.Value.IsEmpty)
                ApplyToReceivers(replicatedPlacementId.Value.ToString());
        }

        public override void OnNetworkDespawn()
        {
            replicatedPlacementId.OnValueChanged -= OnPlacementIdChanged;
            SetReplicaModeOnReceivers(false);
            base.OnNetworkDespawn();
        }

        void SetReplicaModeOnReceivers(bool isClientReplica)
        {
            IMachineIdentityReceiver[] receivers = GetComponents<IMachineIdentityReceiver>();
            for (int i = 0; i < receivers.Length; i++)
                receivers[i]?.SetClientReplicaMode(isClientReplica);
        }

        void OnPlacementIdChanged(FixedString64Bytes previous, FixedString64Bytes current)
        {
            if (!current.IsEmpty)
                ApplyToReceivers(current.ToString());
        }

        string ReadExistingReceiverId()
        {
            IMachineIdentityReceiver[] receivers = GetComponents<IMachineIdentityReceiver>();
            for (int i = 0; i < receivers.Length; i++)
            {
                if (receivers[i] != null && !string.IsNullOrWhiteSpace(receivers[i].PlacementId))
                    return receivers[i].PlacementId;
            }
            return string.Empty;
        }

        void ApplyToReceivers(string placementId)
        {
            if (string.IsNullOrWhiteSpace(placementId))
                return;

            IMachineIdentityReceiver[] receivers = GetComponents<IMachineIdentityReceiver>();
            for (int i = 0; i < receivers.Length; i++)
                receivers[i]?.ApplyPlacementId(placementId);
        }
    }
}
