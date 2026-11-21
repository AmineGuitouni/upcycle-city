using UnityEngine;

namespace UpcycleCity.Core
{
    /// <summary>
    /// Centralized Unity tag constants for Upcycle City.
    /// Kept in sync with ProjectSettings/TagManager.asset by ProjectLayersAndTagsSetup.
    /// </summary>
    public static class GameTags
    {
        // Unity built-in tags
        public const string Untagged = "Untagged";
        public const string Player = "Player";
        public const string MainCamera = "MainCamera";

        // Upcycle City custom tags
        public const string Environment = "Environment";
        public const string Interactable = "Interactable";
        public const string Grabbable = "Grabbable";
        public const string Trash = "Trash";
        public const string Pickup = "Pickup";
        public const string SortingCrate = "SortingCrate";
        public const string Machine = "Machine";
        public const string Citizen = "Citizen";
        public const string Vehicle = "Vehicle";
        public const string PollutionSource = "PollutionSource";

        public static readonly string[] CustomTags =
        {
            Environment,
            Interactable,
            Grabbable,
            Trash,
            Pickup,
            SortingCrate,
            Machine,
            Citizen,
            Vehicle,
            PollutionSource,
        };
    }
}
