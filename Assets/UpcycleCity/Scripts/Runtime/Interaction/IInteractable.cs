using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Waste;

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

    /// <summary>
    /// Implemented by machines or intake stations that accept physical <see cref="TrashItem"/>s
    /// into the shared team Material Stock through <see cref="IGameAuthority.RequestDepositTrash"/>.
    /// </summary>
    public interface ITrashHopper
    {
        /// <summary>Stable unique identifier for this hopper (independent of world position or state sync).</summary>
        string HopperKey { get; }

        Transform Transform { get; }

        /// <summary>Executes authoritative hopper intake on host/solo, disposing <paramref name="item"/> once credited.</summary>
        bool ExecuteAuthoritativeDeposit(
            TrashItem item,
            PlayerInteractor interactor,
            out string message,
            out Color feedbackColor,
            bool emitLocalFeedback = true);
    }
}
