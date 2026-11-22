using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UpcycleCity.Character;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds the Character Creator scene and its assets. Safe to re-run: it asks before replacing the scene,
    /// offers to save open changes first, and only fills empty fields of the parts library, so palette and
    /// style tweaks made in the Inspector are kept. The creator becomes the first scene of the build, and
    /// its Start button loads the Player Gym.
    /// </summary>
    static class CharacterCreatorBuilder
    {
        const string ScenePath = "Assets/UpcycleCity/Scenes/CharacterCreator.unity";
        const string GameplayScenePath = "Assets/UpcycleCity/Scenes/PlayerGym.unity";
        // The intro cinematic (IntroSceneBuilder) plays before the creator when it has been built
        const string IntroScenePath = "Assets/UpcycleCity/Scenes/Intro.unity";
        const string PartsFolder = "Assets/UpcycleCity/Art/UpcycleProps/Character";
        const string LibraryPath = "Assets/UpcycleCity/Settings/Character/CharacterPartsLibrary.asset";
        const string UiFolder = "Assets/UpcycleCity/UI";
        const string ThemePath = UiFolder + "/UpcycleCityTheme.tss";
        const string PanelSettingsPath = UiFolder + "/UpcycleCityPanelSettings.asset";
        const string StyleSheetPath = UiFolder + "/CharacterCreator.uss";
        const string FloorMaterialPath = "Assets/UpcycleCity/Settings/Character/CreatorFloor.mat";

        // (file suffix, display name), in the order the creator shows them
        static readonly (string, string)[] FaceStyles =
            { ("tired", "Tired"), ("wide", "Wide"), ("dot", "Dot"), ("happy", "Happy"), ("squint", "Squint") };
        static readonly (string, string)[] HairStyles =
        {
            ("none", "None"), ("buzz", "Buzz Cut"), ("messy", "Messy"), ("long", "Long"), ("bun", "Bun"),
            ("ponytail", "Ponytail"), ("curly", "Curly"), ("afro", "Afro"), ("beanie", "Beanie"),
        };
        static readonly (string, string)[] FacialHairStyles =
            { ("none", "None"), ("stubble", "Stubble"), ("beard", "Scruffy Beard"), ("moustache", "Moustache") };

        static readonly string[] SkinTones =
            { "#F6D7C3", "#EDC1A4", "#E0AC88", "#C98B62", "#B67A52", "#9A6440", "#7E4E31", "#643C25", "#4E2E1C", "#3A2215" };
        static readonly string[] OutfitColors =
            { "#6B5A3E", "#5A6650", "#4A5560", "#7A4A3A", "#3E3E44", "#8A7A5A", "#5B4A66", "#2F5560" };
        static readonly string[] EyeColors = { "#5B7A3A", "#6B4A2E", "#3A6EA5", "#6E7378", "#8A6A2A", "#2E2420" };
        static readonly string[] HairColors =
        {
            "#141110", "#2A211C", "#4A3426", "#6B4A2E", "#8E3B1F", "#C08F3C", "#D9C38E", "#9A9690", "#D8D6D0",
            "#3E6FB0", "#6A3E9A", "#3E8A4E",
        };

        [MenuItem("Upcycle City/Build Character Creator Scene", priority = 20)]
        static void Build()
        {
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Rebuild Character Creator?",
                    $"{ScenePath} already exists and will be replaced. The parts library and UI assets are kept.",
                    "Rebuild", "Cancel"))
                return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            // New scene first: opening it unloads assets held only by local variables, which would
            // leave references written afterwards pointing at destroyed objects
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CharacterPartsLibrary library = CreateOrFillLibrary();
            PanelSettings panel = CreateOrLoadPanelSettings();
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            BuildStage(library, panel, styleSheet);
            EditorSceneManager.SaveScene(scene, ScenePath);
            UpdateBuildScenes();
            Debug.Log($"[Upcycle City] Built {ScenePath}. Press Play: Start loads the Player Gym.");
        }

        [MenuItem("Upcycle City/Character/Refresh Parts Library Styles", priority = 21)]
        static void RefreshStyles()
        {
            CharacterPartsLibrary library = CreateOrFillLibrary();
            Undo.RecordObject(library, "Refresh Parts Library Styles");
            FillStyles(library, force: true);
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssetIfDirty(library);
            Debug.Log("[Upcycle City] Parts library styles refreshed from " + PartsFolder);
        }

        // ---------------------------------------------------------------- assets

        internal static CharacterPartsLibrary CreateOrFillLibrary()
        {
            var library = AssetDatabase.LoadAssetAtPath<CharacterPartsLibrary>(LibraryPath);
            if (library == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath));
                library = ScriptableObject.CreateInstance<CharacterPartsLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }

            if (library.maleBody == null)
                library.maleBody = Load("body_male");
            if (library.femaleBody == null)
                library.femaleBody = Load("body_female");
            if (library.maleBodyRigged == null)
                library.maleBodyRigged = Load("Rigged/body_male_rigged");
            if (library.femaleBodyRigged == null)
                library.femaleBodyRigged = Load("Rigged/body_female_rigged");
            if (library.rightArmOpen == null) library.rightArmOpen = Load("fp_arm_right_open");
            if (library.rightArmGrip == null) library.rightArmGrip = Load("fp_arm_right_grip");
            if (library.leftArmOpen == null) library.leftArmOpen = Load("fp_arm_left_open");
            if (library.leftArmGrip == null) library.leftArmGrip = Load("fp_arm_left_grip");
            FillStyles(library, force: false);
            if (library.skinTones.Length == 0) library.skinTones = Colors(SkinTones);
            if (library.outfitColors.Length == 0) library.outfitColors = Colors(OutfitColors);
            if (library.eyeColors.Length == 0) library.eyeColors = Colors(EyeColors);
            if (library.hairColors.Length == 0) library.hairColors = Colors(HairColors);

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssetIfDirty(library);
            return library;
        }

        static void FillStyles(CharacterPartsLibrary library, bool force)
        {
            if (force || library.faceStyles.Length == 0)
                library.faceStyles = Parts("face", FaceStyles);
            if (force || library.hairStyles.Length == 0)
                library.hairStyles = Parts("hair", HairStyles);
            if (force || library.facialHairStyles.Length == 0)
                library.facialHairStyles = Parts("beard", FacialHairStyles);
        }

        static CharacterPartsLibrary.Part[] Parts(string prefix, (string, string)[] styles)
        {
            var parts = new List<CharacterPartsLibrary.Part>(styles.Length);
            foreach ((string file, string display) in styles)
            {
                GameObject prefab = file == "none" ? null : Load($"{prefix}_{file}");
                if (file != "none" && prefab == null)
                {
                    Debug.LogWarning($"[Upcycle City] Missing {PartsFolder}/{prefix}_{file}.fbx; '{display}' skipped.");
                    continue;
                }
                parts.Add(new CharacterPartsLibrary.Part { displayName = display, prefab = prefab });
            }
            return parts.ToArray();
        }

        static GameObject Load(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{PartsFolder}/{name}.fbx");

        static Color[] Colors(string[] hex)
        {
            var colors = new Color[hex.Length];
            for (int i = 0; i < hex.Length; i++)
                ColorUtility.TryParseHtmlString(hex[i], out colors[i]);
            return colors;
        }

        static PanelSettings CreateOrLoadPanelSettings()
        {
            Directory.CreateDirectory(UiFolder);
            if (!File.Exists(ThemePath))
            {
                File.WriteAllText(ThemePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(ThemePath);
            }

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel != null)
                return panel;

            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            return panel;
        }

        static Material CreateOrLoadFloorMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(FloorMaterialPath);
            if (mat != null)
                return mat;
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", new Color(0.12f, 0.12f, 0.125f));
            mat.SetFloat("_Smoothness", 0.15f);
            AssetDatabase.CreateAsset(mat, FloorMaterialPath);
            return mat;
        }

        // ---------------------------------------------------------------- scene

        static void BuildStage(CharacterPartsLibrary library, PanelSettings panel, StyleSheet styleSheet)
        {
            // A dark room with a spotlight pool, like a photo studio
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.2f, 0.2f, 0.22f);
            RenderSettings.fog = false;

            var camGo = new GameObject("CreatorCamera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.055f, 0.055f, 0.062f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            camGo.AddComponent<AudioListener>();

            Light key = AddLight("KeySpot", LightType.Spot, new Vector3(0.7f, 4.4f, 2.8f), new Vector3(0f, 0.9f, 0f),
                38f, new Color(1f, 0.96f, 0.9f));
            key.spotAngle = 48f;
            key.innerSpotAngle = 26f;
            key.range = 12f;
            key.shadows = LightShadows.Soft;
            AddLight("Rim", LightType.Directional, new Vector3(0f, 3f, -3f), new Vector3(0f, 1.2f, 0f), 1.1f,
                new Color(0.75f, 0.82f, 1f));
            AddLight("Fill", LightType.Directional, new Vector3(-3f, 2f, 3f), new Vector3(0f, 1.2f, 0f), 0.45f,
                new Color(1f, 0.9f, 0.8f));

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(6f, 0.01f, 6f);
            floor.transform.position = new Vector3(0f, -0.01f, 0f);
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            floor.GetComponent<MeshRenderer>().sharedMaterial = CreateOrLoadFloorMaterial();

            var turntable = new GameObject("Turntable");
            var avatarGo = new GameObject("CharacterAvatar");
            avatarGo.transform.SetParent(turntable.transform, false);
            var avatar = avatarGo.AddComponent<CharacterAvatar>();
            avatar.SetLibrary(library);

            // EventSystem + Input System UI module: routes mouse, touch and gamepad to UI Toolkit
            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystem.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

            var uiGo = new GameObject("CharacterCreatorUI");
            var document = uiGo.AddComponent<UIDocument>();
            using (var docSo = new SerializedObject(document))
            {
                // The panelSettings property setter doesn't persist in edit mode; write the field itself
                docSo.FindProperty("m_PanelSettings").objectReferenceValue = panel;
                docSo.ApplyModifiedPropertiesWithoutUndo();
            }
            var controller = uiGo.AddComponent<CharacterCreatorController>();
            using (var so = new SerializedObject(controller))
            {
                so.FindProperty("avatar").objectReferenceValue = avatar;
                so.FindProperty("turntable").objectReferenceValue = turntable.transform;
                so.FindProperty("creatorCamera").objectReferenceValue = cam;
                so.FindProperty("styleSheet").objectReferenceValue = styleSheet;
                so.FindProperty("gameplayScene").stringValue = Path.GetFileNameWithoutExtension(GameplayScenePath);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static Light AddLight(string name, LightType type, Vector3 position, Vector3 target, float intensity, Color color)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(target - position);
            var light = go.AddComponent<Light>();
            light.type = type;
            light.intensity = intensity;
            light.color = color;
            light.shadows = type == LightType.Directional ? LightShadows.None : LightShadows.Soft;
            return light;
        }

        /// <summary>
        /// Creator first (right after the intro cinematic, when it exists), then the Player Gym; drops entries
        /// whose scene file no longer exists.
        /// </summary>
        static void UpdateBuildScenes()
        {
            var scenes = new List<EditorBuildSettingsScene>();
            bool hasIntro = File.Exists(IntroScenePath);
            if (hasIntro)
                scenes.Add(new EditorBuildSettingsScene(IntroScenePath, true));
            scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            bool hasGameplay = false;
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (s.path == ScenePath || s.path == IntroScenePath || !File.Exists(s.path))
                    continue;
                hasGameplay |= s.path == GameplayScenePath;
                scenes.Add(s);
            }
            if (!hasGameplay)
                scenes.Insert(hasIntro ? 2 : 1, new EditorBuildSettingsScene(GameplayScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
