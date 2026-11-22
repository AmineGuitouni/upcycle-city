using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UpcycleCity.Crowd;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Bakes the <see cref="CrowdNavMesh"/> walking citizens route over, and saves it next to the scene
    /// (<c>Scenes/&lt;Scene&gt;/CrowdNavMesh.asset</c>) so phone builds load it instead of building it at start.
    /// Re-bake after moving buildings, bins, benches or machines.
    /// </summary>
    public static class CrowdNavMeshTool
    {
        [MenuItem("Upcycle City/Crowd/Bake Crowd NavMesh", priority = 40)]
        static void BakeActiveScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (BakeInScene(scene))
                EditorSceneManager.SaveScene(scene);
        }

        /// <summary>Adds a <see cref="CrowdNavMesh"/> to the scene if missing, bakes it and stores the data as an asset.</summary>
        public static bool BakeInScene(Scene scene)
        {
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning("[Upcycle City] Save the scene before baking its crowd nav mesh.");
                return false;
            }

            CrowdNavMesh navMesh = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                navMesh = root.GetComponentInChildren<CrowdNavMesh>(true);
                if (navMesh != null)
                    break;
            }
            if (navMesh == null)
            {
                var go = new GameObject("CrowdNavMesh");
                SceneManager.MoveGameObjectToScene(go, scene);
                navMesh = go.AddComponent<CrowdNavMesh>();
            }

            NavMeshData data = navMesh.BuildNow();
            if (data == null)
            {
                Debug.LogWarning("[Upcycle City] Crowd nav mesh: no walkable colliders found in the area.");
                return false;
            }

            string folder = Path.Combine(Path.GetDirectoryName(scene.path), scene.name).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(scene.path).Replace('\\', '/'), scene.name);
            string assetPath = $"{folder}/CrowdNavMesh.asset";
            if (AssetDatabase.LoadAssetAtPath<NavMeshData>(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(data, assetPath);
            AssetDatabase.SaveAssets();

            navMesh.SetBakedData(AssetDatabase.LoadAssetAtPath<NavMeshData>(assetPath));
            EditorUtility.SetDirty(navMesh);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[Upcycle City] Baked the crowd nav mesh for {scene.name} → {assetPath}");
            return true;
        }
    }
}
