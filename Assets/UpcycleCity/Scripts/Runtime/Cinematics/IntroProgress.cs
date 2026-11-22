using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>Remembers whether the player has watched the intro (PlayerPrefs, fully offline).</summary>
    public static class IntroProgress
    {
        const string PrefsKey = "UpcycleCity.IntroSeen.v1";

        static bool loaded;
        static bool seen;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            loaded = false;
            seen = false;
        }

        public static bool HasSeen
        {
            get
            {
                if (!loaded)
                {
                    seen = PlayerPrefs.GetInt(PrefsKey, 0) == 1;
                    loaded = true;
                }
                return seen;
            }
        }

        public static void MarkSeen()
        {
            seen = true;
            loaded = true;
            PlayerPrefs.SetInt(PrefsKey, 1);
            PlayerPrefs.Save();
        }

        /// <summary>Forget the flag, e.g. to test the first-run hold-to-skip.</summary>
        public static void Clear()
        {
            seen = false;
            loaded = true;
            PlayerPrefs.DeleteKey(PrefsKey);
        }
    }
}
