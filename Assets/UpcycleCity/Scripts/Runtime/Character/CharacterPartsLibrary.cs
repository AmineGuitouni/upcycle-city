using System;
using UnityEngine;

namespace UpcycleCity.Character
{
    /// <summary>
    /// Every option the character creator offers: body per sex, swappable face, hair and facial-hair meshes, and colour
    /// palettes. All parts share one origin (feet on the floor, facing +Z), so they stack without offsets.
    /// Add a style or colour here and it appears in the creator automatically.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Character/Character Parts Library", fileName = "CharacterPartsLibrary")]
    public sealed class CharacterPartsLibrary : ScriptableObject
    {
        [Serializable]
        public struct Part
        {
            public string displayName;
            [Tooltip("Leave empty for 'None' (e.g. a bald option).")]
            public GameObject prefab;
        }

        [Header("Bodies")]
        public GameObject maleBody;
        public GameObject femaleBody;

        [Header("Skinned Bodies (same look, with a skeleton; posed by code, e.g. in the intro)")]
        public GameObject maleBodyRigged;
        public GameObject femaleBodyRigged;

        [Header("Styles")]
        public Part[] faceStyles = Array.Empty<Part>();
        public Part[] hairStyles = Array.Empty<Part>();
        public Part[] facialHairStyles = Array.Empty<Part>();

        [Header("First-Person Arms (camera space, origin at the eye)")]
        public GameObject rightArmOpen;
        public GameObject rightArmGrip;
        public GameObject leftArmOpen;
        public GameObject leftArmGrip;

        [Header("Palettes")]
        public Color[] skinTones = Array.Empty<Color>();
        public Color[] outfitColors = Array.Empty<Color>();
        public Color[] eyeColors = Array.Empty<Color>();
        public Color[] hairColors = Array.Empty<Color>();

        public GameObject BodyFor(CharacterSex sex) => sex == CharacterSex.Female && femaleBody != null ? femaleBody : maleBody;

        /// <summary>The skinned body when asked for and available, otherwise the static one.</summary>
        public GameObject BodyFor(CharacterSex sex, bool rigged)
        {
            if (rigged)
            {
                GameObject skinned = sex == CharacterSex.Female && femaleBodyRigged != null ? femaleBodyRigged : maleBodyRigged;
                if (skinned != null)
                    return skinned;
            }
            return BodyFor(sex);
        }

        /// <summary>Clamps every index into range, so old saves keep working when options are removed.</summary>
        public CharacterAppearance Sanitize(CharacterAppearance a)
        {
            a.sex = a.sex == CharacterSex.Female ? CharacterSex.Female : CharacterSex.Male;
            a.skinTone = Clamp(a.skinTone, skinTones.Length);
            a.outfitColor = Clamp(a.outfitColor, outfitColors.Length);
            a.eyeStyle = Clamp(a.eyeStyle, faceStyles.Length);
            a.eyeColor = Clamp(a.eyeColor, eyeColors.Length);
            a.hairStyle = Clamp(a.hairStyle, hairStyles.Length);
            a.hairColor = Clamp(a.hairColor, hairColors.Length);
            a.facialHair = Clamp(a.facialHair, facialHairStyles.Length);
            return a;
        }

        public CharacterAppearance Randomize()
        {
            return new CharacterAppearance
            {
                sex = UnityEngine.Random.value < 0.5f ? CharacterSex.Male : CharacterSex.Female,
                skinTone = Pick(skinTones.Length),
                outfitColor = Pick(outfitColors.Length),
                eyeStyle = Pick(faceStyles.Length),
                eyeColor = Pick(eyeColors.Length),
                hairStyle = Pick(hairStyles.Length),
                hairColor = Pick(hairColors.Length),
                facialHair = Pick(facialHairStyles.Length),
            };
        }

        public Color SkinColor(int i) => At(skinTones, i, new Color(0.9f, 0.68f, 0.53f));
        public Color OutfitColor(int i) => At(outfitColors, i, new Color(1f, 0.48f, 0.1f));
        public Color EyeColor(int i) => At(eyeColors, i, new Color(0.29f, 0.48f, 0.23f));
        public Color HairColor(int i) => At(hairColors, i, new Color(0.35f, 0.23f, 0.13f));

        static int Clamp(int i, int count) => count == 0 ? 0 : Mathf.Clamp(i, 0, count - 1);
        static int Pick(int count) => count == 0 ? 0 : UnityEngine.Random.Range(0, count);
        static Color At(Color[] colors, int i, Color fallback) => colors.Length == 0 ? fallback : colors[Clamp(i, colors.Length)];
    }
}
