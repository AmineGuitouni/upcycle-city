using System.Collections.Generic;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Crafting;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Installs Editor-side network prefab registration validation into <see cref="CraftingDatabase.ExternalNetworkPrefabRegisteredCheck"/>
    /// and provides a custom Inspector for <see cref="CraftingDatabase"/> with a one-click <b>Validate Database</b> button
    /// and inline pass/error report.
    /// </summary>
    [InitializeOnLoad]
    static class CraftingDatabaseValidationHook
    {
        static CraftingDatabaseValidationHook()
        {
            CraftingDatabase.ExternalNetworkPrefabRegisteredCheck = IsPrefabRegisteredInNetworkPrefabsList;
        }

        public static bool IsPrefabRegisteredInNetworkPrefabsList(GameObject prefab)
        {
            if (prefab == null)
                return false;

            NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(
                NetworkPrefabRegistryTool.NetworkPrefabsListPath);
            if (list == null)
                return false;

            return list.Contains(prefab);
        }
    }

    [CustomEditor(typeof(CraftingDatabase))]
    public sealed class CraftingDatabaseEditor : UnityEditor.Editor
    {
        readonly List<string> validationErrors = new List<string>(16);
        bool hasValidated;
        bool lastValid;

        void OnEnable()
        {
            RunValidation();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Database Validation", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate Database", GUILayout.Height(28f)))
                    RunValidation();

                if (GUILayout.Button("Refresh Network Prefabs", GUILayout.Height(28f)))
                {
                    NetworkPrefabRegistryTool.RefreshNetworkPrefabs(logSummary: true);
                    RunValidation();
                }
            }

            if (!hasValidated)
                return;

            var db = (CraftingDatabase)target;
            if (lastValid)
            {
                EditorGUILayout.HelpBox(
                    $"Validation passed: {db.MachineTypes.Count} machine types, {db.Materials.Count} materials, " +
                    $"{db.Cards.Count} cards, {db.Machines.Count} machines. All prefabs have NetworkObject and are registered.",
                    MessageType.Info);
            }
            else
            {
                string summary = $"Found {validationErrors.Count} validation error(s):\n• " +
                                 string.Join("\n• ", validationErrors);
                EditorGUILayout.HelpBox(summary, MessageType.Error);
            }
        }

        void RunValidation()
        {
            var db = target as CraftingDatabase;
            if (db == null)
                return;

            CraftingDatabase.ExternalNetworkPrefabRegisteredCheck =
                CraftingDatabaseValidationHook.IsPrefabRegisteredInNetworkPrefabsList;

            validationErrors.Clear();
            lastValid = db.Validate(validationErrors);
            hasValidated = true;
        }
    }
}
