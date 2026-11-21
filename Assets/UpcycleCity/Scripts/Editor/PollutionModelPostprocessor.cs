using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Imports every model under Assets/UpcycleCity/Art with Pollution Lit materials, so a new pack
    /// joins the pollution system just by being dropped into its folder (see <see cref="ModelImportRules"/>).
    /// </summary>
    sealed class PollutionModelPostprocessor : AssetPostprocessor
    {
        const string ShaderName = "UpcycleCity/Pollution Lit";

        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        static readonly int SensitivityId = Shader.PropertyToID("_PollutionSensitivity");
        static readonly int GrimeId = Shader.PropertyToID("_GrimeAmount");
        static readonly int FoliageId = Shader.PropertyToID("_FoliageAmount");
        static readonly int FacadeWindowsId = Shader.PropertyToID("_FacadeWindows");
        const string FacadeWindowsKeyword = "_FACADE_WINDOWS";

        // Bump this to re-import every model after changing the rules or this postprocessor
        public override uint GetVersion() => 6;

        // After URP's own FBX material preprocessor (-980), so these materials win
        public override int GetPostprocessOrder() => 100;

        void OnPreprocessModel()
        {
            if (!ModelImportRules.Applies(assetPath))
                return;

            ModelImportRule rule = ModelImportRules.For(assetPath);
            var importer = (ModelImporter)assetImporter;
            importer.globalScale = rule.Scale;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.importCameras = false;
            importer.importLights = false;
            importer.addCollider = false;
            importer.isReadable = false;

            switch (rule.Rig)
            {
                case RigType.None:
                    importer.animationType = ModelImporterAnimationType.None;
                    importer.importAnimation = false;
                    break;
                // Generic only replaces None, so a Humanoid set up in the Inspector is kept
                case RigType.Generic when importer.animationType == ModelImporterAnimationType.None:
                    importer.animationType = ModelImporterAnimationType.Generic;
                    break;
                // Humanoid folders exist for retargeting, so anything else is switched to Humanoid
                case RigType.Humanoid when importer.animationType != ModelImporterAnimationType.Human:
                    importer.animationType = ModelImporterAnimationType.Human;
                    importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    // Characters drop their bone GameObjects: the Animator writes straight to the skin, which
                    // is far cheaper for crowds (expose bones in the Rig tab to attach props to hands)
                    importer.optimizeGameObjects = !ModelImportRules.IsAnimationLibrary(assetPath);
                    break;
            }
        }

        // Clips in Art/Animations loop and stay on the spot: walkers are moved by script, not root motion.
        // Only applied while the clips are untouched, so settings changed in the Inspector are kept.
        void OnPreprocessAnimation()
        {
            if (!ModelImportRules.IsAnimationLibrary(assetPath))
                return;

            var importer = (ModelImporter)assetImporter;
            if (importer.clipAnimations.Length > 0)
                return;

            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            foreach (ModelImporterClipAnimation clip in clips)
            {
                // Mixamo names every take "mixamo.com"; name it after the file instead
                if (clip.name == "mixamo.com")
                    clip.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                clip.loopTime = true;
                clip.loopPose = true;
                clip.lockRootRotation = true;
                clip.keepOriginalOrientation = true;
                clip.lockRootHeightY = true;
                clip.keepOriginalPositionY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalPositionXZ = true;
            }
            importer.clipAnimations = clips;
        }

        void OnPreprocessMaterialDescription(MaterialDescription description, Material material, AnimationClip[] clips)
        {
            if (!ModelImportRules.Applies(assetPath))
                return;

            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"{ShaderName} not found; {assetPath} keeps its default materials.");
                return;
            }

            material.shader = shader;
            ApplyColours(description, material);

            ModelImportRule rule = ModelImportRules.For(assetPath);
            bool metal = ModelImportRules.IsMetal(material.name);
            material.SetFloat(SmoothnessId, ModelImportRules.IsGlossy(material.name) ? 0.8f : metal ? 0.55f : 0.2f);
            material.SetFloat(MetallicId, metal ? 0.6f : 0f);
            material.SetFloat(SensitivityId, rule.Sensitivity);
            material.SetFloat(GrimeId, rule.Grime);
            material.SetFloat(FoliageId, ModelImportRules.FoliageFor(rule, assetPath, material.name));

            // Skyline blocks are unit cubes whose window texture was procedural in Blender
            if (ModelImportRules.IsFacade(material.name))
            {
                material.SetFloat(FacadeWindowsId, 1f);
                material.EnableKeyword(FacadeWindowsKeyword);
            }
        }

        static void ApplyColours(MaterialDescription description, Material material)
        {
            // Every pack here is opaque low-poly; some (Quaternius) ship with zero opacity, which is ignored
            if (description.TryGetProperty("DiffuseColor", out TexturePropertyDescription texture) && texture.texture != null)
            {
                material.SetTexture(BaseMapId, texture.texture);
                material.SetTextureScale(BaseMapId, texture.scale);
                material.SetTextureOffset(BaseMapId, texture.offset);
                material.SetColor(BaseColorId, Color.white);
            }
            else if (description.TryGetProperty("DiffuseColor", out Vector4 diffuse))
            {
                var colour = new Color(diffuse.x, diffuse.y, diffuse.z, 1f);
                material.SetColor(BaseColorId, PlayerSettings.colorSpace == ColorSpace.Linear ? colour.gamma : colour);
            }

            if (description.TryGetProperty("EmissiveColor", out Vector4 emissive))
            {
                float factor = description.TryGetProperty("EmissiveFactor", out float f) ? f : 1f;
                var colour = new Color(emissive.x, emissive.y, emissive.z) * factor;
                if (colour.maxColorComponent > 0.001f)
                    material.SetColor(EmissionColorId, colour);
            }
        }
    }
}
