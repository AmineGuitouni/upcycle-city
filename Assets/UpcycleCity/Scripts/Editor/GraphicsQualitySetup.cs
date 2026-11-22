using UnityEditor;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// <b>Upcycle City → Graphics → Setup Quality Levels</b>: makes exactly four Unity quality levels named
    /// Very Low / Low / Medium / High (the names <see cref="GraphicsQuality"/> looks up), each with its own URP asset in
    /// <c>Assets/Settings/Quality/</c> (copied from Mobile_RPAsset, or PC_RPAsset for High, then tuned). Re-runnable:
    /// existing assets are re-tuned in place, so their GUIDs stay. Phones default to Low, desktop to High
    /// (the runtime then picks per device on first run).
    /// </summary>
    public static class GraphicsQualitySetup
    {
        const string Folder = "Assets/Settings/Quality";
        const string MobileSource = "Assets/Settings/Mobile_RPAsset.asset";
        const string DesktopSource = "Assets/Settings/PC_RPAsset.asset";

        /// <summary>Everything one level sets, in the Unity quality level and its URP asset.</summary>
        struct Level
        {
            public string Name, Asset, Source;
            // URP asset
            public float RenderScale;
            public int Msaa;
            public bool Hdr;
            public bool MainShadows;
            public int ShadowResolution;
            public float ShadowDistance;
            public int Cascades;
            public bool SoftShadows;
            public int SoftShadowQuality; // 1 low, 2 medium, 3 high
            public int AdditionalLightsMode; // 0 off, 1 per pixel, 2 per vertex
            public int AdditionalLightsLimit;
            public bool AdditionalLightShadows;
            public bool DepthTexture, OpaqueTexture;
            public bool LodCrossFade;
            public int LutSize;
            // Quality level
            public int PixelLights;
            public int TextureMipLimit;
            public int Anisotropic; // 0 off, 1 per texture, 2 forced on
            public bool SoftParticles;
            public float LodBias;
            public int SkinWeights; // 1, 2, 4
            public int ParticleRaycastBudget;
        }

        static readonly Level[] Levels =
        {
            new Level
            {
                Name = "Very Low", Asset = "URP_VeryLow.asset", Source = MobileSource,
                RenderScale = 0.6f, Msaa = 1, Hdr = false,
                MainShadows = false, ShadowResolution = 256, ShadowDistance = 0f, Cascades = 1, SoftShadows = false, SoftShadowQuality = 1,
                AdditionalLightsMode = 2, AdditionalLightsLimit = 2, AdditionalLightShadows = false,
                DepthTexture = false, OpaqueTexture = false, LodCrossFade = false, LutSize = 16,
                PixelLights = 0, TextureMipLimit = 1, Anisotropic = 0, SoftParticles = false, LodBias = 0.5f, SkinWeights = 2, ParticleRaycastBudget = 16,
            },
            new Level
            {
                Name = "Low", Asset = "URP_Low.asset", Source = MobileSource,
                RenderScale = 0.75f, Msaa = 1, Hdr = false,
                MainShadows = true, ShadowResolution = 512, ShadowDistance = 25f, Cascades = 1, SoftShadows = false, SoftShadowQuality = 1,
                AdditionalLightsMode = 2, AdditionalLightsLimit = 4, AdditionalLightShadows = false,
                DepthTexture = false, OpaqueTexture = false, LodCrossFade = false, LutSize = 16,
                PixelLights = 1, TextureMipLimit = 0, Anisotropic = 0, SoftParticles = false, LodBias = 0.75f, SkinWeights = 2, ParticleRaycastBudget = 64,
            },
            new Level
            {
                Name = "Medium", Asset = "URP_Medium.asset", Source = MobileSource,
                RenderScale = 0.9f, Msaa = 2, Hdr = true,
                MainShadows = true, ShadowResolution = 1024, ShadowDistance = 50f, Cascades = 2, SoftShadows = true, SoftShadowQuality = 2,
                AdditionalLightsMode = 1, AdditionalLightsLimit = 4, AdditionalLightShadows = false,
                DepthTexture = false, OpaqueTexture = false, LodCrossFade = true, LutSize = 32,
                PixelLights = 2, TextureMipLimit = 0, Anisotropic = 1, SoftParticles = true, LodBias = 1.2f, SkinWeights = 2, ParticleRaycastBudget = 256,
            },
            new Level
            {
                Name = "High", Asset = "URP_High.asset", Source = DesktopSource,
                RenderScale = 1f, Msaa = 4, Hdr = true,
                MainShadows = true, ShadowResolution = 2048, ShadowDistance = 150f, Cascades = 4, SoftShadows = true, SoftShadowQuality = 3,
                AdditionalLightsMode = 1, AdditionalLightsLimit = 8, AdditionalLightShadows = true,
                DepthTexture = true, OpaqueTexture = true, LodCrossFade = true, LutSize = 32,
                PixelLights = 4, TextureMipLimit = 0, Anisotropic = 2, SoftParticles = true, LodBias = 2f, SkinWeights = 4, ParticleRaycastBudget = 1024,
            },
        };

        [MenuItem("Upcycle City/Graphics/Setup Quality Levels")]
        public static void Run()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets/Settings", "Quality");

            var assets = new Object[Levels.Length];
            for (int i = 0; i < Levels.Length; i++)
                assets[i] = CreateOrTunePipelineAsset(Levels[i]);
            AssetDatabase.SaveAssets();

            ConfigureQualitySettings(assets);
            GraphicsQuality.Apply();
            Debug.Log($"[GraphicsQualitySetup] {Levels.Length} quality levels ready: {string.Join(", ", QualitySettings.names)}. Current: {GraphicsQuality.NameOf(GraphicsQuality.Level)}.");
        }

        static Object CreateOrTunePipelineAsset(Level level)
        {
            string path = Folder + "/" + level.Asset;
            if (AssetDatabase.LoadMainAssetAtPath(path) == null && !AssetDatabase.CopyAsset(level.Source, path))
            {
                Debug.LogError($"[GraphicsQualitySetup] Could not copy {level.Source} to {path}.");
                return null;
            }
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            var so = new SerializedObject(asset);
            SetFloat(so, "m_RenderScale", level.RenderScale);
            SetInt(so, "m_MSAA", level.Msaa);
            SetBool(so, "m_SupportsHDR", level.Hdr);
            SetBool(so, "m_MainLightShadowsSupported", level.MainShadows);
            SetInt(so, "m_MainLightShadowmapResolution", level.ShadowResolution);
            SetFloat(so, "m_ShadowDistance", level.MainShadows ? level.ShadowDistance : 0f);
            SetInt(so, "m_ShadowCascadeCount", level.Cascades);
            SetBool(so, "m_SoftShadowsSupported", level.SoftShadows);
            SetInt(so, "m_SoftShadowQuality", level.SoftShadowQuality);
            SetInt(so, "m_AdditionalLightsRenderingMode", level.AdditionalLightsMode);
            SetInt(so, "m_AdditionalLightsPerObjectLimit", level.AdditionalLightsLimit);
            SetBool(so, "m_AdditionalLightShadowsSupported", level.AdditionalLightShadows);
            SetBool(so, "m_RequireDepthTexture", level.DepthTexture);
            SetBool(so, "m_RequireOpaqueTexture", level.OpaqueTexture);
            SetBool(so, "m_EnableLODCrossFade", level.LodCrossFade);
            SetInt(so, "m_ColorGradingLutSize", level.LutSize);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        static void ConfigureQualitySettings(Object[] assets)
        {
            Object[] found = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (found == null || found.Length == 0)
            {
                Debug.LogError("[GraphicsQualitySetup] ProjectSettings/QualitySettings.asset not found.");
                return;
            }
            var so = new SerializedObject(found[0]);
            SerializedProperty list = so.FindProperty("m_QualitySettings");
            list.arraySize = Levels.Length;
            for (int i = 0; i < Levels.Length; i++)
            {
                Level level = Levels[i];
                SerializedProperty q = list.GetArrayElementAtIndex(i);
                q.FindPropertyRelative("name").stringValue = level.Name;
                q.FindPropertyRelative("pixelLightCount").intValue = level.PixelLights;
                q.FindPropertyRelative("shadows").intValue = level.MainShadows ? (level.SoftShadows ? 2 : 1) : 0;
                q.FindPropertyRelative("shadowDistance").floatValue = level.ShadowDistance;
                q.FindPropertyRelative("shadowCascades").intValue = level.Cascades >= 4 ? 4 : level.Cascades >= 2 ? 2 : 1;
                q.FindPropertyRelative("skinWeights").intValue = level.SkinWeights;
                q.FindPropertyRelative("globalTextureMipmapLimit").intValue = level.TextureMipLimit;
                q.FindPropertyRelative("anisotropicTextures").intValue = level.Anisotropic;
                q.FindPropertyRelative("antiAliasing").intValue = 0; // MSAA comes from the URP asset
                q.FindPropertyRelative("softParticles").boolValue = level.SoftParticles;
                q.FindPropertyRelative("realtimeReflectionProbes").boolValue = false;
                q.FindPropertyRelative("vSyncCount").intValue = 0; // GraphicsQuality paces frames with targetFrameRate
                q.FindPropertyRelative("lodBias").floatValue = level.LodBias;
                q.FindPropertyRelative("maximumLODLevel").intValue = 0;
                q.FindPropertyRelative("enableLODCrossFade").boolValue = level.LodCrossFade;
                q.FindPropertyRelative("particleRaycastBudget").intValue = level.ParticleRaycastBudget;
                q.FindPropertyRelative("customRenderPipeline").objectReferenceValue = assets[i];
                SerializedProperty excluded = q.FindPropertyRelative("excludedTargetPlatforms");
                if (excluded != null)
                    excluded.arraySize = 0; // every level available everywhere: the player picks
            }

            // Default level per platform before the runtime picks per device: phones Low, everything else High
            SerializedProperty perPlatform = so.FindProperty("m_PerPlatformDefaultQuality");
            if (perPlatform != null && perPlatform.isArray)
            {
                for (int i = 0; i < perPlatform.arraySize; i++)
                {
                    SerializedProperty pair = perPlatform.GetArrayElementAtIndex(i);
                    SerializedProperty key = pair.FindPropertyRelative("first");
                    SerializedProperty value = pair.FindPropertyRelative("second");
                    if (key == null || value == null)
                        continue;
                    string platform = key.stringValue;
                    bool phone = platform == "Android" || platform == "iPhone" || platform == "WebGL" || platform == "tvOS"
                                 || platform.StartsWith("Nintendo");
                    value.intValue = phone ? 1 : 3;
                }
            }
            so.FindProperty("m_CurrentQuality").intValue = (int)GraphicsQuality.Level; // level i is quality index i
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        static void SetFloat(SerializedObject so, string name, float value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.floatValue = value;
            else Debug.LogWarning($"[GraphicsQualitySetup] URP asset has no {name}.");
        }

        static void SetInt(SerializedObject so, string name, int value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.intValue = value;
            else Debug.LogWarning($"[GraphicsQualitySetup] URP asset has no {name}.");
        }

        static void SetBool(SerializedObject so, string name, bool value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.boolValue = value;
            else Debug.LogWarning($"[GraphicsQualitySetup] URP asset has no {name}.");
        }
    }
}
