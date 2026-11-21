using UnityEngine;

namespace UpcycleCity.Core
{
    /// <summary>
    /// Centralized Unity layer indices, names, and pre-composed LayerMasks for Upcycle City.
    /// Kept in sync with ProjectSettings/TagManager.asset by ProjectLayersAndTagsSetup.
    /// </summary>
    public static class GameLayers
    {
        // Unity built-in layers
        public const int Default = 0;
        public const int TransparentFX = 1;
        public const int IgnoreRaycast = 2;
        public const int Water = 4;
        public const int UI = 5;

        // Upcycle City custom layers (6..15)
        public const int Player = 6;
        public const int Environment = 7;
        public const int Interactable = 8;
        public const int Grabbable = 9;
        public const int HeldObject = 10;
        public const int Pickup = 11;
        public const int Citizen = 12;
        public const int Vehicle = 13;
        public const int TriggerZone = 14;
        /// <summary>First-person arms and held tool, drawn by the overlay view-model camera only.</summary>
        public const int FirstPersonView = 15;

        public const string PlayerName = "Player";
        public const string EnvironmentName = "Environment";
        public const string InteractableName = "Interactable";
        public const string GrabbableName = "Grabbable";
        public const string HeldObjectName = "HeldObject";
        public const string PickupName = "Pickup";
        public const string CitizenName = "Citizen";
        public const string VehicleName = "Vehicle";
        public const string TriggerZoneName = "TriggerZone";
        public const string FirstPersonViewName = "FirstPersonView";

        public static int MaskOf(int layer) => 1 << layer;

        /// <summary>Solid walkable surfaces for PlayerMotor ground & ceiling probes.</summary>
        public static LayerMask GroundMask =>
            MaskOf(Default) | MaskOf(Environment) | MaskOf(Interactable);

        /// <summary>Solid geometry that blocks held physics objects from clipping through walls/stations.</summary>
        public static LayerMask ObstacleMask =>
            MaskOf(Default) | MaskOf(Environment) | MaskOf(Interactable);

        /// <summary>Targets + solid occluders (Environment prevents interacting/grabbing through walls).</summary>
        public static LayerMask InteractMask =>
            MaskOf(Default) | MaskOf(Environment) | MaskOf(Interactable) | MaskOf(Grabbable) | MaskOf(Pickup);

        /// <summary>Sets the layer on a GameObject and all of its children (optionally preserving trigger children).</summary>
        public static void SetLayerRecursively(GameObject root, int layer, bool preserveTriggerZones = false)
        {
            if (root == null || layer < 0 || layer > 31)
                return;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                GameObject go = transforms[i].gameObject;
                if (preserveTriggerZones && go.layer == TriggerZone)
                    continue;
                go.layer = layer;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ConfigureRuntimeLayerCollisions()
        {
            // Prevent held physics objects from pushing or launching the player capsule
            Physics.IgnoreLayerCollision(Player, HeldObject, true);
        }
    }
}
