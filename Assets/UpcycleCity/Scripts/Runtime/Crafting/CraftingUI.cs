using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UpcycleCity.Audio;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Phone-first UI Toolkit crafting, card inspector, and manual generator window styled by <c>UI/Crafting.uss</c>.
    /// <list type="bullet">
    /// <item><b>Zero per-frame UI rebuilds</b>: card tiles are created once per card list and updated in place via zero-allocation <see cref="CraftingRules.ValidateQuick"/>; progress and generator cooldown are polled with change detection.</item>
    /// <item><b>Phone-ready</b>: portrait + landscape responsive layout, <see cref="Screen.safeArea"/> padding, minimum 16 px text, 48 px+ touch targets, full-screen Card Inspector overlay, and platform-aware close labels.</item>
    /// <item><b>Secure debug bar</b>: workshop debug buttons are only built in dev builds / Editor and only visible when <see cref="CraftingDebugGate.IsDebugActionsAllowed"/> is enabled.</item>
    /// <item><b>Dual mode</b>: opens for both <see cref="CraftingMachine"/> and <see cref="ManualEnergyGenerator"/>, ducking ambience via <see cref="SoundManager"/> like <c>ShopUI</c>.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Crafting UI")]
    public sealed class CraftingUI : MonoBehaviour
    {
        public const string DefaultPanelSettingsResource = "CraftingPanelSettings";
        public const string DefaultStyleSheetResource = "CraftingStyles";
        const float CardHoldInspectSeconds = 0.40f;

        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;
        [SerializeField, Min(2f)] float closeDistance = 7.5f;
        [SerializeField] bool showDebugButtons;

        public static CraftingUI Instance { get; private set; }

        enum ActiveTab
        {
            Primary = 0,
            Stock = 1,
        }

        sealed class CardTileView
        {
            public CraftingCard Card;
            public VisualElement Tile;
            public Label Badge;
            public CraftingFailureKind LastFailure;
            public int LastMissingCount = -1;
            public bool LastSelected;
            public bool Initialized;
        }

        UIDocument document;
        VisualElement root;
        VisualElement window;
        VisualElement accentBar;
        Label titleLabel, levelBadgeLabel, subtitleLabel, energyPillLabel, coinsPillLabel;
        Button upgradeButton, primaryTabButton, stockTabButton, filterButton, inspectButton, executeButton, leaveButton;
        VisualElement progressWrap, progressFill;
        Label progressLabel;
        VisualElement cardsBody, cardsGrid, detailPane, detailContent;
        VisualElement generatorPane;
        Label genStatsLabel, genSubLabel, genUpgradeInfoLabel;
        Button genCrankButton, genUpgradeButton;
        ScrollView stockPane;
        VisualElement stockContainer;
        VisualElement debugBar;
        VisualElement footer;
        Label statusBanner, logLine;

        // Full-screen Card Inspector overlay
        VisualElement inspectorOverlay;
        Label inspectorTitle;
        VisualElement inspectorArtWrap;
        VisualElement inspectorDataContent;
        Button inspectorExecuteButton, inspectorCloseButton;
        CraftingCard inspectedCard;

        CraftingMachine currentMachine;
        ManualEnergyGenerator currentGenerator;
        PlayerInteractor currentInteractor;
        TeamMaterialStock boundStock;
        CraftingCard selectedCard;
        ActiveTab activeTab = ActiveTab.Primary;
        bool showAllCards;
        bool ambienceDuckPushed;

        // Zero-alloc tile & progress caches
        readonly List<CraftingCard> displayedCards = new List<CraftingCard>(16);
        readonly List<CardTileView> cardTiles = new List<CardTileView>(16);
        readonly List<MissingRequirement> scratchMissing = new List<MissingRequirement>(8);
        readonly List<string> recentLog = new List<string>(4);
        readonly StringBuilder sb = new StringBuilder(192);

        int lastProgressPercent = -1;
        int lastProgressTenths = -1;
        CraftingCard lastProgressCard;
        int lastGenCooldownTenths = -2;

        // Hold-to-inspect gesture state (no allocations)
        CraftingCard holdCard;
        float holdStartTime;
        bool holdTriggeredInspect;

        // Responsive layout & safe-area cache
        int lastScreenWidth = -1;
        int lastScreenHeight = -1;
        Rect lastSafeArea;
        float lastPanelWidth = -1f;
        float lastPanelHeight = -1f;

        public bool IsOpen { get; private set; }
        public bool IsGeneratorMode => currentGenerator != null;
        public bool IsCardInspectorOpen { get; private set; }
        public CraftingMachine CurrentMachine => currentMachine;
        public ManualEnergyGenerator CurrentGenerator => currentGenerator;
        public CraftingCard SelectedCard => selectedCard;
        public CraftingCard InspectedCard => inspectedCard;
        public string LastStatusMessage { get; private set; } = string.Empty;
        public bool LastStatusSucceeded { get; private set; }
        public int ActiveCardTileCount => cardTiles.Count;
        public bool IsDebugBarVisible => debugBar != null && debugBar.style.display != DisplayStyle.None;

        public VisualElement GetCardTileElement(int index) =>
            index >= 0 && index < cardTiles.Count ? cardTiles[index].Tile : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        public void Configure(PanelSettings settings, StyleSheet sheet, bool debugButtons = false)
        {
            panelSettings = settings;
            styleSheet = sheet;
            showDebugButtons = debugButtons;
            if (document != null && settings != null)
                document.panelSettings = settings;
            if (root != null && sheet != null && !root.styleSheets.Contains(sheet))
                root.styleSheets.Add(sheet);
            UpdateDebugBarVisibility();
        }

        /// <summary>Returns the scene's <see cref="CraftingUI"/>, creating a self-configured instance if none exists.</summary>
        public static CraftingUI EnsureInstance()
        {
            if (Instance != null)
                return Instance;

            CraftingUI existing = FindAnyObjectByType<CraftingUI>();
            if (existing != null)
                return existing;

            var go = new GameObject("CraftingUI");
            var ui = go.AddComponent<CraftingUI>();
            return ui;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            ResolveFallbackAssets();

            var docGo = new GameObject("CraftingDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 55;
            docGo.SetActive(true);

            BuildTree();
            Hide();
            CraftingDebugGate.Changed += OnDebugGateChanged;
        }

        void OnDisable()
        {
            PopAmbienceDuck();
        }

        void OnDestroy()
        {
            CraftingDebugGate.Changed -= OnDebugGateChanged;
            PopAmbienceDuck();
            if (Instance == this)
                Instance = null;
            UnbindEvents();
        }

        void OnDebugGateChanged() => UpdateDebugBarVisibility();

        void ResolveFallbackAssets()
        {
            CraftingUIConfig config = CraftingUIConfig.LoadDefault();
            if (config != null)
            {
                if (panelSettings == null)
                    panelSettings = config.PanelSettings;
                if (styleSheet == null)
                    styleSheet = config.StyleSheet;
            }

            if (panelSettings == null)
                panelSettings = Resources.Load<PanelSettings>(DefaultPanelSettingsResource);
            if (panelSettings == null)
            {
                UIDocument anyDoc = FindAnyObjectByType<UIDocument>();
                if (anyDoc != null && anyDoc.panelSettings != null)
                    panelSettings = anyDoc.panelSettings;
            }
            if (panelSettings == null)
            {
                panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panelSettings.referenceResolution = new Vector2Int(1080, 608);
                panelSettings.match = 0.5f;
            }

            if (styleSheet == null)
                styleSheet = Resources.Load<StyleSheet>(DefaultStyleSheetResource);
        }

        // ------------------------------------------------------------ Build UI Tree

        void BuildTree()
        {
            root = document.rootVisualElement;
            root.Clear();
            cardTiles.Clear();
            debugBar = null;

            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("craft-root");

            var backdrop = new VisualElement();
            backdrop.AddToClassList("craft-backdrop");
            root.Add(backdrop);

            window = new VisualElement();
            window.AddToClassList("craft-window");
            root.Add(window);

            // Header
            var header = new VisualElement();
            header.AddToClassList("craft-header");
            window.Add(header);

            accentBar = new VisualElement();
            accentBar.AddToClassList("craft-accent");
            header.Add(accentBar);

            var headerTitles = new VisualElement();
            headerTitles.AddToClassList("craft-header-titles");
            header.Add(headerTitles);

            var titleRow = new VisualElement();
            titleRow.AddToClassList("craft-title-row");
            headerTitles.Add(titleRow);

            titleLabel = new Label("MANUAL MACHINE");
            titleLabel.AddToClassList("craft-title");
            titleRow.Add(titleLabel);

            levelBadgeLabel = new Label("LVL 0");
            levelBadgeLabel.AddToClassList("craft-level-badge");
            titleRow.Add(levelBadgeLabel);

            subtitleLabel = new Label("Insert a crafting card to refine or build.");
            subtitleLabel.AddToClassList("craft-subtitle");
            headerTitles.Add(subtitleLabel);

            var pills = new VisualElement();
            pills.AddToClassList("craft-header-pills");
            header.Add(pills);

            var nrgPill = new VisualElement();
            nrgPill.AddToClassList("craft-pill");
            var nrgDot = new VisualElement();
            nrgDot.AddToClassList("craft-pill-dot");
            nrgDot.AddToClassList("craft-pill-dot--nrg");
            nrgPill.Add(nrgDot);
            energyPillLabel = new Label("0 NRG");
            energyPillLabel.AddToClassList("craft-pill-text");
            nrgPill.Add(energyPillLabel);
            pills.Add(nrgPill);

            var coinPill = new VisualElement();
            coinPill.AddToClassList("craft-pill");
            var coinDot = new VisualElement();
            coinDot.AddToClassList("craft-pill-dot");
            coinDot.AddToClassList("craft-pill-dot--coin");
            coinPill.Add(coinDot);
            coinsPillLabel = new Label("0 Coins");
            coinsPillLabel.AddToClassList("craft-pill-text");
            coinPill.Add(coinsPillLabel);
            pills.Add(coinPill);

            upgradeButton = new Button(OnUpgradeClicked) { text = "Upgrade → Lv 1" };
            upgradeButton.AddToClassList("craft-upgrade-btn");
            pills.Add(upgradeButton);

            // Toolbar (Tabs + Card Filter)
            var toolbar = new VisualElement();
            toolbar.AddToClassList("craft-toolbar");
            window.Add(toolbar);

            var tabs = new VisualElement();
            tabs.AddToClassList("craft-tabs");
            toolbar.Add(tabs);

            primaryTabButton = new Button(() => SetActiveTab(ActiveTab.Primary)) { text = "Crafting Cards" };
            primaryTabButton.AddToClassList("craft-tab");
            tabs.Add(primaryTabButton);

            stockTabButton = new Button(() => SetActiveTab(ActiveTab.Stock)) { text = "Team Stock" };
            stockTabButton.AddToClassList("craft-tab");
            tabs.Add(stockTabButton);

            filterButton = new Button(ToggleShowAllCards) { text = "This Machine" };
            filterButton.AddToClassList("craft-filter-btn");
            toolbar.Add(filterButton);

            // Processing Progress Bar (visible when machine is running)
            progressWrap = new VisualElement();
            progressWrap.AddToClassList("craft-progress-wrap");
            progressWrap.style.display = DisplayStyle.None;
            window.Add(progressWrap);

            progressLabel = new Label("Processing...");
            progressLabel.AddToClassList("craft-progress-label");
            progressWrap.Add(progressLabel);

            var track = new VisualElement();
            track.AddToClassList("craft-progress-track");
            progressWrap.Add(track);

            progressFill = new VisualElement();
            progressFill.AddToClassList("craft-progress-fill");
            track.Add(progressFill);

            // Main Body
            var body = new VisualElement();
            body.AddToClassList("craft-body");
            window.Add(body);

            // Cards View (CraftingMachine mode)
            cardsBody = new VisualElement();
            cardsBody.AddToClassList("craft-cards-body");
            body.Add(cardsBody);

            var cardsScroll = new ScrollView(ScrollViewMode.Vertical);
            cardsScroll.AddToClassList("craft-cards-pane");
            cardsBody.Add(cardsScroll);

            cardsGrid = new VisualElement();
            cardsGrid.AddToClassList("craft-cards-grid");
            cardsScroll.Add(cardsGrid);

            detailPane = new VisualElement();
            detailPane.AddToClassList("craft-detail-pane");
            cardsBody.Add(detailPane);

            var detailScroll = new ScrollView(ScrollViewMode.Vertical);
            detailScroll.AddToClassList("craft-detail-scroll");
            detailPane.Add(detailScroll);

            detailContent = new VisualElement();
            detailScroll.Add(detailContent);

            var detailActions = new VisualElement();
            detailActions.AddToClassList("craft-detail-actions");
            detailPane.Add(detailActions);

            inspectButton = new Button(OnInspectSelectedClicked) { text = "Inspect Card" };
            inspectButton.AddToClassList("craft-inspect-btn");
            detailActions.Add(inspectButton);

            executeButton = new Button(OnExecuteClicked) { text = "Execute at Machine" };
            executeButton.AddToClassList("craft-execute-btn");
            detailActions.Add(executeButton);

            // Generator View (ManualEnergyGenerator mode)
            generatorPane = new VisualElement();
            generatorPane.AddToClassList("craft-generator-pane");
            generatorPane.style.display = DisplayStyle.None;
            body.Add(generatorPane);

            genStatsLabel = new Label("Pedal Power: +15 NRG per crank");
            genStatsLabel.AddToClassList("craft-gen-stats");
            generatorPane.Add(genStatsLabel);

            genSubLabel = new Label("Tap Pedal Crank to charge the shared team energy pool for all workshop machines.");
            genSubLabel.AddToClassList("craft-gen-sub");
            generatorPane.Add(genSubLabel);

            genCrankButton = new Button(OnGeneratorCrankClicked) { text = "Pedal Crank (+15 NRG)" };
            genCrankButton.AddToClassList("craft-gen-crank-btn");
            generatorPane.Add(genCrankButton);

            var genUpgradeCard = new VisualElement();
            genUpgradeCard.AddToClassList("craft-gen-upgrade-card");
            generatorPane.Add(genUpgradeCard);

            genUpgradeInfoLabel = new Label("Upgrade for higher NRG per crank.");
            genUpgradeInfoLabel.AddToClassList("craft-gen-sub");
            genUpgradeInfoLabel.style.marginBottom = 10;
            genUpgradeCard.Add(genUpgradeInfoLabel);

            genUpgradeButton = new Button(OnUpgradeClicked) { text = "Upgrade Generator" };
            genUpgradeButton.AddToClassList("craft-upgrade-btn");
            genUpgradeCard.Add(genUpgradeButton);

            // Team Material Stock View
            stockPane = new ScrollView(ScrollViewMode.Vertical);
            stockPane.AddToClassList("craft-stock-pane");
            stockPane.style.display = DisplayStyle.None;
            body.Add(stockPane);

            stockContainer = new VisualElement();
            stockPane.Add(stockContainer);

            // Footer: Status Banner + Activity Log + Close/Leave
            footer = new VisualElement();
            footer.AddToClassList("craft-footer");
            window.Add(footer);

            var statusCol = new VisualElement();
            statusCol.AddToClassList("craft-status-col");
            footer.Add(statusCol);

            statusBanner = new Label("Select a card and tap Execute at Machine.");
            statusBanner.AddToClassList("craft-status-banner");
            statusCol.Add(statusBanner);

            logLine = new Label(string.Empty);
            logLine.AddToClassList("craft-log-line");
            statusCol.Add(logLine);

            leaveButton = new Button(Close) { text = GetCloseButtonLabel() };
            leaveButton.AddToClassList("craft-leave-btn");
            footer.Add(leaveButton);

            // Full-Screen Card Inspector Overlay (covers the window when inspecting a card)
            BuildCardInspectorOverlay();

            // Workshop Debug Controls (only built in dev build / Editor when requested)
            UpdateDebugBarVisibility();
            ApplyResponsiveLayout(force: true);
        }

        void BuildCardInspectorOverlay()
        {
            inspectorOverlay = new VisualElement();
            inspectorOverlay.AddToClassList("craft-inspector-overlay");
            inspectorOverlay.style.display = DisplayStyle.None;
            root.Add(inspectorOverlay);

            var panel = new VisualElement();
            panel.AddToClassList("craft-inspector-panel");
            inspectorOverlay.Add(panel);

            var header = new VisualElement();
            header.AddToClassList("craft-inspector-header");
            panel.Add(header);

            inspectorTitle = new Label("CRAFTING CARD INSPECTOR");
            inspectorTitle.AddToClassList("craft-inspector-title");
            header.Add(inspectorTitle);

            inspectorCloseButton = new Button(CloseCardInspector) { text = "✕ Close" };
            inspectorCloseButton.AddToClassList("craft-inspector-close-btn");
            header.Add(inspectorCloseButton);

            var body = new VisualElement();
            body.AddToClassList("craft-inspector-body");
            panel.Add(body);

            inspectorArtWrap = new VisualElement();
            inspectorArtWrap.AddToClassList("craft-inspector-art-wrap");
            body.Add(inspectorArtWrap);

            var dataScroll = new ScrollView(ScrollViewMode.Vertical);
            dataScroll.AddToClassList("craft-inspector-data-scroll");
            body.Add(dataScroll);

            inspectorDataContent = new VisualElement();
            dataScroll.Add(inspectorDataContent);

            var inspFooter = new VisualElement();
            inspFooter.AddToClassList("craft-inspector-footer");
            panel.Add(inspFooter);

            inspectorExecuteButton = new Button(OnInspectorExecuteClicked) { text = "Execute at Machine" };
            inspectorExecuteButton.AddToClassList("craft-execute-btn");
            inspFooter.Add(inspectorExecuteButton);
        }

        void UpdateDebugBarVisibility()
        {
            if (window == null || footer == null)
                return;

            bool canExist = CraftingDebugGate.IsDevEnvironment && showDebugButtons;
            bool shouldShow = canExist && CraftingDebugGate.IsDebugActionsAllowed;

            if (!canExist)
            {
                if (debugBar != null)
                {
                    debugBar.RemoveFromHierarchy();
                    debugBar = null;
                }
                return;
            }

            if (debugBar == null)
            {
                debugBar = new VisualElement();
                debugBar.AddToClassList("craft-debug-bar");

                var dbgLbl = new Label("WORKSHOP DEBUG:");
                dbgLbl.AddToClassList("craft-debug-label");
                debugBar.Add(dbgLbl);

                AddDebugButton(debugBar, "Grant Starter Kit", OnDebugGrantStarterKit, accent: true);
                AddDebugButton(debugBar, "+50 NRG", OnDebugAddEnergy, accent: false);
                AddDebugButton(debugBar, "Debug Lv +", OnDebugUpgradeLevel, accent: false);
                AddDebugButton(debugBar, "Debug Lv −", OnDebugDowngradeLevel, accent: false);
                AddDebugButton(debugBar, "Clear Stock", OnDebugClearStock, accent: false);

                int footerIndex = window.IndexOf(footer);
                if (footerIndex >= 0)
                    window.Insert(footerIndex, debugBar);
                else
                    window.Add(debugBar);
            }

            debugBar.style.display = shouldShow ? DisplayStyle.Flex : DisplayStyle.None;
        }

        static void AddDebugButton(VisualElement parent, string label, Action onClick, bool accent)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList("craft-debug-btn");
            if (accent)
                btn.AddToClassList("craft-debug-btn--accent");
            parent.Add(btn);
        }

        static string GetCloseButtonLabel() =>
            Application.isMobilePlatform || Touchscreen.current != null ? "✕ Close" : "Leave  (Esc)";

        // ------------------------------------------------------------ Open / Close

        /// <summary>Opens the crafting window bound to <paramref name="machine"/>.</summary>
        public void Open(CraftingMachine machine, PlayerInteractor interactor = null)
        {
            if (machine == null)
                return;

            UnbindEvents();
            CloseCardInspector();

            currentMachine = machine;
            currentGenerator = null;
            currentInteractor = interactor ?? PlayerContext.ActiveInteractor;
            boundStock = TeamMaterialStock.EnsureInstance(machine.Database);
            activeTab = ActiveTab.Primary;
            showAllCards = false;
            selectedCard = null;
            ResetProgressCache();

            BindEvents();
            PickDefaultSelectedCard();
            UpdateDebugBarVisibility();
            if (leaveButton != null)
                leaveButton.text = GetCloseButtonLabel();
            ApplyResponsiveLayout(force: true);
            RefreshAll();
            SetNeutralStatus("Select a card and tap Execute at Machine.");

            root.style.display = DisplayStyle.Flex;
            IsOpen = true;
            PushAmbienceDuck();
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.UI);
        }

        /// <summary>Opens the generator mode bound to <paramref name="generator"/> (phone + desktop crank &amp; upgrade).</summary>
        public void OpenGenerator(ManualEnergyGenerator generator, PlayerInteractor interactor = null)
        {
            if (generator == null)
                return;

            UnbindEvents();
            CloseCardInspector();

            currentMachine = null;
            currentGenerator = generator;
            currentInteractor = interactor ?? PlayerContext.ActiveInteractor;
            boundStock = TeamMaterialStock.EnsureInstance(generator.Database);
            activeTab = ActiveTab.Primary;
            showAllCards = false;
            selectedCard = null;
            ResetProgressCache();
            lastGenCooldownTenths = -2;

            BindEvents();
            UpdateDebugBarVisibility();
            if (leaveButton != null)
                leaveButton.text = GetCloseButtonLabel();
            ApplyResponsiveLayout(force: true);
            RefreshAll();
            SetNeutralStatus("Tap Pedal Crank to generate shared team NRG.");

            root.style.display = DisplayStyle.Flex;
            IsOpen = true;
            PushAmbienceDuck();
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.UI);
        }

        /// <summary>Closes the crafting window and restores Gameplay input mode.</summary>
        public void Close()
        {
            if (!IsOpen)
                return;

            CloseCardInspector();
            PopAmbienceDuck();
            Hide();
            UnbindEvents();
            currentMachine = null;
            currentGenerator = null;
            currentInteractor = null;
            selectedCard = null;
            holdCard = null;
            holdTriggeredInspect = false;
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.Gameplay);
        }

        void Hide()
        {
            IsOpen = false;
            if (root != null)
                root.style.display = DisplayStyle.None;
        }

        void PushAmbienceDuck()
        {
            if (ambienceDuckPushed)
                return;

            float ambienceDuck = SoundManager.HasInstance && SoundManager.Instance.Library != null
                ? SoundManager.Instance.Library.ShopOpenAmbienceDuck
                : 0f;
            SoundManager.PushAmbienceDuck(this, ambienceDuck);
            ambienceDuckPushed = true;
        }

        void PopAmbienceDuck()
        {
            if (!ambienceDuckPushed)
                return;
            ambienceDuckPushed = false;
            SoundManager.PopAmbienceDuck(this);
        }

        void BindEvents()
        {
            if (boundStock != null)
                boundStock.Changed += OnStockChanged;
            if (currentMachine != null)
            {
                currentMachine.StateChanged += OnMachineStateChanged;
                currentMachine.CardCompleted += OnMachineCardCompleted;
                currentMachine.CompletionSynced += OnMachineCompletionSynced;
            }
            if (currentGenerator != null)
                currentGenerator.StateChanged += OnGeneratorStateChanged;
            if (currentInteractor != null && currentInteractor.Inventory != null)
                currentInteractor.Inventory.Changed += RefreshHeader;
        }

        void UnbindEvents()
        {
            if (boundStock != null)
                boundStock.Changed -= OnStockChanged;
            if (currentMachine != null)
            {
                currentMachine.StateChanged -= OnMachineStateChanged;
                currentMachine.CardCompleted -= OnMachineCardCompleted;
                currentMachine.CompletionSynced -= OnMachineCompletionSynced;
            }
            if (currentGenerator != null)
                currentGenerator.StateChanged -= OnGeneratorStateChanged;
            if (currentInteractor != null && currentInteractor.Inventory != null)
                currentInteractor.Inventory.Changed -= RefreshHeader;
            boundStock = null;
        }

        void OnStockChanged()
        {
            if (!IsOpen)
                return;
            RefreshAll();
        }

        void OnMachineStateChanged(CraftingMachine machine)
        {
            if (!IsOpen || machine != currentMachine)
                return;
            ResetProgressCache();
            RefreshProgress();
            RefreshHeader();
            RefreshCardsAndDetail();
            if (IsCardInspectorOpen && inspectedCard != null)
                PopulateCardInspector(inspectedCard);
        }

        void OnGeneratorStateChanged(ManualEnergyGenerator generator)
        {
            if (!IsOpen || generator != currentGenerator)
                return;
            lastGenCooldownTenths = -2;
            RefreshHeader();
            RefreshGeneratorPane();
        }

        void OnMachineCardCompleted(CraftingMachine machine, CraftingCard card, GameObject spawned)
        {
            if (!IsOpen || machine != currentMachine || card == null)
                return;
            string msg = CraftingRules.BuildSuccessMessage(card, startedOnly: false);
            if (spawned != null)
                msg += " (placed in workshop)";
            SetStatus(msg, good: true);
            RefreshAll();
        }

        void OnMachineCompletionSynced(CraftingMachine machine, CraftingCard card)
        {
            // On host, OnMachineCardCompleted already displayed the banner with spawn info.
            if (GameAuthority.IsAuthoritative)
                return;
            if (!IsOpen || machine != currentMachine || card == null)
                return;
            SetStatus(CraftingRules.BuildSuccessMessage(card, startedOnly: false), good: true);
            RefreshAll();
        }

        void Update()
        {
            if (!IsOpen)
                return;

            ApplyResponsiveLayout(force: false);

            // Long-press on a card tile opens full-screen inspector
            if (holdCard != null && !holdTriggeredInspect && Time.unscaledTime - holdStartTime >= CardHoldInspectSeconds)
            {
                holdTriggeredInspect = true;
                CraftingCard cardToInspect = holdCard;
                holdCard = null;
                SelectCard(cardToInspect);
                OpenCardInspector(cardToInspect);
            }

            if (currentMachine != null && currentMachine.IsProcessing)
                RefreshProgress();
            else if (currentGenerator != null && activeTab == ActiveTab.Primary)
                RefreshGeneratorCooldownOnly();

            bool cancel = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                          || (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);

            if (cancel)
            {
                if (IsCardInspectorOpen)
                    CloseCardInspector();
                else
                    Close();
                return;
            }

            Transform targetTransform = currentMachine != null
                ? currentMachine.transform
                : (currentGenerator != null ? currentGenerator.transform : null);

            bool tooFar = targetTransform == null
                          || (PlayerContext.Active != null
                              && (PlayerContext.Active.transform.position - targetTransform.position).sqrMagnitude > closeDistance * closeDistance);

            if (tooFar)
                Close();
        }

        void ApplyResponsiveLayout(bool force)
        {
            if (root == null)
                return;

            int sw = Screen.width;
            int sh = Screen.height;
            Rect safe = Screen.safeArea;
            float pw = root.panel != null ? root.panel.visualTree.layout.width : 0f;
            float ph = root.panel != null ? root.panel.visualTree.layout.height : 0f;

            if (!force
                && sw == lastScreenWidth
                && sh == lastScreenHeight
                && safe == lastSafeArea
                && Mathf.Approximately(pw, lastPanelWidth)
                && Mathf.Approximately(ph, lastPanelHeight))
            {
                return;
            }

            lastScreenWidth = sw;
            lastScreenHeight = sh;
            lastSafeArea = safe;
            lastPanelWidth = pw;
            lastPanelHeight = ph;

            bool isPortrait = sh > sw;
            root.EnableInClassList("craft-root--portrait", isPortrait);

            if (sw <= 0 || sh <= 0)
                return;

            float scaleX = pw > 1f ? pw / sw : 1f;
            float scaleY = ph > 1f ? ph / sh : 1f;

            const float BaseOuterPad = 8f;
            float leftInset = Mathf.Max(0f, safe.xMin) * scaleX + BaseOuterPad;
            float rightInset = Mathf.Max(0f, sw - safe.xMax) * scaleX + BaseOuterPad;
            float bottomInset = Mathf.Max(0f, safe.yMin) * scaleY + BaseOuterPad;
            float topInset = Mathf.Max(0f, sh - safe.yMax) * scaleY + BaseOuterPad;

            root.style.paddingLeft = leftInset;
            root.style.paddingRight = rightInset;
            root.style.paddingTop = topInset;
            root.style.paddingBottom = bottomInset;
        }

        // ------------------------------------------------------------ Tabs, Selection & Inspector

        public void SetActiveTab(int tabIndex) => SetActiveTab(tabIndex == 1 ? ActiveTab.Stock : ActiveTab.Primary);

        void SetActiveTab(ActiveTab tab)
        {
            activeTab = tab;
            RefreshTabVisibility();
            if (activeTab == ActiveTab.Stock)
                RebuildStockView();
            else if (currentGenerator != null)
                RefreshGeneratorPane();
            else
                RefreshCardsAndDetail();
        }

        void ToggleShowAllCards()
        {
            showAllCards = !showAllCards;
            RefreshTabVisibility();
            RefreshCardsAndDetail();
        }

        /// <summary>Selects a card programmatically (updates tiles in place without rebuilding them).</summary>
        public void SelectCard(CraftingCard card)
        {
            selectedCard = card;
            RefreshCardsAndDetail();
        }

        /// <summary>Opens the full-screen Card Inspector overlay for <paramref name="card"/>.</summary>
        public void OpenCardInspector(CraftingCard card)
        {
            if (card == null || inspectorOverlay == null)
                return;

            inspectedCard = card;
            IsCardInspectorOpen = true;
            PopulateCardInspector(card);
            inspectorOverlay.style.display = DisplayStyle.Flex;
        }

        /// <summary>Closes the full-screen Card Inspector overlay.</summary>
        public void CloseCardInspector()
        {
            IsCardInspectorOpen = false;
            inspectedCard = null;
            if (inspectorOverlay != null)
                inspectorOverlay.style.display = DisplayStyle.None;
        }

        void PickDefaultSelectedCard()
        {
            CraftingDatabase db = currentMachine != null ? currentMachine.Database : boundStock?.Database;
            if (db == null)
                return;

            displayedCards.Clear();
            if (currentMachine?.Definition != null)
                db.GetCardsForMachine(currentMachine.Definition, displayedCards);

            if (displayedCards.Count > 0)
                selectedCard = displayedCards[0];
            else if (db.Cards.Count > 0)
                selectedCard = db.Cards[0];
        }

        // ------------------------------------------------------------ Refresh UI

        void RefreshAll()
        {
            if (root == null || (currentMachine == null && currentGenerator == null))
                return;

            RefreshHeader();
            RefreshTabVisibility();
            RefreshProgress();

            if (activeTab == ActiveTab.Stock)
                RebuildStockView();
            else if (currentGenerator != null)
                RefreshGeneratorPane();
            else
                RefreshCardsAndDetail();

            if (IsCardInspectorOpen && inspectedCard != null)
                PopulateCardInspector(inspectedCard);
        }

        void RefreshHeader()
        {
            MachineDefinition def = currentMachine != null
                ? currentMachine.Definition
                : (currentGenerator != null ? currentGenerator.Definition : null);

            string displayName = currentMachine != null
                ? currentMachine.DisplayName
                : (currentGenerator != null ? currentGenerator.DisplayName : "STATION");

            int level = currentMachine != null
                ? currentMachine.Level
                : (currentGenerator != null ? currentGenerator.Level : 0);

            int maxLevel = currentMachine != null
                ? currentMachine.MaxLevel
                : (currentGenerator != null ? currentGenerator.MaxLevel : 0);

            accentBar.style.backgroundColor = def != null ? def.AccentColor : new Color(0.18f, 0.56f, 0.54f);
            titleLabel.text = displayName.ToUpperInvariant();
            levelBadgeLabel.text = $"LVL {level}";

            if (currentGenerator != null)
            {
                subtitleLabel.text = def != null && !string.IsNullOrWhiteSpace(def.Description)
                    ? def.Description
                    : "Manual pedal generator — crank to produce shared team NRG.";
            }
            else
            {
                subtitleLabel.text = def != null && !string.IsNullOrWhiteSpace(def.Description)
                    ? def.Description
                    : "Manual upcycling station — drop trash in the hopper or execute a card.";
            }

            int nrg = boundStock != null ? boundStock.Energy : 0;
            int coins = GameAuthority.Current.TeamCoins;
            energyPillLabel.text = $"{nrg} NRG";
            coinsPillLabel.text = $"{coins} Coins";

            int nextLvl = level + 1;
            if (nextLvl > maxLevel)
            {
                upgradeButton.text = $"Max Lv ({maxLevel})";
                upgradeButton.SetEnabled(false);
            }
            else if (def != null && def.TryGetUpgradeCost(nextLvl, out MachineUpgradeCost cost))
            {
                string fullCost = MachineUpgradeHelper.FormatFullCost(in cost);
                upgradeButton.text = $"Upgrade Lv {nextLvl} ({fullCost})";
                upgradeButton.SetEnabled(true);
            }
            else
            {
                upgradeButton.text = $"Upgrade → Lv {nextLvl}";
                upgradeButton.SetEnabled(true);
            }
        }

        void RefreshTabVisibility()
        {
            bool isGen = currentGenerator != null;
            primaryTabButton.text = isGen ? "Generator" : "Cards";
            primaryTabButton.EnableInClassList("craft-tab--active", activeTab == ActiveTab.Primary);
            stockTabButton.EnableInClassList("craft-tab--active", activeTab == ActiveTab.Stock);

            bool showFilter = !isGen && activeTab == ActiveTab.Primary;
            filterButton.style.display = showFilter ? DisplayStyle.Flex : DisplayStyle.None;
            filterButton.text = showAllCards ? "All Cards" : "This Machine";
            filterButton.EnableInClassList("craft-filter-btn--active", showAllCards);

            cardsBody.style.display = !isGen && activeTab == ActiveTab.Primary ? DisplayStyle.Flex : DisplayStyle.None;
            generatorPane.style.display = isGen && activeTab == ActiveTab.Primary ? DisplayStyle.Flex : DisplayStyle.None;
            stockPane.style.display = activeTab == ActiveTab.Stock ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ResetProgressCache()
        {
            lastProgressPercent = -1;
            lastProgressTenths = -1;
            lastProgressCard = null;
        }

        void RefreshProgress()
        {
            if (currentMachine == null || !currentMachine.IsProcessing)
            {
                if (progressWrap.style.display != DisplayStyle.None)
                    progressWrap.style.display = DisplayStyle.None;
                ResetProgressCache();
                return;
            }

            if (progressWrap.style.display != DisplayStyle.Flex)
                progressWrap.style.display = DisplayStyle.Flex;

            float pct = currentMachine.ProcessProgress;
            int pctInt = Mathf.RoundToInt(pct * 100f);
            float rem = currentMachine.ProcessRemainingSeconds;
            int remTenths = Mathf.Max(0, Mathf.RoundToInt(rem * 10f));
            CraftingCard activeCard = currentMachine.ActiveCard;

            if (pctInt == lastProgressPercent
                && remTenths == lastProgressTenths
                && ReferenceEquals(activeCard, lastProgressCard))
            {
                return;
            }

            lastProgressPercent = pctInt;
            lastProgressTenths = remTenths;
            lastProgressCard = activeCard;

            sb.Clear();
            sb.Append("Processing ");
            sb.Append(activeCard != null ? activeCard.DisplayName : "Card");
            sb.Append(" · ");
            sb.Append(pctInt);
            sb.Append("% (");
            sb.Append(remTenths / 10);
            sb.Append('.');
            sb.Append(remTenths % 10);
            sb.Append("s left)");
            progressLabel.text = sb.ToString();
            progressFill.style.width = Length.Percent(pct * 100f);
        }

        void RefreshGeneratorPane()
        {
            if (currentGenerator == null)
                return;

            int perCrank = currentGenerator.EnergyPerCrank;
            genStatsLabel.text = $"Output: +{perCrank} NRG / crank  ·  Total Generated: {currentGenerator.TotalEnergyGenerated} NRG";

            MachineDefinition def = currentGenerator.Definition;
            int nextLvl = currentGenerator.Level + 1;
            if (nextLvl > currentGenerator.MaxLevel)
            {
                genUpgradeInfoLabel.text = $"Fully upgraded (Level {currentGenerator.Level} · +{perCrank} NRG/crank).";
                genUpgradeButton.text = $"Max Lv ({currentGenerator.MaxLevel})";
                genUpgradeButton.SetEnabled(false);
            }
            else if (def != null && def.TryGetUpgradeCost(nextLvl, out MachineUpgradeCost cost))
            {
                string fullCost = MachineUpgradeHelper.FormatFullCost(in cost);
                genUpgradeInfoLabel.text = $"Next Level (Lv {nextLvl}): +{currentGenerator.EnergyPerCrankNextLevel} NRG/crank · Cost: {fullCost}";
                genUpgradeButton.text = $"Upgrade to Lv {nextLvl} ({fullCost})";
                genUpgradeButton.SetEnabled(true);
            }
            else
            {
                genUpgradeInfoLabel.text = $"Upgrade to Level {nextLvl} for +{currentGenerator.EnergyPerCrankNextLevel} NRG/crank.";
                genUpgradeButton.text = $"Upgrade → Lv {nextLvl}";
                genUpgradeButton.SetEnabled(true);
            }

            lastGenCooldownTenths = -2;
            RefreshGeneratorCooldownOnly();
        }

        void RefreshGeneratorCooldownOnly()
        {
            if (currentGenerator == null || genCrankButton == null)
                return;

            float cooldown = currentGenerator.CrankCooldownRemaining;
            int tenths = cooldown > 0.05f ? Mathf.CeilToInt(cooldown * 10f) : 0;
            if (tenths == lastGenCooldownTenths)
                return;

            lastGenCooldownTenths = tenths;
            if (tenths > 0)
            {
                sb.Clear();
                sb.Append("Flywheel Spinning (");
                sb.Append(tenths / 10);
                sb.Append('.');
                sb.Append(tenths % 10);
                sb.Append("s)");
                genCrankButton.text = sb.ToString();
            }
            else
            {
                sb.Clear();
                sb.Append("Pedal Crank (+");
                sb.Append(currentGenerator.EnergyPerCrank);
                sb.Append(" NRG)");
                genCrankButton.text = sb.ToString();
            }
        }

        // ------------------------------------------------------------ Cards Grid (In-Place Updates)

        void RefreshCardsAndDetail()
        {
            if (currentMachine == null)
                return;

            CraftingDatabase db = currentMachine.Database ?? boundStock?.Database;
            displayedCards.Clear();
            if (db != null)
            {
                if (showAllCards || currentMachine.Definition == null)
                {
                    for (int i = 0; i < db.Cards.Count; i++)
                    {
                        if (db.Cards[i] != null)
                            displayedCards.Add(db.Cards[i]);
                    }
                }
                else
                {
                    db.GetCardsForMachine(currentMachine.Definition, displayedCards);
                }
            }

            if (selectedCard == null && displayedCards.Count > 0)
                selectedCard = displayedCards[0];

            // Only rebuild card tiles when the displayed card list itself changes
            if (!TilesMatchDisplayedCards())
            {
                cardsGrid.Clear();
                cardTiles.Clear();
                for (int i = 0; i < displayedCards.Count; i++)
                {
                    CardTileView view = CreateCardTile(displayedCards[i]);
                    cardTiles.Add(view);
                    cardsGrid.Add(view.Tile);
                }
            }

            // Update existing tiles in place (zero rebuild, preserves pointer/tap state)
            MaterialStockLedger ledger = boundStock != null ? boundStock.Ledger : null;
            for (int i = 0; i < cardTiles.Count; i++)
                UpdateCardTileInPlace(cardTiles[i], ledger);

            RebuildDetailPanel();
        }

        bool TilesMatchDisplayedCards()
        {
            if (cardTiles.Count != displayedCards.Count)
                return false;
            for (int i = 0; i < displayedCards.Count; i++)
            {
                if (!ReferenceEquals(cardTiles[i].Card, displayedCards[i]))
                    return false;
            }
            return true;
        }

        CardTileView CreateCardTile(CraftingCard card)
        {
            var tile = new VisualElement();
            tile.AddToClassList("craft-card-tile");

            tile.RegisterCallback<PointerDownEvent>(_ =>
            {
                holdCard = card;
                holdStartTime = Time.unscaledTime;
                holdTriggeredInspect = false;
            });
            tile.RegisterCallback<PointerUpEvent>(_ =>
            {
                if (ReferenceEquals(holdCard, card))
                    holdCard = null;
            });
            tile.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (ReferenceEquals(holdCard, card))
                    holdCard = null;
            });
            tile.RegisterCallback<ClickEvent>(_ =>
            {
                if (holdTriggeredInspect)
                {
                    holdTriggeredInspect = false;
                    return;
                }
                if (ReferenceEquals(selectedCard, card))
                    OpenCardInspector(card);
                else
                    SelectCard(card);
            });

            if (card.CardArtwork != null)
            {
                var art = new VisualElement();
                art.AddToClassList("craft-card-art");
                art.style.backgroundImage = new StyleBackground(card.CardArtwork);
                tile.Add(art);
            }
            else
            {
                tile.Add(BuildProceduralCardFace(card));
            }

            var badge = new Label();
            badge.AddToClassList("craft-card-badge");
            tile.Add(badge);

            return new CardTileView
            {
                Card = card,
                Tile = tile,
                Badge = badge,
            };
        }

        void UpdateCardTileInPlace(CardTileView view, MaterialStockLedger ledger)
        {
            CraftingCard card = view.Card;
            CraftingFailureKind failure = CraftingRules.ValidateQuick(
                card,
                currentMachine.Definition,
                currentMachine.Level,
                currentMachine.IsProcessing,
                ledger,
                isPlayerInRange: true);

            int missingCount = 0;
            if (failure == CraftingFailureKind.InsufficientResources)
            {
                CraftingRules.CollectMissingRequirementsNonAlloc(card, ledger, scratchMissing);
                missingCount = scratchMissing.Count;
            }

            bool isSelected = ReferenceEquals(selectedCard, card);
            if (view.Initialized
                && view.LastFailure == failure
                && view.LastMissingCount == missingCount
                && view.LastSelected == isSelected)
            {
                return;
            }

            view.Initialized = true;
            view.LastFailure = failure;
            view.LastMissingCount = missingCount;
            view.LastSelected = isSelected;

            view.Tile.EnableInClassList("craft-card-tile--selected", isSelected);
            bool dim = failure != CraftingFailureKind.None && failure != CraftingFailureKind.MachineBusy;
            view.Tile.EnableInClassList("craft-card-tile--dim", dim);

            Label badge = view.Badge;
            badge.EnableInClassList("craft-card-badge--locked", failure == CraftingFailureKind.LevelTooLow);
            badge.EnableInClassList("craft-card-badge--other", failure == CraftingFailureKind.IncompatibleMachine);
            badge.EnableInClassList("craft-card-badge--short",
                failure == CraftingFailureKind.MachineBusy || failure == CraftingFailureKind.InsufficientResources);

            if (failure == CraftingFailureKind.None)
            {
                sb.Clear();
                sb.Append("READY · ").Append(card.EnergyCost).Append(" NRG");
                badge.text = sb.ToString();
            }
            else if (failure == CraftingFailureKind.LevelTooLow)
            {
                sb.Clear();
                sb.Append("LOCKED · LVL ").Append(card.ComplexityLevel);
                badge.text = sb.ToString();
            }
            else if (failure == CraftingFailureKind.IncompatibleMachine)
            {
                badge.text = CraftingFormat.MachineTypeLabel(card.RequiredMachineType).ToUpperInvariant();
            }
            else if (failure == CraftingFailureKind.MachineBusy)
            {
                badge.text = "MACHINE BUSY";
            }
            else
            {
                sb.Clear();
                sb.Append("MISSING (").Append(missingCount).Append(')');
                badge.text = sb.ToString();
            }
        }

        VisualElement BuildProceduralCardFace(CraftingCard card)
        {
            var face = new VisualElement();
            face.AddToClassList("craft-proc-card");

            var top = new VisualElement();
            top.AddToClassList("craft-proc-top");
            face.Add(top);

            var tag = new Label(CraftingFormat.CardCategoryTag(card.Category));
            tag.AddToClassList("craft-proc-tag");
            top.Add(tag);

            var lvl = new Label($"LVL {card.ComplexityLevel}");
            lvl.AddToClassList("craft-proc-lvl");
            top.Add(lvl);

            var titleBar = new Label(card.DisplayName.ToUpperInvariant());
            titleBar.AddToClassList("craft-proc-titlebar");
            face.Add(titleBar);

            var center = new VisualElement();
            center.AddToClassList("craft-proc-center");
            face.Add(center);

            sb.Clear();
            for (int i = 0; i < card.Inputs.Length; i++)
            {
                if (!card.Inputs[i].IsValid) continue;
                if (i > 0) sb.Append(" + ");
                sb.Append(card.Inputs[i].material.DisplayName);
            }
            sb.Append("  →  ");
            if (card.Outputs.Length > 0 && card.Outputs[0].IsValid)
                sb.Append(card.Outputs[0].material.DisplayName);
            else
                sb.Append(card.DisplayName);

            var flow = new Label(sb.ToString());
            flow.AddToClassList("craft-proc-flow");
            center.Add(flow);

            string tempPart = card.TemperatureCelsius > 0 ? $"~{card.TemperatureCelsius}°C • " : "ASSEMBLY • ";
            var meta = new Label($"{tempPart}{card.EnergyCost} NRG");
            meta.AddToClassList("craft-proc-meta");
            center.Add(meta);

            var packet = new VisualElement();
            packet.AddToClassList("craft-proc-packet");
            face.Add(packet);

            var packetHdr = new Label("DATA PACKET");
            packetHdr.AddToClassList("craft-proc-packet-hdr");
            packet.Add(packetHdr);

            sb.Clear();
            for (int i = 0; i < card.Inputs.Length; i++)
            {
                if (!card.Inputs[i].IsValid) continue;
                sb.Append("• ").Append(card.Inputs[i].material.DisplayName).Append(": ").Append(card.Inputs[i].Format()).Append('\n');
            }
            sb.Append("• Energy: ").Append(card.EnergyCost).Append(" NRG");

            var packetBody = new Label(sb.ToString());
            packetBody.AddToClassList("craft-proc-packet-body");
            packet.Add(packetBody);

            return face;
        }

        // ------------------------------------------------------------ Detail Panel & Inspector Content

        void RebuildDetailPanel()
        {
            detailContent.Clear();
            if (selectedCard == null)
            {
                detailContent.Add(new Label("No card selected.") { name = "empty-detail" });
                executeButton.SetEnabled(false);
                inspectButton.SetEnabled(false);
                return;
            }

            CraftingValidationResult check = PopulateCardDetails(detailContent, selectedCard);
            inspectButton.SetEnabled(true);
            executeButton.SetEnabled(true);
            executeButton.text = check.IsValid ? "Execute at Machine" : "Execute (Check Reason)";
            executeButton.EnableInClassList("craft-execute-btn--blocked", !check.IsValid);
        }

        void PopulateCardInspector(CraftingCard card)
        {
            if (card == null)
                return;

            inspectorTitle.text = $"{card.DisplayName.ToUpperInvariant()}  ·  LVL {card.ComplexityLevel}";

            inspectorArtWrap.Clear();
            if (card.CardArtwork != null)
            {
                var art = new VisualElement();
                art.AddToClassList("craft-inspector-art");
                art.style.backgroundImage = new StyleBackground(card.CardArtwork);
                inspectorArtWrap.Add(art);
            }
            else
            {
                VisualElement procFace = BuildProceduralCardFace(card);
                procFace.style.width = Length.Percent(100);
                procFace.style.height = Length.Percent(100);
                inspectorArtWrap.Add(procFace);
            }

            inspectorDataContent.Clear();
            CraftingValidationResult check = PopulateCardDetails(inspectorDataContent, card);

            bool hasMachine = currentMachine != null;
            inspectorExecuteButton.style.display = hasMachine ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasMachine)
            {
                inspectorExecuteButton.text = check.IsValid ? "Execute at Machine" : "Execute (Check Reason)";
                inspectorExecuteButton.EnableInClassList("craft-execute-btn--blocked", !check.IsValid);
            }
        }

        CraftingValidationResult PopulateCardDetails(VisualElement container, CraftingCard card)
        {
            MaterialStockLedger ledger = boundStock != null ? boundStock.Ledger : null;
            CraftingValidationResult check = currentMachine != null
                ? CraftingRules.Validate(
                    card,
                    currentMachine.Definition,
                    currentMachine.Level,
                    currentMachine.IsProcessing,
                    ledger,
                    isPlayerInRange: true)
                : CraftingValidationResult.Fail(CraftingFailureKind.InvalidArguments, "Open a crafting machine to execute this card.");

            var tags = new VisualElement();
            tags.AddToClassList("craft-detail-tags");
            var catTag = new Label(CraftingFormat.CardCategoryTag(card.Category));
            catTag.AddToClassList("craft-proc-tag");
            tags.Add(catTag);
            var lvlTag = new Label($"LVL {card.ComplexityLevel}");
            lvlTag.AddToClassList("craft-proc-lvl");
            lvlTag.style.marginLeft = 8;
            tags.Add(lvlTag);
            container.Add(tags);

            var title = new Label(card.DisplayName);
            title.AddToClassList("craft-detail-title");
            container.Add(title);

            if (!string.IsNullOrWhiteSpace(card.Description))
            {
                var desc = new Label(card.Description);
                desc.AddToClassList("craft-detail-desc");
                container.Add(desc);
            }

            string tempStr = card.TemperatureCelsius > 0 ? $"~{card.TemperatureCelsius}°C  ·  " : "";
            var stats = new Label($"{tempStr}{card.EnergyCost} NRG  ·  {card.ProcessingSeconds:0.#}s  ·  {CraftingFormat.MachineTypeLabel(card.RequiredMachineType)}");
            stats.AddToClassList("craft-detail-stats");
            container.Add(stats);

            // Inputs
            var inHdr = new Label("INPUTS REQUIRED");
            inHdr.AddToClassList("craft-section-hdr");
            container.Add(inHdr);

            for (int i = 0; i < card.Inputs.Length; i++)
            {
                MaterialAmount req = card.Inputs[i];
                if (!req.IsValid) continue;
                float have = ledger != null ? ledger.GetAmount(req.material.MaterialId) : 0f;
                bool ok = have + MaterialStockLedger.QuantityEpsilon >= req.amount;
                container.Add(BuildIORow(
                    req.material.DisplayName,
                    req.material.AccentColor,
                    $"{req.material.FormatAmount(have)} / {req.Format()}",
                    isShort: !ok,
                    icon: IconFor(req.material)));
            }

            if (card.EnergyCost > 0)
            {
                int haveNrg = ledger != null ? ledger.Energy : 0;
                bool ok = haveNrg >= card.EnergyCost;
                container.Add(BuildIORow(
                    "Team Energy",
                    new Color(1.0f, 0.64f, 0.16f),
                    $"{haveNrg} / {card.EnergyCost} NRG",
                    isShort: !ok,
                    icon: UpcycleCity.UI.IconLibrary.Active != null ? UpcycleCity.UI.IconLibrary.Active.Get("ui_energy") : null));
            }

            // Outputs
            var outHdr = new Label("OUTPUTS PRODUCED");
            outHdr.AddToClassList("craft-section-hdr");
            container.Add(outHdr);

            for (int i = 0; i < card.Outputs.Length; i++)
            {
                MaterialAmount prod = card.Outputs[i];
                if (!prod.IsValid) continue;
                container.Add(BuildIORow(
                    prod.material.DisplayName,
                    prod.material.AccentColor,
                    $"+{prod.Format()}",
                    isShort: false,
                    icon: IconFor(prod.material)));
            }

            if (card.OutputPrefab != null)
            {
                container.Add(BuildIORow(
                    "World Object Spawn",
                    new Color(0.18f, 0.55f, 0.34f),
                    card.DisplayName,
                    isShort: false));
            }

            var valBox = new Label(check.IsValid ? "Ready to execute at this machine." : check.Message);
            valBox.AddToClassList("craft-validation-box");
            valBox.AddToClassList(check.IsValid ? "craft-validation-box--ok" : "craft-validation-box--err");
            container.Add(valBox);

            return check;
        }

        /// <summary>Material icon: the definition's own sprite, else the shared IconLibrary entry (mat_{id}).</summary>
        static Sprite IconFor(MaterialDefinition material)
        {
            if (material == null)
                return null;
            if (material.Icon != null)
                return material.Icon;
            return UpcycleCity.UI.IconLibrary.Active != null ? UpcycleCity.UI.IconLibrary.Active.ForMaterial(material.MaterialId) : null;
        }

        /// <summary>Rounded tile tinted with the material colour, showing its icon when there is one.</summary>
        static VisualElement MakeSwatch(Color color, Sprite icon)
        {
            var swatch = new VisualElement();
            swatch.AddToClassList("craft-swatch");
            if (icon != null)
            {
                swatch.AddToClassList("craft-swatch--icon");
                color.a = 0.24f;
                var img = new VisualElement();
                img.AddToClassList("craft-swatch-icon");
                img.style.backgroundImage = new StyleBackground(icon);
                swatch.Add(img);
            }
            swatch.style.backgroundColor = color;
            return swatch;
        }

        static VisualElement BuildIORow(string label, Color swatchColor, string qtyText, bool isShort, Sprite icon = null)
        {
            var row = new VisualElement();
            row.AddToClassList("craft-io-row");

            var left = new VisualElement();
            left.AddToClassList("craft-io-left");
            left.Add(MakeSwatch(swatchColor, icon));
            var nameLbl = new Label(label);
            nameLbl.AddToClassList("craft-io-name");
            left.Add(nameLbl);
            row.Add(left);

            var qty = new Label(qtyText);
            qty.AddToClassList("craft-io-qty");
            if (isShort)
                qty.AddToClassList("craft-io-qty--short");
            row.Add(qty);
            return row;
        }

        // ------------------------------------------------------------ Stock Inventory View

        void RebuildStockView()
        {
            stockContainer.Clear();
            CraftingDatabase db = currentMachine != null
                ? currentMachine.Database
                : (currentGenerator != null ? currentGenerator.Database : boundStock?.Database);
            MaterialStockLedger ledger = boundStock != null ? boundStock.Ledger : null;
            if (db == null || ledger == null)
            {
                stockContainer.Add(new Label("No CraftingDatabase loaded."));
                return;
            }

            AddStockGroup("TRASH & COLLECTIBLES", db, ledger, MaterialCategory.Trash, MaterialCategory.Consumable);
            AddStockGroup("REFINED MATERIALS", db, ledger, MaterialCategory.Refined);
            AddStockGroup("BUILT OBJECTS", db, ledger, MaterialCategory.Built);
        }

        void AddStockGroup(string heading, CraftingDatabase db, MaterialStockLedger ledger, params MaterialCategory[] categories)
        {
            var group = new VisualElement();
            group.AddToClassList("craft-stock-group");

            var title = new Label(heading);
            title.AddToClassList("craft-stock-group-title");
            group.Add(title);

            var grid = new VisualElement();
            grid.AddToClassList("craft-stock-grid");
            group.Add(grid);

            int added = 0;
            for (int i = 0; i < db.Materials.Count; i++)
            {
                MaterialDefinition mat = db.Materials[i];
                if (mat == null || !MatchesAnyCategory(mat.Category, categories))
                    continue;

                float qty = ledger.GetAmount(mat.MaterialId);
                var item = new VisualElement();
                item.AddToClassList("craft-stock-item");

                var left = new VisualElement();
                left.AddToClassList("craft-io-left");
                left.Add(MakeSwatch(mat.AccentColor, IconFor(mat)));
                var name = new Label(mat.DisplayName);
                name.AddToClassList("craft-io-name");
                left.Add(name);
                item.Add(left);

                var qtyLbl = new Label(mat.FormatAmount(qty));
                qtyLbl.AddToClassList("craft-stock-qty");
                if (qty <= 0.0001f)
                    qtyLbl.AddToClassList("craft-stock-qty--zero");
                item.Add(qtyLbl);

                grid.Add(item);
                added++;
            }

            if (added > 0)
                stockContainer.Add(group);
        }

        static bool MatchesAnyCategory(MaterialCategory actual, MaterialCategory[] allowed)
        {
            for (int i = 0; i < allowed.Length; i++)
            {
                if (actual == allowed[i])
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------ Button Handlers (All via GameAuthority)

        /// <summary>Executes the currently selected card through <see cref="GameAuthority.Current"/>.</summary>
        public void ExecuteSelectedCard() => OnExecuteClicked();

        void OnInspectSelectedClicked()
        {
            if (selectedCard != null)
                OpenCardInspector(selectedCard);
        }

        void OnInspectorExecuteClicked()
        {
            CraftingMachine machine = currentMachine;
            CraftingCard card = inspectedCard;
            if (machine == null || card == null)
                return;

            selectedCard = card;
            machine.RequestExecuteCard(card, (success, message) =>
            {
                if (this == null)
                    return;

                string text = string.IsNullOrWhiteSpace(message) ? (success ? "Card executed." : "Cannot execute card.") : message;
                Vector3 pos = machine != null ? machine.transform.position : Vector3.zero;
                EmitCardExecutionFeedback(card, success, text, pos);

                if (IsOpen && currentMachine == machine)
                {
                    SetStatus(text, success);
                    if (success)
                        CloseCardInspector();
                    RefreshAll();
                }
            });
        }

        void OnExecuteClicked()
        {
            CraftingMachine machine = currentMachine;
            CraftingCard card = selectedCard;
            if (machine == null || card == null)
                return;

            machine.RequestExecuteCard(card, (success, message) =>
            {
                if (this == null)
                    return;

                string text = string.IsNullOrWhiteSpace(message) ? (success ? "Card executed." : "Cannot execute card.") : message;
                Vector3 pos = machine != null ? machine.transform.position : Vector3.zero;
                EmitCardExecutionFeedback(card, success, text, pos);

                if (IsOpen && currentMachine == machine)
                {
                    SetStatus(text, success);
                    RefreshAll();
                }
            });
        }

        static void EmitCardExecutionFeedback(CraftingCard card, bool success, string message, Vector3 worldPos)
        {
            if (success)
            {
                bool instant = card != null && card.ProcessingSeconds <= 0.01f;
                PlayerFeedbackEvent evt = instant ? PlayerFeedbackEvent.CraftCompleted : PlayerFeedbackEvent.CraftStarted;
                Color accent = instant ? new Color(0.18f, 0.55f, 0.34f) : new Color(0.18f, 0.56f, 0.54f);
                PlayerContext.ActiveFeedback?.Emit(evt, worldPos, 1f, message, accent);
            }
            else
            {
                PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.CraftFailed, worldPos, 1f, message, new Color(0.85f, 0.30f, 0.24f));
            }
        }

        void OnGeneratorCrankClicked()
        {
            ManualEnergyGenerator generator = currentGenerator;
            if (generator == null)
                return;

            generator.RequestCrank((success, message) =>
            {
                if (this == null)
                    return;

                Vector3 pos = generator != null ? generator.transform.position : Vector3.zero;
                if (success)
                {
                    Color color = new Color(0.95f, 0.76f, 0.11f);
                    PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.Interact, pos, 0.8f, message, color);
                }
                else
                {
                    PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.CraftFailed, pos, 1f, message, new Color(0.85f, 0.30f, 0.24f));
                }

                if (IsOpen && currentGenerator == generator)
                {
                    SetStatus(message, success);
                    RefreshAll();
                }
            });
        }

        void OnUpgradeClicked()
        {
            if (currentMachine != null)
            {
                CraftingMachine machine = currentMachine;
                machine.RequestUpgrade((success, message) =>
                {
                    if (this == null)
                        return;

                    Vector3 pos = machine != null ? machine.transform.position : Vector3.zero;
                    EmitUpgradeFeedback(success, message, pos);

                    if (IsOpen && currentMachine == machine)
                    {
                        SetStatus(message, success);
                        RefreshAll();
                    }
                });
            }
            else if (currentGenerator != null)
            {
                ManualEnergyGenerator generator = currentGenerator;
                generator.RequestUpgrade((success, message) =>
                {
                    if (this == null)
                        return;

                    Vector3 pos = generator != null ? generator.transform.position : Vector3.zero;
                    EmitUpgradeFeedback(success, message, pos);

                    if (IsOpen && currentGenerator == generator)
                    {
                        SetStatus(message, success);
                        RefreshAll();
                    }
                });
            }
        }

        static void EmitUpgradeFeedback(bool success, string message, Vector3 worldPos)
        {
            if (success)
            {
                PlayerContext.ActiveFeedback?.Emit(
                    PlayerFeedbackEvent.MachineUpgraded,
                    worldPos,
                    1f,
                    message,
                    new Color(0.91f, 0.50f, 0.15f));
            }
            else
            {
                PlayerContext.ActiveFeedback?.Emit(
                    PlayerFeedbackEvent.CraftFailed,
                    worldPos,
                    1f,
                    message,
                    new Color(0.85f, 0.30f, 0.24f));
            }
        }

        void OnDebugGrantStarterKit()
        {
            boundStock?.RequestGrantStarterKit((ok, msg) =>
            {
                SetStatus(msg, ok);
                RefreshAll();
            });
        }

        void OnDebugAddEnergy()
        {
            boundStock?.RequestAddEnergy(50, (ok, msg) =>
            {
                SetStatus(msg, ok);
                RefreshAll();
            });
        }

        void OnDebugUpgradeLevel()
        {
            if (currentMachine == null)
                return;
            currentMachine.RequestDebugSetLevel(currentMachine.Level + 1, (ok, msg) =>
            {
                SetStatus(msg, ok);
                RefreshAll();
            });
        }

        void OnDebugDowngradeLevel()
        {
            if (currentMachine == null)
                return;
            currentMachine.RequestDebugSetLevel(Mathf.Max(0, currentMachine.Level - 1), (ok, msg) =>
            {
                SetStatus(msg, ok);
                RefreshAll();
            });
        }

        void OnDebugClearStock()
        {
            boundStock?.RequestClearStock((ok, msg) =>
            {
                SetStatus(msg, ok);
                RefreshAll();
            });
        }

        void SetNeutralStatus(string message)
        {
            if (statusBanner == null)
                return;
            statusBanner.text = message ?? string.Empty;
            statusBanner.RemoveFromClassList("craft-status-banner--good");
            statusBanner.RemoveFromClassList("craft-status-banner--bad");
        }

        public void SetStatus(string message, bool good)
        {
            LastStatusMessage = message ?? string.Empty;
            LastStatusSucceeded = good;

            if (statusBanner != null)
            {
                statusBanner.text = LastStatusMessage;
                statusBanner.EnableInClassList("craft-status-banner--good", good);
                statusBanner.EnableInClassList("craft-status-banner--bad", !good);
            }

            if (!string.IsNullOrWhiteSpace(LastStatusMessage))
            {
                recentLog.Insert(0, (good ? "[OK] " : "[!] ") + LastStatusMessage);
                while (recentLog.Count > 3)
                    recentLog.RemoveAt(recentLog.Count - 1);
                if (logLine != null)
                    logLine.text = string.Join("  |  ", recentLog);
            }
        }
    }
}
