using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Networking;
using UpcycleCity.Player;
using UpcycleCity.Props;
using UpcycleCity.Waste;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds the networked Shopping Cart prefab (<c>Resources/ShoppingCart.prefab</c>, spawned by the host when a player
    /// buys a cart at the Quincaillerie) and offers a dev spawn in Play Mode. Carts are bought, not placed in scenes.
    /// </summary>
    public static class ShoppingCartSpawner
    {
        const string ConfigFolder = "Assets/UpcycleCity/Settings/Props";
        const string ConfigPath = "Assets/UpcycleCity/Settings/Props/ShoppingCartConfig.asset";
        public const string PrefabPath = "Assets/UpcycleCity/Resources/" + RecyclingBagService.CartPrefabResource + ".prefab";

        [MenuItem("Upcycle City/Props/Build Shopping Cart Prefab", priority = 49)]
        public static void BuildPrefabMenu()
        {
            GameObject prefab = BuildPrefab();
            NetworkPrefabRegistryTool.RefreshNetworkPrefabs();
            Debug.Log($"[Upcycle City] Shopping Cart prefab built at {PrefabPath} and registered as a network prefab.", prefab);
        }

        /// <summary>Creates or updates the networked cart prefab (non-destructive: keeps the asset's GUID).</summary>
        public static GameObject BuildPrefab()
        {
            ShoppingCartConfig config = CreateOrLoadConfig();
            var root = new GameObject(RecyclingBagService.CartPrefabResource);
            try
            {
                var body = root.AddComponent<Rigidbody>();
                body.mass = config.mass;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

                NetworkObject netObj = root.AddComponent<NetworkObject>();
                netObj.DontDestroyWithOwner = true;
                NetworkTransform nt = root.AddComponent<NetworkTransform>();
                nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
                nt.SyncRotAngleX = false;
                nt.SyncRotAngleZ = false;
                nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
                nt.Interpolate = true;
                root.AddComponent<NetworkMachineIdentity>();
                root.AddComponent<NetworkedShoppingCart>();

                ShoppingCart cart = root.AddComponent<ShoppingCart>();
                cart.SetConfig(config);
                GameLayers.SetLayerRecursively(root, GameLayers.Interactable);

                string folder = Path.GetDirectoryName(PrefabPath);
                if (!AssetDatabase.IsValidFolder(folder))
                    Directory.CreateDirectory(folder);
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Dev: spawns a cart in front of the player through the game authority (host / solo, Play Mode).</summary>
        [MenuItem("Upcycle City/Props/Spawn Shopping Cart (Play Mode)", priority = 50)]
        public static void SpawnShoppingCart()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Spawn Shopping Cart",
                    "Enter Play Mode first. Carts are bought at the Quincaillerie; this dev spawn puts one in front of the player.", "OK");
                return;
            }
            if (!GameAuthority.IsAuthoritative)
            {
                Debug.LogWarning("[Upcycle City] Only the host (or solo) can spawn a cart.");
                return;
            }
            GameObject prefab = Resources.Load<GameObject>(RecyclingBagService.CartPrefabResource);
            if (prefab == null)
            {
                Debug.LogWarning("[Upcycle City] No cart prefab: run Upcycle City → Props → Build Shopping Cart Prefab.");
                return;
            }

            Vector3 position = new Vector3(0f, 0.05f, 2.5f);
            Quaternion rotation = Quaternion.identity;
            PlayerController player = PlayerContext.Active;
            if (player != null)
            {
                Vector3 fwd = player.transform.forward;
                fwd.y = 0f;
                fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
                position = player.transform.position + fwd * 2.2f + Vector3.up * 0.05f;
                rotation = Quaternion.LookRotation(fwd, Vector3.up);   // handle towards the player
            }
            string id = "cart_dev_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            GameObject go = GameAuthority.Current.SpawnNetworkedPrefab(prefab, position, rotation,
                spawned => spawned.GetComponent<ShoppingCart>()?.ApplyPlacementId(id));
            if (go != null)
                Selection.activeGameObject = go;
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
