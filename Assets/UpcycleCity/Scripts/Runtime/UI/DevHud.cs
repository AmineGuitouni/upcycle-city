using UnityEngine;
using UnityEngine.InputSystem;

namespace UpcycleCity.UI
{
    /// <summary>
    /// Developer overlay switch. Debug HUDs (FSM telemetry, ecosystem dashboard) stay hidden in normal
    /// play and only appear after pressing <b>F1</b> in the Editor or a development build.
    /// Release builds can never show them.
    /// </summary>
    public static class DevHud
    {
        /// <summary>True when debug overlays should draw.</summary>
        public static bool DebugVisible { get; private set; }

        /// <summary>Debug overlays are allowed at all (Editor or development build).</summary>
        public static bool Allowed => Debug.isDebugBuild;

        static int lastPolledFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            DebugVisible = false;
            lastPolledFrame = -1;
        }

        /// <summary>Polls F1 once per frame (safe to call from several components).</summary>
        public static void Poll()
        {
            if (lastPolledFrame == Time.frameCount)
                return;
            lastPolledFrame = Time.frameCount;
            if (!Allowed)
            {
                DebugVisible = false;
                return;
            }

            Keyboard kb = Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame)
                DebugVisible = !DebugVisible;
        }

        /// <summary>Forces the overlay state (tests / tooling).</summary>
        public static void SetVisible(bool visible) => DebugVisible = visible && Allowed;
    }
}
