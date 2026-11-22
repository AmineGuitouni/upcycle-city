using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UpcycleCity.Core;

namespace UpcycleCity.Tests.EditMode
{
    /// <summary>
    /// Graphics quality: four Unity quality levels named Very Low / Low / Medium / High each with its own URP asset,
    /// setting a level switches the quality level and saves it (PlayerPrefs), the next start reads it back,
    /// Very Low is far cheaper than High, and the per-device default is sensible.
    /// </summary>
    public sealed class GraphicsQualityEditModeTests
    {
        int savedQuality;
        bool hadLevel, hadCap;
        int savedLevel, savedCap;

        [SetUp]
        public void SetUp()
        {
            savedQuality = QualitySettings.GetQualityLevel();
            hadLevel = PlayerPrefs.HasKey(GraphicsQuality.LevelPrefsKey);
            hadCap = PlayerPrefs.HasKey(GraphicsQuality.FrameRatePrefsKey);
            savedLevel = PlayerPrefs.GetInt(GraphicsQuality.LevelPrefsKey, 0);
            savedCap = PlayerPrefs.GetInt(GraphicsQuality.FrameRatePrefsKey, 0);
        }

        [TearDown]
        public void TearDown()
        {
            GraphicsQuality.ClearSaved();
            if (hadLevel) PlayerPrefs.SetInt(GraphicsQuality.LevelPrefsKey, savedLevel);
            if (hadCap) PlayerPrefs.SetInt(GraphicsQuality.FrameRatePrefsKey, savedCap);
            PlayerPrefs.Save();
            QualitySettings.SetQualityLevel(savedQuality, true);
        }

        [Test]
        public void FourNamedQualityLevels_EachWithItsOwnUrpAsset()
        {
            Assert.AreEqual(new[] { "Very Low", "Low", "Medium", "High" }, QualitySettings.names);
            var seen = new System.Collections.Generic.HashSet<RenderPipelineAsset>();
            for (int i = 0; i < 4; i++)
            {
                RenderPipelineAsset rp = QualitySettings.GetRenderPipelineAssetAt(i);
                Assert.IsNotNull(rp, $"Quality level {i} has no URP asset.");
                Assert.IsTrue(seen.Add(rp), $"Quality level {i} shares its URP asset.");
                Assert.AreEqual(i, GraphicsQuality.QualityIndexOf((GraphicsLevel)i));
            }
        }

        [Test]
        public void VeryLow_IsMuchCheaperThanHigh()
        {
            var veryLow = (UniversalRenderPipelineAsset)QualitySettings.GetRenderPipelineAssetAt(0);
            var high = (UniversalRenderPipelineAsset)QualitySettings.GetRenderPipelineAssetAt(3);
            Assert.LessOrEqual(veryLow.renderScale, 0.65f);
            Assert.AreEqual(1f, high.renderScale, 1e-3f);
            Assert.IsFalse(veryLow.supportsMainLightShadows);
            Assert.IsTrue(high.supportsMainLightShadows);
            Assert.IsFalse(veryLow.supportsHDR);
            Assert.AreEqual(1, veryLow.msaaSampleCount);
            Assert.AreEqual(4, high.msaaSampleCount);

            GraphicsProfile vl = GraphicsQuality.ProfileOf(GraphicsLevel.VeryLow);
            GraphicsProfile hi = GraphicsQuality.ProfileOf(GraphicsLevel.High);
            Assert.IsFalse(vl.PostProcessing);
            Assert.IsTrue(vl.LowShaderDetail);
            Assert.Less(vl.CrowdAnimatedMax, 16);
            Assert.Greater(vl.DrawDistance, 0f);
            Assert.IsTrue(hi.PostProcessing && hi.Bloom);
            Assert.IsFalse(hi.LowShaderDetail);
        }

        [Test]
        public void SetLevel_AppliesQualityLevelAndPersists()
        {
            GraphicsLevel raised = (GraphicsLevel)(-1);
            System.Action<GraphicsLevel> onChanged = l => raised = l;
            GraphicsQuality.Changed += onChanged;
            try
            {
                GraphicsQuality.SetLevel(GraphicsLevel.VeryLow);
                Assert.AreEqual(GraphicsLevel.VeryLow, GraphicsQuality.Level);
                Assert.AreEqual(GraphicsLevel.VeryLow, raised);
                Assert.AreEqual("Very Low", QualitySettings.names[QualitySettings.GetQualityLevel()]);
                Assert.AreEqual(1f, Shader.GetGlobalFloat("_PollutionLowDetail"));
                Assert.AreEqual((int)GraphicsLevel.VeryLow, PlayerPrefs.GetInt(GraphicsQuality.LevelPrefsKey, -1));

                GraphicsQuality.SetLevel(GraphicsLevel.Medium);
                Assert.AreEqual("Medium", QualitySettings.names[QualitySettings.GetQualityLevel()]);
                Assert.AreEqual(0f, Shader.GetGlobalFloat("_PollutionLowDetail"));

                GraphicsQuality.SetFrameRateCap(FrameRateCap.Fps30);
                Assert.AreEqual(30, GraphicsQuality.TargetFrameRate);
                Assert.AreEqual(30, Application.targetFrameRate);
            }
            finally
            {
                GraphicsQuality.Changed -= onChanged;
            }
        }

        [Test]
        public void SavedLevel_IsReadBackOnNextStart_AndDefaultsWithoutOne()
        {
            GraphicsQuality.ClearSaved();
            PlayerPrefs.SetInt(GraphicsQuality.LevelPrefsKey, (int)GraphicsLevel.Low);
            PlayerPrefs.SetInt(GraphicsQuality.FrameRatePrefsKey, 60);
            Assert.AreEqual(GraphicsLevel.Low, GraphicsQuality.Level, "A saved level is read back on (re)start.");
            Assert.AreEqual(FrameRateCap.Fps60, GraphicsQuality.FrameRateCap);
            Assert.AreEqual("Low", QualitySettings.names[QualitySettings.GetQualityLevel()]);

            GraphicsQuality.ClearSaved();
            Assert.IsFalse(GraphicsQuality.HasSavedLevel);
            Assert.AreEqual(GraphicsQuality.DetectDefaultLevel(), GraphicsQuality.Level);
            Assert.AreEqual(GraphicsLevel.High, GraphicsQuality.DetectDefaultLevel(), "Desktop / Editor defaults to High.");
        }

        [Test]
        public void PhoneDefaults_FollowMemoryAndGpu()
        {
            Assert.AreEqual(GraphicsLevel.VeryLow, GraphicsQuality.DefaultLevelForDevice(2048, 512, 50));
            Assert.AreEqual(GraphicsLevel.VeryLow, GraphicsQuality.DefaultLevelForDevice(8192, 2048, 35));
            Assert.AreEqual(GraphicsLevel.Low, GraphicsQuality.DefaultLevelForDevice(4096, 1024, 50));
            Assert.AreEqual(GraphicsLevel.Medium, GraphicsQuality.DefaultLevelForDevice(8192, 2048, 50));
        }
    }
}
