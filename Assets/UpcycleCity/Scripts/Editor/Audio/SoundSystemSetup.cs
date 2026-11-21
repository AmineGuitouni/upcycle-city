using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Audio;

namespace UpcycleCity.Editor.Audio
{
    /// <summary>
    /// Idempotent Editor builder that creates and wires all <see cref="SoundCue"/> assets and the
    /// default <c>Resources/UpcycleCitySoundLibrary.asset</c> from the clips under <c>Assets/UpcycleCity/Audio/</c>.
    /// </summary>
    public static class SoundSystemSetup
    {
        const string AudioRoot = "Assets/UpcycleCity/Audio";
        const string CueSettingsFolder = "Assets/UpcycleCity/Settings/Audio";
        const string ResourcesFolder = "Assets/UpcycleCity/Resources";
        const string LibraryAssetPath = ResourcesFolder + "/" + SoundLibrary.DefaultResourcePath + ".asset";

        [MenuItem("Upcycle City/Audio/Build Sound Library & Cues", priority = 10)]
        public static void BuildSoundLibraryAndCues()
        {
            EnsureFolder(CueSettingsFolder);
            EnsureFolder(ResourcesFolder);

            // Load clips
            AudioClip[] walkSteps = LoadClipsInFolder(AudioRoot + "/SFX/Footsteps", "footstep_walk_");
            AudioClip[] runSteps = LoadClipsInFolder(AudioRoot + "/SFX/Footsteps", "footstep_run_");
            AudioClip hardStep = LoadClip(AudioRoot + "/SFX/Footsteps/hard_footstep.wav");

            AudioClip glass = LoadClip(AudioRoot + "/SFX/Materials/glass.wav");
            AudioClip plastic = LoadClip(AudioRoot + "/SFX/Materials/plastic1.wav");
            AudioClip metal1 = LoadClip(AudioRoot + "/SFX/Materials/metal1.wav");
            AudioClip metal2 = LoadClip(AudioRoot + "/SFX/Materials/metal2.wav");
            AudioClip metal3 = LoadClip(AudioRoot + "/SFX/Materials/metal3.wav");
            AudioClip paperCollect = LoadClip(AudioRoot + "/SFX/Materials/paper_collect.wav");
            AudioClip[] paperRustles = LoadClipsInFolder(AudioRoot + "/SFX/Materials", "paper_rustle_");

            AudioClip pick = LoadClip(AudioRoot + "/SFX/Actions/pick.wav");
            AudioClip metalClick = LoadClip(AudioRoot + "/SFX/Actions/metalClick.ogg");
            AudioClip hitting = LoadClip(AudioRoot + "/SFX/Actions/hitting.mp3");
            AudioClip recycle = LoadClip(AudioRoot + "/SFX/Actions/recycle1.wav");
            AudioClip whistle = LoadClip(AudioRoot + "/SFX/Actions/attention_whistle.wav");

            AudioClip getMoney = LoadClip(AudioRoot + "/SFX/Economy/seller_getting_money.wav");
            AudioClip spendMoney = LoadClip(AudioRoot + "/SFX/Economy/spending_money.mp3");

            AudioClip[] musicTracks = Compact(
                LoadClip(AudioRoot + "/Music/background1.wav"),
                LoadClip(AudioRoot + "/Music/background2.wav"),
                LoadClip(AudioRoot + "/Music/background3.wav"));
            AudioClip cityAmbience = LoadClip(AudioRoot + "/Ambience/city_ambience.wav");

            AudioClip[] metalShort = Compact(metal1, metal3);
            AudioClip[] metalAll = Compact(metal1, metal3, metal2);
            AudioClip[] paperGrabClips = Append(paperRustles, paperCollect);
            AudioClip[] landClips = Append(Compact(hardStep), walkSteps.Length >= 2 ? new[] { walkSteps[0], walkSteps[1] } : walkSteps);

            var bindings = new List<SoundLibrary.CueBinding>();

            void Bind(
                SoundId id,
                string assetName,
                AudioClip[] clips,
                SoundCategory cat,
                Vector2 vol,
                Vector2 pitch,
                bool spatial = true,
                float blend = 0.85f,
                float minDist = 2f,
                float maxDist = 26f,
                float cooldown = 0.05f,
                int maxConcurrent = 4,
                int priority = 128,
                float cutoff = 0f)
            {
                SoundCue cue = CreateOrUpdateCue(
                    assetName,
                    clips,
                    cat,
                    vol,
                    pitch,
                    spatial,
                    blend,
                    minDist,
                    maxDist,
                    cooldown,
                    maxConcurrent,
                    priority,
                    cutoff);
                bindings.Add(new SoundLibrary.CueBinding { id = id, cue = cue });
            }

            // 1. Locomotion (rich clip + pitch variation!)
            Bind(SoundId.FootstepWalk, "SFX_FootstepWalk", walkSteps, SoundCategory.SFX,
                new Vector2(0.32f, 0.42f), new Vector2(0.90f, 1.12f),
                spatial: false, cooldown: 0.14f, maxConcurrent: 3, priority: 140);

            Bind(SoundId.FootstepRun, "SFX_FootstepRun", runSteps, SoundCategory.SFX,
                new Vector2(0.42f, 0.54f), new Vector2(0.92f, 1.14f),
                spatial: false, cooldown: 0.11f, maxConcurrent: 3, priority: 135);

            Bind(SoundId.FootstepCrouch, "SFX_FootstepCrouch", walkSteps, SoundCategory.SFX,
                new Vector2(0.16f, 0.23f), new Vector2(0.84f, 0.98f),
                spatial: false, cooldown: 0.18f, maxConcurrent: 2, priority: 160);

            Bind(SoundId.Jump, "SFX_Jump", Compact(pick, hardStep), SoundCategory.SFX,
                new Vector2(0.34f, 0.44f), new Vector2(1.08f, 1.24f),
                spatial: false, cooldown: 0.12f, maxConcurrent: 2, priority: 100);

            Bind(SoundId.Land, "SFX_Land", landClips, SoundCategory.SFX,
                new Vector2(0.48f, 0.64f), new Vector2(0.82f, 1.00f),
                spatial: false, cooldown: 0.12f, maxConcurrent: 2, priority: 96);

            // 2. Grabbing & Throwing (material-aware + random pitch)
            Bind(SoundId.GrabGeneric, "SFX_GrabGeneric", Compact(pick), SoundCategory.SFX,
                new Vector2(0.48f, 0.60f), new Vector2(0.92f, 1.14f),
                spatial: true, blend: 0.35f, cooldown: 0.05f, maxConcurrent: 3, priority: 90);

            Bind(SoundId.GrabPaper, "SFX_GrabPaper", paperGrabClips, SoundCategory.SFX,
                new Vector2(0.55f, 0.68f), new Vector2(0.92f, 1.12f),
                spatial: true, blend: 0.40f, cooldown: 0.06f, maxConcurrent: 3, priority: 85, cutoff: 0.75f);

            Bind(SoundId.GrabPlastic, "SFX_GrabPlastic", Compact(plastic), SoundCategory.SFX,
                new Vector2(0.58f, 0.72f), new Vector2(0.88f, 1.16f),
                spatial: true, blend: 0.40f, cooldown: 0.06f, maxConcurrent: 3, priority: 85);

            Bind(SoundId.GrabGlass, "SFX_GrabGlass", Compact(glass), SoundCategory.SFX,
                new Vector2(0.54f, 0.68f), new Vector2(0.92f, 1.14f),
                spatial: true, blend: 0.40f, cooldown: 0.06f, maxConcurrent: 3, priority: 85);

            Bind(SoundId.GrabMetal, "SFX_GrabMetal", metalShort, SoundCategory.SFX,
                new Vector2(0.56f, 0.70f), new Vector2(0.90f, 1.15f),
                spatial: true, blend: 0.40f, cooldown: 0.06f, maxConcurrent: 3, priority: 85);

            Bind(SoundId.GrabBulky, "SFX_GrabBulky", Compact(metal2, metal1, hitting), SoundCategory.SFX,
                new Vector2(0.50f, 0.64f), new Vector2(0.78f, 0.94f),
                spatial: true, blend: 0.45f, cooldown: 0.08f, maxConcurrent: 2, priority: 80, cutoff: 0.85f);

            Bind(SoundId.LitterPickerSnap, "SFX_LitterPickerSnap", Compact(metalClick), SoundCategory.SFX,
                new Vector2(0.45f, 0.56f), new Vector2(0.94f, 1.12f),
                spatial: false, cooldown: 0.05f, maxConcurrent: 2, priority: 85);

            Bind(SoundId.Throw, "SFX_Throw", Compact(pick), SoundCategory.SFX,
                new Vector2(0.48f, 0.60f), new Vector2(0.76f, 0.94f),
                spatial: false, cooldown: 0.06f, maxConcurrent: 3, priority: 90);

            Bind(SoundId.Drop, "SFX_Drop", Compact(pick), SoundCategory.SFX,
                new Vector2(0.32f, 0.42f), new Vector2(0.70f, 0.86f),
                spatial: false, cooldown: 0.06f, maxConcurrent: 3, priority: 100);

            // 3. 3D Physics Material Impacts (strict cooldown & concurrency cap for clean piles)
            Bind(SoundId.ImpactPaper, "SFX_ImpactPaper", paperRustles, SoundCategory.SFX,
                new Vector2(0.42f, 0.56f), new Vector2(0.88f, 1.18f),
                spatial: true, blend: 0.92f, minDist: 1.5f, maxDist: 18f, cooldown: 0.08f, maxConcurrent: 3, priority: 170);

            Bind(SoundId.ImpactPlastic, "SFX_ImpactPlastic", Compact(plastic), SoundCategory.SFX,
                new Vector2(0.48f, 0.64f), new Vector2(0.84f, 1.20f),
                spatial: true, blend: 0.92f, minDist: 1.5f, maxDist: 20f, cooldown: 0.07f, maxConcurrent: 4, priority: 165);

            Bind(SoundId.ImpactGlass, "SFX_ImpactGlass", Compact(glass), SoundCategory.SFX,
                new Vector2(0.48f, 0.64f), new Vector2(0.88f, 1.22f),
                spatial: true, blend: 0.92f, minDist: 1.8f, maxDist: 24f, cooldown: 0.07f, maxConcurrent: 4, priority: 160);

            Bind(SoundId.ImpactMetal, "SFX_ImpactMetal", metalAll, SoundCategory.SFX,
                new Vector2(0.48f, 0.62f), new Vector2(0.88f, 1.18f),
                spatial: true, blend: 0.92f, minDist: 1.8f, maxDist: 25f, cooldown: 0.07f, maxConcurrent: 4, priority: 160, cutoff: 0.9f);

            Bind(SoundId.ImpactBulky, "SFX_ImpactBulky", Compact(hitting, metal2), SoundCategory.SFX,
                new Vector2(0.46f, 0.62f), new Vector2(0.78f, 0.98f),
                spatial: true, blend: 0.92f, minDist: 2.0f, maxDist: 28f, cooldown: 0.10f, maxConcurrent: 3, priority: 150, cutoff: 0.9f);

            // 4. Sorting, Recycling & Economy
            Bind(SoundId.SortCorrect, "SFX_SortCorrect", Compact(recycle), SoundCategory.SFX,
                new Vector2(0.68f, 0.80f), new Vector2(0.95f, 1.08f),
                spatial: true, blend: 0.25f, cooldown: 0.10f, maxConcurrent: 3, priority: 50, cutoff: 2.2f);

            Bind(SoundId.SortWrong, "SFX_SortWrong", Compact(whistle), SoundCategory.SFX,
                new Vector2(0.55f, 0.65f), new Vector2(0.90f, 1.02f),
                spatial: false, cooldown: 0.25f, maxConcurrent: 2, priority: 48);

            Bind(SoundId.SellTrash, "SFX_SellTrash", Compact(getMoney), SoundCategory.SFX,
                new Vector2(0.68f, 0.80f), new Vector2(0.95f, 1.08f),
                spatial: true, blend: 0.20f, cooldown: 0.10f, maxConcurrent: 3, priority: 50);

            Bind(SoundId.SpendCoins, "SFX_SpendCoins", Compact(spendMoney), SoundCategory.UI,
                new Vector2(0.62f, 0.74f), new Vector2(0.95f, 1.06f),
                spatial: false, cooldown: 0.08f, maxConcurrent: 2, priority: 45);

            Bind(SoundId.EarnCoins, "SFX_EarnCoins", Compact(getMoney), SoundCategory.SFX,
                new Vector2(0.60f, 0.72f), new Vector2(1.02f, 1.14f),
                spatial: false, cooldown: 0.10f, maxConcurrent: 3, priority: 55);

            Bind(SoundId.BinService, "SFX_BinService", Compact(recycle), SoundCategory.SFX,
                new Vector2(0.66f, 0.78f), new Vector2(0.92f, 1.04f),
                spatial: true, blend: 0.35f, cooldown: 0.15f, maxConcurrent: 2, priority: 60, cutoff: 2.4f);

            // 5. Tools, Pickups & Interaction
            Bind(SoundId.PickupItem, "SFX_PickupItem", Compact(pick), SoundCategory.SFX,
                new Vector2(0.60f, 0.72f), new Vector2(1.08f, 1.24f),
                spatial: false, cooldown: 0.06f, maxConcurrent: 3, priority: 70);

            Bind(SoundId.ToolBroomSweep, "SFX_ToolBroomSweep", Compact(hitting), SoundCategory.SFX,
                new Vector2(0.50f, 0.64f), new Vector2(0.88f, 1.08f),
                spatial: true, blend: 0.30f, cooldown: 0.10f, maxConcurrent: 2, priority: 75, cutoff: 0.65f);

            Bind(SoundId.ToolWrenchRepair, "SFX_ToolWrenchRepair", Compact(metalClick, metal1, hitting), SoundCategory.SFX,
                new Vector2(0.62f, 0.76f), new Vector2(0.92f, 1.10f),
                spatial: true, blend: 0.35f, cooldown: 0.12f, maxConcurrent: 2, priority: 65, cutoff: 0.85f);

            Bind(SoundId.ToolUseGeneric, "SFX_ToolUseGeneric", Compact(metalClick), SoundCategory.SFX,
                new Vector2(0.45f, 0.56f), new Vector2(0.94f, 1.12f),
                spatial: false, cooldown: 0.08f, maxConcurrent: 2, priority: 90);

            Bind(SoundId.Interact, "SFX_Interact", Compact(metalClick), SoundCategory.SFX,
                new Vector2(0.38f, 0.48f), new Vector2(0.96f, 1.10f),
                spatial: true, blend: 0.30f, cooldown: 0.08f, maxConcurrent: 2, priority: 100);

            Bind(SoundId.CartGrab, "SFX_CartGrab", Compact(metalClick, metal1), SoundCategory.SFX,
                new Vector2(0.50f, 0.62f), new Vector2(0.86f, 1.02f),
                spatial: true, blend: 0.45f, cooldown: 0.12f, maxConcurrent: 2, priority: 85);

            // 6. UI & Alerts
            Bind(SoundId.HotbarSwitch, "SFX_HotbarSwitch", Compact(metalClick), SoundCategory.UI,
                new Vector2(0.24f, 0.32f), new Vector2(1.06f, 1.22f),
                spatial: false, cooldown: 0.04f, maxConcurrent: 2, priority: 110);

            Bind(SoundId.UiClick, "SFX_UiClick", Compact(metalClick), SoundCategory.UI,
                new Vector2(0.28f, 0.36f), new Vector2(1.08f, 1.20f),
                spatial: false, cooldown: 0.04f, maxConcurrent: 2, priority: 105);

            Bind(SoundId.InventoryFull, "SFX_InventoryFull", Compact(whistle), SoundCategory.UI,
                new Vector2(0.36f, 0.44f), new Vector2(0.82f, 0.90f),
                spatial: false, cooldown: 0.30f, maxConcurrent: 1, priority: 70);

            Bind(SoundId.AttentionAlert, "SFX_AttentionAlert", Compact(whistle), SoundCategory.UI,
                new Vector2(0.50f, 0.60f), new Vector2(0.96f, 1.04f),
                spatial: false, cooldown: 0.35f, maxConcurrent: 1, priority: 40);

            // Create or update master SoundLibrary in Resources
            SoundLibrary library = AssetDatabase.LoadAssetAtPath<SoundLibrary>(LibraryAssetPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<SoundLibrary>();
                AssetDatabase.CreateAsset(library, LibraryAssetPath);
            }

            library.Configure(bindings.ToArray(), musicTracks, cityAmbience);
            EditorUtility.SetDirty(library);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[UpcycleCity.Audio] Built SoundLibrary ({bindings.Count} cues, {musicTracks.Length} music tracks, ambience={(cityAmbience != null ? cityAmbience.name : "none")}) at {LibraryAssetPath}");
        }

        [MenuItem("Upcycle City/Audio/Reimport Audio With Optimal Settings", priority = 20)]
        public static void ReimportAllAudio()
        {
            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { AudioRoot });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            Debug.Log($"[UpcycleCity.Audio] Reimported {guids.Length} audio clips with optimal import rules.");
        }

        [MenuItem("Upcycle City/Audio/Select Sound Library", priority = 30)]
        public static void SelectSoundLibrary()
        {
            SoundLibrary lib = AssetDatabase.LoadAssetAtPath<SoundLibrary>(LibraryAssetPath);
            if (lib != null)
            {
                Selection.activeObject = lib;
                EditorGUIUtility.PingObject(lib);
            }
            else
            {
                Debug.LogWarning("[UpcycleCity.Audio] SoundLibrary not built yet. Run 'Upcycle City → Audio → Build Sound Library & Cues'.");
            }
        }

        static SoundCue CreateOrUpdateCue(
            string assetName,
            AudioClip[] clips,
            SoundCategory category,
            Vector2 volumeRange,
            Vector2 pitchRange,
            bool spatialize,
            float spatialBlend,
            float minDist,
            float maxDist,
            float minInterval,
            int maxConcurrent,
            int priority,
            float cutoffDuration)
        {
            string path = $"{CueSettingsFolder}/{assetName}.asset";
            SoundCue cue = AssetDatabase.LoadAssetAtPath<SoundCue>(path);
            if (cue == null)
            {
                cue = ScriptableObject.CreateInstance<SoundCue>();
                AssetDatabase.CreateAsset(cue, path);
            }

            cue.Configure(
                clips,
                category,
                volumeRange,
                pitchRange,
                spatialize,
                spatialBlend,
                minDist,
                maxDist,
                minInterval,
                maxConcurrent,
                priority,
                ClipSelectionMode.ShuffleBag,
                isLoop: false,
                cutoffDuration: cutoffDuration);
            EditorUtility.SetDirty(cue);
            return cue;
        }

        static AudioClip LoadClip(string assetPath) => AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);

        static AudioClip[] LoadClipsInFolder(string folderPath, string prefix)
        {
            var result = new List<AudioClip>();
            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { folderPath });
            System.Array.Sort(guids, (a, b) =>
                string.Compare(AssetDatabase.GUIDToAssetPath(a), AssetDatabase.GUIDToAssetPath(b), System.StringComparison.Ordinal));

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (clip != null)
                        result.Add(clip);
                }
            }
            return result.ToArray();
        }

        static AudioClip[] Compact(params AudioClip[] clips)
        {
            var list = new List<AudioClip>();
            if (clips != null)
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    if (clips[i] != null)
                        list.Add(clips[i]);
                }
            }
            return list.ToArray();
        }

        static AudioClip[] Append(AudioClip[] first, params AudioClip[] extra)
        {
            var list = new List<AudioClip>();
            if (first != null)
            {
                for (int i = 0; i < first.Length; i++)
                {
                    if (first[i] != null)
                        list.Add(first[i]);
                }
            }
            if (extra != null)
            {
                for (int i = 0; i < extra.Length; i++)
                {
                    if (extra[i] != null)
                        list.Add(extra[i]);
                }
            }
            return list.ToArray();
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;

            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
