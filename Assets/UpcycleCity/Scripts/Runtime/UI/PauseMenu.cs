using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UpcycleCity.Core;
using UpcycleCity.Player;

namespace UpcycleCity.UI
{
    /// <summary>
    /// The in-game menu: Esc / gamepad Start / the touch ☰ button. Resume, Settings (graphics), Controls, Leave to Main Menu, Quit.
    /// In co-op it lists every player with their slot, host/you tags and ping to the host (ms, colour-coded),
    /// plus the room's join code — and the city keeps running (it's online). In solo the game actually pauses.
    /// Local-only presentation (AGENTS.md §7.5); reads the session through <see cref="GameSession"/>, Netcode-free.
    /// Created automatically once a player exists.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/UI/Pause Menu")]
    public sealed class PauseMenu : MonoBehaviour
    {
        const string PanelSettingsResource = "CraftingPanelSettings";
        const string StyleResource = "PauseMenu";
        const string MainMenuScene = "MainMenu";
        const float RefreshInterval = 0.5f;

        public static PauseMenu Instance { get; private set; }

        UIDocument document;
        VisualElement root, overlay, playersCard, playerList, controlsCard, settingsCard, confirmRow, mainButtons;
        Label title, subtitle, playersHeading, joinCodeLabel;
        Button copyCodeButton, controlsButton, settingsButton;
        bool settingsShown;
        DisplayStyle playersDisplayBeforeSettings, controlsDisplayBeforeSettings;
        readonly List<ISessionPlayer> players = new List<ISessionPlayer>(4);
        readonly List<Row> rows = new List<Row>(4);
        float refreshTimer;
        bool pausedTime;
        float savedTimeScale = 1f;

        public bool IsOpen { get; private set; }

        sealed class Row
        {
            public VisualElement Root, Dot, Bars;
            public VisualElement[] Bar = new VisualElement[3];
            public Label Name, Tags, Ping;
            public int LastPing = int.MinValue;
            public string LastName;
        }

        static readonly Color[] SlotColours =
        {
            new Color(1f, 0.48f, 0.1f),   // P1 hi-vis orange
            new Color(0.18f, 0.55f, 0.34f),
            new Color(0.18f, 0.56f, 0.54f),
            new Color(0.95f, 0.76f, 0.11f),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null)
                return;
            var go = new GameObject("PauseMenu");
            DontDestroyOnLoad(go);
            go.AddComponent<PauseMenu>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            var docGo = new GameObject("PauseMenuDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = Resources.Load<PanelSettings>(PanelSettingsResource);
            document.sortingOrder = 80;
            docGo.SetActive(true);
            Build();
            Hide();
        }

        void OnDestroy()
        {
            RestoreTime();
            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------ building

        void Build()
        {
            root = document.rootVisualElement;
            root.Clear();
            var sheet = Resources.Load<StyleSheet>(StyleResource);
            if (sheet != null)
                root.styleSheets.Add(sheet);
            root.AddToClassList("pm-root");
            root.pickingMode = PickingMode.Ignore;

            overlay = new VisualElement();
            overlay.AddToClassList("pm-overlay");
            root.Add(overlay);

            var window = new VisualElement();
            window.AddToClassList("pm-window");
            overlay.Add(window);

            // Left: title + buttons
            var left = new VisualElement();
            left.AddToClassList("pm-left");
            window.Add(left);
            title = new Label("PAUSED");
            title.AddToClassList("pm-title");
            left.Add(title);
            subtitle = new Label();
            subtitle.AddToClassList("pm-subtitle");
            left.Add(subtitle);

            mainButtons = new VisualElement();
            mainButtons.AddToClassList("pm-buttons");
            left.Add(mainButtons);
            AddButton(mainButtons, "Resume", "pm-button--primary", Close);
            settingsButton = AddButton(mainButtons, "Settings", null, ToggleSettings);
            controlsButton = AddButton(mainButtons, "Controls", null, ToggleControls);
            AddButton(mainButtons, "Leave to Main Menu", null, AskLeave);
            AddButton(mainButtons, "Quit Game", "pm-button--quiet", Quit);

            confirmRow = new VisualElement();
            confirmRow.AddToClassList("pm-confirm");
            left.Add(confirmRow);
            var confirmText = new Label("Leave the game?");
            confirmText.AddToClassList("pm-confirm-text");
            confirmRow.Add(confirmText);
            var confirmButtons = new VisualElement();
            confirmButtons.AddToClassList("pm-confirm-buttons");
            confirmRow.Add(confirmButtons);
            AddButton(confirmButtons, "Leave", "pm-button--danger", Leave);
            AddButton(confirmButtons, "Stay", null, CancelLeave);

            // Right: players (co-op) and the controls card
            var right = new VisualElement();
            right.AddToClassList("pm-right");
            window.Add(right);

            playersCard = new VisualElement();
            playersCard.AddToClassList("pm-card");
            right.Add(playersCard);
            var head = new VisualElement();
            head.AddToClassList("pm-card-head");
            playersCard.Add(head);
            playersHeading = new Label("PLAYERS");
            playersHeading.AddToClassList("pm-card-title");
            head.Add(playersHeading);
            var pingHeading = new Label("PING");
            pingHeading.AddToClassList("pm-card-title");
            pingHeading.AddToClassList("pm-card-title--right");
            head.Add(pingHeading);
            playerList = new VisualElement();
            playerList.AddToClassList("pm-players");
            playersCard.Add(playerList);
            var codeRow = new VisualElement();
            codeRow.AddToClassList("pm-code-row");
            playersCard.Add(codeRow);
            joinCodeLabel = new Label();
            joinCodeLabel.AddToClassList("pm-code");
            codeRow.Add(joinCodeLabel);
            copyCodeButton = AddButton(codeRow, "Copy", "pm-button--small", () =>
            {
                string code = GameSession.JoinCode;
                if (!string.IsNullOrEmpty(code))
                    GUIUtility.systemCopyBuffer = code;
            });

            // Settings: the graphics level selector (same block as the Main Menu), shown in place of the other cards
            settingsCard = new VisualElement();
            settingsCard.AddToClassList("pm-card");
            right.Add(settingsCard);
            var settingsTitle = new Label("SETTINGS");
            settingsTitle.AddToClassList("pm-card-title");
            settingsCard.Add(settingsTitle);
            settingsCard.Add(new GraphicsSettingsView());
            settingsCard.style.display = DisplayStyle.None;

            controlsCard = new VisualElement();
            controlsCard.AddToClassList("pm-card");
            right.Add(controlsCard);
            var controlsTitle = new Label("CONTROLS");
            controlsTitle.AddToClassList("pm-card-title");
            controlsCard.Add(controlsTitle);
            AddControl("Move · Look", "WASD · Mouse");
            AddControl("Run · Jump · Crouch", "Shift · Space · C");
            AddControl("Pick up trash (into your arms / bag)", "Left click · E");
            AddControl("Throw what you hold · Drop it", "Hold left click · Right click");
            AddControl("Open your bag (throw out items)", "Tab · B");
            AddControl("Use a shop, bin, RÉCUP, machine", "E");
            AddControl("Hotbar · use tool / eat", "1–6, wheel, Q/R · F");
            AddControl("This menu", "Esc");
        }

        Button AddButton(VisualElement parent, string text, string extraClass, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("pm-button");
            if (!string.IsNullOrEmpty(extraClass))
                button.AddToClassList(extraClass);
            parent.Add(button);
            return button;
        }

        void AddControl(string action, string keys)
        {
            var row = new VisualElement();
            row.AddToClassList("pm-control");
            var a = new Label(action);
            a.AddToClassList("pm-control-action");
            row.Add(a);
            var k = new Label(keys);
            k.AddToClassList("pm-control-keys");
            row.Add(k);
            controlsCard.Add(row);
        }

        Row MakeRow()
        {
            var row = new Row { Root = new VisualElement() };
            row.Root.AddToClassList("pm-player");
            row.Dot = new VisualElement();
            row.Dot.AddToClassList("pm-player-dot");
            row.Root.Add(row.Dot);
            row.Name = new Label();
            row.Name.AddToClassList("pm-player-name");
            row.Root.Add(row.Name);
            row.Tags = new Label();
            row.Tags.AddToClassList("pm-player-tags");
            row.Root.Add(row.Tags);
            row.Bars = new VisualElement();
            row.Bars.AddToClassList("pm-bars");
            row.Root.Add(row.Bars);
            for (int i = 0; i < 3; i++)
            {
                var bar = new VisualElement();
                bar.AddToClassList("pm-bar");
                bar.style.height = 6 + i * 5;
                row.Bars.Add(bar);
                row.Bar[i] = bar;
            }
            row.Ping = new Label();
            row.Ping.AddToClassList("pm-player-ping");
            row.Root.Add(row.Ping);
            playerList.Add(row.Root);
            return row;
        }

        // ------------------------------------------------------------ open / close

        public void Open()
        {
            if (IsOpen)
                return;
            IsOpen = true;
            bool online = GameAuthority.IsMultiplayer;
            title.text = online ? "MENU" : "PAUSED";
            subtitle.text = online
                ? "Online co-op: the city keeps running while you're in here."
                : "The city waits for you.";
            playersCard.style.display = online ? DisplayStyle.Flex : DisplayStyle.None;
            controlsCard.style.display = online ? DisplayStyle.None : DisplayStyle.Flex;
            controlsButton.text = online ? "Controls" : "Hide controls";
            confirmRow.style.display = DisplayStyle.None;
            mainButtons.style.display = DisplayStyle.Flex;
            settingsShown = false;
            settingsCard.style.display = DisplayStyle.None;
            settingsButton.text = "Settings";
            overlay.style.display = DisplayStyle.Flex;
            refreshTimer = 0f;
            RefreshPlayers();

            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.UI);
            if (!online)
            {
                savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
                Time.timeScale = 0f;
                AudioListener.pause = true;
                pausedTime = true;
            }
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            Hide();
            RestoreTime();
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.Gameplay);
        }

        void Hide()
        {
            IsOpen = false;
            if (overlay != null)
                overlay.style.display = DisplayStyle.None;
        }

        void RestoreTime()
        {
            if (!pausedTime)
                return;
            pausedTime = false;
            Time.timeScale = savedTimeScale;
            AudioListener.pause = false;
        }

        /// <summary>True while the Settings card is shown.</summary>
        public bool SettingsShown => settingsShown;

        /// <summary>Shows or hides the Settings card (in place of the players / controls cards).</summary>
        public void ToggleSettings()
        {
            settingsShown = !settingsShown;
            if (settingsShown)
            {
                playersDisplayBeforeSettings = playersCard.style.display.value;
                controlsDisplayBeforeSettings = controlsCard.style.display.value;
                playersCard.style.display = DisplayStyle.None;
                controlsCard.style.display = DisplayStyle.None;
                settingsCard.style.display = DisplayStyle.Flex;
            }
            else
            {
                settingsCard.style.display = DisplayStyle.None;
                playersCard.style.display = playersDisplayBeforeSettings;
                controlsCard.style.display = controlsDisplayBeforeSettings;
            }
            settingsButton.text = settingsShown ? "Hide settings" : "Settings";
        }

        void ToggleControls()
        {
            if (settingsShown)
                ToggleSettings();
            bool show = controlsCard.style.display == DisplayStyle.None;
            controlsCard.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            controlsButton.text = show ? "Hide controls" : "Controls";
        }

        void AskLeave()
        {
            mainButtons.style.display = DisplayStyle.None;
            confirmRow.style.display = DisplayStyle.Flex;
        }

        void CancelLeave()
        {
            confirmRow.style.display = DisplayStyle.None;
            mainButtons.style.display = DisplayStyle.Flex;
        }

        void Leave()
        {
            bool online = GameAuthority.IsMultiplayer;
            Hide();
            RestoreTime();
            if (online && GameSession.LeaveSessionHandler != null)
                GameSession.LeaveSessionHandler();
            else if (Application.CanStreamedLevelBeLoaded(MainMenuScene))
                SceneManager.LoadScene(MainMenuScene);
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ------------------------------------------------------------ per frame

        void Update()
        {
            PlayerController player = PlayerContext.Active;
            if (!IsOpen)
            {
                if (player != null && player.Input != null && player.Input.InputMode == PlayerInputMode.Gameplay
                    && player.Input.MenuPressedThisFrame && player.Input.InputModeChangedFrame != Time.frameCount)
                    Open();
                return;
            }

            // No player any more (scene changed): close quietly
            if (player == null)
            {
                Hide();
                RestoreTime();
                return;
            }

            bool back = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                        || (Gamepad.current != null && (Gamepad.current.startButton.wasPressedThisFrame || Gamepad.current.buttonEast.wasPressedThisFrame));
            if (back)
            {
                if (confirmRow.style.display == DisplayStyle.Flex)
                    CancelLeave();
                else
                    Close();
                return;
            }

            refreshTimer -= Time.unscaledDeltaTime;
            if (refreshTimer <= 0f)
            {
                refreshTimer = RefreshInterval;
                RefreshPlayers();
            }
        }

        void RefreshPlayers()
        {
            if (playersCard.style.display == DisplayStyle.None)
                return;

            GameSession.CopyPlayers(players);
            playersHeading.text = $"PLAYERS {players.Count}/4";
            while (rows.Count < players.Count)
                rows.Add(MakeRow());
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                bool used = i < players.Count;
                row.Root.style.display = used ? DisplayStyle.Flex : DisplayStyle.None;
                if (!used)
                    continue;

                ISessionPlayer p = players[i];
                string name = p.DisplayName;
                if (!string.Equals(row.LastName, name))
                {
                    row.LastName = name;
                    row.Name.text = name;
                }
                row.Tags.text = p.IsHost && p.IsLocal ? "HOST · YOU" : p.IsHost ? "HOST" : p.IsLocal ? "YOU" : string.Empty;
                row.Dot.style.backgroundColor = SlotColours[Mathf.Clamp(p.Slot - 1, 0, SlotColours.Length - 1)];
                row.Root.EnableInClassList("pm-player--local", p.IsLocal);

                int ping = p.PingMs;
                if (ping == row.LastPing)
                    continue;
                row.LastPing = ping;
                int quality = ping < 0 ? 0 : ping <= 80 ? 3 : ping <= 160 ? 2 : 1;
                Color colour = quality == 3 ? new Color(0.30f, 0.80f, 0.45f)
                    : quality == 2 ? new Color(0.95f, 0.76f, 0.11f)
                    : quality == 1 ? new Color(0.90f, 0.33f, 0.27f)
                    : new Color(0.55f, 0.58f, 0.60f);
                row.Ping.text = p.IsHost ? "host" : ping < 0 ? "—" : $"{ping} ms";
                row.Ping.style.color = colour;
                for (int b = 0; b < 3; b++)
                    row.Bar[b].style.backgroundColor = b < quality ? colour : new Color(1f, 1f, 1f, 0.15f);
            }

            string code = GameSession.JoinCode;
            joinCodeLabel.text = string.IsNullOrEmpty(code) ? "Direct IP session" : $"Join code  {code}";
            copyCodeButton.style.display = string.IsNullOrEmpty(code) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
