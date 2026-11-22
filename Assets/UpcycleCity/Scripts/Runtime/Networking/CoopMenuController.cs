using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Main Menu (title screen) of the <c>MainMenu</c> scene, built in code with UI Toolkit, phone-first landscape:
    /// <list type="bullet">
    /// <item><b>Title</b>: kit-styled title over the live 3D backdrop (<c>MenuBackdrop</c>): player name chip, round
    /// PLAY (solo → Character Creator offline), HOST ROOM, JOIN ROOM, SETTINGS (Online Code / Direct IP card), QUIT.</item>
    /// <item><b>JoinInput</b>: join code (or <c>IP:port</c> in Direct mode).</item>
    /// <item><b>Connecting</b>: status + Cancel.</item>
    /// <item><b>Room</b>: join code with Copy, the player list, host's CHOOSE CHARACTERS (loads the Character Creator
    /// for everyone through NGO scene management) and Leave.</item>
    /// </list>
    /// Disconnect reasons recorded by <see cref="CoopSessionManager"/> show as a banner on return.
    /// Styled by <c>Assets/UpcycleCity/UI/CoopMenu.uss</c>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Networking/Main Menu (Co-op Menu Controller)")]
    public sealed class CoopMenuController : MonoBehaviour
    {
        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;

        enum ScreenMode
        {
            Title,
            JoinInput,
            Connecting,
            Room,
        }

        UIDocument document;
        VisualElement root, panel;
        VisualElement titleView, joinView, connectingView, roomView;
        VisualElement bannerBox, rosterContainer, titleButtons;
        bool settingsOpen;
        Label headerTitle, bannerLabel, joinCodeBadge, roomHintLabel, joinInputHint, connectingStatusLabel;
        TextField nameField, joinInputField;
        Button relayToggleButton, directToggleButton;
        Button soloButton, hostButton, joinButton, joinSubmitButton, cancelConnectingButton, copyCodeButton, continueButton, switchToDirectButton;

        ScreenMode currentScreen = ScreenMode.Title;
        ScreenMode screenBeforeConnecting = ScreenMode.Title;
        // Same-Wi-Fi play needs no cloud account: it's the default; "Online Code" (Relay) needs Unity Cloud linked
        CoopTransportMode selectedTransport = CoopTransportMode.Direct;
        VisualElement lanRoomsBox, lanRoomsList;
        Label lanRoomsHint;
        float lanRefreshTimer;
        int lanShownCount = -1;
        bool busy;
        bool showingCopiedFeedback;
        float copiedFeedbackUntil;

        /// <summary>Current screen name for diagnostics and UI tests (<c>Title</c>, <c>JoinInput</c>, <c>Connecting</c>, <c>Room</c>).</summary>
        public string CurrentScreenName => currentScreen.ToString();

        /// <summary>True when the menu document is visible.</summary>
        public bool IsMenuVisible => root != null && root.style.display != DisplayStyle.None;

        /// <summary>True when the status/error banner is currently visible.</summary>
        public bool IsBannerVisible => bannerBox != null && bannerBox.style.display != DisplayStyle.None;

        /// <summary>Current text displayed in the status/error banner.</summary>
        public string BannerText => bannerLabel != null ? bannerLabel.text : string.Empty;

        /// <summary>True when the Cancel button on the Connecting screen is currently visible.</summary>
        public bool IsCancelButtonVisible => currentScreen == ScreenMode.Connecting
            && cancelConnectingButton != null
            && connectingView != null
            && connectingView.style.display != DisplayStyle.None;

        /// <summary>Current label of the Room Copy code button (<c>"Copy"</c> or <c>"Copied!"</c>).</summary>
        public string CopyCodeButtonText => copyCodeButton != null ? copyCodeButton.text : string.Empty;

        /// <summary>True when the Room Copy code button is visible.</summary>
        public bool IsCopyCodeButtonVisible => currentScreen == ScreenMode.Room
            && copyCodeButton != null
            && copyCodeButton.style.display != DisplayStyle.None;

        /// <summary>True when the host's CHOOSE CHARACTERS button is visible on the Room screen.</summary>
        public bool IsContinueButtonVisible => currentScreen == ScreenMode.Room
            && continueButton != null
            && continueButton.style.display != DisplayStyle.None;

        public void Configure(PanelSettings settings, StyleSheet sheet)
        {
            panelSettings = settings;
            styleSheet = sheet;
        }

        void Awake()
        {
            if (CoopSessionManager.Instance != null && CoopSessionManager.Instance.Settings != null)
            {
                if (panelSettings == null)
                    panelSettings = CoopSessionManager.Instance.Settings.PanelSettings;
                if (styleSheet == null)
                    styleSheet = CoopSessionManager.Instance.Settings.CoopMenuStyleSheet;
                selectedTransport = CoopSessionManager.Instance.Settings.DefaultTransportMode;
            }

            var docGo = new GameObject("MainMenuDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 40;
            docGo.SetActive(true);

            BuildUi();
            ShowScreen(ScreenMode.Title);
            RefreshTransportToggle();
        }

        void OnEnable()
        {
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            BindSessionEvents(true);

            // Returned from a disconnect / leave: show the reason on the title screen
            if (CoopSessionManager.ConsumeDisconnectNotice(out string notice))
            {
                OpenTitle();
                ShowBanner(notice, isError: true, offerDirectSwitch: false);
            }
            else
            {
                SyncScreenToSession();
            }
        }

        void Start()
        {
            BindSessionEvents(true);
            SyncScreenToSession();
        }

        void Update()
        {
            if (showingCopiedFeedback && Time.unscaledTime >= copiedFeedbackUntil)
                ResetCopyButtonVisual();
            UpdateLanRooms();
        }

        // Listen for same-Wi-Fi rooms only while the Join screen is open in Same Wi-Fi mode
        void UpdateLanRooms()
        {
            LanDiscovery lan = LanDiscovery.Instance;
            bool wanted = currentScreen == ScreenMode.JoinInput && selectedTransport == CoopTransportMode.Direct;
            if (lanRoomsBox != null)
                lanRoomsBox.style.display = wanted ? DisplayStyle.Flex : DisplayStyle.None;
            if (lan == null)
                return;
            if (!wanted)
            {
                if (lan.Listening)
                    lan.StopListening();
                lanShownCount = -1;
                return;
            }
            if (!lan.Listening)
                lan.StartListening();

            lanRefreshTimer -= Time.unscaledDeltaTime;
            if (lanRefreshTimer > 0f)
                return;
            lanRefreshTimer = 0.5f;

            IReadOnlyList<LanDiscovery.Room> rooms = lan.Rooms;
            lanRoomsList.Clear();
            for (int i = 0; i < rooms.Count; i++)
            {
                LanDiscovery.Room room = rooms[i];
                string endpoint = room.Endpoint;
                bool full = room.MaxPlayers > 0 && room.Players >= room.MaxPlayers;
                Button b = MakeButton($"{room.HostName}'s room  ·  {room.Players}/{room.MaxPlayers}", "coop-btn coop-btn--teal", () =>
                {
                    joinInputField.SetValueWithoutNotify(endpoint);
                    SubmitJoin(endpoint, CoopTransportMode.Direct);
                });
                b.SetEnabled(!full);
                lanRoomsList.Add(b);
            }
            lanShownCount = rooms.Count;
            lanRoomsHint.text = rooms.Count == 0
                ? "Looking for rooms… make sure everyone is on the same Wi-Fi."
                : "Tap a room to join, or type the host's IP below.";
        }

        void OnDisable()
        {
            BindSessionEvents(false);
        }

        void SyncScreenToSession()
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm == null)
                return;
            if (sm.State == CoopSessionState.Lobby)
            {
                ShowScreen(ScreenMode.Room);
                RefreshRoomUi();
            }
            else if (sm.State == CoopSessionState.Connecting)
            {
                screenBeforeConnecting = ScreenMode.JoinInput;
                ShowConnecting("Connecting to host...");
            }
        }

        void BindSessionEvents(bool bind)
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm == null)
                return;

            sm.StateChanged -= OnSessionStateChanged;
            sm.LobbyChanged -= RefreshRoomUi;
            sm.StatusMessage -= OnSessionStatus;
            sm.ErrorOccurred -= OnSessionError;

            if (bind)
            {
                sm.StateChanged += OnSessionStateChanged;
                sm.LobbyChanged += RefreshRoomUi;
                sm.StatusMessage += OnSessionStatus;
                sm.ErrorOccurred += OnSessionError;
            }
        }

        // ---------------------------------------------------------------- Public actions (buttons + tests)

        /// <summary>Shows the title screen (Play Solo / Host Room / Join Room / Quit).</summary>
        public void OpenTitle()
        {
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            ClearBanner();
            ShowScreen(ScreenMode.Title);
            RefreshTransportToggle();
        }

        /// <summary>PLAY SOLO: no networking; loads the Character Creator offline.</summary>
        public void PlaySolo()
        {
            if (busy)
                return;
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm != null)
                sm.PlaySolo();
            else
                UnityEngine.SceneManagement.SceneManager.LoadScene("CharacterCreator");
        }

        /// <summary>HOST ROOM with the selected (or given) transport.</summary>
        public void HostRoom(CoopTransportMode? transportOverride = null)
        {
            if (transportOverride.HasValue)
            {
                selectedTransport = transportOverride.Value;
                RefreshTransportToggle();
            }
            OnHostClicked();
        }

        /// <summary>Opens the Join Room screen for the selected transport mode.</summary>
        public void OpenJoinInput(CoopTransportMode? transportOverride = null)
        {
            if (transportOverride.HasValue)
                selectedTransport = transportOverride.Value;
            ClearBanner();
            RefreshTransportToggle();
            UpdateJoinPromptForMode();
            ShowScreen(ScreenMode.JoinInput);
        }

        /// <summary>Submits a join attempt using the current or provided code/endpoint.</summary>
        public void SubmitJoin(string codeOrEndpointOverride = null, CoopTransportMode? transportOverride = null)
        {
            if (transportOverride.HasValue)
            {
                selectedTransport = transportOverride.Value;
                RefreshTransportToggle();
            }
            if (codeOrEndpointOverride != null && joinInputField != null)
                joinInputField.SetValueWithoutNotify(codeOrEndpointOverride);
            OnJoinSubmitClicked();
        }

        /// <summary>Cancels an in-flight connection attempt and returns cleanly to the previous screen.</summary>
        public void CancelConnecting()
        {
            SetBusy(false);
            ClearBanner();
            CoopSessionManager.Instance?.LeaveSession();
            ShowScreen(screenBeforeConnecting == ScreenMode.Connecting ? ScreenMode.Title : screenBeforeConnecting);
        }

        /// <summary>Host: moves the whole room on to Character Creation.</summary>
        public void ContinueToCharacters()
        {
            CoopSessionManager.Instance?.GoToCharacterCreation();
        }

        /// <summary>Leaves the room and returns to the title screen.</summary>
        public void LeaveRoom()
        {
            CoopSessionManager.Instance?.LeaveSession();
            OpenTitle();
        }

        /// <summary>Copies the active session join code or Direct IP endpoint to the system clipboard.</summary>
        public bool CopySessionCode()
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            string code = sm != null ? sm.ShareableSessionCode : string.Empty;
            if (string.IsNullOrEmpty(code))
                return false;

            GUIUtility.systemCopyBuffer = code;
            showingCopiedFeedback = true;
            copiedFeedbackUntil = Time.unscaledTime + 1.5f;
            if (copyCodeButton != null)
            {
                copyCodeButton.text = "Copied!";
                copyCodeButton.EnableInClassList("coop-code-copy-btn--copied", true);
            }
            return true;
        }

        void ResetCopyButtonVisual()
        {
            showingCopiedFeedback = false;
            copiedFeedbackUntil = 0f;
            if (copyCodeButton != null)
            {
                copyCodeButton.text = "Copy";
                copyCodeButton.EnableInClassList("coop-code-copy-btn--copied", false);
            }
        }

        static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------------------------------------------------------------- UI Construction

        void BuildUi()
        {
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("coop-root");
            root.AddToClassList("mm-root");

            // Soft dark scrims so text reads over the live 3D backdrop
            root.Add(MakeBox("mm-scrim-right"));
            root.Add(MakeBox("mm-scrim-bottom"));

            // ---- Top-left: player name chip
            var nameChip = MakeBox("mm-chip mm-chip--name");
            nameChip.Add(MakeLabel("CLEANER", "mm-badge"));
            nameField = new TextField { maxLength = 20 };
            nameField.AddToClassList("mm-name-field");
            string initialName = CoopSessionManager.Instance != null ? CoopSessionManager.Instance.LocalPlayerName : "Cleaner";
            nameField.SetValueWithoutNotify(initialName);
            nameField.RegisterValueChangedCallback(e =>
            {
                if (CoopSessionManager.Instance != null)
                    CoopSessionManager.Instance.LocalPlayerName = e.newValue;
            });
            nameChip.Add(nameField);
            root.Add(nameChip);

            // ---- Top-right: co-op chip with the four stream colours
            var infoChip = MakeBox("mm-chip mm-chip--info");
            infoChip.Add(MakeIcon("ui_host", "mm-chip-icon"));
            infoChip.Add(MakeLabel("Online co-op", "mm-chip-caption"));
            infoChip.Add(MakeLabel("1-4", "mm-chip-value"));
            infoChip.Add(MakeBox("mm-chip-divider"));
            var streams = MakeBox("mm-streams");
            foreach (string stream in new[] { "mm-stream--paper", "mm-stream--plastic", "mm-stream--glass", "mm-stream--cans" })
                streams.Add(MakeBox("mm-stream " + stream));
            infoChip.Add(streams);
            root.Add(infoChip);

            // ---- Right column: title, stage pill, slogan, pill buttons
            var brand = MakeBox("mm-brand");
            // Kit wordmark (Art/UI/Logo via USS); the text title shows only if that art is missing
            VisualElement logo = MakeBox("mm-logo");
            brand.Add(logo);
            Label textTitle = MakeLabel("UPCYCLE CITY", "mm-title");
            brand.Add(textTitle);
            logo.RegisterCallback<CustomStyleResolvedEvent>(_ =>
                textTitle.style.display = logo.resolvedStyle.backgroundImage.texture != null
                    || logo.resolvedStyle.backgroundImage.sprite != null
                        ? DisplayStyle.None : DisplayStyle.Flex);
            var stage = MakeBox("mm-stage-pill");
            stage.Add(MakeBox("mm-stage-dot"));
            stage.Add(MakeLabel("AVENUE BOURGUIBA \u2022 TUNIS", "mm-stage-text"));
            brand.Add(stage);
            brand.Add(MakeLabel("Nettoie la ville, construis ton usine de recyclage.", "mm-slogan"));

            titleButtons = MakeBox("mm-actions");
            var coopRow = MakeBox("mm-button-row");
            hostButton = MakeIconButton("HOST ROOM", "ui_host", "mm-pill mm-half", OnHostClicked);
            joinButton = MakeIconButton("JOIN ROOM", "ui_join", "mm-pill mm-half", () => OpenJoinInput());
            coopRow.Add(hostButton);
            coopRow.Add(joinButton);
            titleButtons.Add(coopRow);
            titleButtons.Add(MakeIconButton("SETTINGS", "ui_settings", "mm-pill", ToggleSettings));
            titleButtons.Add(MakeIconButton("QUIT", "ui_close", "mm-pill mm-pill--quiet", QuitGame));
            brand.Add(titleButtons);
            root.Add(brand);

            // ---- Bottom-right: big round PLAY (solo)
            soloButton = new Button(PlaySolo) { text = string.Empty };
            soloButton.AddToClassList("mm-play");
            var playTriangle = MakeBox("mm-play-icon");
            soloButton.Add(playTriangle);
            soloButton.Add(MakeLabel("PLAY", "mm-play-label"));
            soloButton.Add(MakeLabel("SOLO", "mm-play-sub"));
            root.Add(soloButton);

            // ---- Status / error banner (top-centre)
            bannerBox = MakeBox("coop-banner mm-banner");
            bannerLabel = MakeLabel(string.Empty, "coop-banner-text");
            bannerBox.Add(bannerLabel);
            switchToDirectButton = MakeButton("Switch to Direct IP", "coop-btn coop-btn--small coop-btn--amber", () =>
            {
                selectedTransport = CoopTransportMode.Direct;
                RefreshTransportToggle();
                ClearBanner();
                if (currentScreen == ScreenMode.JoinInput)
                    UpdateJoinPromptForMode();
            });
            bannerBox.Add(switchToDirectButton);
            bannerBox.style.display = DisplayStyle.None;
            root.Add(bannerBox);

            // ---- Left card: settings / join / connecting / room
            panel = MakeBox("coop-panel mm-panel");
            root.Add(panel);

            var header = MakeBox("coop-header");
            headerTitle = MakeLabel("SETTINGS", "coop-header-title");
            header.Add(headerTitle);
            panel.Add(header);

            var body = new ScrollView(ScrollViewMode.Vertical);
            body.AddToClassList("coop-scroll");
            panel.Add(body);

            BuildTitleView(body);
            BuildJoinView(body);
            BuildConnectingView(body);
            BuildRoomView(body);

            root.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                root.EnableInClassList("coop-root--portrait", Screen.height > Screen.width);
            });
        }

        /// <summary>Title-screen Settings card: connection mode (Online Code / Direct IP) and graphics quality.</summary>
        void BuildTitleView(VisualElement parent)
        {
            titleView = new VisualElement();
            titleView.AddToClassList("coop-section");
            parent.Add(titleView);

            titleView.Add(MakeLabel("Co-op connection", "coop-label"));
            var transportRow = new VisualElement();
            transportRow.AddToClassList("coop-toggle-row");
            relayToggleButton = MakeButton("Online Code", "coop-toggle", () =>
            {
                selectedTransport = CoopTransportMode.Relay;
                RefreshTransportToggle();
            });
            directToggleButton = MakeButton("Same Wi-Fi", "coop-toggle", () =>
            {
                selectedTransport = CoopTransportMode.Direct;
                RefreshTransportToggle();
            });
            transportRow.Add(directToggleButton);
            transportRow.Add(relayToggleButton);
            titleView.Add(transportRow);
            titleView.Add(MakeLabel("Online Code works over the internet. Direct IP is for testing on the same Wi-Fi.", "coop-hint"));
            // Graphics level + frame-rate cap (applies instantly, saved per device)
            titleView.Add(new UpcycleCity.UI.GraphicsSettingsView());
            titleView.Add(MakeButton("Done", "coop-btn coop-btn--muted", ToggleSettings));
        }

        void ToggleSettings()
        {
            settingsOpen = !settingsOpen;
            if (currentScreen == ScreenMode.Title)
                ShowScreen(ScreenMode.Title);
        }

        static VisualElement MakeBox(string cls)
        {
            var e = new VisualElement();
            foreach (string c in cls.Split(' '))
            {
                if (!string.IsNullOrEmpty(c))
                    e.AddToClassList(c);
            }
            return e;
        }

        static VisualElement MakeIcon(string key, string cls)
        {
            VisualElement e = MakeBox(cls);
            Sprite s = UpcycleCity.UI.IconLibrary.Active != null ? UpcycleCity.UI.IconLibrary.Active.Get(key) : null;
            if (s != null)
                e.style.backgroundImage = new StyleBackground(s);
            else
                e.style.display = DisplayStyle.None;
            return e;
        }

        static Button MakeIconButton(string text, string iconKey, string cls, System.Action onClick)
        {
            Button b = MakeButton(string.Empty, cls, onClick);
            b.Add(MakeIcon(iconKey, "mm-pill-icon"));
            b.Add(MakeLabel(text, "mm-pill-label"));
            return b;
        }

        void BuildJoinView(VisualElement parent)
        {
            joinView = new VisualElement();
            joinView.AddToClassList("coop-section");
            parent.Add(joinView);

            // Rooms heard on this Wi-Fi (LanDiscovery): tap to join, no typing
            lanRoomsBox = new VisualElement();
            lanRoomsBox.AddToClassList("coop-section");
            lanRoomsBox.Add(MakeLabel("ROOMS ON THIS WI-FI", "coop-label"));
            lanRoomsList = new VisualElement();
            lanRoomsBox.Add(lanRoomsList);
            lanRoomsHint = MakeLabel("Looking for rooms… make sure everyone is on the same Wi-Fi.", "coop-hint");
            lanRoomsBox.Add(lanRoomsHint);
            joinView.Add(lanRoomsBox);

            joinInputHint = MakeLabel("Enter the Join Code from the host:", "coop-label");
            joinView.Add(joinInputHint);

            joinInputField = new TextField { maxLength = 32 };
            joinInputField.AddToClassList("coop-input");
            joinInputField.AddToClassList("coop-input--code");
            joinView.Add(joinInputField);

            joinSubmitButton = MakeButton("JOIN ROOM", "coop-btn coop-btn--teal", OnJoinSubmitClicked);
            joinView.Add(joinSubmitButton);

            joinView.Add(MakeButton("Back", "coop-btn coop-btn--muted", () =>
            {
                ClearBanner();
                ShowScreen(ScreenMode.Title);
            }));
        }

        void BuildConnectingView(VisualElement parent)
        {
            connectingView = new VisualElement();
            connectingView.AddToClassList("coop-section");
            parent.Add(connectingView);

            connectingStatusLabel = MakeLabel("Connecting to host...", "coop-hint");
            connectingView.Add(connectingStatusLabel);

            cancelConnectingButton = MakeButton("Cancel", "coop-btn coop-btn--danger", CancelConnecting);
            connectingView.Add(cancelConnectingButton);
        }

        void BuildRoomView(VisualElement parent)
        {
            roomView = new VisualElement();
            roomView.AddToClassList("coop-section");
            parent.Add(roomView);

            var codeRow = new VisualElement();
            codeRow.AddToClassList("coop-code-row");
            joinCodeBadge = MakeLabel("JOIN CODE: —", "coop-code-badge");
            codeRow.Add(joinCodeBadge);
            copyCodeButton = MakeButton("Copy", "coop-code-copy-btn", () => CopySessionCode());
            codeRow.Add(copyCodeButton);
            roomView.Add(codeRow);

            roomView.Add(MakeLabel("Cleaners in the room", "coop-label"));
            rosterContainer = new VisualElement();
            rosterContainer.AddToClassList("coop-roster");
            roomView.Add(rosterContainer);

            roomHintLabel = MakeLabel("Waiting for players...", "coop-hint");
            roomView.Add(roomHintLabel);

            continueButton = MakeButton("CHOOSE CHARACTERS", "coop-btn coop-btn--green", ContinueToCharacters);
            roomView.Add(continueButton);

            roomView.Add(MakeButton("Leave Room", "coop-btn coop-btn--danger", LeaveRoom));
        }

        // ---------------------------------------------------------------- Actions

        async void OnHostClicked()
        {
            if (busy || CoopSessionManager.Instance == null)
                return;

            screenBeforeConnecting = ScreenMode.Title;
            SetBusy(true);
            ClearBanner();
            ShowConnecting(selectedTransport == CoopTransportMode.Relay
                ? "Creating online room..."
                : "Starting Direct Host...");

            (bool ok, string err) = await CoopSessionManager.Instance.HostCoopAsync(selectedTransport);
            if (this == null)
                return;
            SetBusy(false);

            if (!ok)
            {
                if (currentScreen == ScreenMode.Connecting)
                    ShowScreen(ScreenMode.Title);
                if (!string.IsNullOrEmpty(err))
                    ShowBanner(err, isError: true, offerDirectSwitch: selectedTransport == CoopTransportMode.Relay);
                return;
            }

            ClearBanner();
            ShowScreen(ScreenMode.Room);
            RefreshRoomUi();
        }

        async void OnJoinSubmitClicked()
        {
            if (busy || CoopSessionManager.Instance == null)
                return;

            screenBeforeConnecting = ScreenMode.JoinInput;
            SetBusy(true);
            ClearBanner();
            ShowConnecting("Connecting to host...");

            (bool ok, string err) = await CoopSessionManager.Instance.JoinCoopAsync(selectedTransport, joinInputField.value);
            if (this == null)
                return;
            SetBusy(false);

            if (!ok)
            {
                if (currentScreen == ScreenMode.Connecting)
                    ShowScreen(ScreenMode.JoinInput);
                if (!string.IsNullOrEmpty(err))
                    ShowBanner(err, isError: true, offerDirectSwitch: selectedTransport == CoopTransportMode.Relay);
                return;
            }

            // Do not jump to the Room until the host actually approves the connection
            CoopSessionState currentState = CoopSessionManager.Instance.State;
            if (currentState == CoopSessionState.Lobby)
            {
                ClearBanner();
                ShowScreen(ScreenMode.Room);
                RefreshRoomUi();
            }
            else if (currentState == CoopSessionState.Connecting)
            {
                ShowConnecting("Connecting to host...");
            }
        }

        // ---------------------------------------------------------------- State & Visual Refresh

        void OnSessionStateChanged(CoopSessionState nextState)
        {
            if (nextState == CoopSessionState.Connecting)
            {
                ShowConnecting("Connecting to host...");
            }
            else if (nextState == CoopSessionState.Lobby)
            {
                ClearBanner();
                ShowScreen(ScreenMode.Room);
                RefreshRoomUi();
            }
            else if (nextState == CoopSessionState.Offline)
            {
                SetBusy(false);
                bool hasNotice = CoopSessionManager.ConsumeDisconnectNotice(out string notice);
                if (currentScreen == ScreenMode.Connecting)
                {
                    ShowScreen(screenBeforeConnecting == ScreenMode.Connecting ? ScreenMode.Title : screenBeforeConnecting);
                    if (hasNotice)
                        ShowBanner(notice, isError: true, offerDirectSwitch: selectedTransport == CoopTransportMode.Relay);
                }
                else
                {
                    if (currentScreen == ScreenMode.Room)
                        ShowScreen(ScreenMode.Title);
                    if (hasNotice)
                        ShowBanner(notice, isError: true, offerDirectSwitch: false);
                }
            }
        }

        void OnSessionStatus(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;
            if (currentScreen == ScreenMode.Connecting && connectingStatusLabel != null)
                connectingStatusLabel.text = msg;
            else
                ShowBanner(msg, isError: false, offerDirectSwitch: false);
        }

        void OnSessionError(string err)
        {
            if (!string.IsNullOrEmpty(err))
                ShowBanner(err, isError: true, offerDirectSwitch: selectedTransport == CoopTransportMode.Relay);
        }

        void ShowConnecting(string statusText)
        {
            if (connectingStatusLabel != null && !string.IsNullOrEmpty(statusText))
                connectingStatusLabel.text = statusText;
            ShowScreen(ScreenMode.Connecting);
        }

        void ShowScreen(ScreenMode mode)
        {
            if (mode != ScreenMode.Room && showingCopiedFeedback)
                ResetCopyButtonVisual();

            currentScreen = mode;
            root.style.display = DisplayStyle.Flex;
            if (mode != ScreenMode.Title)
                settingsOpen = false;

            // The left card only appears for Settings / Join / Connecting / Room; the title buttons + PLAY otherwise
            bool cardVisible = mode != ScreenMode.Title || settingsOpen;
            panel.style.display = cardVisible ? DisplayStyle.Flex : DisplayStyle.None;
            if (titleButtons != null)
                titleButtons.style.display = mode == ScreenMode.Title ? DisplayStyle.Flex : DisplayStyle.None;
            if (soloButton != null)
                soloButton.style.display = mode == ScreenMode.Title ? DisplayStyle.Flex : DisplayStyle.None;

            titleView.style.display = mode == ScreenMode.Title ? DisplayStyle.Flex : DisplayStyle.None;
            joinView.style.display = mode == ScreenMode.JoinInput ? DisplayStyle.Flex : DisplayStyle.None;
            connectingView.style.display = mode == ScreenMode.Connecting ? DisplayStyle.Flex : DisplayStyle.None;
            roomView.style.display = mode == ScreenMode.Room ? DisplayStyle.Flex : DisplayStyle.None;

            headerTitle.text = mode switch
            {
                ScreenMode.JoinInput => "JOIN ROOM",
                ScreenMode.Connecting => "CONNECTING...",
                ScreenMode.Room => "CO-OP ROOM",
                _ => "SETTINGS",
            };
        }

        void RefreshTransportToggle()
        {
            relayToggleButton.EnableInClassList("coop-toggle--active", selectedTransport == CoopTransportMode.Relay);
            directToggleButton.EnableInClassList("coop-toggle--active", selectedTransport == CoopTransportMode.Direct);
        }

        void UpdateJoinPromptForMode()
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (selectedTransport == CoopTransportMode.Relay)
            {
                joinInputHint.text = "Enter the Join Code shared by the host:";
                joinInputField.SetValueWithoutNotify(string.Empty);
            }
            else
            {
                // Pre-fill this Wi-Fi's prefix (e.g. "192.168.1.") so friends only type the host's last number
                string mine = LanDiscovery.FindLocalIPv4();
                string prefix = !string.IsNullOrEmpty(mine) && mine.LastIndexOf('.') > 0 ? mine.Substring(0, mine.LastIndexOf('.') + 1) : string.Empty;
                joinInputHint.text = "Or type the host's Wi-Fi IP (shown on the host's Room screen):";
                joinInputField.SetValueWithoutNotify(prefix);
            }
        }

        void RefreshRoomUi()
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm == null || rosterContainer == null)
                return;

            if (sm.ActiveTransportMode == CoopTransportMode.Relay)
            {
                string code = string.IsNullOrEmpty(sm.JoinCode) ? "..." : sm.JoinCode;
                joinCodeBadge.text = $"JOIN CODE:  {code}";
            }
            else
            {
                string ip = sm.LocalWifiAddress;
                joinCodeBadge.text = sm.IsHostingSession()
                    ? $"SAME WI-FI ROOM  ·  HOST IP  {(string.IsNullOrEmpty(ip) ? "(no Wi-Fi)" : ip)}"
                    : $"SAME WI-FI ROOM  ·  {sm.ActiveEndpointLabel}";
            }

            if (copyCodeButton != null)
                copyCodeButton.style.display = string.IsNullOrEmpty(sm.ShareableSessionCode) ? DisplayStyle.None : DisplayStyle.Flex;

            rosterContainer.Clear();
            IReadOnlyList<CoopLobbyPlayer> players = sm.LobbyPlayers;
            for (int i = 0; i < players.Count; i++)
            {
                CoopLobbyPlayer p = players[i];
                var row = new VisualElement();
                row.AddToClassList("coop-roster-row");
                row.Add(MakeLabel(p.FormattedTag, "coop-roster-slot"));
                row.Add(MakeLabel(p.IsHost ? $"{p.displayName}  (HOST)" : p.displayName.ToString(), "coop-roster-name"));
                rosterContainer.Add(row);
            }

            bool isHost = NetSession.IsHost;
            continueButton.style.display = isHost ? DisplayStyle.Flex : DisplayStyle.None;
            roomHintLabel.text = isHost
                ? $"{players.Count}/{sm.MaxPlayers} cleaners. Share the code, then choose characters together."
                : $"{players.Count}/{sm.MaxPlayers} cleaners. Waiting for the host to continue...";
        }

        void ShowBanner(string message, bool isError, bool offerDirectSwitch)
        {
            bannerLabel.text = message;
            bannerBox.style.display = DisplayStyle.Flex;
            bannerBox.EnableInClassList("coop-banner--error", isError);
            bannerBox.EnableInClassList("coop-banner--info", !isError);
            switchToDirectButton.style.display = offerDirectSwitch ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ClearBanner()
        {
            bannerLabel.text = string.Empty;
            bannerBox.style.display = DisplayStyle.None;
            switchToDirectButton.style.display = DisplayStyle.None;
        }

        void SetBusy(bool value)
        {
            busy = value;
            soloButton?.SetEnabled(!value);
            hostButton?.SetEnabled(!value);
            joinButton?.SetEnabled(!value);
            joinSubmitButton?.SetEnabled(!value);
        }

        static Label MakeLabel(string text, string cls)
        {
            var label = new Label(text);
            foreach (string c in cls.Split(' '))
            {
                if (!string.IsNullOrEmpty(c))
                    label.AddToClassList(c);
            }
            return label;
        }

        static Button MakeButton(string text, string cls, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            foreach (string c in cls.Split(' '))
            {
                if (!string.IsNullOrEmpty(c))
                    button.AddToClassList(c);
            }
            return button;
        }
    }
}
