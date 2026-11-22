using UnityEngine;
using UpcycleCity.Pollution;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Designer tuning for the intro cinematic ("The Turtle"). Read live by <see cref="IntroDirector"/>, so
    /// timings and looks can be adjusted from the Inspector while the intro plays.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Cinematics/Intro Config", fileName = "IntroConfig")]
    public sealed class IntroConfig : ScriptableObject
    {
        [Header("Flow")]
        [Tooltip("Scene loaded when the intro ends or is skipped.")]
        public string nextScene = "MainMenu";
        [Tooltip("Seconds the skip input must be held. After the intro has been seen once, a single press skips.")]
        [Min(0f)] public float skipHoldSeconds = 1.2f;
        [Tooltip("Height of each letterbox bar as a fraction of the screen (0.12 is about 2.35:1 on 16:9).")]
        [Range(0f, 0.25f)] public float letterbox = 0.12f;

        [Header("Beat durations (seconds)")]
        [Min(0.5f)] public float walkAlongShore = 11f;
        [Min(0.5f)] public float discovery = 6.5f;
        [Min(0.5f)] public float kneel = 6.5f;
        [Min(0.5f)] public float memoryShore = 9f;
        [Min(0.5f)] public float memoryUnderwater = 12f;
        [Min(0.5f)] public float snapBack = 6.5f;
        [Min(0.5f)] public float walkHome = 9f;
        [Min(0.5f)] public float shackInterior = 12f;
        [Min(0.5f)] public float promise = 6f;
        [Min(0.5f)] public float title = 3.5f;

        [Header("Transitions")]
        [Min(0f)] public float fadeInSeconds = 2.5f;
        [Min(0f)] public float flashSeconds = 0.35f;
        [Min(0f)] public float flashHoldSeconds = 0.25f;
        [Min(0f)] public float flashOutSeconds = 1.4f;
        [Min(0f)] public float blackCutSeconds = 0.9f;

        [Header("Actor")]
        [Min(0.1f)] public float walkSpeed = 1.15f;
        [Min(0.1f)] public float tiredWalkSpeed = 0.8f;
        public CharacterRigPoser.WalkStyle shoreWalk = CharacterRigPoser.WalkStyle.Tired;
        public CharacterRigPoser.WalkStyle homeWalk = CharacterRigPoser.WalkStyle.Steady;

        [Header("The memory")]
        [Tooltip("Seconds for the grey grade to bleed into the dream colours.")]
        [Min(0.1f)] public float colourBleedSeconds = 2f;
        [Tooltip("Seconds of the lens ripple when the memory begins.")]
        [Min(0.1f)] public float rippleSeconds = 1.4f;

        [Header("Sea turtle")]
        [Min(0.1f)] public float turtleSwimSpeed = 1.6f;
        [Min(0.1f)] public float turtleStrokeRate = 0.75f;

        [Header("Text")]
        [TextArea(2, 4)]
        public string promiseLine = "I promise.\nEvery coin I earn goes back into cleaning this city.";
        public string titleText = "UPCYCLE CITY";

        [Header("Atmosphere overrides (blended over the pollution profile)")]
        public AtmosphereState underwater = new AtmosphereState
        {
            sunColor = new Color(0.75f, 1f, 0.98f),
            sunIntensity = 1.4f,
            sunElevation = 70f,
            ambientSky = new Color(0.35f, 0.8f, 0.9f),
            ambientEquator = new Color(0.2f, 0.55f, 0.7f),
            ambientGround = new Color(0.2f, 0.35f, 0.4f),
            fogColor = new Color(0.1f, 0.48f, 0.6f),
            fogDensity = 0.05f,
        };
        public AtmosphereState dusk = new AtmosphereState
        {
            sunColor = new Color(1f, 0.55f, 0.32f),
            sunIntensity = 0.75f,
            sunElevation = 7f,
            ambientSky = new Color(0.36f, 0.33f, 0.42f),
            ambientEquator = new Color(0.42f, 0.32f, 0.28f),
            ambientGround = new Color(0.16f, 0.14f, 0.13f),
            fogColor = new Color(0.52f, 0.4f, 0.36f),
            fogDensity = 0.018f,
        };
        public AtmosphereState interior = new AtmosphereState
        {
            sunColor = new Color(0.55f, 0.6f, 0.85f),
            sunIntensity = 0.12f,
            sunElevation = 10f,
            ambientSky = new Color(0.17f, 0.17f, 0.22f),
            ambientEquator = new Color(0.15f, 0.13f, 0.13f),
            ambientGround = new Color(0.08f, 0.07f, 0.07f),
            fogColor = new Color(0.05f, 0.05f, 0.06f),
            fogDensity = 0.001f,
        };

        [Header("Grey grade weight (colour creeps back as he makes the promise)")]
        [Range(0f, 1f)] public float duskGrimWeight = 0.75f;
        [Range(0f, 1f)] public float shackGrimWeight = 0.6f;
        [Range(0f, 1f)] public float promiseGrimWeight = 0.2f;

        [Header("Shack window (cold night to warm dawn as he makes the promise)")]
        public Color windowColdColor = new Color(0.55f, 0.62f, 0.95f);
        public Color windowDawnColor = new Color(1f, 0.72f, 0.42f);
        [Min(0f)] public float windowColdIntensity = 4f;
        [Min(0f)] public float windowDawnIntensity = 9f;

        [Header("Shack lamp")]
        public Color lampColdColor = new Color(1f, 0.82f, 0.6f);
        public Color lampWarmColor = new Color(1f, 0.7f, 0.38f);
        [Min(0f)] public float lampColdIntensity = 5f;
        [Min(0f)] public float lampWarmIntensity = 9f;

        [Header("Audio (empty slots are skipped)")]
        public AudioClip grimMusic;
        public AudioClip dreamMusic;
        public AudioClip[] waveClips = new AudioClip[0];
        public AudioClip coinClip;
        public AudioClip windLoop;
        public AudioClip underwaterLoop;
        public AudioClip heartbeat;
        public AudioClip sting;
        public AudioClip[] footsteps = new AudioClip[0];
        public AudioClip[] gullCries = new AudioClip[0];
        [Range(0f, 1f)] public float musicVolume = 0.7f;
        [Range(0f, 1f)] public float wavesVolume = 0.55f;
        [Range(0f, 1f)] public float windVolume = 0.32f;
        [Range(0f, 1f)] public float underwaterVolume = 0.55f;
        [Range(0f, 1f)] public float heartbeatVolume = 0.85f;
        [Range(0f, 1f)] public float stingVolume = 0.9f;
        [Tooltip("Seconds between wave crashes (random in range).")]
        public Vector2 waveInterval = new Vector2(1.6f, 3.4f);
    }
}
