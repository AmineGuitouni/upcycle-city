using UnityEditor;
using UnityEngine;
using UpcycleCity.Ecosystem;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// EcosystemBalance inspector: difficulty preset buttons (undoable) above the sliders.
    /// </summary>
    [CustomEditor(typeof(EcosystemBalance))]
    public sealed class EcosystemBalanceEditor : UnityEditor.Editor
    {
        static readonly string[] PresetLabels = { "Relaxed", "Normal (Demo)", "Challenging" };

        public override void OnInspectorGUI()
        {
            var balance = (EcosystemBalance)target;
            EditorGUILayout.LabelField("Difficulty presets (overwrite all values · Ctrl+Z to undo)", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < PresetLabels.Length; i++)
            {
                if (GUILayout.Button(PresetLabels[i], EditorStyles.miniButton))
                {
                    Undo.RecordObject(balance, $"Apply {PresetLabels[i]} Ecosystem Balance");
                    balance.ApplyPreset((EcosystemDifficulty)i);
                    EditorUtility.SetDirty(balance);
                }
            }
            EditorGUILayout.EndHorizontal();
            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Live: changes apply to the running city now and are saved to the asset.", MessageType.None);
            EditorGUILayout.Space(4f);
            DrawDefaultInspector();
        }
    }

    /// <summary>
    /// Shows the city's EcosystemBalance inline under the CityEcosystemManager, so selecting the manager
    /// in the scene is enough to tune difficulty and rates (live in Play Mode).
    /// </summary>
    [CustomEditor(typeof(CityEcosystemManager))]
    public sealed class CityEcosystemManagerEditor : UnityEditor.Editor
    {
        UnityEditor.Editor balanceEditor;
        static bool showBalance = true;

        void OnDisable()
        {
            if (balanceEditor != null)
                DestroyImmediate(balanceEditor);
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var balance = serializedObject.FindProperty("balance").objectReferenceValue as EcosystemBalance;
            if (balance == null)
            {
                EditorGUILayout.HelpBox("Assign an EcosystemBalance asset to tune the city's difficulty and rates in one place. " +
                                        "Without one, each pollution system uses its own Inspector values.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(6f);
            showBalance = EditorGUILayout.InspectorTitlebar(showBalance, balance);
            if (!showBalance)
                return;
            CreateCachedEditor(balance, typeof(EcosystemBalanceEditor), ref balanceEditor);
            EditorGUI.indentLevel++;
            balanceEditor.OnInspectorGUI();
            EditorGUI.indentLevel--;
        }
    }
}
