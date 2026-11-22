using UnityEngine;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Optional networking hook implemented by <c>NetworkedPhysicsItem</c> in <c>UpcycleCity.Networking</c>.
    /// Allows <c>PhysicsGrabController</c> and <c>TrashItem</c> to cooperate with ownership transfer, grab locking,
    /// and authoritative despawning while keeping <c>UpcycleCity.Runtime</c> completely free of Netcode dependencies.
    /// </summary>
    public interface INetworkedPhysicsGrabbable
    {
        /// <summary>True when any player (local or remote) currently holds this item.</summary>
        bool IsHeldByAnyone { get; }

        /// <summary>False when another co-op player currently holds this item.</summary>
        bool CanLocalGrab { get; }

        /// <summary>Called when the local player begins a pickup anticipation so ownership can be requested early.</summary>
        void OnLocalPickupStarted();

        /// <summary>Called if a local pickup anticipation is cancelled before grabbing.</summary>
        void OnLocalPickupCancelled();

        /// <summary>Called when the local player drops or throws the item.</summary>
        void OnLocalReleased(Vector3 releaseVelocity, bool thrown);

        /// <summary>
        /// Called by <c>TrashItem.Dispose()</c>. Returns true if the networking layer took responsibility
        /// for despawning the object (or suppressing client-side local destruction).
        /// </summary>
        bool HandleDispose();
    }
}
