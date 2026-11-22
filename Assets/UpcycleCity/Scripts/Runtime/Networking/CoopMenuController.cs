using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UpcycleCity.Character;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Mobile-friendly UI Toolkit flow shown after the Character Creator:
    /// Play Solo / Host Co-op / Join Co-op (Relay join code or Direct IP fallback),
    /// live lobby roster (up to 4 players), and clear error / disconnect banners.
    /// Styled by <c>Assets/UpcycleCity/UI/CoopMenu.uss</c>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Networking/Co-op Menu Controller")]
    public sealed class CoopMenuController : MonoBehaviour
    {
        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;
        [SerializeField] CharacterCreatorController characterCreator;

        enum ScreenMode
        {
            Hidden,
            ModeSelect,
            JoinInput,
            Connecting,
            Lobby,
        }

        UIDocument document;
        VisualElement root, overlay, panel;
        VisualElement modeView, joinView, connectingView, lobbyView;
        VisualElement bannerBox, rosterContainer;
        Label headerTitle, bannerLabel, joinCodeBadge, lobbyHintLabel, joinInputHint, connectingStatusLabel;
        TextField nameField, joinInputField;
        Button relayToggleButton, directToggleButton;
        Button hostButton, joinSubmitButton, cancelConnectingButton, copyCodeButton, startCoopButton, switchToDirectButton;

        ScreenMode currentScreen = ScreenMode.Hidden;
        ScreenMode screenBeforeConnecting = ScreenMode.ModeSelect;
        CoopTransportMode selectedTransport = CoopTransportMode.Relay;
        bool busy;
        bool showingCopiedFeedback;
        float copiedFeedbackUntil;

        /// <summary>Current menu screen name for diagnostics and UI tests (<c>Hidden</c>, <c>ModeSelect</c>, <c>JoinInput</c>, <c>Connecting</c>, <c>Lobby</c>).</summary>
        public string CurrentScreenName => currentScreen.ToString();

        /// <summary>True when any co-op menu screen is visible.</summary>
        public bool IsMenuVisible => currentScreen != ScreenMode.Hidden && root != null && root.style.display != DisplayStyle.None;

        /// <summary>True when the status/error banner is currently visible.</summary>
        public bool IsBannerVisible => bannerBox != null && bannerBox.style.display != DisplayStyle.None;

        /// <summary>Current text displayed in the status/error banner.</summary>
        public string BannerText => bannerLabel != null ? bannerLabel.text : string.Empty;

        /// <summary>True when the Cancel button on the Connecting screen is currently visible.</summary>
        public bool IsCancelButtonVisible => currentScreen == ScreenMode.Connecting
            && cancelConnectingButton != null
            && connectingView != null
            && connectingView.style.display != DisplayStyle.None;

        /// <summary>Current label of the Lobby Copy code button (<c>"Copy"</c> or <c>"Copied!"</c>).</summary>
        public string CopyCodeButtonText => copyCodeButton != null ? copyCodeButton.text : string.Empty;

        /// <summary>True when the Lobby Copy code button is visible.</summary>
        public bool IsCopyCodeButtonVisible => currentScreen == ScreenMode.Lobby
            && copyCodeButton != null
            && copyCodeButton.style.display != DisplayStyle.None;

        public void Configure(PanelSettings settings, StyleSheet sheet, CharacterCreatorController creator = null)
        {
            panelSettings = settings;
            styleSheet = sheet;
            characterCreator = creator;
        }

        void Awake()
        {
            if (characterCreator == null)
                characterCreator = FindAnyObjectByType<CharacterCreatorController>();

            if (CoopSessionManager.Instance != null && CoopSessionManager.Instance.Settings != null)
            {
                if (panelSettings == null)
                    panelSettings = CoopSessionManager.Instance.Settings.PanelSettings;
                if (styleSheet == null)
                    styleSheet = CoopSessionManager.Instance.Settings.CoopMenuStyleSheet;
                selectedTransport = CoopSessionManager.Instance.Settings.DefaultTransportMode;
            }

            var docGo = new GameObject("CoopMenuDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 40;
            docGo.SetActive(true);

            BuildUi();
            ShowScreen(ScreenMode.Hidden);
        }

        void OnEnable()
        {
            CharacterCreatorController.StartRequested += OnCharacterCreatorStart;
            BindSessionEvents(true);

            // If we returned from a disconnect, open Mode Select immediately with the disconnect reason
            if (CoopSessionManager.ConsumeDisconnectNotice(out string notice))
            {
                OpenModeSelect();
                ShowBanner(notice, isError: true, offerDirectSwitch: false);
            }
            else if (CoopSessionManager.Instance != null)
            {
                CoopSessionState sessionState = CoopSessionManager.Instance.State;
                if (sessionState == CoopSessionState.Lobby)
                {
                    ShowScreen(ScreenMode.Lobby);
                    RefreshLobbyUi();
                }
                else if (sessionState == CoopSessionState.Connecting)
                {
                    screenBeforeConnecting = ScreenMode.JoinInput;
                    ShowConnecting("Connecting to host...");
                }
            }
        }

        void Start()
        {
            if (characterCreator == null)
                characterCreator = FindAnyObjectByType<CharacterCreatorController>();

            BindSessionEvents(true);
            if (CoopSessionManager.Instance != null)
            {
                CoopSessionState sessionState = CoopSessionManager.Instance.State;
                if (sessionState == CoopSessionState.Lobby)
                {
                    ShowScreen(ScreenMode.Lobby);
                    RefreshLobbyUi();
                }
                else if (sessionState == CoopSessionState.Connecting)
                {
                    screenBeforeConnecting = ScreenMode.JoinInput;
                    ShowConnecting("Connecting to host...");
                }
            }

            if (currentScreen != ScreenMode.Hidden)
                characterCreator?.SetCreatorPanelVisible(false);
        }

        void Update()
        {
            if (showingCopiedFeedback && Time.unscaledTime >= copiedFeedbackUntil)
                ResetCopyButtonVisual();
        }

        void OnDisable()
        {
            CharacterCreatorController.StartRequested -= OnCharacterCreatorStart;
            BindSessionEvents(false);
        }

        void BindSessionEvents(bool bind)
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm == null)
                return;

            sm.StateChanged -= OnSessionStateChanged;
            sm.LobbyChanged -= RefreshLobbyUi;
            sm.StatusMessage -= OnSessionStatus;
            sm.ErrorOccurred -= OnSessionError;

            if (bind)
            {
                sm.StateChanged += OnSessionStateChanged;
                sm.LobbyChanged += RefreshLobbyUi;
                sm.StatusMessage += OnSessionStatus;
                sm.ErrorOccurred += OnSessionError;
            }
        }

        bool OnCharacterCreatorStart(CharacterCreatorController creator)
        {
            characterCreator = creator;
            OpenModeSelect();
            return true;
        }

        /// <summary>Opens the Play Solo / Host Co-op / Join Co-op choice screen over the character studio.</summary>
        public void OpenModeSelect()
        {
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            ClearBanner();
            ShowScreen(ScreenMode.ModeSelect);
            RefreshTransportToggle();
        }

        /// <summary>Opens the Join Co-op screen for the selected transport mode (also usable by tests).</summary>
        public void OpenJoinInput(CoopTransportMode? transportOverride = null)
        {
            if (transportOverride.HasValue)
                selectedTransport = transportOverride.Value;
            ClearBanner();
            RefreshTransportToggle();
            UpdateJoinPromptForMode();
            ShowScreen(ScreenMode.JoinInput);
        }

        /// <summary>Submits a join attempt using the current or provided code/endpoint (also usable by tests).</summary>
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

        /// <summary>Cancels an in-flight connection attempt and returns cleanly to the previous menu screen.</summary>
        public void CancelConnecting()
        {
            SetBusy(false);
            ClearBanner();
            CoopSessionManager.Instance?.LeaveSession();
            ShowScreen(screenBeforeConnecting == ScreenMode.Hidden ? ScreenMode.JoinInput : screenBeforeConnecting);
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

        void BackToCreator()
        {
            ClearBanner();
            ShowScreen(ScreenMode.Hidden);
            characterCreator?.SetCreatorPanelVisible(true);
        }

        // ---------------------------------------------------------------- UI Construction

        void BuildUi()
        {
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("coop-root");

            overlay = new VisualElement();
            overlay.AddToClassList("coop-overlay");
            root.Add(overlay);

            panel = new VisualElement();
            panel.AddToClassList("coop-panel");
            overlay.Add(panel);

            var header = new VisualElement();
            header.AddToClassList("coop-header");
            headerTitle = MakeLabel("UPCYCLE CITY · PLAY", "coop-header-title");
            header.Add(headerTitle);
            panel.Add(header);

            bannerBox = new VisualElement();
            bannerBox.AddToClassList("coop-banner");
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
            panel.Add(bannerBox);

            var body = new ScrollView(ScrollViewMode.Vertical);
            body.AddToClassList("coop-scroll");
            panel.Add(body);

            BuildModeView(body);
            BuildJoinView(body);
            BuildConnectingView(body);
            BuildLobbyView(body);

            root.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                root.EnableInClassList("coop-root--portrait", Screen.height > Screen.width);
            });
        }

        void BuildModeView(VisualElement parent)
        {
            modeView = new VisualElement();
            modeView.AddToClassList("coop-section");
            parent.Add(modeView);

            modeView.Add(MakeLabel("Player Name", "coop-label"));
            nameField = new TextField { maxLength = 20 };
            nameField.AddToClassList("coop-input");
            string initialName = CoopSessionManager.Instance != null ? CoopSessionManager.Instance.LocalPlayerName : "Cleaner";
            nameField.SetValueWithoutNotify(initialName);
            nameField.RegisterValueChangedCallback(e =>
            {
                if (CoopSessionManager.Instance != null)
                    CoopSessionManager.Instance.LocalPlayerName = e.newValue;
            });
            modeView.Add(nameField);

            var soloButton = MakeButton("PLAY SOLO (OFFLINE)", "coop-btn coop-btn--green", OnPlaySoloClicked);
            modeView.Add(soloButton);

            modeView.Add(MakeLabel("Online Co-op (Up to 4 Players)", "coop-label coop-label--spaced"));

            var transportRow = new VisualElement();
            transportRow.AddToClassList("coop-toggle-row");
            relayToggleButton = MakeButton("Online Code (Relay)", "coop-toggle", () =>
            {
                selectedTransport = CoopTransportMode.Relay;
                RefreshTransportToggle();
            });
            directToggleButton = MakeButton("Direct IP (LAN / Local)", "coop-toggle", () =>
            {
                selectedTransport = CoopTransportMode.Direct;
                RefreshTransportToggle();
            });
            transportRow.Add(relayToggleButton);
            transportRow.Add(directToggleButton);
            modeView.Add(transportRow);

            hostButton = MakeButton("HOST CO-OP SESSION", "coop-btn coop-btn--teal", OnHostClicked);
            modeView.Add(hostButton);

            var joinButton = MakeButton("JOIN CO-OP SESSION", "coop-btn coop-btn--amber", () => OpenJoinInput());
            modeView.Add(joinButton);

            var backBtn = MakeButton("Back to Character Creator", "coop-btn coop-btn--muted", BackToCreator);
            modeView.Add(backBtn);
        }

        void BuildJoinView(VisualElement parent)
        {
            joinView = new VisualElement();
            joinView.AddToClassList("coop-section");
            parent.Add(joinView);

            joinInputHint = MakeLabel("Enter the 6-character Join Code from the host:", "coop-label");
            joinView.Add(joinInputHint);

            joinInputField = new TextField { maxLength = 32 };
            joinInputField.AddToClassList("coop-input");
            joinInputField.AddToClassList("coop-input--code");
            joinView.Add(joinInputField);

            joinSubmitButton = MakeButton("CONNECT TO SESSION", "coop-btn coop-btn--teal", OnJoinSubmitClicked);
            joinView.Add(joinSubmitButton);

            var backBtn = MakeButton("Back", "coop-btn coop-btn--muted", () =>
            {
                ClearBanner();
                ShowScreen(ScreenMode.ModeSelect);
            });
            joinView.Add(backBtn);
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

        void BuildLobbyView(VisualElement parent)
        {
            lobbyView = new VisualElement();
            lobbyView.AddToClassList("coop-section");
            parent.Add(lobbyView);

            var codeRow = new VisualElement();
            codeRow.AddToClassList("coop-code-row");
            joinCodeBadge = MakeLabel("SESSION CODE: —", "coop-code-badge");
            codeRow.Add(joinCodeBadge);
            copyCodeButton = MakeButton("Copy", "coop-code-copy-btn", () => CopySessionCode());
            codeRow.Add(copyCodeButton);
            lobbyView.Add(codeRow);

            lobbyView.Add(MakeLabel("Connected Cleaners", "coop-label"));
            rosterContainer = new VisualElement();
            rosterContainer.AddToClassList("coop-roster");
            lobbyView.Add(rosterContainer);

            lobbyHintLabel = MakeLabel("Waiting for players...", "coop-hint");
            lobbyView.Add(lobbyHintLabel);

            startCoopButton = MakeButton("START CITY CLEANUP", "coop-btn coop-btn--green", OnStartCoopClicked);
            lobbyView.Add(startCoopButton);

            var leaveBtn = MakeButton("Leave Lobby", "coop-btn coop-btn--danger", OnLeaveLobbyClicked);
            lobbyView.Add(leaveBtn);
        }

        // ---------------------------------------------------------------- Actions

        void OnPlaySoloClicked()
        {
            if (busy)
                return;
            string creatorScene = characterCreator != null && !string.IsNullOrWhiteSpace(characterCreator.GameplayScene)
                ? characterCreator.GameplayScene
                : "PlayerGym";

            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm != null)
                sm.PlaySolo(creatorScene);
            else
                UnityEngine.SceneManagement.SceneManager.LoadScene(creatorScene);
        }

        async void OnHostClicked()
        {
            if (busy || CoopSessionManager.Instance == null)
                return;

            screenBeforeConnecting = ScreenMode.ModeSelect;
            SetBusy(true);
            ClearBanner();
            ShowConnecting(selectedTransport == CoopTransportMode.Relay
                ? "Creating online Relay session..."
                : "Starting Direct Host...");

            (bool ok, string err) = await CoopSessionManager.Instance.HostCoopAsync(selectedTransport);
            if (this == null)
                return;
            SetBusy(false);

            if (!ok)
            {
                if (currentScreen == ScreenMode.Connecting)
                    ShowScreen(ScreenMode.ModeSelect);
                if (!string.IsNullOrEmpty(err))
                    ShowBanner(err, isError: true, offerDirectSwitch: selectedTransport == CoopTransportMode.Relay);
                return;
            }

            ClearBanner();
            ShowScreen(ScreenMode.Lobby);
            RefreshLobbyUi();
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

            // Do not jump to Lobby until the host actually approves the connection
            CoopSessionState currentState = CoopSessionManager.Instance.State;
            if (currentState == CoopSessionState.Lobby)
            {
                ClearBanner();
                ShowScreen(ScreenMode.Lobby);
                RefreshLobbyUi();
            }
            else if (currentState == CoopSessionState.Connecting)
            {
                ShowConnecting("Connecting to host...");
            }
        }

        void OnStartCoopClicked()
        {
            CoopSessionManager.Instance?.StartCoopGameplay();
        }

        void OnLeaveLobbyClicked()
        {
            CoopSessionManager.Instance?.LeaveSession();
            OpenModeSelect();
        }

        // ---------------------------------------------------------------- State & Visual Refresh

        void OnSessionStateChanged(CoopSessionState nextState)
        {
            if (nextState == CoopSessionState.Connecting)
            {
                if (currentScreen == ScreenMode.Hidden)
                    screenBeforeConnecting = ScreenMode.JoinInput;
                ShowConnecting("Connecting to host...");
            }
            else if (nextState == CoopSessionState.Lobby)
            {
                ClearBanner();
                ShowScreen(ScreenMode.Lobby);
                RefreshLobbyUi();
            }
            else if (nextState == CoopSessionState.LoadingGame || nextState == CoopSessionState.InGame)
            {
                ShowScreen(ScreenMode.Hidden);
            }
            else if (nextState == CoopSessionState.Offline)
            {
                SetBusy(false);
                bool hasNotice = CoopSessionManager.ConsumeDisconnectNotice(out string notice);
                if (currentScreen == ScreenMode.Connecting)
                {
                    ScreenMode target = screenBeforeConnecting == ScreenMode.Hidden
                        ? ScreenMode.JoinInput
                        : screenBeforeConnecting;
                    ShowScreen(target);
                    if (hasNotice)
                        ShowBanner(notice, isError: true, offerDirectSwitch: selectedTransport == CoopTransportMode.Relay);
                }
                else if (currentScreen == ScreenMode.Lobby)
                {
                    ShowScreen(ScreenMode.ModeSelect);
                    if (hasNotice)
                        ShowBanner(notice, isError: true, offerDirectSwitch: false);
                }
                else if (hasNotice)
                {
                    if (currentScreen == ScreenMode.Hidden)
                        ShowScreen(ScreenMode.ModeSelect);
                    ShowBanner(notice, isError: true, offerDirectSwitch: selectedTransport == CoopTransportMode.Relay);
                }
            }
        }

        void OnSessionStatus(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;
            if (currentScreen == ScreenMode.Connecting && connectingStatusLabel != null)
                connectingStatusLabel.text = msg;
            else if (currentScreen != ScreenMode.Hidden)
                ShowBanner(msg, isError: false, offerDirectSwitch: false);
        }

        void OnSessionError(string err)
        {
            if (!string.IsNullOrEmpty(err) && currentScreen != ScreenMode.Hidden)
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
            if (mode != ScreenMode.Lobby && showingCopiedFeedback)
                ResetCopyButtonVisual();

            currentScreen = mode;
            bool visible = mode != ScreenMode.Hidden;
            root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible)
                return;

            // Keep the underlying Character Creator panel hidden whenever any co-op menu screen is shown
            characterCreator?.SetCreatorPanelVisible(false);

            modeView.style.display = mode == ScreenMode.ModeSelect ? DisplayStyle.Flex : DisplayStyle.None;
            joinView.style.display = mode == ScreenMode.JoinInput ? DisplayStyle.Flex : DisplayStyle.None;
            connectingView.style.display = mode == ScreenMode.Connecting ? DisplayStyle.Flex : DisplayStyle.None;
            lobbyView.style.display = mode == ScreenMode.Lobby ? DisplayStyle.Flex : DisplayStyle.None;

            headerTitle.text = mode switch
            {
                ScreenMode.JoinInput => "JOIN CO-OP SESSION",
                ScreenMode.Connecting => "CONNECTING...",
                ScreenMode.Lobby => "CO-OP LOBBY",
                _ => "UPCYCLE CITY · PLAY",
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
                string addr = sm != null && sm.Settings != null ? sm.Settings.DefaultDirectAddress : "127.0.0.1";
                ushort port = sm != null && sm.Settings != null ? sm.Settings.DefaultDirectPort : (ushort)7777;
                joinInputHint.text = "Enter Host IP:Port (for LAN or local testing):";
                joinInputField.SetValueWithoutNotify($"{addr}:{port}");
            }
        }

        void RefreshLobbyUi()
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
                joinCodeBadge.text = $"DIRECT HOST:  {sm.ActiveEndpointLabel}";
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
                string hostSuffix = p.IsHost ? "  (HOST)" : string.Empty;
                var slotLabel = MakeLabel(p.FormattedTag, "coop-roster-slot");
                var playerName = MakeLabel($"{p.displayName}{hostSuffix}", "coop-roster-name");
                row.Add(slotLabel);
                row.Add(playerName);
                rosterContainer.Add(row);
            }

            bool isHost = NetSession.IsHost;
            startCoopButton.style.display = isHost ? DisplayStyle.Flex : DisplayStyle.None;
            lobbyHintLabel.text = isHost
                ? $"{players.Count}/{sm.MaxPlayers} cleaners ready. Share the code or tap Start anytime."
                : $"{players.Count}/{sm.MaxPlayers} cleaners in lobby. Waiting for host to start...";
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
            hostButton?.SetEnabled(!value);
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
