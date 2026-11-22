using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UpcycleCity.Interaction;
using UpcycleCity.Player;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Reusable networked physics-grabbable component. Add this component (alongside <see cref="NetworkObject"/>,
    /// owner-authoritative <see cref="NetworkTransform"/>, and <see cref="Rigidbody"/>) to ANY trash, tool,
    /// crafted item, or prop prefab to make it co-op ready:
    /// <list type="bullet">
    /// <item>Host-validated mutual exclusion so two players can never hold the same item.</item>
    /// <item>Owner transfer during pickup anticipation so local holding feels immediate and lag-free.</item>
    /// <item>Server ownership reclaim + velocity replication on drop/throw.</item>
    /// <item>Automatic drop when a holding player disconnects.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkTransform))]
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("Upcycle City/Networking/Networked Physics Item")]
    public sealed class NetworkedPhysicsItem : NetworkBehaviour, INetworkedPhysicsGrabbable
    {
        public const ulong NoHolder = ulong.MaxValue;
        const float MaxGrabRange = 8.0f;
        const float MaxReleaseSnapDistance = 6.5f;
        const float MaxReleaseSpeed = 25.0f;

        static readonly List<NetworkedPhysicsItem> ActiveItems = new List<NetworkedPhysicsItem>(64);

        readonly NetworkVariable<ulong> holderClientId = new NetworkVariable<ulong>(
            NoHolder,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        Rigidbody body;
        NetworkTransform networkTransform;
        IGrabbable grabbable;
        ulong lastHolderClientId = NoHolder;

        /// <summary>ClientId of the player currently holding this item, or <see cref="NoHolder"/> when free.</summary>
        public ulong HolderClientId => holderClientId.Value;

        /// <summary>Server-side ClientId of the most recent player who held or threw this item, or <see cref="NoHolder"/> if untouched.</summary>
        public ulong LastHolderClientId => lastHolderClientId;

        /// <summary>True when any player currently holds this item.</summary>
        public bool IsHeldByAnyone => holderClientId.Value != NoHolder;

        /// <summary>True if the local player is allowed to grab this item right now.</summary>
        public bool CanLocalGrab
        {
            get
            {
                if (!IsSpawned)
                    return true;
                ulong holder = holderClientId.Value;
                return holder == NoHolder || holder == NetworkManager.LocalClientId;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            ActiveItems.Clear();
        }

        /// <summary>Server helper: releases any items held by a client that just disconnected.</summary>
        public static void ReleaseAllHeldBy(ulong clientId)
        {
            for (int i = ActiveItems.Count - 1; i >= 0; i--)
            {
                NetworkedPhysicsItem item = ActiveItems[i];
                if (item == null || !item.IsSpawned || !item.IsServer)
                    continue;
                if (item.holderClientId.Value == clientId)
                    item.ForceServerRelease(item.transform.position, item.transform.rotation, Vector3.zero);
            }
        }

        void OnValidate()
        {
            EnsureDontDestroyWithOwner();
        }

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            networkTransform = GetComponent<NetworkTransform>();
            grabbable = GetComponent<IGrabbable>();
            if (networkTransform != null)
                networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            EnsureDontDestroyWithOwner();
        }

        void EnsureDontDestroyWithOwner()
        {
            NetworkObject netObj = GetComponent<NetworkObject>();
            if (netObj != null && !netObj.DontDestroyWithOwner)
                netObj.DontDestroyWithOwner = true;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            EnsureDontDestroyWithOwner();
            lastHolderClientId = NoHolder;
            if (!ActiveItems.Contains(this))
                ActiveItems.Add(this);

            holderClientId.OnValueChanged += OnHolderChanged;
            RefreshKinematicState();
        }

        public override void OnNetworkDespawn()
        {
            holderClientId.OnValueChanged -= OnHolderChanged;
            lastHolderClientId = NoHolder;
            ActiveItems.Remove(this);
            ForceReleaseLocalGrabber();
            base.OnNetworkDespawn();
        }

        public override void OnGainedOwnership()
        {
            base.OnGainedOwnership();
            RefreshKinematicState();
        }

        public override void OnLostOwnership()
        {
            base.OnLostOwnership();
            RefreshKinematicState();
            ForceReleaseLocalGrabber();
        }

        public void OnLocalPickupStarted()
        {
            if (!IsSpawned)
                return;
            RequestGrabServerRpc();
        }

        public void OnLocalPickupCancelled()
        {
            if (!IsSpawned)
                return;
            if (holderClientId.Value == NetworkManager.LocalClientId)
                ReleaseServerRpc(transform.position, transform.rotation, Vector3.zero, false);
        }

        public void OnLocalReleased(Vector3 releaseVelocity, bool thrown)
        {
            if (!IsSpawned)
                return;
            ReleaseServerRpc(transform.position, transform.rotation, releaseVelocity, thrown);
        }

        public bool HandleDispose()
        {
            if (!IsSpawned)
                return false;

            if (IsServer)
            {
                if (NetworkObject != null && NetworkObject.IsSpawned)
                    NetworkObject.Despawn(true);
            }

            // On clients, suppress local Destroy(gameObject) — the host's Despawn replicates cleanly
            return true;
        }

        void ForceReleaseLocalGrabber()
        {
            if (grabbable != null && PlayerContext.Active != null && PlayerContext.Active.Grabber != null)
                PlayerContext.Active.Grabber.ForceReleaseIfHolding(grabbable);
        }

        void OnHolderChanged(ulong previous, ulong current)
        {
            RefreshKinematicState();
            if (current != NoHolder && current != NetworkManager.LocalClientId)
                ForceReleaseLocalGrabber();
        }

        void RefreshKinematicState()
        {
            if (body == null || !IsSpawned)
                return;

            // Owner simulates locally (Server when free, grabbing client while held); non-owners follow NetworkTransform
            bool shouldSimulate = IsOwner;
            if (body.isKinematic == shouldSimulate)
                body.isKinematic = !shouldSimulate;

            if (shouldSimulate)
            {
                bool heldByLocal = holderClientId.Value == NetworkManager.LocalClientId;
                body.useGravity = !heldByLocal;
                body.linearDamping = heldByLocal ? 4f : 0.25f;
                body.angularDamping = heldByLocal ? 6f : 0.5f;
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RequestGrabServerRpc(RpcParams rpcParams = default)
        {
            ulong senderId = rpcParams.Receive.SenderClientId;
            ulong currentHolder = holderClientId.Value;
            if (currentHolder != NoHolder && currentHolder != senderId)
            {
                GrabRejectedClientRpc(RpcTarget.Single(senderId, RpcTargetUse.Temp));
                return;
            }

            if (TryGetSenderPosition(senderId, out Vector3 senderPos)
                && Vector3.Distance(senderPos, transform.position) > MaxGrabRange)
            {
                GrabRejectedClientRpc(RpcTarget.Single(senderId, RpcTargetUse.Temp));
                return;
            }

            holderClientId.Value = senderId;
            lastHolderClientId = senderId;
            if (NetworkObject.OwnerClientId != senderId)
                NetworkObject.ChangeOwnership(senderId);

            RefreshKinematicState();
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void GrabRejectedClientRpc(RpcParams rpcParams = default)
        {
            ForceReleaseLocalGrabber();
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ReleaseServerRpc(Vector3 position, Quaternion rotation, Vector3 velocity, bool thrown, RpcParams rpcParams = default)
        {
            ulong senderId = rpcParams.Receive.SenderClientId;
            // Strictly only the active holder may release and supply a drop/throw pose & velocity
            if (holderClientId.Value != senderId)
                return;

            lastHolderClientId = senderId;
            Vector3 safePos = SanitizeReleasePosition(senderId, position);
            Quaternion safeRot = IsFinite(rotation) ? rotation.normalized : transform.rotation;
            Vector3 safeVel = SanitizeVelocity(velocity);

            ForceServerRelease(safePos, safeRot, safeVel);
        }

        void ForceServerRelease(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            holderClientId.Value = NoHolder;
            if (NetworkObject.OwnerClientId != NetworkManager.ServerClientId)
                NetworkObject.RemoveOwnership();

            transform.SetPositionAndRotation(position, rotation);
            if (body != null)
            {
                body.isKinematic = false;
                body.useGravity = true;
                body.position = position;
                body.rotation = rotation;
                body.linearDamping = 0.25f;
                body.angularDamping = 0.5f;
                body.linearVelocity = velocity;
            }

            if (networkTransform != null && IsSpawned)
                networkTransform.Teleport(position, rotation, transform.localScale);
        }

        bool TryGetSenderPosition(ulong senderId, out Vector3 pos)
        {
            if (NetworkManager != null
                && NetworkManager.ConnectedClients.TryGetValue(senderId, out NetworkClient client)
                && client != null
                && client.PlayerObject != null)
            {
                pos = client.PlayerObject.transform.position;
                return true;
            }
            pos = Vector3.zero;
            return false;
        }

        Vector3 SanitizeReleasePosition(ulong senderId, Vector3 requestedPos)
        {
            if (!IsFinite(requestedPos))
                return transform.position;

            Vector3 anchor = TryGetSenderPosition(senderId, out Vector3 senderPos)
                ? senderPos
                : transform.position;

            Vector3 offset = requestedPos - anchor;
            if (offset.magnitude > MaxReleaseSnapDistance)
                return anchor + offset.normalized * MaxReleaseSnapDistance;
            return requestedPos;
        }

        static Vector3 SanitizeVelocity(Vector3 velocity)
        {
            if (!IsFinite(velocity))
                return Vector3.zero;
            float mag = velocity.magnitude;
            return mag > MaxReleaseSpeed ? velocity * (MaxReleaseSpeed / mag) : velocity;
        }

        static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        static bool IsFinite(Quaternion q) =>
            float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w)
            && (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 0.0001f;
    }
}
