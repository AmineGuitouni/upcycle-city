using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Networking;
using UpcycleCity.Waste;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Idempotent Editor setup tool that builds the co-op networking assets:
    /// <c>NetworkPlayer.prefab</c> (variant of <c>Player.prefab</c>), <c>NetworkTrashItem.prefab</c>,
    /// <c>NetworkCityState.prefab</c>, <c>NetworkSettings.asset</c>, <c>UpcycleCityNetworkPrefabs.asset</c>,
    /// and wires <see cref="CoopSessionManager"/> + <see cref="CoopMenuController"/> into <c>CharacterCreator.unity</c>.
    /// </summary>
    public static class CoopMultiplayerSetup
    {
        const string OfflinePlayerPrefabPath = "Assets/UpcycleCity/Prefabs/Player/Player.prefab";
        const string NetworkPlayerPrefabPath = "Assets/UpcycleCity/Prefabs/Player/NetworkPlayer.prefab";
        const string NetworkPrefabsFolder = "Assets/UpcycleCity/Prefabs/Networking";
        const string NetworkTrashPrefabPath = NetworkPrefabsFolder + "/NetworkTrashItem.prefab";
        const string NetworkCityStatePrefabPath = NetworkPrefabsFolder + "/NetworkCityState.prefab";
        const string NetworkSettingsPath = NetworkPrefabRegistryTool.NetworkSettingsFolder + "/NetworkSettings.asset";
        const string PanelSettingsPath = "Assets/UpcycleCity/UI/UpcycleCityPanelSettings.asset";
        const string CoopMenuStylePath = "Assets/UpcycleCity/UI/CoopMenu.uss";
        const string CharacterCreatorScenePath = "Assets/UpcycleCity/Scenes/CharacterCreator.unity";

        [MenuItem("Upcycle City/Networking/Setup Co-op Prefabs & Scene", priority = 51)]
        public static void SetupAll()
        {
            Directory.CreateDirectory(NetworkPrefabsFolder);
            Directory.CreateDirectory(NetworkPrefabRegistryTool.NetworkSettingsFolder);

            GameObject networkPlayerPrefab = CreateOrUpdateNetworkPlayerPrefab();
            GameObject networkTrashPrefab = CreateOrUpdateNetworkTrashPrefab();
            GameObject networkCityStatePrefab = CreateOrUpdateNetworkCityStatePrefab(networkTrashPrefab);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int registered = NetworkPrefabRegistryTool.RefreshNetworkPrefabs(logSummary: true);
            NetworkPrefabsList prefabsList = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(
                NetworkPrefabRegistryTool.NetworkPrefabsListPath);

            NetworkSettings settings = CreateOrUpdateNetworkSettings(
                prefabsList,
                networkPlayerPrefab,
                networkTrashPrefab,
                networkCityStatePrefab);

            WireCharacterCreatorScene();

            AssetDatabase.SaveAssets();
            Debug.Log($"[Upcycle City] Co-op setup complete ({registered} network prefabs registered, NetworkSettings + CharacterCreator wired).");
        }

        static GameObject CreateOrUpdateNetworkPlayerPrefab()
        {
            GameObject basePlayer = AssetDatabase.LoadAssetAtPath<GameObject>(OfflinePlayerPrefabPath);
            if (basePlayer == null)
            {
                Debug.LogError($"[Upcycle City] Missing offline player prefab at {OfflinePlayerPrefabPath}.");
                return null;
            }

            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkPlayerPrefabPath);
            GameObject instance = existing != null
                ? (GameObject)PrefabUtility.InstantiatePrefab(existing)
                : (GameObject)PrefabUtility.InstantiatePrefab(basePlayer);
            instance.name = "NetworkPlayer";

            if (instance.GetComponent<NetworkObject>() == null)
                instance.AddComponent<NetworkObject>();

            NetworkTransform nt = instance.GetComponent<NetworkTransform>();
            if (nt == null)
                nt = instance.AddComponent<NetworkTransform>();
            ConfigureOwnerNetworkTransform(nt);

            if (instance.GetComponent<NetworkPlayerController>() == null)
                instance.AddComponent<NetworkPlayerController>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(instance, NetworkPlayerPrefabPath);
            Object.DestroyImmediate(instance);
            return saved;
        }

        static GameObject CreateOrUpdateNetworkTrashPrefab()
        {
            var go = new GameObject("NetworkTrashItem");
            go.layer = GameLayers.Grabbable;
            go.tag = GameTags.Trash;

            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.14f, 0f);
            box.size = new Vector3(0.28f, 0.28f, 0.28f);

            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.5f;
            rb.linearDamping = 0.25f;
            rb.angularDamping = 0.5f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            go.AddComponent<TrashItem>();
            go.AddComponent<FocusHighlight>();
            NetworkObject netObj = go.AddComponent<NetworkObject>();
            netObj.DontDestroyWithOwner = true;

            NetworkTransform nt = go.AddComponent<NetworkTransform>();
            ConfigureOwnerNetworkTransform(nt);

            go.AddComponent<NetworkedPhysicsItem>();
            go.AddComponent<NetworkTrashArchetype>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, NetworkTrashPrefabPath);
            Object.DestroyImmediate(go);
            return saved;
        }

        static GameObject CreateOrUpdateNetworkCityStatePrefab(GameObject networkTrashPrefab)
        {
            var go = new GameObject("NetworkCityState");
            go.AddComponent<NetworkObject>();
            NetworkStateReplicator replicator = go.AddComponent<NetworkStateReplicator>();
            replicator.Configure(networkTrashPrefab, 0.25f, 25);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, NetworkCityStatePrefabPath);
            Object.DestroyImmediate(go);
            return saved;
        }

        static NetworkSettings CreateOrUpdateNetworkSettings(
            NetworkPrefabsList prefabsList,
            GameObject playerPrefab,
            GameObject trashPrefab,
            GameObject cityStatePrefab)
        {
            NetworkSettings settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(NetworkSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<NetworkSettings>();
                AssetDatabase.CreateAsset(settings, NetworkSettingsPath);
            }

            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            StyleSheet menuStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>(CoopMenuStylePath);

            settings.ConfigureAssets(prefabsList, playerPrefab, trashPrefab, cityStatePrefab, panel, menuStyle);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            return settings;
        }

        static void ConfigureOwnerNetworkTransform(NetworkTransform nt)
        {
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            nt.Interpolate = true;
            nt.SyncPositionX = true;
            nt.SyncPositionY = true;
            nt.SyncPositionZ = true;
            nt.SyncRotAngleX = true;
            nt.SyncRotAngleY = true;
            nt.SyncRotAngleZ = true;
            nt.SyncScaleX = false;
            nt.SyncScaleY = false;
            nt.SyncScaleZ = false;
        }

        static void WireCharacterCreatorScene()
        {
            if (!File.Exists(CharacterCreatorScenePath))
                return;

            string previousScenePath = SceneManager.GetActiveScene().path;
            Scene creatorScene = EditorSceneManager.OpenScene(CharacterCreatorScenePath, OpenSceneMode.Single);

            // Reload NetworkSettings AFTER OpenScene, since OpenSceneMode.Single unloads unreferenced ScriptableObjects
            NetworkSettings settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(NetworkSettingsPath);
            WireCoopIntoActiveCreatorScene(settings);

            EditorSceneManager.MarkSceneDirty(creatorScene);
            EditorSceneManager.SaveScene(creatorScene, CharacterCreatorScenePath);

            if (!string.IsNullOrEmpty(previousScenePath) && previousScenePath != CharacterCreatorScenePath && File.Exists(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }

        /// <summary>Wires CoopSessionManager + NetworkManager + UnityTransport + CoopMenuController into the active CharacterCreator scene.</summary>
        public static void WireCoopIntoActiveCreatorScene(NetworkSettings settings = null)
        {
            if (settings == null)
                settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(NetworkSettingsPath);
            if (settings == null)
            {
                Debug.LogWarning($"[Upcycle City] Could not load NetworkSettings at {NetworkSettingsPath}; skipping co-op scene wiring.");
                return;
            }

            // Ensure CoopSessionRoot with NetworkManager + UnityTransport + CoopSessionManager
            CoopSessionManager existingSession = Object.FindAnyObjectByType<CoopSessionManager>();
            GameObject sessionGo = existingSession != null ? existingSession.gameObject : new GameObject("CoopSessionManager");
            CoopSessionManager sessionManager = existingSession != null ? existingSession : sessionGo.AddComponent<CoopSessionManager>();
            sessionManager.Configure(settings);

            NetworkManager nm = sessionGo.GetComponent<NetworkManager>();
            if (nm == null)
                nm = sessionGo.AddComponent<NetworkManager>();

            UnityTransport transport = sessionGo.GetComponent<UnityTransport>();
            if (transport == null)
                transport = sessionGo.AddComponent<UnityTransport>();

            nm.NetworkConfig ??= new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = transport;
            nm.NetworkConfig.TickRate = settings.TickRate;
            nm.NetworkConfig.EnableSceneManagement = true;
            nm.NetworkConfig.ConnectionApproval = true;
            nm.NetworkConfig.PlayerPrefab = settings.NetworkPlayerPrefab;
            if (settings.NetworkPrefabsList != null)
            {
                nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
                nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(settings.NetworkPrefabsList);
            }
            EditorUtility.SetDirty(nm);
            EditorUtility.SetDirty(sessionManager);

            // Ensure CoopMenuController in the CharacterCreator scene
            CoopMenuController existingMenu = Object.FindAnyObjectByType<CoopMenuController>();
            GameObject menuGo = existingMenu != null ? existingMenu.gameObject : new GameObject("CoopMenuUI");
            CoopMenuController menu = existingMenu != null ? existingMenu : menuGo.AddComponent<CoopMenuController>();
            CharacterCreatorController creator = Object.FindAnyObjectByType<CharacterCreatorController>();
            menu.Configure(settings.PanelSettings, settings.CoopMenuStyleSheet, creator);
            EditorUtility.SetDirty(menu);
        }
    }
}
