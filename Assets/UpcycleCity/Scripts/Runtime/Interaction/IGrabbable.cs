using UnityEngine;
using UpcycleCity.Player;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Implemented by any physical world object that can be grabbed one-at-a-time via physics
    /// (trash items, bulky objects, crates, crafted furniture, repair parts, etc.).
    /// </summary>
    public interface IGrabbable
    {
        Transform Transform { get; }
        Rigidbody Body { get; }
        string DisplayName { get; }
        string GrabPrompt { get; }
        bool IsHeld { get; }
        bool IsBulky { get; }
        float CarrySpeedMultiplier { get; }
        float HoldDistanceOffset { get; }

        bool CanGrab(PhysicsGrabController grabber);
        void OnGrabbed(PhysicsGrabController grabber);
        void OnReleased(PhysicsGrabController grabber, Vector3 releaseVelocity, bool thrown);
        void SetIgnoreCollision(Collider other, bool ignore);

        /// <summary>Called when the player's crosshair begins targeting this grabbable.</summary>
        void OnFocusEnter(PlayerInteractor interactor) { }

        /// <summary>Called when the player's crosshair stops targeting this grabbable.</summary>
        void OnFocusExit(PlayerInteractor interactor) { }
    }
}
