using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UpcycleCity.Character;
using UpcycleCity.Cinematics;
using UpcycleCity.Networking;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds the <c>MainMenu</c> title scene and wires the MVP session flow:
    /// Intro → Main Menu → (Room) → Character Creator → EcosystemSandbox.
    /// <list type="bullet">
    /// <item>MainMenu.unity: camera, EventSystem, persistent <see cref="CoopSessionManager"/> (+ NetworkManager,
    /// UnityTransport) and the <see cref="CoopMenuController"/> title screen on <c>UI/MainMenuPanelSettings</c>
    /// (1080x608 landscape reference).</item>
    /// <item>CharacterCreator.unity: removes the old co-op overlay / session objects (the session now lives in the
    /// menu and persists) and points Start at the Avenue.</item>
    /// <item>NetworkSettings scene names, IntroConfig.nextScene and the build scene order.</item>
    /// </list>
    /// Scenes are built/edited additively so whatever scene is open stays untouched. Safe to re-run.
    /// </summary>
    public static class MainMenuBuilder
    {
        public const string MainMenuScenePath = "Assets/UpcycleCity/Scenes/MainMenu.unity";
        const string IntroScenePath = "Assets/UpcycleCity/Scenes/Intro.unity";
        const string CreatorScenePath = "Assets/UpcycleCity/Scenes/CharacterCreator.unity";
        const string AvenueScenePath = "Assets/UpcycleCity/Scenes/EcosystemSandbox.unity";
        const string GymScenePath = "Assets/UpcycleCity/Scenes/PlayerGym.unity";
        const string WorkshopScenePath = "Assets/UpcycleCity/Scenes/CraftingWorkshop.unity";
        const string NetworkSettingsPath = "Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset";
        const string IntroConfigPath = "Assets/UpcycleCity/Settings/Intro/IntroConfig.asset";
        const string MenuPanelSettingsPath = "Assets/UpcycleCity/UI/MainMenuPanelSettings.asset";
        const string MenuPanelTemplatePath = "Assets/UpcycleCity/Resources/CraftingPanelSettings.asset";
        const string CoopMenuStylePath = "Assets/UpcycleCity/UI/CoopMenu.uss";

        /// <summary>Canonical build order; scenes are loaded by name, so anything else keeps its place after these.</summary>
        static readonly string[] CanonicalOrder =
        {
            IntroScenePath, MainMenuScenePath, CreatorScenePath, AvenueScenePath, GymScenePath, WorkshopScenePath,
        };

        [MenuItem("Upcycle City/Build Main Menu Scene", priority = 19)]
        static void BuildFromMenu()
        {
            if (File.Exists(MainMenuScenePath) && !EditorUtility.DisplayDialog("Rebuild Main Menu?",
                    $"{MainMenuScenePath} already exists and will be replaced.", "Rebuild", "Cancel"))
                return;
            Build();
        }

        /// <summary>Non-interactive build (also used from the Unity CLI).</summary>
        public static void Build()
        {
            NetworkSettings settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(NetworkSettingsPath);
            if (settings != null)
            {
                settings.ConfigureScenes("MainMenu", "CharacterCreator", "EcosystemSandbox", "EcosystemSandbox");
                EditorUtility.SetDirty(settings);
            }

            IntroConfig intro = AssetDatabase.LoadAssetAtPath<IntroConfig>(IntroConfigPath);
            if (intro != null && intro.nextScene != "MainMenu")
            {
                intro.nextScene = "MainMenu";
                EditorUtility.SetDirty(intro);
            }
            AssetDatabase.SaveAssets();

            PanelSettings menuPanel = CreateOrLoadMenuPanelSettings();
            BuildMenuScene(settings, menuPanel);
            RewireCreatorScene();
            ApplyBuildSceneOrder();
            Debug.Log($"[Upcycle City] Built {MainMenuScenePath}: Intro → Main Menu → (Room) → Character Creator → EcosystemSandbox.");
        }

        static PanelSettings CreateOrLoadMenuPanelSettings()
        {
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(MenuPanelSettingsPath);
            if (panel != null)
                return panel;
            if (AssetDatabase.CopyAsset(MenuPanelTemplatePath, MenuPanelSettingsPath))
                return AssetDatabase.LoadAssetAtPath<PanelSettings>(MenuPanelSettingsPath);

            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1080, 608);
            panel.match = 0.5f;
            AssetDatabase.CreateAsset(panel, MenuPanelSettingsPath);
            return panel;
        }

        static void BuildMenuScene(NetworkSettings settings, PanelSettings menuPanel)
        {
            Scene existing = SceneManager.GetSceneByPath(MainMenuScenePath);
            if (existing.IsValid() && existing.isLoaded)
                EditorSceneManager.CloseScene(existing, true);

            Scene previousActive = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);

            var camGo = new GameObject("MenuCamera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.11f, 0.10f);
            camGo.AddComponent<AudioListener>();

            // EventSystem + Input System UI module: routes mouse, touch and gamepad to UI Toolkit
            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystem.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

            if (settings != null)
                CoopMultiplayerSetup.WireCoopIntoActiveMenuScene(settings, menuPanel);
            else
                Debug.LogWarning("[Upcycle City] NetworkSettings missing: run Upcycle City → Networking → Setup Co-op Prefabs & Scene, then rebuild the Main Menu.");

            // NewScene(Additive) leaves the stray objects in the new scene; make sure everything we made landed there
            foreach (GameObject go in new[] { camGo, eventSystem })
            {
                if (go.scene != scene)
                    SceneManager.MoveGameObjectToScene(go, scene);
            }

            EditorSceneManager.SaveScene(scene, MainMenuScenePath);
            if (previousActive.IsValid() && previousActive.isLoaded)
                SceneManager.SetActiveScene(previousActive);
            if (SceneManager.sceneCount > 1)
                EditorSceneManager.CloseScene(scene, true);
        }

        /// <summary>
        /// Removes the pre-menu co-op objects from the Character Creator (session + overlay now live in the Main Menu
        /// and persist) and points its Start at the Avenue. Opens the scene additively unless it is already open.
        /// </summary>
        public static void RewireCreatorScene()
        {
            if (!File.Exists(CreatorScenePath))
                return;

            Scene scene = SceneManager.GetSceneByPath(CreatorScenePath);
            bool openedHere = false;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(CreatorScenePath, OpenSceneMode.Additive);
                openedHere = true;
            }

            bool changed = false;
            foreach (GameObject rootGo in scene.GetRootGameObjects())
            {
                if (rootGo.GetComponentInChildren<CoopMenuController>(true) != null
                    || rootGo.GetComponentInChildren<CoopSessionManager>(true) != null)
                {
                    Object.DestroyImmediate(rootGo);
                    changed = true;
                    continue;
                }

                foreach (CharacterCreatorController creator in rootGo.GetComponentsInChildren<CharacterCreatorController>(true))
                {
                    using var so = new SerializedObject(creator);
                    SerializedProperty gameplay = so.FindProperty("gameplayScene");
                    SerializedProperty menu = so.FindProperty("menuScene");
                    if (gameplay != null && gameplay.stringValue != "EcosystemSandbox")
                    {
                        gameplay.stringValue = "EcosystemSandbox";
                        changed = true;
                    }
                    if (menu != null && menu.stringValue != "MainMenu")
                    {
                        menu.stringValue = "MainMenu";
                        changed = true;
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            if (openedHere && SceneManager.sceneCount > 1)
                EditorSceneManager.CloseScene(scene, true);
        }

        /// <summary>
        /// Build order: Intro, MainMenu, CharacterCreator, EcosystemSandbox, PlayerGym, CraftingWorkshop, then any other
        /// listed scenes. Missing files are dropped. Every scene builder calls this so rebuilds keep the order.
        /// </summary>
        public static void ApplyBuildSceneOrder()
        {
            var ordered = new List<EditorBuildSettingsScene>();
            var listed = new Dictionary<string, EditorBuildSettingsScene>();
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (File.Exists(s.path) && !listed.ContainsKey(s.path))
                    listed.Add(s.path, s);
            }

            foreach (string path in CanonicalOrder)
            {
                if (!File.Exists(path))
                    continue;
                ordered.Add(new EditorBuildSettingsScene(path, true));
                listed.Remove(path);
            }

            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (listed.Remove(s.path))
                    ordered.Add(s);
            }
            EditorBuildSettings.scenes = ordered.ToArray();
        }
    }
}
