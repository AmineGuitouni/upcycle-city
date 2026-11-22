using System;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Implemented by the networking layer (<c>NetworkedShoppingCart</c>) for a physics prop one player at a time
    /// drives, such as the Shopping Cart. Keeps gameplay code Netcode-free: the host hands control (NGO ownership, so the
    /// pusher simulates the body locally and its owner-authoritative transform replicates) to the pusher and takes it
    /// back on release. Without a spawned network object (solo) the local machine always simulates.
    /// </summary>
    public interface INetworkedPushable
    {
        /// <summary>True while the object is spawned in an online session.</summary>
        bool IsNetworkSpawned { get; }

        /// <summary>True when this machine simulates the body (the owner in co-op; always in solo).</summary>
        bool HasLocalAuthority { get; }

        /// <summary>Client id that currently simulates the body (the server when nobody pushes).</summary>
        ulong ControllerClientId { get; }

        /// <summary>Host: gives control (ownership) to <paramref name="clientId"/>.</summary>
        void AuthoritativeGrantControl(ulong clientId);

        /// <summary>Host: takes control back to the server.</summary>
        void AuthoritativeReclaimControl();

        /// <summary>Raised on any machine when it gains or loses control of the body.</summary>
        event Action AuthorityChanged;
    }
}
