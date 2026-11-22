using System;
using UnityEngine;

namespace UpcycleCity.Character
{
    public enum CharacterSex
    {
        Male = 0,
        Female = 1,
    }

    /// <summary>
    /// The player's look, as indices into a <see cref="CharacterPartsLibrary"/>. A plain value type, so it is
    /// cheap to copy, compare and save as JSON. Out-of-range indices are clamped by the library.
    /// </summary>
    [Serializable]
    public struct CharacterAppearance : IEquatable<CharacterAppearance>
    {
        public CharacterSex sex;
        public int skinTone;
        public int outfitColor;
        public int eyeStyle;
        public int eyeColor;
        public int hairStyle;
        public int hairColor;
        public int facialHair;

        /// <summary>Starting look: worn brown coat, tired eyes, messy brown hair and stubble.</summary>
        public static CharacterAppearance Default => new CharacterAppearance
        {
            sex = CharacterSex.Male,
            skinTone = 3,
            outfitColor = 0,
            eyeStyle = 0,
            eyeColor = 0,
            hairStyle = 2,
            hairColor = 2,
            facialHair = 1,
        };

        public bool Equals(CharacterAppearance o) =>
            sex == o.sex && skinTone == o.skinTone && outfitColor == o.outfitColor && eyeStyle == o.eyeStyle &&
            eyeColor == o.eyeColor && hairStyle == o.hairStyle && hairColor == o.hairColor &&
            facialHair == o.facialHair;

        public override bool Equals(object obj) => obj is CharacterAppearance o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine((int)sex, skinTone, outfitColor, eyeStyle, eyeColor, hairStyle, hairColor, facialHair);
    }

    /// <summary>
    /// Keeps the chosen <see cref="CharacterAppearance"/> across scenes and sessions (PlayerPrefs, fully offline).
    /// </summary>
    public static class CharacterAppearanceStore
    {
        const string PrefsKey = "UpcycleCity.CharacterAppearance.v2";

        static bool loaded;
        static CharacterAppearance current;
        static bool hasSessionOverride;
        static CharacterAppearance sessionOverride;

        /// <summary>Raised after <see cref="Save"/> or <see cref="SetSessionOverride"/>.</summary>
        public static event Action<CharacterAppearance> Changed;

        /// <summary>True once the player has finished the character creator at least once.</summary>
        public static bool HasSaved => PlayerPrefs.HasKey(PrefsKey);

        /// <summary>The active look (session override if set, otherwise saved look or <see cref="CharacterAppearance.Default"/>).</summary>
        public static CharacterAppearance Current
        {
            get
            {
                if (hasSessionOverride)
                    return sessionOverride;
                if (!loaded)
                    Load();
                return current;
            }
        }

        public static void Save(CharacterAppearance appearance)
        {
            hasSessionOverride = false;
            current = appearance;
            loaded = true;
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(appearance));
            PlayerPrefs.Save();
            Changed?.Invoke(appearance);
        }

        /// <summary>
        /// Applies a temporary appearance override for the current process only without modifying PlayerPrefs
        /// (used by CLI test flags such as <c>-coop-outfit</c>).
        /// </summary>
        public static void SetSessionOverride(CharacterAppearance appearance)
        {
            sessionOverride = appearance;
            hasSessionOverride = true;
            Changed?.Invoke(appearance);
        }

        /// <summary>Clears any temporary session override and restores the saved PlayerPrefs look.</summary>
        public static void ClearSessionOverride()
        {
            if (!hasSessionOverride)
                return;
            hasSessionOverride = false;
            Changed?.Invoke(Current);
        }

        static void Load()
        {
            loaded = true;
            current = CharacterAppearance.Default;
            string json = PlayerPrefs.GetString(PrefsKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return;
            try
            {
                current = JsonUtility.FromJson<CharacterAppearance>(json);
            }
            catch (ArgumentException)
            {
                current = CharacterAppearance.Default;
            }
        }

        // Domain reload is off in this project: clear statics before each Play Mode session
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            loaded = false;
            current = default;
            hasSessionOverride = false;
            sessionOverride = default;
            Changed = null;
        }
    }
}
