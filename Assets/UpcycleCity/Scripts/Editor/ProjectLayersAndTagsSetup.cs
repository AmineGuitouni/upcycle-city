using UnityEditor;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Ensures Upcycle City's standard Layers, Tags, and Physics Layer Collision rules
    /// are registered in ProjectSettings/TagManager.asset and DynamicsManager.asset.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectLayersAndTagsSetup
    {
        static readonly (int Index, string Name)[] CustomLayers =
        {
            (GameLayers.Player, GameLayers.PlayerName),
            (GameLayers.Environment, GameLayers.EnvironmentName),
            (GameLayers.Interactable, GameLayers.InteractableName),
            (GameLayers.Grabbable, GameLayers.GrabbableName),
            (GameLayers.HeldObject, GameLayers.HeldObjectName),
            (GameLayers.Pickup, GameLayers.PickupName),
            (GameLayers.Citizen, GameLayers.CitizenName),
            (GameLayers.Vehicle, GameLayers.VehicleName),
            (GameLayers.TriggerZone, GameLayers.TriggerZoneName),
        };

        static ProjectLayersAndTagsSetup()
        {
            EditorApplication.delayCall += () => EnsureAll(logWhenUnchanged: false);
        }

        [MenuItem("Upcycle City/Setup Project Layers & Tags")]
        public static void SetupMenu()
        {
            EnsureAll(logWhenUnchanged: true);
        }

        public static void EnsureAll(bool logWhenUnchanged = false)
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
                return;

            bool changed = false;
            using (var tagManager = new SerializedObject(assets[0]))
            {
                SerializedProperty layersProp = tagManager.FindProperty("layers");
                SerializedProperty tagsProp = tagManager.FindProperty("tags");

                // 1. Register layers in fixed slots 6..14
                for (int i = 0; i < CustomLayers.Length; i++)
                {
                    (int index, string name) = CustomLayers[i];
                    if (index >= layersProp.arraySize)
                        continue;

                    SerializedProperty slot = layersProp.GetArrayElementAtIndex(index);
                    if (slot.stringValue != name)
                    {
                        slot.stringValue = name;
                        changed = true;
                    }
                }

                // 2. Register custom tags idempotently
                for (int i = 0; i < GameTags.CustomTags.Length; i++)
                {
                    string tag = GameTags.CustomTags[i];
                    if (!ContainsString(tagsProp, tag))
                    {
                        int insertAt = tagsProp.arraySize;
                        tagsProp.InsertArrayElementAtIndex(insertAt);
                        tagsProp.GetArrayElementAtIndex(insertAt).stringValue = tag;
                        changed = true;
                    }
                }

                if (changed)
                {
                    tagManager.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssets();
                }
            }

            // 3. Ensure Player never collides with HeldObject in the Physics matrix
            if (!Physics.GetIgnoreLayerCollision(GameLayers.Player, GameLayers.HeldObject))
            {
                Physics.IgnoreLayerCollision(GameLayers.Player, GameLayers.HeldObject, true);
                changed = true;
            }

            if (changed || logWhenUnchanged)
                Debug.Log("[Upcycle City] Project Layers (6..14), Tags, and Physics Collision Matrix are synchronized.");
        }

        static bool ContainsString(SerializedProperty arrayProp, string value)
        {
            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                if (arrayProp.GetArrayElementAtIndex(i).stringValue == value)
                    return true;
            }
            return false;
        }
    }
}
