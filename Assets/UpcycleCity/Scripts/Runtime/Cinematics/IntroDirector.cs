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
    /// <item>A poor man trudges along a grey, littered beach at dawn, wind in his coat.</item>
    /// <item>He finds a dead sea turtle tangled in a plastic bag (rack focus, the music stops, a heartbeat).</item>
    /// <item>He kneels, lays a hand on its shell, wipes his eyes.</item>
    /// <item>The memory: the lens ripples, colour bleeds back and the beach heals in front of him: the litter
    /// dissolves away, the sky clears, gulls wheel, and the turtle plays in turquoise shallows.</item>
    /// <item>The memory dives: light, fish and caustics; the turtle rolls past and swims up into the light.</item>
    /// <item>A hard cut back to grey. He works the bag free from the turtle and keeps it.</item>
    /// <item>He walks home through a run-down street at dusk, the bag swinging from his hand.</item>
    /// <item>In his shack he sets the bag beside a tin of coins and drops one in. Dawn reaches the window.</item>
    /// <item>The promise, word by word; the title; then the next scene (the character creator).</item>
    /// </list>
    /// Shots, marks and paths are named children of <see cref="anchors"/> (placed by IntroSceneBuilder and free
    /// to move by hand); timings and looks come from <see cref="IntroConfig"/>, read live.
    /// Hold Space / Enter / Esc / gamepad South / touch to skip (one press once the intro has been seen).
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Intro Director")]
    public sealed class IntroDirector : MonoBehaviour
    {
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
        [Tooltip("Lens distortion, chromatic aberration and bloom, pulsed as the memory begins.")]
        [SerializeField] Volume transitionVolume;
        [SerializeField] Renderer[] waterRenderers = new Renderer[0];
        [Tooltip("Shown only during the memory (gulls, splashes, sparkles).")]
        [SerializeField] GameObject memoryOnly;
        [Tooltip("Shown only in the grey present (flies, blowing grit, the tumbling bag).")]
        [SerializeField] GameObject presentOnly;
        [SerializeField] CausticsCookie caustics;
        [SerializeField] FootprintTrail footprints;

        [Header("Props")]
        [SerializeField] GameObject deadTurtle;
        [SerializeField] SeaTurtle shoreTurtle;
        [SerializeField] SeaTurtle underwaterTurtle;
        [Tooltip("The bag caught round the dead turtle's neck.")]
        [SerializeField] Transform tangledBag;
        [Tooltip("The same bag as he carries it (hidden until he frees it).")]
        [SerializeField] Transform heldBag;
        [SerializeField] ParticleSystem sandPuff;
        [SerializeField] Transform coin;
        [SerializeField] ParticleSystem coinGlint;
        [SerializeField] Light lamp;
        [SerializeField] Light windowLight;

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
        AudioSource grimMusic, dreamMusic, wavesSource, sfxSource, windSource, underwaterSource, heartbeatSource;
        AudioLowPassFilter wavesLowPass;
        float grimTarget, dreamTarget, wavesGain, windTarget, underwaterTarget, heartbeatTarget;
        float musicFadeSpeed = 0.5f;
        float nextWave;
        bool audioTaken;
        bool ambienceWasMuted;
        Gull[] gulls = new Gull[0];

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
            if (memoryOnly != null)
                gulls = memoryOnly.GetComponentsInChildren<Gull>(true);
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
            var L = CharacterRigPoser.Side.Left;
            var R = CharacterRigPoser.Side.Right;

            // Opening state: black screen, grey beach, wind
            SetPresent();
            SetAtmosphere(null);
            overlay.SetLetterbox(config.letterbox);
            overlay.SetFade(1f);
            SetActive(coin, false);
            SetActive(heldBag, false);
            SetLamp(0f);
            SetWindow(0f);
            actor.Place(Mark("Mark_ShoreStart"));
            actor.SetPose(0f, 0f, 6f);
            actor.SetWalkStyle(config.shoreWalk);
            SetMusic(grim: 1f, dream: 0f, fadeSpeed: 0.35f);
            wavesGain = 1f;
            windTarget = 1f;

            // 1. Trudging along the shore
            BeginBeat(1, "Walk along the shore");
            Vector3[] shorePath = Path("Path_Shore");
            float shoreWalkTime = config.walkAlongShore + config.discovery * 0.5f;
            float shoreSpeed = Mathf.Max(0.2f, actor.PathLength(shorePath) / shoreWalkTime);
            Coroutine walking = StartWalk(shorePath, shoreSpeed);
            cinematicCamera.Shake = 0.6f;
            cinematicCamera.Move(Shot("B1_From"), Shot("B1_To"), config.walkAlongShore + 0.6f);
            cinematicCamera.RackFocus(actor.transform, 0.1f);
            StartCoroutine(overlay.FadeTo(0f, config.fadeInSeconds));
            yield return Until(config.walkAlongShore);

            // 2. Discovery: he stops; focus pulls from him to the turtle; the music stops, a heartbeat
            BeginBeat(2, "Discovery");
            cinematicCamera.Shake = 0.8f;
            cinematicCamera.Move(Shot("B2_From"), Shot("B2_To"), config.discovery);
            cinematicCamera.RackFocus(actor.transform, 0.1f);
            yield return walking;
            Transform turtleBody = deadTurtle != null ? deadTurtle.transform : null;
            if (turtleBody != null)
            {
                StartCoroutine(actor.TurnTowards(turtleBody.position, 1.1f));
                StartCoroutine(actor.LookAt(turtleBody, 0.9f));
                cinematicCamera.RackFocus(turtleBody, 1.6f);
            }
            StartCoroutine(actor.Slump(16f, 1.6f));
            SetMusic(grim: 0f, dream: 0f, fadeSpeed: 0.8f);
            heartbeatTarget = 1f;
            wavesGain = 0.5f;
            windTarget = 0.6f;
            yield return Until(config.discovery);

            // 3. He kneels, lays a hand on the shell, wipes his eyes
            BeginBeat(3, "Kneel");
            cinematicCamera.Shake = 1.1f;
            cinematicCamera.Move(Shot("B3_From"), Shot("B3_To"), config.kneel);
            cinematicCamera.RackFocus(actor.transform, 0.8f);
            // One slow step closer, then down onto one knee
            Transform kneelMark = Mark("Mark_Kneel");
            if (kneelMark != null)
                yield return actor.WalkPath(new[] { kneelMark.position }, 0.45f);
            StartCoroutine(actor.Slump(2f, 1.6f));
            yield return actor.Kneel(1.6f);
            Transform shell = TurtlePart("Shell");
            if (shell != null)
            {
                // A hand on the near edge of the shell
                Vector3 toMan = actor.transform.position - shell.position;
                toMan.y = 0f;
                yield return actor.Reach(L, shell.position + toMan.normalized * 0.28f + Vector3.up * 0.16f, 1.1f);
            }
            yield return Until(Mathf.Max(3.6f, config.kneel - 2.6f));
            Transform head = actor.Poser.Bone("Head");
            if (head != null)
            {
                yield return actor.Reach(R, head.position + actor.transform.forward * 0.16f + Vector3.up * 0.05f, 0.7f);
                yield return new WaitForSeconds(0.5f);
                StartCoroutine(actor.Release(R, 0.8f));
            }
            SetMusic(grim: 0.35f, dream: 0f, fadeSpeed: 0.3f);
            yield return Until(config.kneel);

            // 4. The memory: the lens ripples, colour bleeds in and the beach heals in front of his eyes
            BeginBeat(4, "Memory: the shore");
            heartbeatTarget = 0f;
            StartCoroutine(Ripple(config.rippleSeconds));
            yield return overlay.Flash(config.flashSeconds, config.flashHoldSeconds, 0.9f, () =>
            {
                actor.gameObject.SetActive(false);   // we now see through his eyes
                cinematicCamera.Shake = 0.9f;
                cinematicCamera.Move(Shot("B4_From"), Shot("B4_To"), config.memoryShore);
                if (shoreTurtle != null)
                {
                    cinematicCamera.LookAt(shoreTurtle.transform, Vector3.zero, 0.6f);
                    cinematicCamera.RackFocus(shoreTurtle.transform, 0.5f);
                }
                SetActive(memoryOnly, true);
                SetActive(presentOnly, false);
                SetActive(tangledBag, false);
                SetMusic(grim: 0f, dream: 1f, fadeSpeed: 0.6f);
                wavesGain = 0.8f;
                windTarget = 0.25f;
                pollution.SetPollution(0f); // blends over the controller's transition time: the litter dissolves
            });
            StartCoroutine(BleedColour(1f, 0f, config.colourBleedSeconds));
            yield return Until(config.memoryShore);

            // 5. Underwater dream: the turtle loops around the camera among fish and light, then swims up into it
            BeginBeat(5, "Memory: underwater");
            yield return overlay.Flash(0.25f, 0.1f, 1.1f, () =>
            {
                SetAtmosphere(config.underwater);
                SetUnderwaterBackground(true);
                if (caustics != null) caustics.enabled = true;
                cinematicCamera.Shake = 1f;
                cinematicCamera.Move(Shot("B5_From"), Shot("B5_To"), config.memoryUnderwater);
                if (underwaterTurtle != null)
                {
                    cinematicCamera.LookAt(underwaterTurtle.transform, Vector3.zero, 0.85f);
                    cinematicCamera.RackFocus(underwaterTurtle.transform, 0.3f);
                }
                wavesGain = 0.4f;
                windTarget = 0f;
                underwaterTarget = 1f;
                SetUnderwaterAudio(true);
            });
            yield return Until(config.memoryUnderwater - 1.2f);
            // Into the light
            yield return CinematicActor.Tween(1.2f, t => overlay.SetFlash(t * t));

            // 6. A hard cut back to grey, on a low boom. He works the bag free and keeps it.
            BeginBeat(6, "Snap back");
            SetAtmosphere(null);
            SetUnderwaterBackground(false);
            if (caustics != null) caustics.enabled = false;
            SetActive(memoryOnly, false);
            SetActive(presentOnly, true);
            SetPresent();
            actor.gameObject.SetActive(true);
            actor.SetPose(1f, 0f, 10f);
            actor.Poser.reachLeftWeight = 0f;
            actor.Poser.reachRightWeight = 0f;
            if (turtleBody != null)
                actor.Poser.lookTarget = turtleBody;
            cinematicCamera.ClearLook();
            cinematicCamera.Shake = 1.1f;
            cinematicCamera.Move(Shot("B6_From"), Shot("B6_To"), config.snapBack);
            cinematicCamera.RackFocus(actor.transform, 0.1f);
            cinematicCamera.Jolt(2.2f);
            SetUnderwaterAudio(false);
            underwaterTarget = 0f;
            SetMusic(grim: 0f, dream: 0f, fadeSpeed: 50f); // the dream cuts off
            PlaySfx(config.sting, config.stingVolume);
            wavesGain = 0.6f;
            windTarget = 0.7f;
            overlay.SetFlash(0f);

            yield return Until(1.2f);
            if (tangledBag != null)
            {
                // Both hands to the bag, a tug, and it comes free in his right hand
                Vector3 bagAt = tangledBag.position;
                StartCoroutine(actor.Reach(L, bagAt + actor.transform.right * -0.06f, 1f));
                yield return actor.Reach(R, bagAt + actor.transform.right * 0.06f, 1f);
                yield return new WaitForSeconds(0.25f);
                Vector3 pull = actor.transform.position + actor.transform.forward * 0.35f + Vector3.up * 0.75f;
                StartCoroutine(actor.Reach(L, pull - actor.transform.right * 0.08f, 0.7f));
                StartCoroutine(actor.Reach(R, pull, 0.7f));
                yield return CinematicActor.Follow(tangledBag, () => actor.Poser.Palm(R), 0.35f);
                Burst(sandPuff, bagAt, 18);
                SetActive(tangledBag, false);
                SetActive(heldBag, true);
                actor.Hold(heldBag, R);
                if (heldBag != null && heldBag.TryGetComponent(out Pendulum swing))
                    swing.ResetRest();
                yield return new WaitForSeconds(0.45f);
                StartCoroutine(actor.Release(L, 0.8f));
                StartCoroutine(actor.Release(R, 1.2f));
            }
            yield return Until(config.snapBack - 1.9f);
            StartCoroutine(actor.LookAway(1.2f));
            StartCoroutine(actor.Stand(1.7f));
            actor.SetCarry(14f);
            yield return Until(config.snapBack);

            // 7. Walk home at dusk
            BeginBeat(7, "Walk home");
            float half = config.blackCutSeconds * 0.5f;
            yield return overlay.FadeTo(1f, half);
            SetAtmosphere(config.dusk);
            SetGrim(config.duskGrimWeight);
            pollution.SetPollution(0.85f, instant: true);
            wavesGain = 0f;
            windTarget = 0.45f;
            SetMusic(grim: 0.8f, dream: 0f, fadeSpeed: 0.5f);
            actor.Place(Mark("Mark_HomeStart"));
            actor.SetPose(0f, 0f, 10f);
            actor.SetWalkStyle(config.homeWalk);
            Vector3[] homePath = Path("Path_Home");
            float homeSpeed = Mathf.Max(0.2f, actor.PathLength(homePath) / Mathf.Max(1f, config.walkHome - half));
            StartWalk(homePath, homeSpeed);
            cinematicCamera.Shake = 0.5f;
            cinematicCamera.Move(Shot("B7_From"), Shot("B7_To"), config.walkHome);
            cinematicCamera.RackFocus(actor.transform, 0.1f);
            yield return overlay.FadeTo(0f, half);
            yield return Until(config.walkHome - half);
            yield return overlay.FadeTo(1f, half);

            // 8. The shack: the bag set down beside the coin tin, one coin from his pocket, dawn at the window
            BeginBeat(8, "The shack");
            StopWalk();
            SetAtmosphere(config.interior);
            SetGrim(config.shackGrimWeight);
            windTarget = 0.15f;
            actor.Place(Mark("Mark_Interior"));
            actor.SetPose(0f, 1f, 4f);
            float wide = config.shackInterior * 0.45f;
            cinematicCamera.Shake = 0.6f;
            cinematicCamera.Move(Shot("B8_From"), Shot("B8_To"), wide);
            cinematicCamera.RackFocus(actor.transform, 0.1f);
            yield return overlay.FadeTo(0f, half);
            yield return Until(1.6f);
            Transform bagRest = Mark("Mark_BagRest");
            if (heldBag != null && bagRest != null)
            {
                yield return actor.Reach(R, bagRest.position + Vector3.up * 0.08f, 1.1f);
                actor.SetCarry(0f);
                if (heldBag.TryGetComponent(out Pendulum bagSwing))
                    bagSwing.enabled = false; // it lies still once it is set down
                yield return CinematicActor.Toss(heldBag, bagRest.position, bagRest.rotation, 0.04f, 0.4f);
                StartCoroutine(actor.Release(R, 1f));
            }
            yield return Until(wide);

            cinematicCamera.Shake = 0.4f;
            cinematicCamera.Move(Shot("B8b_From"), Shot("B8b_To"), config.shackInterior - wide);
            Transform tin = Mark("Mark_CoinDrop");
            if (tin != null)
                cinematicCamera.RackFocus(tin, 0.8f);
            Transform coinStart = Mark("Mark_CoinStart");
            if (coin != null && coinStart != null && tin != null)
            {
                // A hand into the coat pocket, out with a coin, over the tin, and let go
                Transform hips = actor.Poser.Bone("Hips");
                Vector3 pocket = hips != null ? hips.position + actor.transform.right * -0.2f + actor.transform.forward * 0.08f : coinStart.position;
                yield return actor.Reach(L, pocket, 0.8f);
                SetActive(coin, true);
                coin.SetParent(actor.Poser.Hand(L), true);
                coin.position = actor.Poser.Palm(L);
                yield return actor.Reach(L, coinStart.position, 1.1f);
                yield return new WaitForSeconds(0.3f);
                yield return CinematicActor.Toss(coin, tin.position, tin.rotation, 0.05f, 0.35f);
                PlaySfx(config.coinClip, 0.9f);
                Burst(coinGlint, tin.position + Vector3.up * 0.04f, 12);
                SetActive(coin, false);
                StartCoroutine(actor.Release(L, 1.2f));
            }
            // Hope: the lamp warms, dawn reaches the window, a little colour comes back, he looks up
            StartCoroutine(actor.Slump(-4f, 2.5f));
            if (windowLight != null)
                StartCoroutine(actor.LookAtPoint(windowLight.transform.position, 2.5f, 0.8f));
            float grimFrom = grimVolume != null ? grimVolume.weight : 0f;
            StartCoroutine(CinematicActor.Tween(4f, t =>
            {
                SetLamp(t);
                SetWindow(t);
                SetGrim(Mathf.Lerp(grimFrom, config.promiseGrimWeight, t));
            }));
            SetMusic(grim: 0f, dream: 0.8f, fadeSpeed: 0.25f);
            yield return Until(config.shackInterior);

            // 9. The promise, word by word, then the title
            BeginBeat(9, "Promise");
            cinematicCamera.Shake = 0.35f;
            cinematicCamera.Move(Shot("B9_From"), Shot("B9_To"), config.promise + 1.5f);
            Transform face = actor.Poser.Bone("Head");
            if (face != null)
                cinematicCamera.RackFocus(face, 0.1f);
            yield return new WaitForSeconds(0.6f);
            yield return overlay.ShowLineWords(config.promiseLine, 0.32f, 0.7f);
            yield return Until(Mathf.Max(BeatTime + 0.8f, config.promise - 1.5f));
            SetMusic(grim: 0f, dream: 0f, fadeSpeed: 0.35f);
            windTarget = 0f;
            yield return overlay.FadeTo(1f, 1.5f);
            overlay.HideText();
            yield return overlay.ShowTitleTracking(config.titleText, 1.6f);
            yield return new WaitForSeconds(Mathf.Max(0.2f, config.title - 2.6f));
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

        Transform TurtlePart(string name)
        {
            if (deadTurtle == null)
                return null;
            foreach (Transform t in deadTurtle.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                    return t;
            }
            return deadTurtle.transform;
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

        static void SetActive(Component c, bool on)
        {
            if (c != null)
                c.gameObject.SetActive(on);
        }

        static void SetActive(GameObject go, bool on)
        {
            if (go != null)
                go.SetActive(on);
        }

        static void Burst(ParticleSystem system, Vector3 at, int count)
        {
            if (system == null)
                return;
            system.transform.position = at;
            system.Emit(count);
        }

        // ---------------------------------------------------------------- world state

        /// <summary>The grey present, instantly: full pollution, grey grade, litter and the dead turtle back.</summary>
        void SetPresent()
        {
            pollution.SetPollution(1f, instant: true);
            SetGrim(1f);
            if (dreamVolume != null)
                dreamVolume.weight = 0f;
            if (transitionVolume != null)
                transitionVolume.weight = 0f;
            SetActive(memoryOnly, false);
            SetActive(presentOnly, true);
            SetActive(tangledBag, !(heldBag != null && heldBag.gameObject.activeSelf));
            SetWaterDream(0f);
        }

        IEnumerator BleedColour(float grimFrom, float grimTo, float seconds)
        {
            yield return CinematicActor.Tween(seconds, t =>
            {
                SetGrim(Mathf.Lerp(grimFrom, grimTo, t));
                if (dreamVolume != null)
                    dreamVolume.weight = Mathf.Lerp(1f - grimFrom, 1f - grimTo, t);
                SetWaterDream(Mathf.Lerp(1f - grimFrom, 1f - grimTo, t));
            });
        }

        IEnumerator Ripple(float seconds)
        {
            if (transitionVolume == null)
                yield break;
            yield return CinematicActor.Tween(seconds, t => transitionVolume.weight = Mathf.Sin(t * Mathf.PI));
            transitionVolume.weight = 0f;
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

        void SetWindow(float dawn)
        {
            if (windowLight == null)
                return;
            windowLight.color = Color.Lerp(config.windowColdColor, config.windowDawnColor, dawn);
            windowLight.intensity = Mathf.Lerp(config.windowColdIntensity, config.windowDawnIntensity, dawn);
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
            windTarget = underwaterTarget = heartbeatTarget = 0f;
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
            windSource = NewSource("Intro_Wind", config.windLoop, loop: true);
            underwaterSource = NewSource("Intro_Underwater", config.underwaterLoop, loop: true);
            heartbeatSource = NewSource("Intro_Heartbeat", config.heartbeat, loop: true);
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

        static void Fade(AudioSource source, float target, float speed, float dt)
        {
            if (source != null)
                source.volume = Mathf.MoveTowards(source.volume, target, speed * dt);
        }

        void TickAudio(float dt)
        {
            if (config == null || grimMusic == null)
                return;

            float music = config.musicVolume * Bus(SoundCategory.Music);
            float step = musicFadeSpeed * Mathf.Max(music, 0.01f);
            Fade(grimMusic, grimTarget * music, step, dt);
            Fade(dreamMusic, dreamTarget * music, step, dt);

            float ambience = Bus(SoundCategory.Ambience);
            float sfx = Bus(SoundCategory.SFX);
            Fade(windSource, windTarget * config.windVolume * ambience, 0.4f, dt);
            Fade(underwaterSource, underwaterTarget * config.underwaterVolume * ambience, 0.8f, dt);
            Fade(heartbeatSource, heartbeatTarget * config.heartbeatVolume * sfx, 0.5f, dt);
            if (footprints != null)
                footprints.VolumeScale = sfx;
            foreach (Gull g in gulls)
                if (g != null) g.VolumeScale = ambience;

            if (wavesGain > 0f && config.waveClips != null && config.waveClips.Length > 0 && Time.time >= nextWave)
            {
                AudioClip clip = config.waveClips[Random.Range(0, config.waveClips.Length)];
                if (clip != null)
                {
                    wavesSource.pitch = Random.Range(0.9f, 1.05f);
                    wavesSource.PlayOneShot(clip, config.wavesVolume * wavesGain * Random.Range(0.75f, 1f) * ambience);
                }
                nextWave = Time.time + Random.Range(config.waveInterval.x, Mathf.Max(config.waveInterval.x, config.waveInterval.y));
            }
        }
    }
}
