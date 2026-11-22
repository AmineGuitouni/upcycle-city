using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace UpcycleCity.Character
{
    /// <summary>
    /// Character Creator screen (UI Toolkit, kit look over the live 3D street backdrop): an appearance card with
    /// Hide / Body / Eyes / Hair tabs, option pills and ringed colour swatches generated from the
    /// <see cref="CharacterPartsLibrary"/>, a turntable (slider or drag), Surprise me!, a Menu pill and a big round
    /// PLAY button (READY in co-op); the camera frames the full body or the head. PLAY saves the look and loads gameplay
    /// (or, in co-op, <see cref="StartRequested"/> handlers turn it into READY); Back on the first step returns
    /// to the Main Menu (<see cref="MenuRequested"/> lets the co-op session leave cleanly first).
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
        [Tooltip("Scene loaded by Start when nothing else handles it (must be in the build profile's scene list).")]
        [SerializeField] string gameplayScene = "EcosystemSandbox";
        [Tooltip("Scene loaded by Back on the first step (the title screen).")]
        [SerializeField] string menuScene = "MainMenu";

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

        VisualElement root, content, panel, cardBody, brand;
        VisualElement[] stepDots;
        Label headerTitle, playLabel;
        Button backButton, nextButton, hideTab;
        bool collapsed, brandHidden;
        Slider turntableSlider;
        bool panelHidden;
        string startLabel = "Start";

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

            // Drag anywhere on the live 3D scene to turn the character
            var stage = new VisualElement();
            stage.AddToClassList("cc-stage");
            stage.RegisterCallback<PointerDownEvent>(OnStagePointerDown);
            stage.RegisterCallback<PointerMoveEvent>(OnStagePointerMove);
            stage.RegisterCallback<PointerUpEvent>(OnStagePointerUp);
            root.Add(stage);

            // Top-left: back to the menu
            backButton = MakeButton(string.Empty, "cc-menu-pill", OnMenuPressed);
            backButton.Add(MakeLabel("\u2039", "cc-menu-arrow"));
            backButton.Add(MakeLabel("MENU", "cc-menu-label"));
            root.Add(backButton);

            // Right: title block
            brand = new VisualElement();
            brand.AddToClassList("cc-brand");
            // Kit wordmark (Art/UI/Logo/logo_upcycle_city.png via USS); text title as fallback when the art is missing
            var logo = new VisualElement();
            logo.AddToClassList("cc-brand-logo");
            brand.Add(logo);
            Label fallbackTitle = MakeLabel("UPCYCLE CITY", "cc-brand-title");
            brand.Add(fallbackTitle);
            logo.RegisterCallback<CustomStyleResolvedEvent>(_ =>
                fallbackTitle.style.display = logo.resolvedStyle.backgroundImage.texture != null
                    || logo.resolvedStyle.backgroundImage.sprite != null
                        ? DisplayStyle.None : DisplayStyle.Flex);
            var stagePill = new VisualElement();
            stagePill.AddToClassList("cc-stage-pill");
            var dot = new VisualElement();
            dot.AddToClassList("cc-stage-dot");
            stagePill.Add(dot);
            stagePill.Add(MakeLabel("STAGE 1 \u2022 BAB BHAR", "cc-stage-text"));
            brand.Add(stagePill);
            brand.Add(MakeLabel("Clean up the city, one bag at a time", "cc-brand-sub"));
            root.Add(brand);

            // Left: appearance card with tabs
            panel = new VisualElement();
            panel.AddToClassList("cc-panel");
            root.Add(panel);

            var tabs = new VisualElement();
            tabs.AddToClassList("cc-tabs");
            hideTab = MakeButton("Hide", "cc-tab", ToggleCollapsed);
            hideTab.AddToClassList("cc-tab--hide");
            tabs.Add(hideTab);
            stepDots = new VisualElement[StepTitles.Length];
            for (int i = 0; i < StepTitles.Length; i++)
            {
                Step target = (Step)i;
                Button tab = MakeButton(StepTitles[i], "cc-tab", () =>
                {
                    SetCollapsed(false);
                    ShowStep(target);
                });
                stepDots[i] = tab;
                tabs.Add(tab);
            }
            panel.Add(tabs);

            cardBody = new VisualElement();
            cardBody.AddToClassList("cc-card-body");
            panel.Add(cardBody);

            cardBody.Add(MakeLabel("Customize Appearance", "cc-title"));
            headerTitle = MakeLabel("Who's cleaning up the city today?", "cc-subtitle");
            cardBody.Add(headerTitle);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("cc-scroll");
            content = new VisualElement();
            content.AddToClassList("cc-content");
            scroll.Add(content);
            cardBody.Add(scroll);

            var footer = new VisualElement();
            footer.AddToClassList("cc-footer");
            var turnRow = new VisualElement();
            turnRow.AddToClassList("cc-turn-row");
            turnRow.Add(MakeLabel("\u21bb", "cc-turn-icon"));
            turntableSlider = new Slider(-180f, 180f);
            turntableSlider.AddToClassList("cc-turntable");
            turntableSlider.SetValueWithoutNotify(yaw);
            turntableSlider.RegisterValueChangedCallback(e =>
            {
                yaw = e.newValue;
                ApplyYaw();
            });
            turnRow.Add(turntableSlider);
            footer.Add(turnRow);
            footer.Add(MakeButton("Surprise me!", "cc-random", Randomize));
            cardBody.Add(footer);

            // Bottom-right: big round PLAY (READY in co-op)
            nextButton = MakeButton(string.Empty, "cc-play", StartGame);
            var triangle = new VisualElement();
            triangle.AddToClassList("cc-play-icon");
            nextButton.Add(triangle);
            playLabel = MakeLabel(PlayText(), "cc-play-label");
            nextButton.Add(playLabel);
            root.Add(nextButton);

            root.RegisterCallback<GeometryChangedEvent>(_ => UpdateOrientationClass());
            root.style.display = panelHidden ? DisplayStyle.None : DisplayStyle.Flex;
            SetCollapsed(collapsed);
        }

        string PlayText() => string.Equals(startLabel, "Start", StringComparison.OrdinalIgnoreCase)
            ? "PLAY"
            : startLabel.ToUpperInvariant();

        void ToggleCollapsed() => SetCollapsed(!collapsed);

        void SetCollapsed(bool value)
        {
            collapsed = value;
            if (cardBody != null)
                cardBody.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
            panel?.EnableInClassList("cc-panel--collapsed", collapsed);
            hideTab?.EnableInClassList("cc-tab--active", collapsed);
            if (stepDots != null)
            {
                for (int i = 0; i < stepDots.Length; i++)
                    stepDots[i].EnableInClassList("cc-tab--active", !collapsed && i == (int)step);
            }
        }

        /// <summary>Horizontal offset of the character in landscape (fraction of screen width, + = right).</summary>
        public void SetLandscapeShift(float shift) => landscapeShift = Mathf.Clamp(shift, -0.3f, 0.3f);

        /// <summary>Shows or hides the right-hand title block (the co-op crew card takes that side).</summary>
        public void SetBrandVisible(bool visible)
        {
            brandHidden = !visible;
            if (brand != null)
                brand.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OnMenuPressed()
        {
            step = Step.Body;
            OnBack();
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
            backButton.SetEnabled(!string.IsNullOrEmpty(menuScene) || MenuRequested != null);
            for (int i = 0; i < stepDots.Length; i++)
                stepDots[i].EnableInClassList("cc-tab--active", !collapsed && i == (int)step);
            if (brand != null)
                brand.style.display = brandHidden ? DisplayStyle.None : DisplayStyle.Flex;
            RebuildContent();
        }

        void OnBack()
        {
            if (step > Step.Body)
            {
                ShowStep(step - 1);
                return;
            }

            if (MenuRequested != null)
            {
                foreach (Delegate d in MenuRequested.GetInvocationList())
                {
                    if (d is Func<CharacterCreatorController, bool> handler && handler(this))
                        return;
                }
            }
            if (!string.IsNullOrEmpty(menuScene))
                SceneManager.LoadScene(menuScene);
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
                if (names.Length == 2)
                    b.AddToClassList("cc-option--half");
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

        /// <summary>
        /// Optional interceptor invoked when the player presses Back/Menu on the first step.
        /// Return true if another system (such as the co-op session leaving to the menu) handles it.
        /// </summary>
        public static event Func<CharacterCreatorController, bool> MenuRequested;

        /// <summary>Default gameplay scene configured on this creator.</summary>
        public string GameplayScene => gameplayScene;

        /// <summary>The look currently being edited (not necessarily saved yet).</summary>
        public CharacterAppearance CurrentAppearance => appearance;

        /// <summary>Label of the final step's confirm button (<c>Start</c> solo, <c>Ready</c> in co-op).</summary>
        public string StartLabel => startLabel;

        /// <summary>Changes the final step's confirm button label (co-op shows <c>Ready</c>).</summary>
        public void SetStartLabel(string label)
        {
            startLabel = string.IsNullOrWhiteSpace(label) ? "Start" : label;
            if (playLabel != null)
                playLabel.text = PlayText();
            nextButton?.EnableInClassList("cc-play--ready", !string.Equals(startLabel, "Start", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Saves the current look and starts (same as pressing the final step's Start/Ready button).</summary>
        public void Confirm() => StartGame();

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
            MenuRequested = null;
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

        [Header("Layout-aware framing")]
        [Tooltip("Panel pixels kept free on the right when the title column is hidden (the co-op crew card sits there).")]
        [SerializeField, Min(0f)] float reservedRightWhenNoTitle = 520f;
        [Tooltip("Width (m) the character needs in the free gap: full body, head close-up.")]
        [SerializeField] Vector2 requiredWidth = new Vector2(1.25f, 0.55f);

        /// <summary>Screen fraction (0..1, left→right) where the character is centred this frame (tests / tooling).</summary>
        public float CharacterScreenFraction { get; private set; } = 0.5f;

        /// <summary>
        /// The horizontal strip (fractions of the screen) between the appearance card and the title column / crew card,
        /// read from the live UI layout so the character never sits under either at any aspect ratio.
        /// </summary>
        bool TryGetFreeRegion(out float left, out float right)
        {
            left = 0f;
            right = 1f;
            if (root == null || root.panel == null)
                return false;
            float width = root.panel.visualTree.layout.width;
            if (float.IsNaN(width) || width < 1f)
                return false;

            bool uiShown = !panelHidden && root.resolvedStyle.display != DisplayStyle.None;
            float margin = width * 0.015f;
            left = uiShown && panel != null && !float.IsNaN(panel.worldBound.xMax) && panel.worldBound.width > 0f
                ? (panel.worldBound.xMax + margin) / width
                : 0.04f;

            bool titleShown = uiShown && brand != null && !brandHidden && brand.worldBound.width > 0f;
            right = titleShown
                ? (brand.worldBound.xMin - margin) / width
                : 1f - reservedRightWhenNoTitle / width;
            if (right <= left + 0.05f)
                right = left + 0.05f;
            return true;
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
            float tanHalf = Mathf.Tan(creatorCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = creatorCamera.aspect;

            Transform cam = creatorCamera.transform;
            Quaternion rot = Quaternion.LookRotation(Vector3.back, Vector3.up); // the character faces +Z

            float fraction = 0.5f + landscapeShift;
            if (aspect >= 1f && TryGetFreeRegion(out float left, out float right))
            {
                // Centre the character in the free gap and pull back until it fits there
                fraction = (left + right) * 0.5f;
                float need = Mathf.Lerp(requiredWidth.x, requiredWidth.y, t);
                float fitDistance = need / Mathf.Max(0.05f, (right - left) * 2f * tanHalf * aspect);
                distance = Mathf.Max(distance, fitDistance);
            }
            CharacterScreenFraction = fraction;

            Vector3 pos = look - rot * Vector3.forward * distance;
            float halfHeight = distance * tanHalf;
            if (aspect >= 1f)
                pos -= rot * Vector3.right * (halfHeight * aspect * 2f * (fraction - 0.5f));
            else
                pos -= rot * Vector3.up * (halfHeight * 0.45f);

            cam.SetPositionAndRotation(pos, rot);
        }
    }
}
