using UnityEngine;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Shared state written by the on-screen touch controls (<c>UpcycleCity.UI.TouchControls</c>) and merged
    /// additively by <see cref="PlayerInputReader"/> into the same Move / Look / Jump / Sprint / Crouch /
    /// Attack / Interact / Drop / UseItem values the Player action map produces. Keyboard, mouse and gamepad
    /// keep working at the same time. Purely local presentation/input — never networked.
    /// </summary>
    public static class VirtualTouchInput
    {
        /// <summary>True while the on-screen controls are shown (raw touchscreen look/tap is then ignored).</summary>
        public static bool Active;

        /// <summary>Joystick value, magnitude ≤ 1.</summary>
        public static Vector2 Move;

        public static bool SprintHeld;
        public static bool CrouchHeld;
        public static bool JumpHeld;
        public static bool AttackHeld;
        public static bool InteractHeld;
        public static bool DropHeld;
        public static bool UseItemHeld;

        static Vector2 pendingLook;

        /// <summary>Adds a drag delta in screen pixels (right-half look zone).</summary>
        public static void AddLook(Vector2 screenPixels) => pendingLook += screenPixels;

        /// <summary>Returns and clears the accumulated look delta.</summary>
        public static Vector2 ConsumeLook()
        {
            Vector2 v = pendingLook;
            pendingLook = Vector2.zero;
            return v;
        }

        /// <summary>Releases every virtual control (overlay hidden, window opened, scene change).</summary>
        public static void ReleaseAll()
        {
            Move = Vector2.zero;
            SprintHeld = CrouchHeld = JumpHeld = AttackHeld = InteractHeld = DropHeld = UseItemHeld = false;
            pendingLook = Vector2.zero;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = false;
            ReleaseAll();
        }
    }
}
