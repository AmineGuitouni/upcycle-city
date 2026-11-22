using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UpcycleCity.Core;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Mobile-scaled UI Toolkit in-game co-op HUD overlay (only visible when online in gameplay; zero work offline):
    /// <list type="bullet">
    /// <item>Session pill showing role, join code (or Direct IP), player count, and Leave button (rebuilt only on change).</item>
    /// <item>Advance-warned city event notification &amp; server-clock countdown banner (rebuilt only when second/title changes).</item>
    /// <item>Pooled screen-space <c>"P2 · Name"</c> nameplates projected above remote players via <see cref="RuntimePanelUtils.CameraTransformWorldToPanel"/>.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Networking/Co-op In-Game HUD")]
    public sealed class CoopInGameHud : MonoBehaviour
    {
        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;

        UIDocument document;
        VisualElement root;
        Label sessionPillLabel;
        Button copyButton;
        VisualElement eventContainer;
        VisualElement eventPill;
        Label eventLabel;
        VisualElement nameplatesLayer;

        readonly List<Label> nameplatePool = new List<Label>(4);
        readonly List<string> cachedNameplateTexts = new List<string>(4);

        // Cached strings/state so steady-state gameplay allocates zero strings per frame
        bool hudVisible;
        bool cachedIsHost;
        CoopTransportMode cachedTransport;
        string cachedCodeOrEndpoint;
        int cachedPlayerCount = -1;
        int cachedMaxPlayers = -1;
        bool showingCopiedFeedback;
        float copiedFeedbackUntil;

        string cachedEventTitle = string.Empty;
        int cachedEventSeconds = -1;
        byte cachedEventSeverity;
        bool eventVisible;

        /// <summary>Current text shown on the in-game Copy code button (<c>"Copy"</c> or <c>"Copied!"</c>).</summary>
        public string CopyButtonText => copyButton != null ? copyButton.text : string.Empty;

        /// <summary>True when the in-game Copy code button is visible.</summary>
        public bool IsCopyButtonVisible => hudVisible && copyButton != null && copyButton.style.display != DisplayStyle.None;

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
            if (copyButton != null)
            {
                copyButton.text = "Copied!";
                copyButton.EnableInClassList("coop-hud-copy-btn--copied", true);
            }
            return true;
        }

        public void Configure(PanelSettings settings, StyleSheet sheet)
        {
            if (settings != null)
                panelSettings = settings;
            if (sheet != null)
                styleSheet = sheet;

            if (document != null)
            {
                if (panelSettings != null && document.panelSettings != panelSettings)
                    document.panelSettings = panelSettings;
                if (root != null && styleSheet != null && !root.styleSheets.Contains(styleSheet))
                    root.styleSheets.Add(styleSheet);
            }
        }

        void Awake()
        {
            if (CoopSessionManager.Instance != null && CoopSessionManager.Instance.Settings != null)
            {
                if (panelSettings == null)
                    panelSettings = CoopSessionManager.Instance.Settings.PanelSettings;
                if (styleSheet == null)
                    styleSheet = CoopSessionManager.Instance.Settings.CoopMenuStyleSheet;
            }

            var docGo = new GameObject("CoopInGameHudDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 35;
            docGo.SetActive(true);

            BuildUi();
            SetHudVisible(false);
        }

        void BuildUi()
        {
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("coop-hud-root");
            root.pickingMode = PickingMode.Ignore;

            // Pooled remote player nameplate layer (behind top session bar)
            nameplatesLayer = new VisualElement();
            nameplatesLayer.AddToClassList("coop-hud-nameplates");
            nameplatesLayer.pickingMode = PickingMode.Ignore;
            root.Add(nameplatesLayer);

            // Top-left session pill + Copy + Leave buttons
            var bar = new VisualElement();
            bar.AddToClassList("coop-hud-bar");
            bar.pickingMode = PickingMode.Ignore;
            root.Add(bar);

            var pill = new VisualElement();
            pill.AddToClassList("coop-hud-pill");
            pill.pickingMode = PickingMode.Ignore;
            sessionPillLabel = new Label();
            sessionPillLabel.AddToClassList("coop-hud-pill-text");
            sessionPillLabel.pickingMode = PickingMode.Ignore;
            pill.Add(sessionPillLabel);
            bar.Add(pill);

            copyButton = new Button(OnCopyClicked) { text = "Copy" };
            copyButton.AddToClassList("coop-hud-copy-btn");
            bar.Add(copyButton);

            var leaveBtn = new Button(OnLeaveClicked) { text = "Leave" };
            leaveBtn.AddToClassList("coop-hud-leave-btn");
            bar.Add(leaveBtn);

            // Top-center city event banner
            eventContainer = new VisualElement();
            eventContainer.AddToClassList("coop-hud-event");
            eventContainer.pickingMode = PickingMode.Ignore;
            eventContainer.style.display = DisplayStyle.None;
            root.Add(eventContainer);

            eventPill = new VisualElement();
            eventPill.AddToClassList("coop-hud-event-pill");
            eventPill.pickingMode = PickingMode.Ignore;
            eventLabel = new Label();
            eventLabel.AddToClassList("coop-hud-event-text");
            eventLabel.pickingMode = PickingMode.Ignore;
            eventPill.Add(eventLabel);
            eventContainer.Add(eventPill);
        }

        void OnCopyClicked()
        {
            CopySessionCode();
        }

        void OnLeaveClicked()
        {
            CoopSessionManager.Instance?.LeaveSession("You left the co-op session.");
        }

        void LateUpdate()
        {
            CoopSessionManager sm = CoopSessionManager.Instance;
            bool shouldShow = sm != null && sm.State == CoopSessionState.InGame && NetSession.IsOnline;
            if (!shouldShow)
            {
                if (hudVisible)
                    SetHudVisible(false);
                return;
            }

            if (!hudVisible)
                SetHudVisible(true);

            RefreshSessionBarIfChanged(sm);
            RefreshCopyFeedbackTimer();
            RefreshEventBannerIfChanged();
            UpdateRemoteNameplates();
        }

        void SetHudVisible(bool visible)
        {
            hudVisible = visible;
            if (root != null)
                root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

            if (!visible)
            {
                ResetCopyButtonVisual();
                for (int i = 0; i < nameplatePool.Count; i++)
                    nameplatePool[i].style.display = DisplayStyle.None;
            }
        }

        void RefreshCopyFeedbackTimer()
        {
            if (showingCopiedFeedback && Time.unscaledTime >= copiedFeedbackUntil)
                ResetCopyButtonVisual();
        }

        void ResetCopyButtonVisual()
        {
            showingCopiedFeedback = false;
            copiedFeedbackUntil = 0f;
            if (copyButton != null)
            {
                copyButton.text = "Copy";
                copyButton.EnableInClassList("coop-hud-copy-btn--copied", false);
            }
        }

        void RefreshSessionBarIfChanged(CoopSessionManager sm)
        {
            bool isHost = NetSession.IsHost;
            CoopTransportMode mode = sm.ActiveTransportMode;
            string codeOrEndpoint = sm.ShareableSessionCode;
            int count = Mathf.Max(1, sm.ConnectedPlayerCount);
            int max = sm.MaxPlayers;

            if (isHost == cachedIsHost
                && mode == cachedTransport
                && string.Equals(codeOrEndpoint, cachedCodeOrEndpoint, System.StringComparison.Ordinal)
                && count == cachedPlayerCount
                && max == cachedMaxPlayers)
            {
                return;
            }

            cachedIsHost = isHost;
            cachedTransport = mode;
            cachedCodeOrEndpoint = codeOrEndpoint;
            cachedPlayerCount = count;
            cachedMaxPlayers = max;

            string codePart = mode == CoopTransportMode.Relay && !string.IsNullOrEmpty(codeOrEndpoint)
                ? $"CODE: {codeOrEndpoint}"
                : $"DIRECT · {codeOrEndpoint}";
            string rolePart = isHost ? "HOST" : "CO-OP";
            sessionPillLabel.text = $"{rolePart} · {codePart} · {count}/{max}";

            if (copyButton != null)
                copyButton.style.display = string.IsNullOrEmpty(codeOrEndpoint) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void RefreshEventBannerIfChanged()
        {
            IGameAuthority authority = GameAuthority.Current;
            float remaining = authority != null ? authority.ActiveEventTimeRemaining : 0f;
            string title = authority != null ? authority.ActiveEventTitle : string.Empty;
            byte severity = authority != null ? authority.ActiveEventSeverity : (byte)0;

            bool active = remaining > 0.05f && !string.IsNullOrEmpty(title);
            if (!active)
            {
                if (eventVisible)
                {
                    eventVisible = false;
                    eventContainer.style.display = DisplayStyle.None;
                }
                return;
            }

            int seconds = Mathf.CeilToInt(remaining);
            if (!eventVisible)
            {
                eventVisible = true;
                eventContainer.style.display = DisplayStyle.Flex;
            }

            if (seconds != cachedEventSeconds
                || severity != cachedEventSeverity
                || !string.Equals(title, cachedEventTitle, System.StringComparison.Ordinal))
            {
                cachedEventSeconds = seconds;
                cachedEventSeverity = severity;
                cachedEventTitle = title;
                eventLabel.text = $"EVENT: {title}  ·  {seconds}s";
                eventPill.EnableInClassList("coop-hud-event-pill--urgent", severity >= 2);
            }
        }

        void UpdateRemoteNameplates()
        {
            Camera cam = Camera.main;
            IPanel panel = root != null ? root.panel : null;
            IReadOnlyList<NetworkPlayerController> remotes = NetworkPlayerController.RemotePlayers;

            int visibleCount = 0;
            if (cam != null && panel != null)
            {
                for (int i = 0; i < remotes.Count; i++)
                {
                    NetworkPlayerController remote = remotes[i];
                    if (remote == null || !remote.isActiveAndEnabled)
                        continue;

                    Vector3 worldPos = remote.NameplateWorldPosition;
                    Vector3 viewportPos = cam.WorldToViewportPoint(worldPos);
                    if (viewportPos.z <= 0.25f || viewportPos.z > 42f
                        || viewportPos.x < -0.1f || viewportPos.x > 1.1f
                        || viewportPos.y < -0.1f || viewportPos.y > 1.15f)
                    {
                        continue;
                    }

                    Vector2 panelPos = RuntimePanelUtils.CameraTransformWorldToPanel(panel, worldPos, cam);
                    Label plate = GetOrCreateNameplate(visibleCount);
                    string labelText = remote.DisplayLabel;
                    if (!string.Equals(cachedNameplateTexts[visibleCount], labelText, System.StringComparison.Ordinal))
                    {
                        cachedNameplateTexts[visibleCount] = labelText;
                        plate.text = labelText;
                    }

                    plate.style.left = panelPos.x;
                    plate.style.top = panelPos.y;
                    plate.style.display = DisplayStyle.Flex;
                    visibleCount++;
                }
            }

            for (int i = visibleCount; i < nameplatePool.Count; i++)
                nameplatePool[i].style.display = DisplayStyle.None;
        }

        Label GetOrCreateNameplate(int index)
        {
            while (nameplatePool.Count <= index)
            {
                var label = new Label();
                label.AddToClassList("coop-hud-nameplate");
                label.pickingMode = PickingMode.Ignore;
                label.style.display = DisplayStyle.None;
                nameplatesLayer.Add(label);
                nameplatePool.Add(label);
                cachedNameplateTexts.Add(string.Empty);
            }
            return nameplatePool[index];
        }
    }
}
