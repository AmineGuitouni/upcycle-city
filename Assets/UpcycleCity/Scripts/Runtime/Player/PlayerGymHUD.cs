using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.UI;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Local player's HUD owner. Spawns the clean UI Toolkit <see cref="GameHud"/> (chips, hotbar, prompt,
    /// toasts, touch controls) and keeps the old IMGUI telemetry (FSM state, speed, grab reach, input scheme,
    /// controls legend) as a developer overlay drawn only after pressing F1 in the Editor / development builds.
    /// Enabled only for the local player (see NetworkPlayerController), so the HUD is local-only presentation.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Player Gym HUD")]
    public sealed class PlayerGymHUD : MonoBehaviour
    {
        [SerializeField] PlayerController player;
        [SerializeField] string stageTitle = "PHASE 1 · PLAYER GYM";
        [SerializeField] bool showControlsLegend = true;

        GameHud gameHud;

        /// <summary>The clean gameplay HUD this component owns (null until enabled).</summary>
        public GameHud GameHud => gameHud;

        public void ConfigureDisplay(string title, bool showControls)
        {
            if (!string.IsNullOrWhiteSpace(title))
                stageTitle = title;
            showControlsLegend = showControls;
            if (gameHud != null)
                gameHud.Bind(player, LocationFallback());
        }

        string LocationFallback()
        {
            string t = string.IsNullOrWhiteSpace(stageTitle) ? "Avenue Habib Bourguiba" : stageTitle;
            int dot = t.IndexOf('·');
            if (dot > 0)
                t = t.Substring(0, dot).Trim();
            return t.Length > 0 && char.IsUpper(t[t.Length - 1]) ? TitleCase(t) : t;
        }

        static string TitleCase(string upper)
        {
            char[] c = upper.ToLowerInvariant().ToCharArray();
            bool start = true;
            for (int i = 0; i < c.Length; i++)
            {
                if (start && char.IsLetter(c[i]))
                    c[i] = char.ToUpperInvariant(c[i]);
                start = c[i] == ' ' || c[i] == '-';
            }
            return new string(c);
        }

        void EnsureGameHud()
        {
            if (gameHud == null)
            {
                var go = new GameObject("GameHud");
                go.transform.SetParent(transform, false);
                gameHud = go.AddComponent<GameHud>();
            }
            gameHud.gameObject.SetActive(true);
            gameHud.Bind(player, LocationFallback());
        }

        struct Toast
        {
            public string Text;
            public Color Color;
            public float Remaining;
        }

        readonly List<Toast> toasts = new List<Toast>(8);
        float coinPulse;
        int coinDelta;
        PlayerBoosts boosts;

        Texture2D whiteTex;
        GUIStyle pillStyle;
        GUIStyle labelStyle;
        GUIStyle smallStyle;
        GUIStyle centerPromptStyle;
        GUIStyle slotLabelStyle;
        GUIStyle slotKeyStyle;
        bool stylesReady;

        static readonly Color PanelDark = new Color(0.09f, 0.11f, 0.13f, 0.90f);
        static readonly Color BrandGreen = new Color(0.180f, 0.545f, 0.341f, 0.96f);
        static readonly Color BrandRust = new Color(0.706f, 0.337f, 0.180f, 0.96f);
        static readonly Color CoinYellow = new Color(0.949f, 0.761f, 0.106f, 1f);
        static readonly Color SlotSelected = new Color(0.180f, 0.545f, 0.341f, 0.95f);
        static readonly Color SlotNormal = new Color(0.11f, 0.13f, 0.15f, 0.88f);

        void Awake()
        {
            if (player == null)
                player = GetComponent<PlayerController>() ?? PlayerContext.Active;
        }

        void OnEnable()
        {
            PlayerContext.PlayerRegistered += OnPlayerRegistered;
            BindPlayer(player != null ? player : PlayerContext.Active);
            EnsureGameHud();
        }

        void OnDisable()
        {
            PlayerContext.PlayerRegistered -= OnPlayerRegistered;
            UnbindPlayer();
            if (gameHud != null)
                gameHud.gameObject.SetActive(false);
        }

        void OnPlayerRegistered(PlayerController activePlayer)
        {
            if (player == null)
            {
                BindPlayer(activePlayer);
                if (gameHud != null)
                    gameHud.Bind(player, LocationFallback());
            }
        }

        void BindPlayer(PlayerController target)
        {
            if (target == null)
                return;
            UnbindPlayer();
            player = target;
            if (player.Interactor != null)
                player.Interactor.FeedbackRequested += AddToast;
            if (player.Inventory != null)
                player.Inventory.CoinsChanged += OnCoinsChanged;
        }

        void OnCoinsChanged(int total, int delta)
        {
            coinDelta = delta;
            coinPulse = 1f;
        }

        void UnbindPlayer()
        {
            if (player != null && player.Interactor != null)
                player.Interactor.FeedbackRequested -= AddToast;
            if (player != null && player.Inventory != null)
                player.Inventory.CoinsChanged -= OnCoinsChanged;
        }

        void OnDestroy()
        {
            if (whiteTex != null)
                Destroy(whiteTex);
        }

        void Update()
        {
            DevHud.Poll();
            float dt = Time.deltaTime;
            coinPulse = Mathf.MoveTowards(coinPulse, 0f, dt * 0.8f);
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                Toast t = toasts[i];
                t.Remaining -= dt;
                if (t.Remaining <= 0f)
                    toasts.RemoveAt(i);
                else
                    toasts[i] = t;
            }
        }

        void AddToast(string message, Color color)
        {
            if (toasts.Count >= 5)
                toasts.RemoveAt(0);
            toasts.Add(new Toast { Text = message, Color = color, Remaining = 2.6f });
        }

        void EnsureStyles()
        {
            if (stylesReady)
                return;

            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();

            pillStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
            };

            smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.86f, 0.9f, 0.92f) },
            };

            centerPromptStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            slotLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = Color.white },
            };

            slotKeyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.82f, 0.86f, 0.9f) },
            };

            stylesReady = true;
        }

        void OnGUI()
        {
            // Player-facing HUD is the UI Toolkit GameHud; this IMGUI telemetry is a dev overlay (F1)
            if (!DevHud.DebugVisible)
                return;
            if (player == null || player.Motor == null || player.StateMachine == null)
                return;

            EnsureStyles();
            if (player.Input != null && player.Input.InputMode == PlayerInputMode.UI)
                return;

            DrawTopLeftStatePanel();
            DrawTopRightCoinsAndControls();
        }

        // Legacy IMGUI widgets kept for reference/dev use (superseded by GameHud)
        void DrawLegacyWidgets()
        {
            DrawCrosshairAndPrompt();
            DrawToasts();
            DrawBoosts();
            DrawHotbar();
        }

        void DrawTopLeftStatePanel()
        {
            // Stage badge pill
            // FSM & Physics telemetry card (dev overlay, below the HUD location chip)
            DrawBox(new Rect(20, 60, 275, 108), PanelDark);
            string stateName = player.StateMachine.CurrentName.ToUpperInvariant();
            string grounded = player.Motor.IsGrounded ? $"GROUND ({player.Motor.SlopeAngle:0}°)" : "AIR";
            float reach = player.Grabber != null ? player.Grabber.EffectiveGrabReach : 0f;
            string scheme = player.Input != null && !string.IsNullOrEmpty(player.Input.CurrentControlScheme)
                ? player.Input.CurrentControlScheme
                : "Auto";

            GUI.Label(new Rect(32, 66, 255, 22), $"FSM STATE:  {stateName}", labelStyle);
            GUI.Label(new Rect(32, 90, 255, 20), $"Speed: {player.Motor.HorizontalSpeed:0.0} m/s   Vy: {player.Motor.VerticalSpeed:+0.0;-0.0;0.0}", smallStyle);
            GUI.Label(new Rect(32, 112, 255, 20), $"{grounded}   |   Grab Reach: {reach:0.0} m", smallStyle);
            GUI.Label(new Rect(32, 134, 255, 20), $"Input Scheme: {scheme}", smallStyle);
        }

        void DrawTopRightCoinsAndControls()
        {
            float w = 295f;
            if (!showControlsLegend)
                return;

            // Compact controls legend (dev overlay, under the telemetry card)
            DrawBox(new Rect(w - 275, 176, 275, 118), PanelDark);
            GUI.Label(new Rect(w - 263, 182, 255, 20), "WASD Move · Shift Sprint · Ctrl/C Crouch", smallStyle);
            GUI.Label(new Rect(w - 263, 202, 255, 20), "Space Jump (tap = short hop)", smallStyle);
            GUI.Label(new Rect(w - 263, 222, 255, 20), "LMB Grab Trash / Hold Throw · RMB Drop", smallStyle);
            GUI.Label(new Rect(w - 263, 242, 255, 20), "E Pick Up Tool/Coins or Interact · F Use Tool", smallStyle);
            GUI.Label(new Rect(w - 263, 262, 255, 20), "1-6 / Scroll Hotbar · Esc Unlock Cursor", smallStyle);
        }

        void DrawCrosshairAndPrompt()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            bool hasTarget = player.Interactor != null && !string.IsNullOrEmpty(player.Interactor.ActivePrompt);
            Color dotColor = hasTarget ? CoinYellow : Color.white;
            float dotSize = hasTarget ? 8f : 5f;
            DrawBox(new Rect(cx - dotSize * 0.5f, cy - dotSize * 0.5f, dotSize, dotSize), dotColor);

            // Throw charge meter
            if (player.Grabber != null && player.Grabber.IsChargingThrow)
            {
                float barW = 180f;
                float barH = 12f;
                float bx = cx - barW * 0.5f;
                float by = cy + 22f;
                DrawBox(new Rect(bx - 2, by - 2, barW + 4, barH + 4), PanelDark);
                float fill = player.Grabber.ThrowChargeNormalized;
                Color fillCol = Color.Lerp(BrandGreen, CoinYellow, fill);
                DrawBox(new Rect(bx, by, barW * fill, barH), fillCol);
            }

            if (hasTarget)
            {
                string prompt = player.Interactor.ActivePrompt;
                float boxW = Mathf.Clamp(prompt.Length * 8.6f + 36f, 220f, Screen.width - 80f);
                Rect rect = new Rect(cx - boxW * 0.5f, cy + 44f, boxW, 34f);
                DrawBox(rect, PanelDark);
                GUI.Label(rect, prompt, centerPromptStyle);
            }
        }

        void DrawToasts()
        {
            if (toasts.Count == 0)
                return;

            float cx = Screen.width * 0.5f;
            float y = 24f;
            for (int i = 0; i < toasts.Count; i++)
            {
                Toast toast = toasts[i];
                float alpha = Mathf.Clamp01(toast.Remaining * 1.4f);
                float boxW = Mathf.Clamp(toast.Text.Length * 8.4f + 34f, 200f, 520f);
                Rect rect = new Rect(cx - boxW * 0.5f, y, boxW, 30f);

                Color bg = toast.Color;
                bg.a = 0.92f * alpha;
                DrawBox(rect, bg);

                Color prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(rect, toast.Text, pillStyle);
                GUI.color = prev;
                y += 36f;
            }
        }

        void DrawBoosts()
        {
            if (boosts == null)
                boosts = player.GetComponent<PlayerBoosts>();
            if (boosts == null || boosts.ActiveBoosts.Count == 0)
                return;

            const float chipW = 150f, chipH = 26f, gap = 8f;
            int n = boosts.ActiveBoosts.Count;
            float x = (Screen.width - (n * chipW + (n - 1) * gap)) * 0.5f;
            float y = Screen.height - 78f - 20f - 28f - chipH - 10f;
            for (int i = 0; i < n; i++)
            {
                PlayerBoosts.Active boost = boosts.ActiveBoosts[i];
                Rect r = new Rect(x + i * (chipW + gap), y, chipW, chipH);
                DrawBox(r, PanelDark);
                DrawBox(new Rect(r.x, r.yMax - 4f, r.width * boost.Normalized, 4f), boost.Color);
                GUI.Label(new Rect(r.x + 8f, r.y, r.width - 12f, r.height - 3f), $"{boost.Label}  {Mathf.CeilToInt(boost.Remaining)}s", smallStyle);
            }
        }

        void DrawHotbar()
        {
            if (player.Inventory == null)
                return;

            int count = player.Inventory.SlotCount;
            float slotSize = 78f;
            float gap = 8f;
            float totalW = count * slotSize + (count - 1) * gap;
            float startX = (Screen.width - totalW) * 0.5f;
            float y = Screen.height - slotSize - 20f;

            // Header pill explaining inventory scope cleanly over bright ground
            float headerW = Mathf.Max(totalW, 430f);
            float headerX = (Screen.width - headerW) * 0.5f;
            DrawBox(new Rect(headerX, y - 28f, headerW, 22f), PanelDark);
            GUI.Label(new Rect(headerX, y - 28f, headerW, 22f), "TOOLS & EQUIPMENT  ·  Trash is grabbed by physics (1 at a time)", smallStyle);

            for (int i = 0; i < count; i++)
            {
                Rect r = new Rect(startX + i * (slotSize + gap), y, slotSize, slotSize);
                bool selected = i == player.Inventory.SelectedIndex;
                InventorySlot slot = player.Inventory.GetSlot(i);

                DrawBox(r, selected ? SlotSelected : SlotNormal);
                if (selected)
                    DrawBox(new Rect(r.x, r.y + r.height - 4f, r.width, 4f), CoinYellow);

                GUI.Label(new Rect(r.x + 6f, r.y + 4f, 20f, 18f), (i + 1).ToString(), slotKeyStyle);

                if (!slot.IsEmpty)
                {
                    DrawBox(new Rect(r.x + r.width - 14f, r.y + 6f, 8f, 8f), slot.Item.AccentColor);
                    string label = slot.Count > 1 ? $"{slot.Item.DisplayName}\nx{slot.Count}" : slot.Item.DisplayName;
                    GUI.Label(new Rect(r.x + 4f, r.y + 18f, r.width - 8f, r.height - 24f), label, slotLabelStyle);
                }
                else
                {
                    GUI.Label(new Rect(r.x + 4f, r.y + 24f, r.width - 8f, 26f), "—", slotLabelStyle);
                }
            }
        }

        void DrawBox(Rect rect, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = prev;
        }
    }
}
