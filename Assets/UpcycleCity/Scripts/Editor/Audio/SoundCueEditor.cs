using UnityEditor;
using UnityEngine;
using UpcycleCity.Audio;

namespace UpcycleCity.Editor.Audio
{
    /// <summary>
    /// Custom Inspector for <see cref="SoundCue"/> with one-click randomized auditioning right in Edit Mode.
    /// </summary>
    [CustomEditor(typeof(SoundCue))]
    public sealed class SoundCueEditor : UnityEditor.Editor
    {
        AudioSource previewSource;
        float lastPreviewPitch = -1f;
        int lastPreviewClipIndex = -1;

        void OnDisable()
        {
            DestroyPreviewSource();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var cue = (SoundCue)target;
            EditorGUILayout.Space(8f);

            using (new EditorGUI.DisabledScope(!cue.HasValidClip))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview (Randomized)", GUILayout.Height(26f)))
                    PlayPreview(cue);

                if (GUILayout.Button("Stop", GUILayout.Width(64f), GUILayout.Height(26f)))
                    StopPreview();
            }

            if (!cue.HasValidClip)
                EditorGUILayout.HelpBox("Assign at least one AudioClip above to audition this cue.", MessageType.Info);
        }

        void PlayPreview(SoundCue cue)
        {
            AudioClip[] clips = cue.Clips;
            if (clips == null || clips.Length == 0)
                return;

            int idx = Random.Range(0, clips.Length);
            if (clips.Length > 1 && idx == lastPreviewClipIndex)
                idx = (idx + 1) % clips.Length;
            lastPreviewClipIndex = idx;

            AudioClip clip = clips[idx];
            if (clip == null)
                return;

            EnsurePreviewSource();
            float pitch = cue.SamplePitch(lastPreviewPitch);
            float volume = cue.SampleVolume();
            lastPreviewPitch = pitch;

            previewSource.Stop();
            previewSource.clip = clip;
            previewSource.volume = volume;
            previewSource.pitch = pitch;
            previewSource.loop = false;
            previewSource.spatialBlend = 0f;
            previewSource.Play();
        }

        void StopPreview()
        {
            if (previewSource != null)
                previewSource.Stop();
        }

        void EnsurePreviewSource()
        {
            if (previewSource != null)
                return;
            var go = EditorUtility.CreateGameObjectWithHideFlags(
                "SoundCuePreviewer",
                HideFlags.HideAndDontSave,
                typeof(AudioSource));
            previewSource = go.GetComponent<AudioSource>();
            previewSource.playOnAwake = false;
        }

        void DestroyPreviewSource()
        {
            if (previewSource == null)
                return;
            previewSource.Stop();
            DestroyImmediate(previewSource.gameObject);
            previewSource = null;
        }
    }
}
