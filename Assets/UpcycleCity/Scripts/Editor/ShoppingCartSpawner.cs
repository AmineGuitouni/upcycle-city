using System.IO;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Player;
using UpcycleCity.Props;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Spawns a pushable <see cref="ShoppingCart"/> into the currently open scene (near the player)
    /// without rebuilding the scene.
    /// </summary>
    public static class ShoppingCartSpawner
    {
        const string ConfigFolder = "Assets/UpcycleCity/Settings/Props";
        const string ConfigPath = "Assets/UpcycleCity/Settings/Props/ShoppingCartConfig.asset";


        [MenuItem("Upcycle City/Props/Spawn Shopping Cart", priority = 50)]
        public static void SpawnShoppingCart()
        {
            ShoppingCartConfig config = CreateOrLoadConfig();

            var go = new GameObject("ShoppingCart");

            // Place 2.2m in front of the player if present, otherwise at the world origin height.
            PlayerController player = Object.FindAnyObjectByType<PlayerController>();
            if (player != null)
            {
                Vector3 fwd = player.transform.forward;
                fwd.y = 0f;
                fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
                go.transform.position = player.transform.position + fwd * 2.2f + Vector3.up * 0.05f;
                go.transform.rotation = Quaternion.LookRotation(-fwd, Vector3.up); // handle toward the player
            }
            else
            {
                go.transform.position = new Vector3(0f, 0.05f, 2.5f);
            }

            var body = go.AddComponent<Rigidbody>();
            body.mass = config.mass;

            var cart = go.AddComponent<ShoppingCart>();
            cart.SetConfig(config);

            GameLayers.SetLayerRecursively(go, GameLayers.Interactable);

            Undo.RegisterCreatedObjectUndo(go, "Spawn Shopping Cart");
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            Debug.Log($"[ShoppingCartSpawner] Spawned ShoppingCart at {go.transform.position}. Enter Play Mode to build visuals and push it.");
        }


        static ShoppingCartConfig CreateOrLoadConfig()
        {
            var existing = AssetDatabase.LoadAssetAtPath<ShoppingCartConfig>(ConfigPath);
            if (existing != null)
                return existing;

            if (!AssetDatabase.IsValidFolder(ConfigFolder))
                Directory.CreateDirectory(ConfigFolder);

            var config = ScriptableObject.CreateInstance<ShoppingCartConfig>();
            AssetDatabase.CreateAsset(config, ConfigPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return config;
        }
    }
}
