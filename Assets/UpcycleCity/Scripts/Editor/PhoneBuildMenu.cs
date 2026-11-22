using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// One-click Android build for playtesting co-op on real phones: every enabled scene in the build list
    /// (Intro → MainMenu → CharacterCreator → EcosystemSandbox …), IL2CPP ARM64, internet permission on,
    /// an installable APK at Builds/Android/UpcycleCity.apk. Bumps the version code each build so phones update.
    /// Online play by join code needs the project linked to Unity Cloud (Project Settings → Services) with Relay on.
    /// </summary>
    public static class PhoneBuildMenu
    {
        const string OutputDir = "Builds/Android";
        const string ApkName = "UpcycleCity.apk";

        [MenuItem("Upcycle City/Build/Android APK (phones)", priority = 200)]
        public static void BuildAndroidApk()
        {
            if (string.IsNullOrEmpty(CloudProjectSettings.projectId))
            {
                bool go = EditorUtility.DisplayDialog("Upcycle City · Android build",
                    "This project isn't linked to Unity Cloud yet, so online join codes (Relay) won't work on the phones.\n\n" +
                    "Link it in Edit → Project Settings → Services, enable Relay in the Unity Cloud dashboard, then build again.\n\n" +
                    "Build anyway (Direct IP / same Wi-Fi only)?",
                    "Build anyway", "Cancel");
                if (!go)
                    return;
            }
            Build();
        }

        /// <summary>Builds without dialogs (CLI / batch use). Returns true on success.</summary>
        public static bool Build()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android
                && !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError("[Upcycle City] Could not switch the build target to Android (is the Android module installed?).");
                return false;
            }

            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.Android.bundleVersionCode = Mathf.Max(1, PlayerSettings.Android.bundleVersionCode + 1);
            EditorUserBuildSettings.buildAppBundle = false;

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled && File.Exists(s.path)).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Upcycle City] No enabled scenes in the build settings.");
                return false;
            }

            Directory.CreateDirectory(OutputDir);
            string path = Path.Combine(OutputDir, ApkName);
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });

            BuildSummary summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[Upcycle City] Android APK built: {Path.GetFullPath(path)} " +
                          $"({summary.totalSize / (1024f * 1024f):0.0} MB, {summary.totalTime.TotalMinutes:0.0} min, version code {PlayerSettings.Android.bundleVersionCode}). " +
                          $"Scenes: {string.Join(", ", scenes.Select(Path.GetFileNameWithoutExtension))}");
                EditorUtility.RevealInFinder(path);
                return true;
            }
            Debug.LogError($"[Upcycle City] Android build {summary.result}: {summary.totalErrors} error(s). See the console.");
            return false;
        }
    }
}
