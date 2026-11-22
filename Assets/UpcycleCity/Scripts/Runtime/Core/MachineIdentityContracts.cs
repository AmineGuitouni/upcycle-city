using System;
using UnityEngine;

namespace UpcycleCity.Core
{
    /// <summary>
    /// Implemented by placeable/networked machines whose stable placement ID may be assigned by the host
    /// at runtime (e.g. workshop build mode) and replicated to clients via <c>NetworkMachineIdentity</c>.
    /// </summary>
    public interface IMachineIdentityReceiver
    {
        /// <summary>Stable placement ID (scene-serialized GUID or host-assigned runtime GUID).</summary>
        string PlacementId { get; }

        /// <summary>
        /// Applies a new placement ID, re-registering custom action handlers, synced state entries,
        /// and hopper entries under the new stable key if the ID changed.
        /// </summary>
        void ApplyPlacementId(string newPlacementId);

        /// <summary>
        /// Marks this instance as a non-authoritative client replica of a network-spawned machine so it
        /// suppresses host-side custom-action handler registration, hopper registration, and completion ticks.
        /// </summary>
        void SetClientReplicaMode(bool isClientReplica);
    }

    /// <summary>
    /// Implemented by <c>NetworkMachineIdentity</c> so machines can detect when their placement ID will be
    /// supplied by network spawn replication, avoiding throwaway GUIDs or premature registration before
    /// <c>OnNetworkSpawn</c> without coupling gameplay assemblies to <c>Unity.Netcode</c>.
    /// </summary>
    public interface INetworkMachineIdentityProvider
    {
        /// <summary>True when running in multiplayer and the owning <c>NetworkObject</c> has not spawned yet.</summary>
        bool IsAwaitingNetworkSpawn { get; }
    }

    /// <summary>
    /// Serializable snapshot of a placed machine's identity, upgrade tier, transform, and optional in-progress job.
    /// Designed for workshop build-mode persistence (save/load and host-authoritative placement).
    /// </summary>
    [Serializable]
    public struct MachineSaveState
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion;
        public string placementId;
        public string machineId;
        public int level;
        public Vector3 position;
        public Quaternion rotation;
        /// <summary>Active crafting card ID if a job was processing when saved (empty when idle).</summary>
        public string activeCardId;
        /// <summary>Remaining processing seconds for <see cref="activeCardId"/> (0 when idle).</summary>
        public float remainingProcessingSeconds;
    }
}
