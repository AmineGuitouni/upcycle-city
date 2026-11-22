using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UpcycleCity.Core;
using UpcycleCity.Crafting;
using UpcycleCity.Ecosystem;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds the dedicated <c>Assets/UpcycleCity/Scenes/CraftingWorkshop.unity</c> test &amp; showcase scene
    /// containing all 5 manual crafting machines, the pedal energy generator, physical trash to throw into hoppers,
    /// <see cref="TeamMaterialStock"/> pre-seeded with the Starter Kit, <see cref="CraftingUI"/>, and the Player rig.
    /// Adds the scene to Build Settings at index &gt;= 3 without altering existing scene indices 0–2.
    /// </summary>
    public static class CraftingWorkshopBuilder
    {
        public const string ScenePath = "Assets/UpcycleCity/Scenes/CraftingWorkshop.unity";
        const string PlayerPrefabPath = "Assets/UpcycleCity/Prefabs/Player/Player.prefab";
        const string WasteCatalogPath = "Assets/UpcycleCity/Settings/Ecosystem/WasteCatalog.asset";

        [MenuItem("Upcycle City/Crafting/Build Crafting Workshop Scene", priority = 61)]
        public static void BuildWorkshopMenu()
        {
            if (File.Exists(ScenePath) && !Application.isBatchMode)
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Replace Crafting Workshop Scene?",
                    $"'{ScenePath}' already exists. Replace it with a freshly built Crafting Workshop scene?",
                    "Replace",
                    "Cancel");
                if (!overwrite)
                    return;
            }

            BuildWorkshopScene(interactive: false);
        }

        /// <summary>Headless-friendly entry point to build or rebuild <c>CraftingWorkshop.unity</c>.</summary>
        public static void BuildWorkshopScene(bool interactive = false)
        {
            if (interactive && File.Exists(ScenePath) && !Application.isBatchMode)
            {
                if (!EditorUtility.DisplayDialog("Replace Crafting Workshop Scene?", $"Replace '{ScenePath}'?", "Replace", "Cancel"))
                    return;
            }

            CraftingContentBuilder.Content content = CraftingContentBuilder.EnsureAll(overwriteExistingTuning: false);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Sun & Atmosphere
            var lightingRoot = new GameObject("Lighting").transform;
            var sunGo = new GameObject("Directional Light");
            sunGo.transform.SetParent(lightingRoot);
            sunGo.transform.rotation = Quaternion.Euler(46f, -34f, 0f);
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.45f;
            sun.color = new Color(1.0f, 0.96f, 0.88f);
            sun.shadows = LightShadows.Soft;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.74f, 0.84f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.56f, 0.54f);
            RenderSettings.ambientGroundColor = new Color(0.32f, 0.30f, 0.28f);

            // 2. Workshop Courtyard Floor & Low Perimeter Walls
            var envRoot = new GameObject("WorkshopEnvironment").transform;
            Material floorMat = CreateSimpleUrpMaterial("Workshop_Floor", new Color(0.38f, 0.40f, 0.41f));
            Material padMat = CreateSimpleUrpMaterial("Workshop_Pad", new Color(0.28f, 0.33f, 0.34f));
            Material wallMat = CreateSimpleUrpMaterial("Workshop_Wall", new Color(0.84f, 0.81f, 0.75f));
            Material accentMat = CreateSimpleUrpMaterial("Workshop_Accent", new Color(0.18f, 0.56f, 0.54f));

            CreateBlock("Floor", envRoot, new Vector3(0f, -0.25f, 0f), new Vector3(36f, 0.5f, 36f), floorMat);
            CreateBlock("MachineBayPad", envRoot, new Vector3(0f, 0.01f, 5.0f), new Vector3(24f, 0.04f, 9f), padMat, withCollider: false);
            CreateBlock("Stripe_Front", envRoot, new Vector3(0f, 0.035f, 0.6f), new Vector3(24f, 0.02f, 0.22f), accentMat, withCollider: false);

            // Low courtyard perimeter walls
            CreateBlock("Wall_North", envRoot, new Vector3(0f, 1.6f, 14f), new Vector3(36f, 3.2f, 0.6f), wallMat);
            CreateBlock("Wall_South", envRoot, new Vector3(0f, 1.0f, -14f), new Vector3(36f, 2.0f, 0.6f), wallMat);
            CreateBlock("Wall_West", envRoot, new Vector3(-16f, 1.4f, 0f), new Vector3(0.6f, 2.8f, 28f), wallMat);
            CreateBlock("Wall_East", envRoot, new Vector3(16f, 1.4f, 0f), new Vector3(0.6f, 2.8f, 28f), wallMat);

            // 3. Systems (TeamMaterialStock + CraftingUI + EventSystem)
            var systemsRoot = new GameObject("Systems").transform;

            var stockGo = new GameObject("TeamMaterialStock");
            stockGo.transform.SetParent(systemsRoot);
            TeamMaterialStock stock = stockGo.AddComponent<TeamMaterialStock>();
            stock.Configure(content.database, content.starterKit, applyKitOnStart: true, initialEnergy: 50, energyCap: 500, enableDebugActions: true);

            var uiGo = new GameObject("CraftingUI");
            uiGo.transform.SetParent(systemsRoot);
            CraftingUI ui = uiGo.AddComponent<CraftingUI>();
            if (content.uiConfig != null)
                ui.Configure(content.uiConfig.PanelSettings, content.uiConfig.StyleSheet, debugButtons: true);

            var eventSysGo = new GameObject("EventSystem");
            eventSysGo.transform.SetParent(systemsRoot);
            eventSysGo.AddComponent<EventSystem>();
            eventSysGo.AddComponent<InputSystemUIInputModule>();

            // 4. All 6 Manual Machines arranged in an inviting workshop arc facing the player (-Z -> +Z)
            var machinesRoot = new GameObject("ManualMachines").transform;
            PlaceMachine(content.pulperPrefab, "Station_ManualPulper", "workshop_pulper", machinesRoot, new Vector3(-8.5f, 0f, 5.2f), 170f);
            PlaceMachine(content.plasticMelterPrefab, "Station_PlasticMelter", "workshop_plastic_melter", machinesRoot, new Vector3(-5.0f, 0f, 6.2f), 175f);
            PlaceMachine(content.crucibleFurnacePrefab, "Station_CrucibleFurnace", "workshop_crucible_furnace", machinesRoot, new Vector3(-1.5f, 0f, 6.6f), 180f);
            PlaceMachine(content.tyreStripperPrefab, "Station_TyreStripper", "workshop_tyre_stripper", machinesRoot, new Vector3(2.0f, 0f, 6.6f), 180f);
            PlaceMachine(content.workbenchPrefab, "Station_UpcycleWorkbench", "workshop_workbench", machinesRoot, new Vector3(5.6f, 0f, 6.0f), -172f);
            PlaceMachine(content.pedalGeneratorPrefab, "Station_PedalGenerator", "workshop_pedal_generator", machinesRoot, new Vector3(9.0f, 0f, 4.8f), -160f);

            // 5. Physical Trash Samples to throw or E-deposit into hoppers
            SpawnWorkshopTrashSamples();

            // 6. Player Rig facing the machines
            GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (playerPrefab == null)
            {
                PlayerGymBuilder.BuildPlayerGym();
                playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            }
            if (playerPrefab != null)
            {
                var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
                player.transform.SetPositionAndRotation(new Vector3(0f, 0.1f, -3.5f), Quaternion.identity);
                PlayerGymHUD hud = player.GetComponent<PlayerGymHUD>();
                if (hud != null)
                {
                    using (var so = new SerializedObject(hud))
                    {
                        so.FindProperty("stageTitle").stringValue = "UPCYCLE CRAFTING WORKSHOP";
                        so.FindProperty("showControlsLegend").boolValue = true;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            EnsureSceneInBuildSettingsPreservingFirstThree(ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Upcycle City] Built Crafting Workshop scene at {ScenePath}.");
        }

        static void PlaceMachine(GameObject prefab, string instanceName, string placementId, Transform parent, Vector3 position, float yawDegrees)
        {
            if (prefab == null)
                return;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = instanceName;
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));

            CraftingMachine machine = go.GetComponent<CraftingMachine>();
            if (machine != null)
            {
                using (var so = new SerializedObject(machine))
                {
                    so.FindProperty("placementId").stringValue = placementId;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(machine);
            }

            ManualEnergyGenerator generator = go.GetComponent<ManualEnergyGenerator>();
            if (generator != null)
            {
                using (var so = new SerializedObject(generator))
                {
                    so.FindProperty("placementId").stringValue = placementId;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(generator);
            }
        }

        static void SpawnWorkshopTrashSamples()
        {
            WasteCatalog catalog = AssetDatabase.LoadAssetAtPath<WasteCatalog>(WasteCatalogPath);
            if (catalog == null || catalog.Entries.Count == 0)
                return;

            var trashRoot = new GameObject("WorkshopTrashSamples").transform;
            string[] wanted =
            {
                "Plastic Bottle",
                "Shipping Carton",
                "Crushed Can",
                "Wooden Pallet",
                "Old Tyre",
                "Plastic Bottle",
                "Crushed Can",
            };

            float startX = -4.5f;
            for (int i = 0; i < wanted.Length; i++)
            {
                WasteCatalog.Entry entry = FindEntryByName(catalog, wanted[i]);
                if (entry == null)
                    continue;
                Vector3 pos = new Vector3(startX + i * 1.5f, 0.65f, 1.5f);
                WasteCatalog.SpawnEntryLocal(entry, WasteSourceKind.FactoryScrap, pos, Vector3.zero, trashRoot);
            }
        }

        static WasteCatalog.Entry FindEntryByName(WasteCatalog catalog, string displayName)
        {
            for (int i = 0; i < catalog.Entries.Count; i++)
            {
                WasteCatalog.Entry e = catalog.Entries[i];
                if (e != null && string.Equals(e.displayName, displayName, System.StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        static void CreateBlock(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, bool withCollider = true)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.layer = GameLayers.Environment;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            if (!withCollider)
                Object.DestroyImmediate(go.GetComponent<Collider>());
            if (mat != null)
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static Material CreateSimpleUrpMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            else
                mat.color = color;
            return mat;
        }

        /// <summary>
        /// Ensures <paramref name="scenePath"/> is enabled in Build Settings at index &gt;= 3,
        /// leaving indices 0, 1, and 2 untouched.
        /// </summary>
        public static void EnsureSceneInBuildSettingsPreservingFirstThree(string scenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = 0; i < scenes.Count; i++)
            {
                if (string.Equals(scenes[i].path, scenePath, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!scenes[i].enabled)
                    {
                        scenes[i].enabled = true;
                        EditorBuildSettings.scenes = scenes.ToArray();
                    }
                    return;
                }
            }

            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
