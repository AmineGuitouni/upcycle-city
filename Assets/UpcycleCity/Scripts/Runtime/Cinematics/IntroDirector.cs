using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UpcycleCity.Audio;
using UpcycleCity.Pollution;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Runs the intro cinematic "The Turtle" as a sequence of timed beats:
    /// <list type="number">
    /// <item>A poor man walks a grey, littered beach at dawn.</item>
    /// <item>He finds a dead sea turtle tangled in a plastic bag.</item>
    /// <item>He kneels beside it.</item>
    /// <item>Flashback (his memory, the same beach before it was ruined): the turtle playing in clear water.</item>
    /// <item>The memory dives underwater: colour, light and fish.</item>
    /// <item>Snap back to grey. He frees the bag from the turtle and keeps it.</item>
    /// <item>He walks home through a run-down street at dusk.</item>
    /// <item>In his shack he sets the bag beside a tin of coins and drops one in.</item>
    /// <item>The promise, the title, then the next scene (the character creator).</item>
    /// </list>
    /// Shots, marks and paths are named children of <see cref="anchors"/> (placed by IntroSceneBuilder and
    /// free to move by hand); timings and looks come from <see cref="IntroConfig"/>, read live.
    /// Hold Space / Enter / Esc / gamepad South / touch to skip (one press once the intro has been seen).
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Intro Director")]
    public sealed class IntroDirector : MonoBehaviour
    {
        enum World { Grim, Memory }

        [SerializeField] IntroConfig config;

        [Header("Cast and crew")]
        [SerializeField] CinematicCamera cinematicCamera;
        [SerializeField] CinematicOverlay overlay;
        [SerializeField] CinematicActor actor;
        [Tooltip("Parent of every CameraAnchor, Mark_* and Path_* object, found by name.")]
        [SerializeField] Transform anchors;

        [Header("World")]
        [SerializeField] PollutionController pollution;
        [SerializeField] Light sun;
        [SerializeField] Volume grimVolume;
        [SerializeField] Volume dreamVolume;
        [SerializeField] Renderer[] waterRenderers = new Renderer[0];

        [Header("Props")]
        [SerializeField] GameObject litter;
        [SerializeField] GameObject deadTurtle;
        [SerializeField] SeaTurtle shoreTurtle;
        [SerializeField] SeaTurtle underwaterTurtle;
        [SerializeField] Transform bag;
        [SerializeField] Transform coin;
        [SerializeField] Light lamp;

        static readonly int DreamId = Shader.PropertyToID("_Dream");

        /// <summary>The beat being played (1-9), 0 before the start and 10 once finished. For debugging.</summary>
        public int Beat { get; private set; }
        public string BeatName { get; private set; } = "";
        public float BeatTime => Time.time - beatStart;

        float beatStart;
        Coroutine sequence;
        Coroutine actorWalk;
        bool finishing;
        InputAction skipAction;
        float skipHeld;

        // Atmosphere override, blended over the pollution profile each LateUpdate
        AtmosphereState overrideState;
        float overrideWeight;

        // Audio
        AudioSource grimMusic, dreamMusic, wavesSource, sfxSource;
        AudioLowPassFilter wavesLowPass;
        float grimTarget, dreamTarget, wavesGain;
        float musicFadeSpeed = 0.5f;
        float nextWave;
        bool audioTaken;
        bool ambienceWasMuted;

        MaterialPropertyBlock block;

        // ---------------------------------------------------------------- lifecycle

        void OnEnable()
        {
            skipAction = new InputAction("SkipIntro", InputActionType.Button);
            skipAction.AddBinding("<Keyboard>/space");
            skipAction.AddBinding("<Keyboard>/enter");
            skipAction.AddBinding("<Keyboard>/escape");
            skipAction.AddBinding("<Gamepad>/buttonSouth");
            skipAction.AddBinding("<Gamepad>/start");
            skipAction.AddBinding("<Pointer>/press");
            skipAction.Enable();
        }

        void OnDisable()
        {
            skipAction?.Disable();
            skipAction?.Dispose();
            skipAction = null;
            ReleaseAudio();
        }

        void Start()
        {
            if (config == null)
            {
                Debug.LogError("[Upcycle City] IntroDirector has no IntroConfig; skipping the intro.", this);
                LoadNext();
                return;
            }
            CreateAudio();
            sequence = StartCoroutine(Sequence());
        }

        void Update()
        {
            TickSkip();
            TickAudio(Time.deltaTime);
        }

        void LateUpdate()
        {
            if (overrideWeight > 0f && pollution != null && pollution.Profile != null)
                ApplyAtmosphere(AtmosphereState.Lerp(pollution.Profile.AtmosphereAt(pollution.Pollution), overrideState, overrideWeight));
        }

        // ---------------------------------------------------------------- the story

        IEnumerator Sequence()
        {
            // Opening state: black screen, grey beach
            SetWorld(World.Grim);
            SetAtmosphere(null);
            overlay.SetLetterbox(config.letterbox);
            overlay.SetFade(1f);
            if (coin != null)
                coin.gameObject.SetActive(false);
            SetLamp(0f);
            actor.Place(Mark("Mark_ShoreStart"));
            actor.SetPose(0f, 0f, 4f);
            SetMusic(grim: 1f, dream: 0f, fadeSpeed: 0.35f);
            wavesGain = 1f;

            // 1. Walk along the shore
            BeginBeat(1, "Walk along the shore");
            Vector3[] shorePath = Path("Path_Shore");
            float shoreWalkTime = config.walkAlongShore + config.discovery * 0.55f;
            float shoreSpeed = Mathf.Max(0.2f, actor.PathLength(shorePath) / shoreWalkTime);
            Coroutine walking = StartWalk(shorePath, shoreSpeed);
            cinematicCamera.Shake = 0.6f;
            cinematicCamera.Move(Shot("B1_From"), Shot("B1_To"), config.walkAlongShore + 0.6f);
            StartCoroutine(overlay.FadeTo(0f, config.fadeInSeconds));
            yield return Until(config.walkAlongShore);

            // 2. Discovery: the turtle in the foreground, he stops and looks down
            BeginBeat(2, "Discovery");
            cinematicCamera.Shake = 0.8f;
            cinematicCamera.Move(Shot("B2_From"), Shot("B2_To"), config.discovery);
            yield return walking;
            if (deadTurtle != null)
                StartCoroutine(actor.TurnTowards(deadTurtle.transform.position, 1.2f));
            StartCoroutine(actor.Slump(14f, 1.6f));
            yield return Until(config.discovery);

            // 3. He kneels; the world goes quiet
            BeginBeat(3, "Kneel");
            cinematicCamera.Shake = 1.2f;
            cinematicCamera.Move(Shot("B3_From"), Shot("B3_To"), config.kneel);
            SetMusic(grim: 0.55f, dream: 0f, fadeSpeed: 0.4f);
            wavesGain = 0.45f;
            StartCoroutine(actor.Slump(6f, 1.8f));
            yield return actor.Kneel(1.8f);
            yield return Until(config.kneel);

            // 4. The memory: the same shore, clean, the turtle playing in turquoise shallows
            BeginBeat(4, "Memory: the shore");
            yield return overlay.Flash(config.flashSeconds, config.flashHoldSeconds, config.flashOutSeconds, () =>
            {
                SetWorld(World.Memory);
                cinematicCamera.Shake = 0.9f;
                cinematicCamera.Move(Shot("B4_From"), Shot("B4_To"), config.memoryShore);
                if (shoreTurtle != null)
                    cinematicCamera.LookAt(shoreTurtle.transform, Vector3.zero, 0.35f);
                SetMusic(grim: 0f, dream: 1f, fadeSpeed: 1.5f);
                wavesGain = 0.8f;
            });
            yield return Until(config.memoryShore);

            // 5. Underwater dream: the turtle loops around the camera among fish and light
            BeginBeat(5, "Memory: underwater");
            yield return overlay.Flash(0.25f, 0.1f, 1.1f, () =>
            {
                SetAtmosphere(config.underwater);
                SetUnderwaterBackground(true);
                cinematicCamera.Shake = 1f;
                cinematicCamera.Move(Shot("B5_From"), Shot("B5_To"), config.memoryUnderwater);
                if (underwaterTurtle != null)
                    cinematicCamera.LookAt(underwaterTurtle.transform, Vector3.zero, 0.85f);
                wavesGain = 0.5f;
                SetUnderwaterAudio(true);
            });
            yield return Until(config.memoryUnderwater);

            // 6. Snap back to grey. He frees the bag and keeps it.
            BeginBeat(6, "Snap back");
            yield return overlay.Flash(0.08f, 0.05f, 0.45f, () =>
            {
                SetAtmosphere(null);
                SetUnderwaterBackground(false);
                SetWorld(World.Grim);
                cinematicCamera.ClearLook();
                cinematicCamera.Shake = 1.1f;
                cinematicCamera.Move(Shot("B6_From"), Shot("B6_To"), config.snapBack);
                SetUnderwaterAudio(false);
                SetMusic(grim: 0.6f, dream: 0f, fadeSpeed: 50f); // the dream cuts off
                wavesGain = 0.6f;
            });
            yield return Until(1.6f);
            if (bag != null)
            {
                yield return CinematicActor.Toss(bag, actor.HandPosition, bag.rotation, 0.25f, 1.1f);
                actor.Hold(bag);
            }
            yield return Until(config.snapBack - 2f);
            StartCoroutine(actor.Stand(1.6f));
            yield return Until(config.snapBack);

            // 7. Walk home at dusk
            BeginBeat(7, "Walk home");
            float half = config.blackCutSeconds * 0.5f;
            yield return overlay.FadeTo(1f, half);
            SetAtmosphere(config.dusk);
            SetGrim(config.duskGrimWeight);
            if (pollution != null)
                pollution.SetPollution(0.85f, instant: true);
            wavesGain = 0f;
            SetMusic(grim: 0.8f, dream: 0f, fadeSpeed: 0.5f);
            actor.Place(Mark("Mark_HomeStart"));
            actor.SetPose(0f, 0f, 10f);
            Vector3[] homePath = Path("Path_Home");
            float homeSpeed = Mathf.Max(0.2f, actor.PathLength(homePath) / Mathf.Max(1f, config.walkHome - half));
            StartWalk(homePath, homeSpeed);
            cinematicCamera.Shake = 0.5f;
            cinematicCamera.Move(Shot("B7_From"), Shot("B7_To"), config.walkHome);
            yield return overlay.FadeTo(0f, half);
            yield return Until(config.walkHome - half);
            yield return overlay.FadeTo(1f, half);

            // 8. Inside the shack: the bag beside the coin tin, one coin goes in, the lamp warms
            BeginBeat(8, "The shack");
            StopWalk();
            SetAtmosphere(config.interior);
            SetGrim(config.shackGrimWeight);
            actor.Place(Mark("Mark_Interior"));
            yield return actor.Sit(0f);
            actor.SetSlump(10f);
            if (bag != null)
                actor.Hold(bag);
            float wide = config.shackInterior * 0.45f;
            cinematicCamera.Shake = 0.6f;
            cinematicCamera.Move(Shot("B8_From"), Shot("B8_To"), wide);
            yield return overlay.FadeTo(0f, half);
            yield return Until(2.2f);
            Transform bagRest = Mark("Mark_BagRest");
            if (bag != null && bagRest != null)
                yield return CinematicActor.Toss(bag, bagRest.position, bagRest.rotation, 0.2f, 1.1f);
            yield return Until(wide);

            cinematicCamera.Shake = 0.4f;
            cinematicCamera.Move(Shot("B8b_From"), Shot("B8b_To"), config.shackInterior - wide);
            yield return Until(wide + 1.2f);
            Transform coinStart = Mark("Mark_CoinStart");
            Transform coinDrop = Mark("Mark_CoinDrop");
            if (coin != null && coinStart != null && coinDrop != null)
            {
                coin.SetPositionAndRotation(coinStart.position, coinStart.rotation);
                coin.gameObject.SetActive(true);
                yield return CinematicActor.Toss(coin, coinDrop.position, coinDrop.rotation, 0.12f, 0.6f);
                PlaySfx(config.coinClip, 0.9f);
                coin.gameObject.SetActive(false);
            }
            StartCoroutine(actor.Slump(0f, 2.5f));
            // Hope: the lamp warms and a little colour comes back into the world
            float grimFrom = grimVolume != null ? grimVolume.weight : 0f;
            StartCoroutine(CinematicActor.Tween(3.5f, t =>
            {
                SetLamp(t);
                SetGrim(Mathf.Lerp(grimFrom, config.promiseGrimWeight, t));
            }));
            yield return Until(config.shackInterior);

            // 9. The promise, then the title
            BeginBeat(9, "Promise");
            yield return overlay.ShowLine(config.promiseLine, 1.6f);
            yield return Until(Mathf.Max(1.7f, config.promise - 1.5f));
            SetMusic(grim: 0f, dream: 0f, fadeSpeed: 0.4f);
            yield return overlay.FadeTo(1f, 1.5f);
            overlay.HideText();
            yield return overlay.ShowTitle(config.titleText, 1.2f);
            yield return new WaitForSeconds(Mathf.Max(0.2f, config.title - 2.2f));
            yield return overlay.HideTitle(1f);

            Finish(skipped: false);
        }

        // ---------------------------------------------------------------- beats and lookups

        void BeginBeat(int index, string name)
        {
            Beat = index;
            BeatName = name;
            beatStart = Time.time;
        }

        /// <summary>Waits until the current beat has run for the given time.</summary>
        IEnumerator Until(float beatSeconds)
        {
            while (Time.time - beatStart < beatSeconds)
                yield return null;
        }

        CameraAnchor Shot(string name)
        {
            Transform t = Find(name);
            CameraAnchor anchor = t != null ? t.GetComponent<CameraAnchor>() : null;
            if (anchor == null)
                Debug.LogWarning($"[Upcycle City] Intro shot '{name}' is missing under {anchors?.name}.", this);
            return anchor;
        }

        Transform Mark(string name) => Find(name);

        Vector3[] Path(string name)
        {
            Transform root = Find(name);
            if (root == null)
                return new Vector3[0];
            var points = new Vector3[root.childCount];
            for (int i = 0; i < points.Length; i++)
                points[i] = root.GetChild(i).position;
            return points;
        }

        Transform Find(string name)
        {
            if (anchors == null)
                return null;
            foreach (Transform t in anchors.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                    return t;
            }
            return null;
        }

        /// <summary>Starts a walk as its own coroutine, so the sequence can cut away while he is still walking.</summary>
        Coroutine StartWalk(Vector3[] path, float speed)
        {
            StopWalk();
            actorWalk = StartCoroutine(actor.WalkPath(path, speed));
            return actorWalk;
        }

        void StopWalk()
        {
            if (actorWalk != null)
                StopCoroutine(actorWalk);
            actorWalk = null;
            actor.Halt();
        }

        // ---------------------------------------------------------------- world state

        void SetWorld(World world)
        {
            bool memory = world == World.Memory;
            if (pollution != null)
                pollution.SetPollution(memory ? 0f : 1f, instant: true);
            if (grimVolume != null)
                grimVolume.weight = memory ? 0f : 1f;
            if (dreamVolume != null)
                dreamVolume.weight = memory ? 1f : 0f;
            if (litter != null)
                litter.SetActive(!memory);
            if (deadTurtle != null)
                deadTurtle.SetActive(!memory);
            if (shoreTurtle != null)
                shoreTurtle.gameObject.SetActive(memory);
            if (actor != null)
                actor.gameObject.SetActive(!memory);
            SetWaterDream(memory ? 1f : 0f);
        }

        void SetWaterDream(float dream)
        {
            block ??= new MaterialPropertyBlock();
            foreach (Renderer r in waterRenderers)
            {
                if (r == null)
                    continue;
                r.GetPropertyBlock(block);
                block.SetFloat(DreamId, dream);
                r.SetPropertyBlock(block);
            }
        }

        /// <summary>Overrides the atmosphere (fog, ambient, sun) on top of the pollution profile; null releases it.</summary>
        void SetAtmosphere(AtmosphereState? state)
        {
            if (state.HasValue)
            {
                overrideState = state.Value;
                overrideWeight = 1f;
                return;
            }
            overrideWeight = 0f;
            // Let the controller re-apply its own atmosphere
            if (pollution != null)
                pollution.SetPollution(pollution.TargetPollution, instant: true);
        }

        void ApplyAtmosphere(in AtmosphereState a)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = a.fogColor;
            RenderSettings.fogDensity = a.fogDensity;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = a.ambientSky;
            RenderSettings.ambientEquatorColor = a.ambientEquator;
            RenderSettings.ambientGroundColor = a.ambientGround;
            if (sun != null)
            {
                sun.color = a.sunColor;
                sun.intensity = a.sunIntensity;
                sun.transform.rotation = Quaternion.Euler(a.sunElevation, sun.transform.eulerAngles.y, 0f);
            }
        }

        void SetGrim(float weight)
        {
            if (grimVolume != null)
                grimVolume.weight = Mathf.Clamp01(weight);
        }

        /// <summary>Underwater the sky must not show past the fog: clear to the fog colour instead.</summary>
        void SetUnderwaterBackground(bool underwater)
        {
            Camera cam = cinematicCamera != null ? cinematicCamera.Camera : null;
            if (cam == null)
                return;
            cam.clearFlags = underwater ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
            cam.backgroundColor = config.underwater.fogColor;
        }

        void SetLamp(float warmth)
        {
            if (lamp == null)
                return;
            lamp.color = Color.Lerp(config.lampColdColor, config.lampWarmColor, warmth);
            lamp.intensity = Mathf.Lerp(config.lampColdIntensity, config.lampWarmIntensity, warmth);
        }

        // ---------------------------------------------------------------- skipping and leaving

        void TickSkip()
        {
            if (finishing || skipAction == null || overlay == null)
                return;

            if (IntroProgress.HasSeen && skipAction.WasPressedThisFrame())
            {
                Finish(skipped: true);
                return;
            }

            skipHeld = skipAction.IsPressed() ? skipHeld + Time.deltaTime : 0f;
            float hold = Mathf.Max(0.05f, config != null ? config.skipHoldSeconds : 1f);
            overlay.SetSkipProgress(skipHeld / hold);
            if (skipHeld >= hold)
                Finish(skipped: true);
        }

        void Finish(bool skipped)
        {
            if (finishing)
                return;
            finishing = true;
            Beat = 10;
            BeatName = skipped ? "Skipped" : "Finished";
            if (sequence != null)
                StopCoroutine(sequence);
            StartCoroutine(Leave(skipped));
        }

        IEnumerator Leave(bool skipped)
        {
            IntroProgress.MarkSeen();
            overlay.SetSkipProgress(0f);
            SetMusic(grim: 0f, dream: 0f, fadeSpeed: 1.2f);
            wavesGain = 0f;
            if (skipped)
                yield return overlay.FadeTo(1f, 0.6f);
            LoadNext();
        }

        void LoadNext()
        {
            ReleaseAudio();
            string next = config != null ? config.nextScene : "CharacterCreator";
            if (!Application.CanStreamedLevelBeLoaded(next))
            {
                Debug.LogError($"[Upcycle City] Intro: scene '{next}' is not in the build settings.", this);
                return;
            }
            SceneManager.LoadSceneAsync(next);
        }

        // ---------------------------------------------------------------- audio

        void CreateAudio()
        {
            grimMusic = NewSource("Intro_GrimMusic", config.grimMusic, loop: true);
            dreamMusic = NewSource("Intro_DreamMusic", config.dreamMusic, loop: true);
            wavesSource = NewSource("Intro_Waves", null, loop: false);
            wavesLowPass = wavesSource.gameObject.AddComponent<AudioLowPassFilter>();
            wavesLowPass.cutoffFrequency = 22000f;
            sfxSource = NewSource("Intro_Sfx", null, loop: false);

            // The city's own music and street ambience pause for the intro
            if (SoundManager.HasInstance)
            {
                ambienceWasMuted = SoundManager.IsCategoryMuted(SoundCategory.Ambience);
                SoundManager.SetMusicPaused(true);
                SoundManager.SetCategoryMuted(SoundCategory.Ambience, true, save: false);
                audioTaken = true;
            }
        }

        void ReleaseAudio()
        {
            if (!audioTaken)
                return;
            audioTaken = false;
            if (SoundManager.HasInstance)
            {
                SoundManager.SetCategoryMuted(SoundCategory.Ambience, ambienceWasMuted, save: false);
                SoundManager.SetMusicPaused(false);
            }
        }

        AudioSource NewSource(string name, AudioClip clip, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.loop = loop;
            source.clip = clip;
            source.volume = 0f;
            if (clip != null && loop)
                source.Play();
            return source;
        }

        void SetMusic(float grim, float dream, float fadeSpeed)
        {
            grimTarget = grim;
            dreamTarget = dream;
            musicFadeSpeed = fadeSpeed;
            // A memory starts from its beginning each time it surfaces
            if (dream > 0f && dreamMusic != null && dreamMusic.clip != null && dreamMusic.volume <= 0.001f)
            {
                dreamMusic.time = 0f;
                if (!dreamMusic.isPlaying)
                    dreamMusic.Play();
            }
        }

        void SetUnderwaterAudio(bool underwater)
        {
            if (wavesLowPass != null)
                wavesLowPass.cutoffFrequency = underwater ? 650f : 22000f;
        }

        void PlaySfx(AudioClip clip, float volume)
        {
            if (clip != null && sfxSource != null)
                sfxSource.PlayOneShot(clip, volume * Bus(SoundCategory.SFX));
        }

        /// <summary>Effective volume of a SoundManager bus (master included), before the intro muted anything.</summary>
        float Bus(SoundCategory category)
        {
            if (!SoundManager.HasInstance)
                return 1f;
            bool muted = SoundManager.IsCategoryMuted(SoundCategory.Master) ||
                         (category == SoundCategory.Ambience ? ambienceWasMuted : SoundManager.IsCategoryMuted(category));
            return muted ? 0f : SoundManager.GetCategoryVolume(SoundCategory.Master) * SoundManager.GetCategoryVolume(category);
        }

        void TickAudio(float dt)
        {
            if (config == null || grimMusic == null)
                return;

            float music = config.musicVolume * Bus(SoundCategory.Music);
            float step = musicFadeSpeed * dt;
            grimMusic.volume = Mathf.MoveTowards(grimMusic.volume, grimTarget * music, step * Mathf.Max(music, 0.01f));
            dreamMusic.volume = Mathf.MoveTowards(dreamMusic.volume, dreamTarget * music, step * Mathf.Max(music, 0.01f));

            if (wavesGain > 0f && config.waveClips != null && config.waveClips.Length > 0 && Time.time >= nextWave)
            {
                AudioClip clip = config.waveClips[Random.Range(0, config.waveClips.Length)];
                if (clip != null)
                {
                    wavesSource.pitch = Random.Range(0.9f, 1.05f);
                    wavesSource.PlayOneShot(clip, config.wavesVolume * wavesGain * Random.Range(0.75f, 1f) * Bus(SoundCategory.Ambience));
                }
                nextWave = Time.time + Random.Range(config.waveInterval.x, Mathf.Max(config.waveInterval.x, config.waveInterval.y));
            }
        }
    }
}
