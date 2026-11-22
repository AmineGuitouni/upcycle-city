using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UpcycleCity.Core;
using UpcycleCity.Crafting;
using UpcycleCity.Interaction;
using UpcycleCity.Waste;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Sets up street-bin searching: the shared <see cref="BinLootTable"/> (Resources), the <c>organic_waste</c>
    /// material in the Crafting Database, and a <see cref="CompostBin"/> beside the sorting bins of the open scene.
    /// Safe to run again: it only adds what is missing and never overwrites tuning.
    /// </summary>
    public static class BinSearchSetup
    {
        public const string LootTablePath = "Assets/UpcycleCity/Resources/BinLootTable.asset";
        const string WheelieModel = "Assets/UpcycleCity/Art/UpcycleProps/Machines/wheelie_paper.fbx";

        [MenuItem("Upcycle City/Waste/Setup Bin Search (Loot Table + Compost Bin)", priority = 30)]
        static void SetupMenu()
        {
            EnsureLootTable();
            EnsureOrganicMaterial();
            Scene scene = SceneManager.GetActiveScene();
            if (EnsureCompostBin(scene) != null)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            AssetDatabase.SaveAssets();
        }

        public static BinLootTable EnsureLootTable()
        {
            var table = AssetDatabase.LoadAssetAtPath<BinLootTable>(LootTablePath);
            if (table != null)
                return table;
            table = ScriptableObject.CreateInstance<BinLootTable>();
            table.SetEntries(BinLootTable.DefaultEntries());
            AssetDatabase.CreateAsset(table, LootTablePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Upcycle City] Created the bin loot table at {LootTablePath}");
            return table;
        }

        /// <summary>Adds <c>organic_waste</c> to the Crafting Database (appended, so existing stock indices don't move).</summary>
        public static MaterialDefinition EnsureOrganicMaterial()
        {
            string path = $"{CraftingContentBuilder.MaterialsDir}/Mat_OrganicWaste.asset";
            var material = AssetDatabase.LoadAssetAtPath<MaterialDefinition>(path);
            if (material == null)
            {
                material = ScriptableObject.CreateInstance<MaterialDefinition>();
                material.Configure("organic_waste", "Organic Waste",
                    "Food scraps, peels and coffee grounds sorted out of street bins. Compost feedstock for fertiliser.",
                    MaterialCategory.Trash, MaterialUnit.Kilograms, new Color(0.471f, 0.341f, 0.180f),
                    acceptTrash: false, yieldPerItem: 0.5f);
                AssetDatabase.CreateAsset(material, path);
            }

            var database = AssetDatabase.LoadAssetAtPath<CraftingDatabase>(CraftingContentBuilder.DatabasePath);
            if (database != null && !database.Materials.Contains(material))
            {
                using (var so = new SerializedObject(database))
                {
                    SerializedProperty list = so.FindProperty("materials");
                    list.arraySize++;
                    list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = material;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorUtility.SetDirty(database);
                Debug.Log("[Upcycle City] Added Organic Waste to the Crafting Database.");
            }
            return material;
        }

        /// <summary>Places a compost bin next to the CANS sorting bin, if the scene has sorting bins and no compost bin yet.</summary>
        public static CompostBin EnsureCompostBin(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            if (roots.Any(r => r.GetComponentInChildren<CompostBin>(true) != null))
                return null;
            SortingCrate anchor = roots.SelectMany(r => r.GetComponentsInChildren<SortingCrate>(true))
                .OrderByDescending(c => c.AcceptedStream == WasteStream.Cans)
                .FirstOrDefault();
            if (anchor == null)
            {
                Debug.LogWarning("[Upcycle City] No sorting bins in this scene: place a Compost Bin by hand.");
                return null;
            }

            var go = new GameObject("CompostBin_Organic");
            go.transform.SetParent(anchor.transform.parent, false);
            go.transform.SetPositionAndRotation(anchor.transform.position + anchor.transform.right * 1.8f, anchor.transform.rotation);

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(WheelieModel);
            if (model != null)
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, go.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
            }
            BoxCollider solid = go.AddComponent<BoxCollider>();
            solid.center = new Vector3(0f, 0.42f, 0f);
            solid.size = new Vector3(0.98f, 0.84f, 0.98f);

            CompostBin compost = go.AddComponent<CompostBin>();
            go.AddComponent<FocusHighlight>();
            GameLayers.SetLayerRecursively(go, GameLayers.Interactable);
            Undo.RegisterCreatedObjectUndo(go, "Add Compost Bin");
            Debug.Log($"[Upcycle City] Added a compost bin to {scene.name} beside {anchor.name}.");
            return compost;
        }
    }
}
