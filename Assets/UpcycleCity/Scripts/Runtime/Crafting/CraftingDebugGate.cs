using System;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Centralized security gate for crafting debug cheats (grant starter kit, add NRG, clear stock,
    /// set machine level) and the CraftingUI debug bar.
    /// <para>
    /// Always returns <c>false</c> in non-development release builds regardless of scene settings.
    /// In Editor and Development Builds, defaults to <c>false</c> unless explicitly enabled by a
    /// workshop/test scene or unit test so clients cannot invoke debug cheats in normal gameplay.
    /// </para>
    /// </summary>
    public static class CraftingDebugGate
    {
        static bool allowDebugActionsInDev;

        /// <summary>True only when running inside the Unity Editor or a Development Build.</summary>
        public static bool IsDevEnvironment =>
#if UNITY_EDITOR || DEBUG
            Debug.isDebugBuild || Application.isEditor;
#else
            false;
#endif

        /// <summary>
        /// True only when running in a dev build/editor AND debug actions are explicitly enabled.
        /// Always false in release builds.
        /// </summary>
        public static bool IsDebugActionsAllowed => IsDevEnvironment && allowDebugActionsInDev;

        /// <summary>Raised whenever <see cref="IsDebugActionsAllowed"/> changes so stations can register/unregister debug handlers.</summary>
        public static event Action Changed;

        /// <summary>Enables or disables crafting debug actions (no-op in release builds).</summary>
        public static void SetDebugActionsAllowed(bool allowed)
        {
#if UNITY_EDITOR || DEBUG
            bool next = allowed && IsDevEnvironment;
#else
            bool next = false;
#endif
            if (allowDebugActionsInDev == next)
                return;
            allowDebugActionsInDev = next;
            Changed?.Invoke();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            allowDebugActionsInDev = false;
            Changed = null;
        }
    }
}
