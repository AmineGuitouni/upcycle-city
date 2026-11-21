using System;
using UnityEngine;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Lightweight runtime service locator for the active player instance so stations, crates,
    /// HUD, pollution zones, and events never rely on expensive scene-wide FindAnyObjectByType calls.
    /// </summary>
    public static class PlayerContext
    {
        public static PlayerController Active { get; private set; }
        public static PlayerInventory ActiveInventory => Active != null ? Active.Inventory : null;
        public static PlayerInteractor ActiveInteractor => Active != null ? Active.Interactor : null;
        public static PhysicsGrabController ActiveGrabber => Active != null ? Active.Grabber : null;
        public static PlayerFeedbackHub ActiveFeedback => Active != null ? Active.Feedback : null;
        public static PlayerModifiers ActiveModifiers => Active != null ? Active.Modifiers : null;
        public static bool HasActivePlayer => Active != null;

        public static event Action<PlayerController> PlayerRegistered;
        public static event Action<PlayerController> PlayerUnregistered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
            PlayerRegistered = null;
            PlayerUnregistered = null;
        }

        internal static void Register(PlayerController player)
        {
            if (player == null || Active == player)
                return;
            Active = player;
            PlayerRegistered?.Invoke(player);
        }

        internal static void Unregister(PlayerController player)
        {
            if (player == null || Active != player)
                return;
            Active = null;
            PlayerUnregistered?.Invoke(player);
        }
    }
}
