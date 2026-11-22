using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Plays the game the way a player sees it — from the first scene in the build (the intro cinematic) — without
    /// closing the scene that is open. The start scene only applies to this one play session and is cleared as soon
    /// as Play Mode ends, so pressing Play normally (and the Test Runner) still uses the open scene.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayFromStartMenu
    {
        const string IntroScenePath = "Assets/UpcycleCity/Scenes/Intro.unity";
        const string MainMenuScenePath = "Assets/UpcycleCity/Scenes/MainMenu.unity";
        const string PendingKey = "UpcycleCity.PlayFromStart.Pending";

        static PlayFromStartMenu()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("Upcycle City/Play From Start (Intro Cinematic) %#&p", priority = 0)]
        static void PlayFromIntro() => PlayFrom(FirstBuildScene() ?? IntroScenePath);

        [MenuItem("Upcycle City/Play From Main Menu", priority = 1)]
        static void PlayFromMainMenu() => PlayFrom(MainMenuScenePath);

        [MenuItem("Upcycle City/Reset Intro 'Seen' Flag (first-watch, no skip)", priority = 2)]
        static void ResetIntroSeen()
        {
            Cinematics.IntroProgress.Clear();
            Debug.Log("[Upcycle City] The intro will play as a first watch again (hold to skip).");
        }

        [MenuItem("Upcycle City/Play From Start (Intro Cinematic) %#&p", true)]
        [MenuItem("Upcycle City/Play From Main Menu", true)]
        static bool CanPlay() => !EditorApplication.isPlayingOrWillChangePlaymode;

        static void PlayFrom(string scenePath)
        {
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            if (scene == null)
            {
                Debug.LogError($"[Upcycle City] Can't play from '{scenePath}': scene not found.");
                return;
            }
            // Offer to save the open scenes first, like pressing Play would
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            EditorSceneManager.playModeStartScene = scene;
            SessionState.SetBool(PendingKey, true);
            EditorApplication.isPlaying = true;
        }

        static string FirstBuildScene()
        {
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (s.enabled && !string.IsNullOrEmpty(s.path))
                    return s.path;
            }
            return null;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            // One session only: once we're back in Edit Mode, Play uses the open scene again
            if (change == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(PendingKey, false))
            {
                EditorSceneManager.playModeStartScene = null;
                SessionState.EraseBool(PendingKey);
            }
        }
    }
}
