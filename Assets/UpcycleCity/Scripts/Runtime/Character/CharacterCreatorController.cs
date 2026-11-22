using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace UpcycleCity.Character
{
    /// <summary>
    /// Character Creator screen (UI Toolkit): Body / Eyes / Hair steps with Back and Next, option buttons
    /// and colour swatches generated from the <see cref="CharacterPartsLibrary"/>, a turntable (slider or
    /// drag), and a camera that frames the full body or the head. Start saves the look and loads gameplay.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    [AddComponentMenu("Upcycle City/Character/Character Creator Controller")]
    public sealed class CharacterCreatorController : MonoBehaviour
    {
        [SerializeField] CharacterAvatar avatar;
        [Tooltip("Rotated by the turntable; usually the avatar's parent.")]
        [SerializeField] Transform turntable;
        [SerializeField] Camera creatorCamera;
        [SerializeField] StyleSheet styleSheet;
        [Tooltip("Scene loaded by Start (must be in the build profile's scene list).")]
        [SerializeField] string gameplayScene = "PlayerGym";

        [Header("Camera Framing")]
        [SerializeField] Vector3 bodyTarget = new Vector3(0f, 1.0f, 0f);
        [SerializeField, Min(0.5f)] float bodyDistance = 4.7f;
        [SerializeField] Vector3 headTarget = new Vector3(0f, 1.52f, 0f);
        [SerializeField, Min(0.3f)] float headDistance = 1.9f;
        [Tooltip("How far right of centre the character sits in landscape, as a fraction of screen width.")]
        [SerializeField, Range(0f, 0.3f)] float landscapeShift = 0.16f;
        [SerializeField, Min(0.5f)] float framingSpeed = 5f;

        [Header("Turntable")]
        [SerializeField] float startYaw = -20f;
        [SerializeField, Min(0.01f)] float dragDegreesPerPixel = 0.35f;
        [SerializeField, Min(0f)] float idleBobAmount = 0.006f;

        enum Step
        {
            Body,
            Eyes,
            Hair,
        }

        static readonly string[] StepTitles = { "Body", "Eyes", "Hair" };

        CharacterPartsLibrary library;
        CharacterAppearance appearance;
        Step step;
        float yaw;
        float framing; // 0 = full body, 1 = head close-up
        bool dragging;
        float dragStartX, dragStartYaw;

        VisualElement root, content;
        VisualElement[] stepDots;
        Label headerTitle;
        Button backButton, nextButton;
        Slider turntableSlider;
        bool panelHidden;

        void Awake()
        {
            if (creatorCamera == null)
                creatorCamera = Camera.main;
            library = avatar != null ? avatar.Library : null;
        }

        void OnEnable()
        {
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            appearance = CharacterAppearanceStore.Current;
            if (library != null)
                appearance = library.Sanitize(appearance);
            avatar?.Apply(appearance);

            yaw = startYaw;
            BuildUi();
            ShowStep(Step.Body);
            ApplyYaw();
            framing = TargetFraming();
            UpdateCamera(snap: true);
        }

        // ---------------------------------------------------------------- UI construction

        void BuildUi()
        {
            var document = GetComponent<UIDocument>();
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("cc-root");

            var stage = new VisualElement();
            stage.AddToClassList("cc-stage");
            stage.RegisterCallback<PointerDownEvent>(OnStagePointerDown);
            stage.RegisterCallback<PointerMoveEvent>(OnStagePointerMove);
            stage.RegisterCallback<PointerUpEvent>(OnStagePointerUp);
            root.Add(stage);

            root.Add(MakeLabel("Customize Appearance", "cc-title"));
            root.Add(MakeLabel("Who's cleaning up the city today?", "cc-subtitle"));

            var panel = new VisualElement();
            panel.AddToClassList("cc-panel");
            root.Add(panel);

            var header = new VisualElement();
            header.AddToClassList("cc-header");
            backButton = MakeButton("Back", "cc-nav", () => ShowStep(step - 1));
            headerTitle = MakeLabel(string.Empty, "cc-header-title");
            nextButton = MakeButton("Next", "cc-nav", OnNext);
            header.Add(backButton);
            header.Add(headerTitle);
            header.Add(nextButton);
            panel.Add(header);

            var tabs = new VisualElement();
            tabs.AddToClassList("cc-tabs");
            stepDots = new VisualElement[StepTitles.Length];
            for (int i = 0; i < stepDots.Length; i++)
            {
                stepDots[i] = new VisualElement();
                stepDots[i].AddToClassList("cc-tab-dot");
                tabs.Add(stepDots[i]);
            }
            panel.Add(tabs);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("cc-scroll");
            content = new VisualElement();
            content.AddToClassList("cc-content");
            scroll.Add(content);
            panel.Add(scroll);

            var footer = new VisualElement();
            footer.AddToClassList("cc-footer");
            footer.Add(MakeButton("Surprise me!", "cc-random", Randomize));
            panel.Add(footer);

            turntableSlider = new Slider(-180f, 180f);
            turntableSlider.AddToClassList("cc-turntable");
            turntableSlider.SetValueWithoutNotify(yaw);
            turntableSlider.RegisterValueChangedCallback(e =>
            {
                yaw = e.newValue;
                ApplyYaw();
            });
            root.Add(turntableSlider);

            root.RegisterCallback<GeometryChangedEvent>(_ => UpdateOrientationClass());
            root.style.display = panelHidden ? DisplayStyle.None : DisplayStyle.Flex;
        }

        static Label MakeLabel(string text, string cls)
        {
            var label = new Label(text);
            label.AddToClassList(cls);
            return label;
        }

        static Button MakeButton(string text, string cls, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList(cls);
            return button;
        }

        void UpdateOrientationClass()
        {
            bool portrait = Screen.height > Screen.width;
            root.EnableInClassList("cc-root--portrait", portrait);
        }

        // ---------------------------------------------------------------- steps

        void ShowStep(Step next)
        {
            step = (Step)Mathf.Clamp((int)next, 0, StepTitles.Length - 1);
            headerTitle.text = StepTitles[(int)step];
            backButton.SetEnabled(step > Step.Body);

            bool last = step == Step.Hair;
            nextButton.text = last ? "Start" : "Next";
            nextButton.EnableInClassList("cc-nav--start", last);

            for (int i = 0; i < stepDots.Length; i++)
            {
                stepDots[i].EnableInClassList("cc-tab-dot--done", i < (int)step);
                stepDots[i].EnableInClassList("cc-tab-dot--current", i == (int)step);
            }
            RebuildContent();
        }

        void OnNext()
        {
            if (step == Step.Hair)
                StartGame();
            else
                ShowStep(step + 1);
        }

        void RebuildContent()
        {
            content.Clear();
            if (library == null)
            {
                content.Add(MakeLabel("No Character Parts Library assigned to the avatar.", "cc-hint"));
                return;
            }

            switch (step)
            {
                case Step.Body:
                    AddSection("Sex");
                    AddOptions(new[] { "Male", "Female" }, (int)appearance.sex, i => appearance.sex = (CharacterSex)i);
                    AddSection("Skin Tone");
                    AddSwatches(library.skinTones, appearance.skinTone, i => appearance.skinTone = i);
                    AddSection("Outfit Colour");
                    AddSwatches(library.outfitColors, appearance.outfitColor, i => appearance.outfitColor = i);
                    break;

                case Step.Eyes:
                    AddSection("Eyes");
                    AddOptions(Names(library.faceStyles), appearance.eyeStyle, i => appearance.eyeStyle = i);
                    AddSection("Eye Colour");
                    AddSwatches(library.eyeColors, appearance.eyeColor, i => appearance.eyeColor = i);
                    content.Add(MakeLabel("Eye colour shows on Tired, Wide and Squint eyes.", "cc-hint"));
                    break;

                case Step.Hair:
                    AddSection("Hair");
                    AddOptions(Names(library.hairStyles), appearance.hairStyle, i => appearance.hairStyle = i);
                    AddSection("Facial Hair");
                    AddOptions(Names(library.facialHairStyles), appearance.facialHair, i => appearance.facialHair = i);
                    AddSection("Hair Colour");
                    AddSwatches(library.hairColors, appearance.hairColor, i => appearance.hairColor = i);
                    content.Add(MakeLabel("Hair colour also tints the eyebrows and beard.", "cc-hint"));
                    break;
            }
        }

        static string[] Names(CharacterPartsLibrary.Part[] parts)
        {
            var names = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                names[i] = string.IsNullOrWhiteSpace(parts[i].displayName) ? $"Style {i + 1}" : parts[i].displayName;
            return names;
        }

        void AddSection(string title) => content.Add(MakeLabel(title, "cc-section-label"));

        void AddOptions(string[] names, int selected, Action<int> choose)
        {
            var grid = new VisualElement();
            grid.AddToClassList("cc-grid");
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                Button b = MakeButton(names[i], "cc-option", () => Choose(choose, index));
                b.EnableInClassList("cc-option--selected", i == selected);
                grid.Add(b);
            }
            content.Add(grid);
        }

        void AddSwatches(Color[] colors, int selected, Action<int> choose)
        {
            var row = new VisualElement();
            row.AddToClassList("cc-swatches");
            for (int i = 0; i < colors.Length; i++)
            {
                int index = i;
                var swatch = new VisualElement();
                swatch.AddToClassList("cc-swatch");
                swatch.style.backgroundColor = colors[i];
                swatch.EnableInClassList("cc-swatch--selected", i == selected);
                swatch.RegisterCallback<ClickEvent>(_ => Choose(choose, index));
                row.Add(swatch);
            }
            content.Add(row);
        }

        void Choose(Action<int> choose, int index)
        {
            choose(index);
            avatar?.Apply(appearance);
            RebuildContent();
        }

        void Randomize()
        {
            if (library == null)
                return;
            appearance = library.Randomize();
            avatar?.Apply(appearance);
            RebuildContent();
        }

        /// <summary>
        /// Optional interceptor invoked when the player clicks Start after saving their look.
        /// Return true if another screen (such as the Co-op Mode/Lobby menu) handles loading gameplay.
        /// </summary>
        public static event Func<CharacterCreatorController, bool> StartRequested;

        /// <summary>Default gameplay scene configured on this creator.</summary>
        public string GameplayScene => gameplayScene;

        /// <summary>True while the Character Creator panel is visible.</summary>
        public bool IsCreatorPanelVisible => !panelHidden && root != null && root.style.display != DisplayStyle.None;

        /// <summary>Shows or hides the Character Creator panel while keeping the 3D avatar turntable visible.</summary>
        public void SetCreatorPanelVisible(bool visible)
        {
            panelHidden = !visible;
            if (root == null)
                return;
            root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void StartGame()
        {
            CharacterAppearanceStore.Save(appearance);
            if (StartRequested != null)
            {
                foreach (Delegate d in StartRequested.GetInvocationList())
                {
                    if (d is Func<CharacterCreatorController, bool> handler && handler(this))
                        return;
                }
            }
            if (!string.IsNullOrEmpty(gameplayScene))
                SceneManager.LoadScene(gameplayScene);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            StartRequested = null;
        }

        // ---------------------------------------------------------------- turntable

        void OnStagePointerDown(PointerDownEvent e)
        {
            dragging = true;
            dragStartX = e.position.x;
            dragStartYaw = yaw;
            ((VisualElement)e.currentTarget).CapturePointer(e.pointerId);
        }

        void OnStagePointerMove(PointerMoveEvent e)
        {
            if (!dragging)
                return;
            yaw = Mathf.Repeat(dragStartYaw - (e.position.x - dragStartX) * dragDegreesPerPixel + 180f, 360f) - 180f;
            turntableSlider.SetValueWithoutNotify(yaw);
            ApplyYaw();
        }

        void OnStagePointerUp(PointerUpEvent e)
        {
            dragging = false;
            ((VisualElement)e.currentTarget).ReleasePointer(e.pointerId);
        }

        void ApplyYaw()
        {
            if (turntable != null)
                turntable.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // ---------------------------------------------------------------- camera

        float TargetFraming() => step == Step.Body ? 0f : 1f;

        void Update()
        {
            UpdateCamera(snap: false);

            if (avatar != null && idleBobAmount > 0f)
            {
                // A gentle breath so the static pose doesn't feel frozen
                float breath = 1f + Mathf.Sin(Time.time * 2.1f) * idleBobAmount;
                avatar.transform.localScale = new Vector3(1f, breath, 1f);
            }
        }

        void UpdateCamera(bool snap)
        {
            if (creatorCamera == null)
                return;

            float target = TargetFraming();
            framing = snap ? target : Mathf.MoveTowards(framing, target, Time.deltaTime * framingSpeed * 0.5f);
            float t = Mathf.SmoothStep(0f, 1f, framing);

            Vector3 look = Vector3.Lerp(bodyTarget, headTarget, t);
            float distance = Mathf.Lerp(bodyDistance, headDistance, t);

            Transform cam = creatorCamera.transform;
            Quaternion rot = Quaternion.LookRotation(Vector3.back, Vector3.up); // the character faces +Z
            Vector3 pos = look - rot * Vector3.forward * distance;

            // Keep the character clear of the panel: left panel in landscape, bottom panel in portrait
            float halfHeight = distance * Mathf.Tan(creatorCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = creatorCamera.aspect;
            if (aspect >= 1f)
                pos -= rot * Vector3.right * (halfHeight * aspect * 2f * landscapeShift);
            else
                pos -= rot * Vector3.up * (halfHeight * 0.45f);

            cam.SetPositionAndRotation(pos, rot);
        }
    }
}
