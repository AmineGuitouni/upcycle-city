using System;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UpcycleCity.Interaction;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Co-op side of a pushable prop (the Shopping Cart): the host owns and simulates it while it's free; when a player
    /// starts pushing, the host hands NGO ownership to that player so they drive the body locally with no lag, and the
    /// owner-authoritative <see cref="NetworkTransform"/> streams it to everyone else (kinematic, interpolated). Letting
    /// go returns ownership to the server. If the pusher disconnects NGO hands ownership back to the server and the cart
    /// notices (<see cref="ControllerClientId"/> no longer matches its pusher) and frees itself.
    /// <para>Who pushes and what's inside stay in the cart's host-authoritative synced records; this component only moves
    /// simulation authority, so gameplay code stays Netcode-free (<see cref="INetworkedPushable"/>).</para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkTransform))]
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("Upcycle City/Networking/Networked Shopping Cart")]
    public sealed class NetworkedShoppingCart : NetworkBehaviour, INetworkedPushable
    {
        NetworkTransform networkTransform;

        public event Action AuthorityChanged;

        public bool IsNetworkSpawned => IsSpawned;

        public bool HasLocalAuthority => !IsSpawned || IsOwner;

        public ulong ControllerClientId => IsSpawned ? OwnerClientId : NetworkManager.ServerClientId;

        void OnValidate() => Configure();

        void Awake()
        {
            networkTransform = GetComponent<NetworkTransform>();
            Configure();
        }

        void Configure()
        {
            NetworkTransform nt = networkTransform != null ? networkTransform : GetComponent<NetworkTransform>();
            if (nt != null)
                nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            NetworkObject netObj = GetComponent<NetworkObject>();
            if (netObj != null && !netObj.DontDestroyWithOwner)
                netObj.DontDestroyWithOwner = true;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            AuthorityChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            AuthorityChanged?.Invoke();
        }

        public override void OnGainedOwnership()
        {
            base.OnGainedOwnership();
            AuthorityChanged?.Invoke();
        }

        public override void OnLostOwnership()
        {
            base.OnLostOwnership();
            AuthorityChanged?.Invoke();
        }

        public void AuthoritativeGrantControl(ulong clientId)
        {
            if (!IsSpawned || !IsServer)
                return;
            if (clientId == NetworkManager.ServerClientId)
            {
                AuthoritativeReclaimControl();
                return;
            }
            if (OwnerClientId != clientId && NetworkManager.ConnectedClients.ContainsKey(clientId))
                NetworkObject.ChangeOwnership(clientId);
        }

        public void AuthoritativeReclaimControl()
        {
            if (!IsSpawned || !IsServer)
                return;
            if (OwnerClientId != NetworkManager.ServerClientId)
                NetworkObject.RemoveOwnership();
        }
    }
}
