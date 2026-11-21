using UnityEditor;
using UnityEngine;
using UpcycleCity.Player;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// PlayerConfig inspector: range sliders plus one-click feel presets (undoable).
    /// </summary>
    [CustomEditor(typeof(PlayerConfig))]
    public sealed class PlayerConfigEditor : UnityEditor.Editor
    {
        static readonly string[] PresetLabels = { "Arcade (Default)", "Snappy", "Heavy", "Low Gravity" };

        public override void OnInspectorGUI()
        {
            DrawPresetButtons((PlayerConfig)target);
            EditorGUILayout.Space(4f);
            DrawDefaultInspector();
        }

        internal static void DrawPresetButtons(PlayerConfig config)
        {
            EditorGUILayout.LabelField("Presets (overwrite all values · Ctrl+Z to undo)", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < PresetLabels.Length; i++)
            {
                if (GUILayout.Button(PresetLabels[i], EditorStyles.miniButton))
                {
                    Undo.RecordObject(config, $"Apply {PresetLabels[i]} Player Preset");
                    config.ApplyPreset((PlayerTuningPreset)i);
                    EditorUtility.SetDirty(config);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    /// <summary>
    /// Shows the assigned PlayerConfig inline under the PlayerController, so selecting the Player
    /// in the scene is enough to tweak speeds, forces and grab feel live during Play Mode.
    /// </summary>
    [CustomEditor(typeof(PlayerController))]
    public sealed class PlayerControllerEditor : UnityEditor.Editor
    {
        UnityEditor.Editor configEditor;
        static bool showConfig = true;

        void OnDisable()
        {
            if (configEditor != null)
                DestroyImmediate(configEditor);
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var config = serializedObject.FindProperty("config").objectReferenceValue as PlayerConfig;
            if (config == null)
            {
                EditorGUILayout.HelpBox("Assign a PlayerConfig asset to tune movement here.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(6f);
            showConfig = EditorGUILayout.InspectorTitlebar(showConfig, config);
            if (!showConfig)
                return;

            EditorGUILayout.HelpBox(
                "Live tuning: edits apply instantly in Play Mode and are saved to the asset (they persist after you stop).",
                MessageType.None);

            CreateCachedEditor(config, typeof(PlayerConfigEditor), ref configEditor);
            EditorGUI.indentLevel++;
            configEditor.OnInspectorGUI();
            EditorGUI.indentLevel--;
        }
    }
}
