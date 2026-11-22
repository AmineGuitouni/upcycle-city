using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Crafting;
using UpcycleCity.UI;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Scans <c>Assets/UpcycleCity/Art/Icons/**</c>, (re)builds <c>Assets/UpcycleCity/Resources/IconLibrary.asset</c>,
    /// and populates empty <see cref="MaterialDefinition"/> <c>icon</c> fields by matching <c>materialId</c>.
    /// </summary>
    public static class IconLibraryBuilder
    {
        const string IconsRoot = "Assets/UpcycleCity/Art/Icons";
        const string SourceSubfolder = "Assets/UpcycleCity/Art/Icons/_Source/";
        const string ResourcesFolder = "Assets/UpcycleCity/Resources";
        const string LibraryAssetPath = "Assets/UpcycleCity/Resources/IconLibrary.asset";
        const string MaterialsFolder = "Assets/UpcycleCity/Settings/Crafting/Materials";

        [MenuItem("Upcycle City/UI/Rebuild Icon Library")]
        public static void RebuildIconLibrary()
        {
            if (!AssetDatabase.IsValidFolder(IconsRoot))
            {
                Debug.LogWarning($"[IconLibraryBuilder] Icons folder not found: {IconsRoot}");
                return;
            }

            EnsureFolder(ResourcesFolder);

            string[] spriteGuids = AssetDatabase.FindAssets("t:Sprite", new[] { IconsRoot });
            var entriesByKey = new SortedDictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

            foreach (string guid in spriteGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    continue;

                if (path.StartsWith(SourceSubfolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null)
                    continue;

                string key = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(key))
                    continue;

                if (entriesByKey.ContainsKey(key))
                {
                    Debug.LogWarning($"[IconLibraryBuilder] Duplicate icon key '{key}' at {path}; overwriting previous entry.");
                }

                entriesByKey[key] = sprite;
            }

            var entries = new List<IconLibrary.Entry>(entriesByKey.Count);
            foreach (var kv in entriesByKey)
            {
                entries.Add(new IconLibrary.Entry { key = kv.Key, sprite = kv.Value });
            }

            var library = AssetDatabase.LoadAssetAtPath<IconLibrary>(LibraryAssetPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<IconLibrary>();
                AssetDatabase.CreateAsset(library, LibraryAssetPath);
            }

            library.SetEntries(entries);
            EditorUtility.SetDirty(library);

            int materialsAssigned = AssignMissingMaterialIcons(library);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[IconLibraryBuilder] Rebuilt {LibraryAssetPath} with {entries.Count} icons; assigned {materialsAssigned} MaterialDefinition icon(s).");
        }

        static int AssignMissingMaterialIcons(IconLibrary library)
        {
            if (!AssetDatabase.IsValidFolder(MaterialsFolder))
                return 0;

            int assigned = 0;
            string[] matGuids = AssetDatabase.FindAssets("t:MaterialDefinition", new[] { MaterialsFolder });
            foreach (string guid in matGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<MaterialDefinition>(path);
                if (mat == null || string.IsNullOrEmpty(mat.MaterialId))
                    continue;

                var so = new SerializedObject(mat);
                var iconProp = so.FindProperty("icon");
                if (iconProp == null || iconProp.objectReferenceValue != null)
                    continue;

                Sprite sprite = library.ForMaterial(mat.MaterialId);
                if (sprite == null)
                    continue;

                iconProp.objectReferenceValue = sprite;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(mat);
                assigned++;
            }

            return assigned;
        }

        static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
                AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
