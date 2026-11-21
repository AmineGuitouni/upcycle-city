using UnityEngine;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Implemented by world stations, buttons, crates, doors, and machines the player can interact with.
    /// </summary>
    public interface IInteractable
    {
        Transform Transform { get; }
        string InteractionPrompt { get; }
        bool CanInteract(PlayerInteractor interactor);
        void Interact(PlayerInteractor interactor);

        /// <summary>Called when the player's crosshair begins targeting this interactable.</summary>
        void OnFocusEnter(PlayerInteractor interactor) { }

        /// <summary>Called when the player's crosshair stops targeting this interactable.</summary>
        void OnFocusExit(PlayerInteractor interactor) { }
    }
}
