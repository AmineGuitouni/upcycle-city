using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Networking;
using UpcycleCity.Player;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Automatically collects every prefab under <c>Assets/UpcycleCity</c> containing a <see cref="NetworkObject"/>
    /// into <c>Assets/UpcycleCity/Settings/Networking/UpcycleCityNetworkPrefabs.asset</c> and validates that
    /// networked prefabs have the required components and authority settings.
    /// </summary>
    public static class NetworkPrefabRegistryTool
    {
        public const string NetworkSettingsFolder = "Assets/UpcycleCity/Settings/Networking";
        public const string NetworkPrefabsListPath = NetworkSettingsFolder + "/UpcycleCityNetworkPrefabs.asset";
        const string LegacyDefaultNetworkPrefabsPath = "Assets/DefaultNetworkPrefabs.asset";

        [MenuItem("Upcycle City/Networking/Refresh Network Prefabs", priority = 50)]
        public static void RefreshNetworkPrefabsMenu()
        {
            int count = RefreshNetworkPrefabs(logSummary: true);
            Debug.Log($"[Upcycle City] Refreshed NetworkPrefabsList ({count} registered prefab(s)) at {NetworkPrefabsListPath}.");
        }

        /// <summary>
        /// Disables NGO's auto-generated <c>Assets/DefaultNetworkPrefabs.asset</c> in
        /// <c>ProjectSettings/NetcodeForGameObjects.asset</c> so <see cref="NetworkPrefabsListPath"/> is the single
        /// canonical prefab registry, and removes any stale <c>Assets/DefaultNetworkPrefabs.asset</c>.
        /// </summary>
        public static void EnsureDefaultNetworkPrefabsDisabled()
        {
            Type settingsType = Type.GetType("Unity.Netcode.Editor.Configuration.NetcodeForGameObjectsProjectSettings, Unity.Netcode.Editor");
            if (settingsType != null)
            {
                PropertyInfo instanceProp = settingsType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                object settingsObj = instanceProp?.GetValue(null);
                if (settingsObj != null)
                {
                    FieldInfo genField = settingsType.GetField("GenerateDefaultNetworkPrefabs", BindingFlags.Public | BindingFlags.Instance);
                    bool currentlyEnabled = genField != null && genField.GetValue(settingsObj) is bool b && b;
                    bool fileMissing = !File.Exists("ProjectSettings/NetcodeForGameObjects.asset");
                    if (currentlyEnabled || fileMissing)
                    {
                        genField?.SetValue(settingsObj, false);
                        MethodInfo saveMethod = settingsType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (saveMethod != null)
                            saveMethod.Invoke(settingsObj, null);
                        else
                            settingsType.GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(settingsObj, new object[] { true });
                    }
                }
            }

            if (AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(LegacyDefaultNetworkPrefabsPath) != null
                || File.Exists(LegacyDefaultNetworkPrefabsPath))
            {
                AssetDatabase.DeleteAsset(LegacyDefaultNetworkPrefabsPath);
            }
        }

        /// <summary>
        /// Scans <c>Assets/UpcycleCity</c> for prefabs with <see cref="NetworkObject"/>, updates
        /// <see cref="NetworkPrefabsList"/>, and emits validation warnings for misconfigured prefabs.
        /// </summary>
        public static int RefreshNetworkPrefabs(bool logSummary = false)
        {
            EnsureDefaultNetworkPrefabsDisabled();
            Directory.CreateDirectory(NetworkSettingsFolder);
            NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsListPath);
            if (list == null)
            {
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                AssetDatabase.CreateAsset(list, NetworkPrefabsListPath);
            }

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/UpcycleCity" });
            var discovered = new List<GameObject>(16);
            var discoveredSet = new HashSet<GameObject>();

            for (int i = 0; i < prefabGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                if (string.IsNullOrEmpty(path))
                    continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                NetworkObject netObj = prefab.GetComponent<NetworkObject>();
                if (netObj == null)
                    continue;

                ValidateNetworkPrefab(prefab, path);
                if (discoveredSet.Add(prefab))
                    discovered.Add(prefab);
            }

            // Remove missing or no-longer-networked entries
            bool dirty = false;
            var existing = new List<NetworkPrefab>(list.PrefabList);
            for (int i = 0; i < existing.Count; i++)
            {
                NetworkPrefab entry = existing[i];
                if (entry == null || entry.Prefab == null || !discoveredSet.Contains(entry.Prefab))
                {
                    list.Remove(entry);
                    dirty = true;
                }
            }

            // Add newly discovered networked prefabs
            for (int i = 0; i < discovered.Count; i++)
            {
                GameObject prefab = discovered[i];
                if (!list.Contains(prefab))
                {
                    list.Add(new NetworkPrefab { Prefab = prefab });
                    dirty = true;
                }
            }

            if (dirty)
            {
                EditorUtility.SetDirty(list);
                AssetDatabase.SaveAssetIfDirty(list);
            }

            if (logSummary)
            {
                for (int i = 0; i < discovered.Count; i++)
                    Debug.Log($"[Upcycle City] Registered network prefab: {AssetDatabase.GetAssetPath(discovered[i])}");
            }

            return discovered.Count;
        }

        static void ValidateNetworkPrefab(GameObject prefab, string path)
        {
            NetworkObject netObj = prefab.GetComponent<NetworkObject>();
            NetworkTransform netTransform = prefab.GetComponent<NetworkTransform>();
            NetworkedPhysicsItem physicsItem = prefab.GetComponent<NetworkedPhysicsItem>();
            NetworkPlayerController playerNet = prefab.GetComponent<NetworkPlayerController>();

            if (physicsItem != null)
            {
                if (netObj != null && !netObj.DontDestroyWithOwner)
                {
                    Debug.LogWarning(
                        $"[Upcycle City] Network prefab '{path}' has NetworkedPhysicsItem but NetworkObject.DontDestroyWithOwner is false. " +
                        "Enable DontDestroyWithOwner so a client disconnecting while holding this item drops it instead of destroying it.",
                        prefab);
                }

                if (prefab.GetComponent<Rigidbody>() == null)
                    Debug.LogWarning($"[Upcycle City] Network prefab '{path}' has NetworkedPhysicsItem but is missing a Rigidbody.", prefab);

                if (netTransform == null)
                    Debug.LogWarning($"[Upcycle City] Network prefab '{path}' has NetworkedPhysicsItem but is missing a NetworkTransform.", prefab);
                else if (netTransform.AuthorityMode != NetworkTransform.AuthorityModes.Owner)
                    Debug.LogWarning($"[Upcycle City] Network prefab '{path}' should set NetworkTransform.AuthorityMode = Owner for responsive grabbing.", prefab);
            }

            if (playerNet != null)
            {
                if (prefab.GetComponent<PlayerController>() == null)
                    Debug.LogWarning($"[Upcycle City] Network prefab '{path}' has NetworkPlayerController but is missing PlayerController.", prefab);

                if (netTransform == null)
                    Debug.LogWarning($"[Upcycle City] Network prefab '{path}' has NetworkPlayerController but is missing NetworkTransform.", prefab);
                else if (netTransform.AuthorityMode != NetworkTransform.AuthorityModes.Owner)
                    Debug.LogWarning($"[Upcycle City] Network prefab '{path}' should set NetworkTransform.AuthorityMode = Owner for local player feel.", prefab);
            }

            NetworkRigidbody netRb = prefab.GetComponent<NetworkRigidbody>();
            if (netRb != null && netRb.AutoUpdateKinematicState)
            {
                Debug.LogWarning(
                    $"[Upcycle City] Network prefab '{path}' uses NetworkRigidbody with AutoUpdateKinematicState enabled, " +
                    "which forces Rigidbody.isKinematic=true offline. Prefer NetworkedPhysicsItem or NetworkPlayerController.",
                    prefab);
            }
        }
    }

    /// <summary>
    /// Automatically keeps <see cref="NetworkPrefabRegistryTool.NetworkPrefabsListPath"/> up to date whenever
    /// prefabs under <c>Assets/UpcycleCity</c> are added, moved, or deleted.
    /// </summary>
    sealed class NetworkPrefabAutoRegistrar : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (!TouchesUpcyclePrefab(importedAssets)
                && !TouchesUpcyclePrefab(deletedAssets)
                && !TouchesUpcyclePrefab(movedAssets)
                && !TouchesUpcyclePrefab(movedFromAssetPaths))
                return;

            // Only auto-refresh once the NetworkPrefabsList asset exists
            if (AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabRegistryTool.NetworkPrefabsListPath) == null)
                return;

            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    NetworkPrefabRegistryTool.RefreshNetworkPrefabs(logSummary: false);
            };
        }

        static bool TouchesUpcyclePrefab(string[] paths)
        {
            if (paths == null)
                return false;
            for (int i = 0; i < paths.Length; i++)
            {
                string p = paths[i];
                if (!string.IsNullOrEmpty(p)
                    && p.StartsWith("Assets/UpcycleCity/", System.StringComparison.OrdinalIgnoreCase)
                    && p.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
