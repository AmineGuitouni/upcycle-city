using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UpcycleCity.Character;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Co-op "crew" card shown over the Character Creator while a co-op party is in Character Creation
    /// (<see cref="CoopSessionManager.IsInCharacterCreation"/>): the roster with each player's ready state, a
    /// READY / CHANGE LOOK toggle, the host's START button (enabled once everyone is ready) and LEAVE.
    /// It also routes the creator's buttons: Start becomes READY in co-op and starts solo gameplay offline;
    /// Menu leaves the session first. Added automatically to the persistent <see cref="CoopSessionManager"/>.
    /// Styled by <c>UI/CoopMenu.uss</c> (<c>coop-crew-*</c>).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Networking/Co-op Character Lobby Panel")]
    public sealed class CoopCharacterLobbyPanel : MonoBehaviour
    {
        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;

        UIDocument document;
        VisualElement root, card, rosterContainer;
        Label codeLabel, hintLabel;
        Button readyButton, startButton;
        bool visible;
        CoopSessionManager boundSession;
        CharacterCreatorController creator;

        /// <summary>True while the crew card is shown (co-op party in the Character Creator).</summary>
        public bool IsVisible => visible;

        /// <summary>Current READY toggle text (<c>READY</c> or <c>CHANGE LOOK</c>).</summary>
        public string ReadyButtonText => readyButton != null ? readyButton.text : string.Empty;

        /// <summary>True when the host's START button is shown and enabled.</summary>
        public bool IsStartButtonEnabled => visible
            && startButton != null
            && startButton.style.display != DisplayStyle.None
            && startButton.enabledSelf;

        public void Configure(PanelSettings settings, StyleSheet sheet)
        {
            panelSettings = settings;
            styleSheet = sheet;
            if (document != null && panelSettings != null)
                document.panelSettings = panelSettings;
            if (root != null && styleSheet != null && !root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
        }

        void OnEnable()
        {
            CharacterCreatorController.StartRequested += OnCreatorStartRequested;
            CharacterCreatorController.MenuRequested += OnCreatorMenuRequested;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDisable()
        {
            CharacterCreatorController.StartRequested -= OnCreatorStartRequested;
            CharacterCreatorController.MenuRequested -= OnCreatorMenuRequested;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            BindSession(null);
        }

        void Start()
        {
            EnsureDocument();
            BindSession(GetComponent<CoopSessionManager>());
            Refresh();
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            creator = null;
            Refresh();
        }

        void BindSession(CoopSessionManager sm)
        {
            if (boundSession != null)
            {
                boundSession.StateChanged -= OnSessionStateChanged;
                boundSession.LobbyChanged -= Refresh;
                boundSession.ErrorOccurred -= OnSessionError;
            }
            boundSession = sm;
            if (boundSession != null)
            {
                boundSession.StateChanged += OnSessionStateChanged;
                boundSession.LobbyChanged += Refresh;
                boundSession.ErrorOccurred += OnSessionError;
            }
        }

        void OnSessionStateChanged(CoopSessionState _) => Refresh();

        void OnSessionError(string message)
        {
            if (visible && hintLabel != null && !string.IsNullOrEmpty(message))
                hintLabel.text = message;
        }

        // ---------------------------------------------------------------- Creator routing

        bool OnCreatorStartRequested(CharacterCreatorController source)
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm == null)
                return false;

            creator = source;
            if (sm.State == CoopSessionState.Lobby)
            {
                sm.SetLocalReady(true, source.CurrentAppearance);
                source.SetCreatorPanelVisible(false);
                Refresh();
                return true;
            }

            if (sm.State == CoopSessionState.Offline)
            {
                sm.StartSoloGameplay(source.GameplayScene);
                return true;
            }
            return false;
        }

        bool OnCreatorMenuRequested(CharacterCreatorController source)
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            if (sm == null || sm.State == CoopSessionState.Offline)
                return false;
            sm.LeaveSession();
            return true;
        }

        // ---------------------------------------------------------------- Actions (also used by tests)

        /// <summary>READY toggle: confirms the creator's current look, or un-readies to edit it again.</summary>
        public void PressReady()
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            CharacterCreatorController c = ResolveCreator();
            if (sm == null || c == null)
                return;

            if (sm.LocalPlayerReady)
            {
                sm.SetLocalReady(false, c.CurrentAppearance);
                c.SetCreatorPanelVisible(true);
                Refresh();
            }
            else
            {
                c.Confirm(); // saves the look, then routes back through OnCreatorStartRequested
            }
        }

        /// <summary>Host START: loads the gameplay scene for everyone once all players are ready.</summary>
        public void PressStart() => CoopSessionManager.Instance?.StartCoopGameplay();

        /// <summary>Leaves the co-op session and returns to the Main Menu.</summary>
        public void PressLeave() => CoopSessionManager.Instance?.LeaveSession();

        CharacterCreatorController ResolveCreator()
        {
            if (creator == null)
                creator = FindAnyObjectByType<CharacterCreatorController>();
            return creator;
        }

        // ---------------------------------------------------------------- UI

        void EnsureDocument()
        {
            if (document != null)
                return;

            var docGo = new GameObject("CoopCrewDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 45;
            docGo.SetActive(true);
            BuildUi();
        }

        void BuildUi()
        {
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("coop-crew-root");
            root.pickingMode = PickingMode.Ignore;

            card = new VisualElement();
            card.AddToClassList("coop-crew-card");
            root.Add(card);

            var header = new VisualElement();
            header.AddToClassList("coop-crew-header");
            header.Add(MakeLabel("CO-OP CREW", "coop-crew-title"));
            codeLabel = MakeLabel(string.Empty, "coop-crew-code");
            header.Add(codeLabel);
            card.Add(header);

            rosterContainer = new VisualElement();
            rosterContainer.AddToClassList("coop-crew-roster");
            card.Add(rosterContainer);

            hintLabel = MakeLabel(string.Empty, "coop-crew-hint");
            card.Add(hintLabel);

            readyButton = MakeButton("READY", "coop-btn coop-btn--small coop-btn--amber coop-crew-btn", PressReady);
            card.Add(readyButton);
            startButton = MakeButton("START CLEANUP", "coop-btn coop-btn--small coop-btn--green coop-crew-btn", PressStart);
            card.Add(startButton);
            card.Add(MakeButton("Leave", "coop-btn coop-btn--small coop-btn--muted coop-crew-btn", PressLeave));

            root.RegisterCallback<GeometryChangedEvent>(_ =>
                root.EnableInClassList("coop-crew-root--portrait", Screen.height > Screen.width));
            root.style.display = DisplayStyle.None;
        }

        void Refresh()
        {
            if (this == null)
                return;
            EnsureDocument();

            CoopSessionManager sm = CoopSessionManager.Instance;
            bool show = sm != null && sm.IsInCharacterCreation;
            visible = show;
            root.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                // Back to solo behaviour on a creator we touched (only searched for while showing)
                if (creator != null)
                {
                    creator.SetStartLabel("Start");
                    creator.SetBrandVisible(true);
                    creator.SetCreatorPanelVisible(true);
                }
                return;
            }

            CharacterCreatorController c = ResolveCreator();
            if (c != null)
            {
                c.SetStartLabel("Ready");
                c.SetBrandVisible(false); // the crew card takes the right-hand side
            }

            string code = sm.ShareableSessionCode;
            codeLabel.text = string.IsNullOrEmpty(code) ? string.Empty : code;

            rosterContainer.Clear();
            IReadOnlyList<CoopLobbyPlayer> players = sm.LobbyPlayers;
            for (int i = 0; i < players.Count; i++)
            {
                CoopLobbyPlayer p = players[i];
                var row = new VisualElement();
                row.AddToClassList("coop-crew-row");
                row.Add(MakeLabel(p.FormattedTag, "coop-roster-slot"));
                row.Add(MakeLabel(p.IsHost ? $"{p.displayName} (host)" : p.displayName.ToString(), "coop-crew-name"));
                Label status = MakeLabel(p.IsReady ? "READY" : "choosing…", "coop-crew-status");
                status.EnableInClassList("coop-crew-status--ready", p.IsReady);
                row.Add(status);
                rosterContainer.Add(row);
            }

            bool localReady = sm.LocalPlayerReady;
            readyButton.text = localReady ? "CHANGE LOOK" : "READY";
            readyButton.EnableInClassList("coop-btn--amber", !localReady);
            readyButton.EnableInClassList("coop-btn--muted", localReady);
            if (c != null && !localReady && !c.IsCreatorPanelVisible)
                c.SetCreatorPanelVisible(true);

            bool isHost = NetSession.IsHost;
            bool allReady = sm.AllPlayersReady;
            startButton.style.display = isHost ? DisplayStyle.Flex : DisplayStyle.None;
            startButton.SetEnabled(isHost && allReady);

            int readyCount = sm.ReadyPlayerCount;
            if (allReady)
                hintLabel.text = isHost ? "Everyone is ready. Start the cleanup!" : "Everyone is ready. Waiting for the host to start...";
            else
                hintLabel.text = $"{readyCount}/{players.Count} ready. Pick your look, then press READY.";
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
