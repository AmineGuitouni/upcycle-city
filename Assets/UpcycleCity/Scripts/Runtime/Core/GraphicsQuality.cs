using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UpcycleCity.Core
{
    /// <summary>The four graphics levels the player can pick (Settings on the Main Menu and in the pause menu).</summary>
    public enum GraphicsLevel
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
    }

    /// <summary>Frame-rate limit. <see cref="Auto"/> follows the graphics level (30 on Very Low / Low phones, 60 otherwise).</summary>
    public enum FrameRateCap
    {
        Auto = 0,
        Fps30 = 30,
        Fps60 = 60,
    }

    /// <summary>
    /// Game-side knobs of a graphics level, read by the systems that cost the most (crowd animation, draw distance,
    /// post-processing, the pollution shader's grime detail). Render scale, shadows, MSAA, HDR, LOD bias, texture
    /// mips and anisotropic filtering live in the Unity quality level and its URP asset
    /// (<b>Upcycle City → Graphics → Setup Quality Levels</b>).
    /// </summary>
    public readonly struct GraphicsProfile
    {
        /// <summary>Most crowd NPCs animated at once (the rest show their posed mesh).</summary>
        public readonly int CrowdAnimatedMax;
        /// <summary>Multiplier on how far away crowd NPCs are still animated.</summary>
        public readonly float CrowdAnimateDistanceScale;
        /// <summary>Small things (litter, pickups, interactables, citizens, vehicles) further than this from the camera are
        /// culled, in metres (0 = camera far plane). Buildings and the street always draw.</summary>
        public readonly float DrawDistance;
        /// <summary>Post-processing (colour grade, smog grade, vignette, bloom) on game cameras.</summary>
        public readonly bool PostProcessing;
        /// <summary>Bloom (the most expensive post effect).</summary>
        public readonly bool Bloom;
        /// <summary>Skips the per-pixel noise grime blotches and wall rain streaks of the pollution shader.</summary>
        public readonly bool LowShaderDetail;
        /// <summary>Frame-rate target on phones when the cap is <see cref="FrameRateCap.Auto"/>.</summary>
        public readonly int MobileFrameRate;

        public GraphicsProfile(int crowdAnimatedMax, float crowdAnimateDistanceScale, float drawDistance,
            bool postProcessing, bool bloom, bool lowShaderDetail, int mobileFrameRate)
        {
            CrowdAnimatedMax = crowdAnimatedMax;
            CrowdAnimateDistanceScale = crowdAnimateDistanceScale;
            DrawDistance = drawDistance;
            PostProcessing = postProcessing;
            Bloom = bloom;
            LowShaderDetail = lowShaderDetail;
            MobileFrameRate = mobileFrameRate;
        }
    }

    /// <summary>
    /// The player's graphics setting: one of four levels (Very Low / Low / Medium / High), applied instantly and
    /// saved in PlayerPrefs. First run picks a default from the device (phones: Very Low / Low / Medium by memory
    /// and GPU, desktop: High). Applying a level switches the Unity quality level of the same name (its URP asset
    /// carries render scale, shadows, MSAA, HDR...), sets the frame-rate target, and updates the game-side
    /// <see cref="Current"/> profile that systems read (crowd LOD, draw distance, post-processing, shader detail).
    /// Local-only presentation: never networked, every player picks their own.
    /// </summary>
    public static class GraphicsQuality
    {
        public const string LevelPrefsKey = "UpcycleCity.Graphics.Level";
        public const string FrameRatePrefsKey = "UpcycleCity.Graphics.FrameRateCap";
        public const int LevelCount = 4;

        /// <summary>Display names, also the names of the Unity quality levels.</summary>
        public static readonly string[] LevelNames = { "Very Low", "Low", "Medium", "High" };

        static readonly GraphicsProfile[] Profiles =
        {
            //                   crowd anim, anim dist, draw dist, post,  bloom, low detail, phone fps
            new GraphicsProfile(10, 0.35f, 70f, false, false, true, 30),  // Very Low
            new GraphicsProfile(24, 0.55f, 110f, true, false, false, 30), // Low
            new GraphicsProfile(56, 0.8f, 0f, true, true, false, 60),     // Medium
            new GraphicsProfile(int.MaxValue, 1f, 0f, true, true, false, 60), // High
        };

        static readonly int LowDetailId = Shader.PropertyToID("_PollutionLowDetail");

        static bool initialised;
        static GraphicsLevel level = GraphicsLevel.High;
        static FrameRateCap frameRateCap = FrameRateCap.Auto;
        static int version;

        // Cameras whose post-processing we switched off, so it comes back when the level goes up again
        static readonly HashSet<Camera> PostForcedOff = new HashSet<Camera>();
        static readonly Dictionary<Camera, int> CullVersion = new Dictionary<Camera, int>();
        static readonly float[] CullDistances = new float[32];

        // Layers the draw distance applies to (see GameLayers)
        static readonly int[] SmallThingLayers =
        {
            GameLayers.Interactable, GameLayers.Grabbable, GameLayers.Pickup, GameLayers.Citizen, GameLayers.Vehicle,
        };
        static Volume noBloomVolume;

        /// <summary>Raised after a level or frame-rate cap is applied.</summary>
        public static event Action<GraphicsLevel> Changed;

        /// <summary>The level in use.</summary>
        public static GraphicsLevel Level
        {
            get { EnsureInitialised(); return level; }
        }

        /// <summary>The frame-rate cap in use.</summary>
        public static FrameRateCap FrameRateCap
        {
            get { EnsureInitialised(); return frameRateCap; }
        }

        /// <summary>Game-side settings of the current level.</summary>
        public static GraphicsProfile Current
        {
            get { EnsureInitialised(); return Profiles[(int)level]; }
        }

        /// <summary>Game-side settings of any level.</summary>
        public static GraphicsProfile ProfileOf(GraphicsLevel l) => Profiles[Mathf.Clamp((int)l, 0, LevelCount - 1)];

        public static string NameOf(GraphicsLevel l) => LevelNames[Mathf.Clamp((int)l, 0, LevelCount - 1)];

        /// <summary>Frame rate the current level and cap aim for: 30 on Very Low / Low phones, 60 otherwise (unless capped).</summary>
        public static int TargetFrameRate
        {
            get
            {
                EnsureInitialised();
                if (frameRateCap != FrameRateCap.Auto)
                    return (int)frameRateCap;
                return Application.isMobilePlatform ? Profiles[(int)level].MobileFrameRate : 60;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            initialised = false;
            Changed = null;
            PostForcedOff.Clear();
            CullVersion.Clear();
            noBloomVolume = null;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap() => EnsureInitialised();

        static void EnsureInitialised()
        {
            if (initialised)
                return;
            initialised = true;
            level = HasSavedLevel ? (GraphicsLevel)Mathf.Clamp(PlayerPrefs.GetInt(LevelPrefsKey), 0, LevelCount - 1) : DetectDefaultLevel();
            int cap = PlayerPrefs.GetInt(FrameRatePrefsKey, 0);
            frameRateCap = cap == 30 ? FrameRateCap.Fps30 : cap == 60 ? FrameRateCap.Fps60 : FrameRateCap.Auto;
            if (Application.isPlaying)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
            ApplyInternal(raiseEvent: false);
        }

        /// <summary>True once the player (or a previous run) has saved a level.</summary>
        public static bool HasSavedLevel => PlayerPrefs.HasKey(LevelPrefsKey);

        /// <summary>
        /// First-run default: desktop / Editor → High; phones → Very Low (≤ 3 GB RAM or an old GPU),
        /// Low (≤ 6 GB) or Medium.
        /// </summary>
        public static GraphicsLevel DetectDefaultLevel()
        {
            if (!Application.isMobilePlatform)
                return GraphicsLevel.High;
            return DefaultLevelForDevice(SystemInfo.systemMemorySize, SystemInfo.graphicsMemorySize, SystemInfo.graphicsShaderLevel);
        }

        /// <summary>Phone default from device memory (MB), GPU memory (MB) and shader model (e.g. 45, 50).</summary>
        public static GraphicsLevel DefaultLevelForDevice(int systemMemoryMb, int graphicsMemoryMb, int shaderLevel)
        {
            if (systemMemoryMb > 0 && systemMemoryMb <= 3200 || shaderLevel > 0 && shaderLevel < 45)
                return GraphicsLevel.VeryLow;
            if (systemMemoryMb > 0 && systemMemoryMb <= 6200 || graphicsMemoryMb > 0 && graphicsMemoryMb < 1024)
                return GraphicsLevel.Low;
            return GraphicsLevel.Medium;
        }

        /// <summary>Applies and saves a level.</summary>
        public static void SetLevel(GraphicsLevel newLevel, bool save = true)
        {
            EnsureInitialised();
            level = (GraphicsLevel)Mathf.Clamp((int)newLevel, 0, LevelCount - 1);
            if (save)
            {
                PlayerPrefs.SetInt(LevelPrefsKey, (int)level);
                PlayerPrefs.Save();
            }
            ApplyInternal(raiseEvent: true);
        }

        /// <summary>Applies and saves a frame-rate cap.</summary>
        public static void SetFrameRateCap(FrameRateCap cap, bool save = true)
        {
            EnsureInitialised();
            frameRateCap = cap;
            if (save)
            {
                PlayerPrefs.SetInt(FrameRatePrefsKey, (int)cap);
                PlayerPrefs.Save();
            }
            ApplyInternal(raiseEvent: true);
        }

        /// <summary>Re-applies the current level (e.g. after the quality levels were rebuilt).</summary>
        public static void Apply() => SetLevel(Level, save: false);

        /// <summary>Forgets the saved level and cap (tests / tooling); the next read picks the device default.</summary>
        public static void ClearSaved()
        {
            PlayerPrefs.DeleteKey(LevelPrefsKey);
            PlayerPrefs.DeleteKey(FrameRatePrefsKey);
            initialised = false;
        }

        /// <summary>Index of the Unity quality level for a graphics level: matched by name, else clamped by order.</summary>
        public static int QualityIndexOf(GraphicsLevel l)
        {
            string[] names = QualitySettings.names;
            string wanted = NameOf(l);
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return Mathf.Clamp((int)l, 0, Mathf.Max(0, names.Length - 1));
        }

        static void ApplyInternal(bool raiseEvent)
        {
            int index = QualityIndexOf(level);
            if (QualitySettings.GetQualityLevel() != index)
                QualitySettings.SetQualityLevel(index, applyExpensiveChanges: true);

            // vSync stays off in every quality level (phones ignore it anyway); the target paces frames everywhere
            Application.targetFrameRate = TargetFrameRate;

            GraphicsProfile profile = Profiles[(int)level];
            Shader.SetGlobalFloat(LowDetailId, profile.LowShaderDetail ? 1f : 0f);
            if (Application.isPlaying)
                UpdateBloomOverride(!profile.Bloom);

            version++;
            if (raiseEvent)
                Changed?.Invoke(level);
        }

        // ------------------------------------------------------------ bloom off (Low)

        static void UpdateBloomOverride(bool disableBloom)
        {
            if (!disableBloom)
            {
                if (noBloomVolume != null)
                    noBloomVolume.enabled = false;
                return;
            }
            if (noBloomVolume == null)
            {
                var go = new GameObject("GraphicsQuality_NoBloom") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                noBloomVolume = go.AddComponent<Volume>();
                noBloomVolume.isGlobal = true;
                noBloomVolume.priority = 10000f;
                VolumeProfile volumeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                volumeProfile.hideFlags = HideFlags.DontSave;
                Bloom bloom = volumeProfile.Add<Bloom>(true);
                bloom.intensity.Override(0f);
                noBloomVolume.sharedProfile = volumeProfile;
            }
            noBloomVolume.enabled = true;
        }

        // ------------------------------------------------------------ per camera (post-processing, draw distance)

        static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game)
                return;
            if (!camera.TryGetComponent(out UniversalAdditionalCameraData data))
                return;

            GraphicsProfile profile = Profiles[(int)level];

            // Post-processing: switched off on Very Low, restored on the cameras we switched off
            if (!profile.PostProcessing)
            {
                if (data.renderPostProcessing)
                {
                    data.renderPostProcessing = false;
                    PostForcedOff.Add(camera);
                }
            }
            else if (PostForcedOff.Count > 0 && PostForcedOff.Remove(camera))
            {
                data.renderPostProcessing = true;
            }

            // Draw distance (base cameras only; the first-person overlay keeps its own)
            if (data.renderType != CameraRenderType.Base)
                return;
            if (CullVersion.TryGetValue(camera, out int applied) && applied == version)
                return;
            CullVersion[camera] = version;
            float distance = profile.DrawDistance > 0f && profile.DrawDistance < camera.farClipPlane ? profile.DrawDistance : 0f;
            Array.Clear(CullDistances, 0, CullDistances.Length);
            for (int i = 0; i < SmallThingLayers.Length; i++)
                CullDistances[SmallThingLayers[i]] = distance;
            if (distance > 0f)
                camera.layerCullSpherical = true;
            camera.layerCullDistances = CullDistances;
        }
    }
}
