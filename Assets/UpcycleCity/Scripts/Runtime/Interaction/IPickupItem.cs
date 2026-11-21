using UnityEngine;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Implemented strictly by items that belong in the player's inventory (tools, usables, coins).
    /// Physical grabbables like TrashItem intentionally implement IGrabbable instead of IPickupItem.
    /// </summary>
    public interface IPickupItem
    {
        Transform Transform { get; }
        string PickupPrompt { get; }
        bool CanCollect(PlayerInteractor interactor);
        bool TryCollect(PlayerInteractor interactor);

        /// <summary>Called when the player's crosshair begins targeting this pickup.</summary>
        void OnFocusEnter(PlayerInteractor interactor) { }

        /// <summary>Called when the player's crosshair stops targeting this pickup.</summary>
        void OnFocusExit(PlayerInteractor interactor) { }
    }
}
