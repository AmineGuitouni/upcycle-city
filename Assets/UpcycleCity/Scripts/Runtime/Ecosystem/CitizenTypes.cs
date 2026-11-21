using UnityEngine;

namespace UpcycleCity.Ecosystem
{
    public enum CitizenPersonality : byte
    {
        EcoConscious = 0,
        RushedCommuter = 1,
        CasualStroller = 2,
        CarelessLitterer = 3,
    }

    public enum CitizenActionState : byte
    {
        Walking = 0,
        UsingBin = 1,
        Littering = 2,
        VisitingLandmark = 3,
        Resting = 4,
        ReactingToSmog = 5,
        CelebratingClean = 6,
    }

    public enum CitizenPoiKind : byte
    {
        ShopWindow = 0,
        Fountain = 1,
        Bench = 2,
        Cafe = 3,
    }

    /// <summary>Immutable behavioral traits for each citizen personality archetype.</summary>
    public readonly struct CitizenPersonalityProfile
    {
        public readonly string Label;
        public readonly float WalkSpeedMultiplier;
        public readonly float BinSearchRadius;
        public readonly float BaseBinPreference;
        public readonly float BaseLitterChance;
        public readonly float LandmarkPauseChance;
        public readonly float SmogSensitivity;

        public CitizenPersonalityProfile(
            string label,
            float walkSpeedMultiplier,
            float binSearchRadius,
            float baseBinPreference,
            float baseLitterChance,
            float landmarkPauseChance,
            float smogSensitivity)
        {
            Label = label;
            WalkSpeedMultiplier = walkSpeedMultiplier;
            BinSearchRadius = binSearchRadius;
            BaseBinPreference = baseBinPreference;
            BaseLitterChance = baseLitterChance;
            LandmarkPauseChance = landmarkPauseChance;
            SmogSensitivity = smogSensitivity;
        }

        public static CitizenPersonalityProfile For(CitizenPersonality personality)
        {
            switch (personality)
            {
                case CitizenPersonality.EcoConscious:
                    return new CitizenPersonalityProfile("Eco Citizen", 1.04f, 15.0f, 0.94f, 0.06f, 0.35f, 0.85f);
                case CitizenPersonality.RushedCommuter:
                    return new CitizenPersonalityProfile("Commuter", 1.28f, 6.5f, 0.48f, 0.45f, 0.14f, 0.65f);
                case CitizenPersonality.CasualStroller:
                    return new CitizenPersonalityProfile("Stroller", 0.88f, 10.5f, 0.68f, 0.28f, 0.52f, 0.72f);
                default:
                    return new CitizenPersonalityProfile("Careless", 0.95f, 5.0f, 0.20f, 0.78f, 0.25f, 0.35f);
            }
        }
    }

    /// <summary>
    /// Lightweight world-space reaction cue shown briefly above nearby Tier-1 visible citizens
    /// when they perform a meaningful action (recycled, littered, coughed in smog, admired plaza).
    /// </summary>
    public struct CitizenReactionCue
    {
        public Transform Anchor;
        public string Text;
        public Color AccentColor;
        public float Remaining;
        public float Duration;
    }
}
