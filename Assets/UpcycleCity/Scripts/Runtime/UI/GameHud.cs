using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;

namespace UpcycleCity.UI
{
    /// <summary>
    /// Clean phone-first gameplay HUD (UI Toolkit, 1080×608 reference): location chip, air &amp; clean chip with
    /// the four stream colours, team coin pill, slim smog / city-event chips, crosshair + contextual keycap
    /// prompt, throw-charge bar, boost chips, rounded icon hotbar, feedback toasts and citizen reaction cues.
    /// Local-only presentation: created by <see cref="PlayerGymHUD"/> for the local player only.
    /// Text is rebuilt only when a value changes, so steady-state frames allocate nothing.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/UI/Game HUD")]
    public sealed class GameHud : MonoBehaviour
    {
        public const string PanelSettingsResource = "CraftingPanelSettings";
        public const string StyleSheetResource = "GameHud";

        const int MaxToasts = 4;
        const int MaxPromptParts = 4;
        const int MaxCues = 6;
        const float ToastSeconds = 2.8f;

        static readonly Color CleanGreen = new Color(0.30f, 0.80f, 0.48f);
        static readonly Color WarnAmber = new Color(0.96f, 0.72f, 0.18f);
        static readonly Color PollutedRed = new Color(0.93f, 0.36f, 0.28f);

        // Cached "0".."999" strings: number labels never allocate after warm-up
        static string[] numberCache;

        PlayerController player;
        PlayerInventory inventory;
        PlayerInteractor interactor;
        PhysicsGrabController grabber;
        PlayerBoosts boosts;
        string fallbackLocation = "Avenue Habib Bourguiba";

        UIDocument document;
        VisualElement root;
        VisualElement hudLayer;
        TouchControls touch;

        Label locationBadge;
        Label locationLabel;
        Label airValueLabel;
        Label coinLabel;
        Label coinDeltaLabel;
        VisualElement smogChip;
        Label smogLabel;
        VisualElement eventChip;
        Label eventTitleLabel;
        Label eventTimeLabel;

        VisualElement crosshair;
        VisualElement promptRow;
        readonly VisualElement[] promptParts = new VisualElement[MaxPromptParts];
        readonly Label[] promptKeys = new Label[MaxPromptParts];
        readonly Label[] promptTexts = new Label[MaxPromptParts];
        VisualElement chargeTrack;
        VisualElement chargeFill;

        VisualElement boostRow;
        readonly List<VisualElement> boostChips = new List<VisualElement>(4);
        readonly List<Label> boostNames = new List<Label>(4);
        readonly List<Label> boostTimes = new List<Label>(4);
        readonly List<VisualElement> boostBars = new List<VisualElement>(4);

        VisualElement hotbar;
        readonly List<VisualElement> slotRoots = new List<VisualElement>(6);
        readonly List<VisualElement> slotIcons = new List<VisualElement>(6);
        readonly List<Label> slotFallbacks = new List<Label>(6);
        readonly List<Label> slotCounts = new List<Label>(6);
        Label hotbarItemName;

        VisualElement toastStack;
        readonly Label[] toastLabels = new Label[MaxToasts];
        readonly float[] toastRemaining = new float[MaxToasts];

        VisualElement cueLayer;
        readonly Label[] cueLabels = new Label[MaxCues];

        // Change caches
        string cachedPrompt = string.Empty;
        bool cachedTouch;
        int cachedAir = -1;
        int cachedAirBand = -1;
        CityDistrict cachedDistrict;
        bool cachedDistrictResolved;
        int cachedDistrictBand = -1;
        int cachedSmogState = -1;
        string cachedEventTitle = string.Empty;
        int cachedEventSeconds = -1;
        bool cachedHidden;
        bool cachedHasTarget;
        bool cachedCharging;
        float coinDeltaTimer;
        float slowTick;
        bool hotbarDirty = true;
        float hotbarNameTimer;

        /// <summary>True while the on-screen touch controls are shown.</summary>
        public bool TouchControlsVisible => VirtualTouchInput.Active;

        /// <summary>The HUD's root (tests / tooling).</summary>
        public VisualElement Root => root;

        /// <summary>Binds the HUD to the local player. Called by <see cref="PlayerGymHUD"/>.</summary>
        public void Bind(PlayerController target, string locationFallback)
        {
            if (!string.IsNullOrWhiteSpace(locationFallback))
                fallbackLocation = locationFallback;
            if (target == player)
                return;
            Unbind();
            player = target;
            if (player == null)
                return;
            inventory = player.Inventory;
            interactor = player.Interactor;
            grabber = player.Grabber;
            boosts = player.GetComponent<PlayerBoosts>();
            if (interactor != null)
                interactor.FeedbackRequested += ShowToast;
            if (inventory != null)
            {
                inventory.CoinsChanged += OnCoinsChanged;
                inventory.Changed += MarkHotbarDirty;
                inventory.SelectedSlotChanged += OnSelectedSlotChanged;
                SetCoins(inventory.Coins);
            }
            hotbarDirty = true;
        }

        void Unbind()
        {
            if (interactor != null)
                interactor.FeedbackRequested -= ShowToast;
            if (inventory != null)
            {
                inventory.CoinsChanged -= OnCoinsChanged;
                inventory.Changed -= MarkHotbarDirty;
                inventory.SelectedSlotChanged -= OnSelectedSlotChanged;
            }
            player = null;
            inventory = null;
            interactor = null;
            grabber = null;
            boosts = null;
        }

        static string Num(int value)
        {
            if (numberCache == null)
            {
                numberCache = new string[1000];
                for (int i = 0; i < numberCache.Length; i++)
                    numberCache[i] = i.ToString();
            }
            return value >= 0 && value < numberCache.Length ? numberCache[value] : value.ToString();
        }

        void Awake()
        {
            var docGo = new GameObject("GameHudDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = Resources.Load<PanelSettings>(PanelSettingsResource);
            document.sortingOrder = -20;
            docGo.SetActive(true);
            Build();
        }

        void OnEnable()
        {
            if (document != null)
                document.gameObject.SetActive(true);
            if (root == null && document != null)
                Build();
            hotbarDirty = true;
        }

        void OnDisable()
        {
            touch?.Shutdown();
            if (document != null)
                document.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            Unbind();
        }

        // ------------------------------------------------------------------ build

        static VisualElement El(string cls, VisualElement parent = null)
        {
            var e = new VisualElement();
            e.AddToClassList(cls);
            e.pickingMode = PickingMode.Ignore;
            parent?.Add(e);
            return e;
        }

        static Label Lbl(string cls, string text, VisualElement parent)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            l.pickingMode = PickingMode.Ignore;
            parent.Add(l);
            return l;
        }

        static VisualElement Icon(string key, string cls, VisualElement parent)
        {
            var e = El(cls, parent);
            Sprite s = IconLibrary.Active != null ? IconLibrary.Active.Get(key) : null;
            if (s != null)
                e.style.backgroundImage = new StyleBackground(s);
            return e;
        }

        void Build()
        {
            root = document.rootVisualElement;
            if (root == null)
                return;
            root.Clear();
            StyleSheet sheet = Resources.Load<StyleSheet>(StyleSheetResource);
            if (sheet != null && !root.styleSheets.Contains(sheet))
                root.styleSheets.Add(sheet);
            root.AddToClassList("uc-hud-root");
            root.pickingMode = PickingMode.Ignore;

            cueLayer = El("uc-cue-layer", root);
            for (int i = 0; i < MaxCues; i++)
            {
                cueLabels[i] = Lbl("uc-cue", string.Empty, cueLayer);
                cueLabels[i].style.display = DisplayStyle.None;
            }

            hudLayer = El("uc-hud", root);
            touch = new TouchControls(hudLayer);

            // ---- Top-left: location
            VisualElement topLeft = El("uc-top-left", hudLayer);
            VisualElement loc = El("uc-chip", topLeft);
            loc.AddToClassList("uc-chip--location");
            locationBadge = Lbl("uc-badge", "CITY", loc);
            locationLabel = Lbl("uc-chip-title", fallbackLocation, loc);

            // ---- Top-right: air & clean + coins
            VisualElement topRight = El("uc-top-right", hudLayer);
            VisualElement air = El("uc-chip", topRight);
            air.AddToClassList("uc-chip--air");
            El("uc-air-dot", air);
            Lbl("uc-chip-caption", "Air & Clean", air);
            airValueLabel = Lbl("uc-chip-value", "--%", air);
            El("uc-chip-divider", air);
            VisualElement streams = El("uc-streams", air);
            El("uc-stream", streams).AddToClassList("uc-stream--paper");
            El("uc-stream", streams).AddToClassList("uc-stream--plastic");
            El("uc-stream", streams).AddToClassList("uc-stream--glass");
            El("uc-stream", streams).AddToClassList("uc-stream--cans");

            VisualElement coins = El("uc-chip", topRight);
            coins.AddToClassList("uc-chip--coins");
            El("uc-coin-icon", coins); // kit coin art (HUD/hud_coin.png) via GameHud.uss
            coinLabel = Lbl("uc-coin-value", "0", coins);
            coinDeltaLabel = Lbl("uc-coin-delta", string.Empty, topRight);
            coinDeltaLabel.style.opacity = 0f;

            // ---- Top-centre: event + smog chips, then toasts
            VisualElement topCenter = El("uc-top-center", hudLayer);
            eventChip = El("uc-chip", topCenter);
            eventChip.AddToClassList("uc-chip--event");
            Lbl("uc-badge", "EVENT", eventChip).AddToClassList("uc-badge--event");
            eventTitleLabel = Lbl("uc-chip-title", string.Empty, eventChip);
            eventTimeLabel = Lbl("uc-chip-value", string.Empty, eventChip);
            eventChip.style.display = DisplayStyle.None;

            smogChip = El("uc-chip", topCenter);
            smogChip.AddToClassList("uc-chip--smog");
            El("uc-smog-dot", smogChip);
            smogLabel = Lbl("uc-chip-title", string.Empty, smogChip);
            smogChip.style.display = DisplayStyle.None;

            toastStack = El("uc-toasts", topCenter);
            for (int i = 0; i < MaxToasts; i++)
            {
                toastLabels[i] = Lbl("uc-toast", string.Empty, toastStack);
                toastLabels[i].style.display = DisplayStyle.None;
            }

            // ---- Centre: crosshair, prompt, charge
            VisualElement center = El("uc-center", hudLayer);
            crosshair = El("uc-crosshair", center);
            El("uc-crosshair-dot", crosshair);
            chargeTrack = El("uc-charge", center);
            chargeFill = El("uc-charge-fill", chargeTrack);
            chargeTrack.style.display = DisplayStyle.None;
            promptRow = El("uc-prompt", center);
            for (int i = 0; i < MaxPromptParts; i++)
            {
                VisualElement part = El("uc-prompt-part", promptRow);
                promptKeys[i] = Lbl("uc-keycap", string.Empty, part);
                promptTexts[i] = Lbl("uc-prompt-text", string.Empty, part);
                promptParts[i] = part;
                part.style.display = DisplayStyle.None;
            }
            promptRow.style.display = DisplayStyle.None;

            // ---- Bottom: boosts, item name, hotbar
            VisualElement bottom = El("uc-bottom", hudLayer);
            boostRow = El("uc-boosts", bottom);
            hotbarItemName = Lbl("uc-hotbar-name", string.Empty, bottom);
            hotbarItemName.style.opacity = 0f;
            hotbar = El("uc-hotbar", bottom);

            slotRoots.Clear();
            slotIcons.Clear();
            slotFallbacks.Clear();
            slotCounts.Clear();
            boostChips.Clear();
            boostNames.Clear();
            boostTimes.Clear();
            boostBars.Clear();
            hotbarDirty = true;

            if (inventory != null)
                SetCoins(inventory.Coins);
        }

        void EnsureSlots(int count)
        {
            while (slotRoots.Count < count)
            {
                int index = slotRoots.Count;
                var slot = new VisualElement();
                slot.AddToClassList("uc-slot");
                slot.pickingMode = PickingMode.Position;
                slot.RegisterCallback<PointerDownEvent>(_ => inventory?.SelectSlot(index));
                Lbl("uc-slot-key", Num(index + 1), slot);
                slotIcons.Add(El("uc-slot-icon", slot));
                slotFallbacks.Add(Lbl("uc-slot-fallback", string.Empty, slot));
                slotCounts.Add(Lbl("uc-slot-count", string.Empty, slot));
                hotbar.Add(slot);
                slotRoots.Add(slot);
            }
            for (int i = 0; i < slotRoots.Count; i++)
                slotRoots[i].style.display = i < count ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ------------------------------------------------------------------ events

        void MarkHotbarDirty() => hotbarDirty = true;

        void OnSelectedSlotChanged(int index, InventorySlot slot)
        {
            hotbarDirty = true;
            hotbarItemName.text = slot.IsEmpty ? string.Empty : slot.Item.DisplayName;
            hotbarNameTimer = slot.IsEmpty ? 0f : 1.6f;
        }

        void OnCoinsChanged(int total, int delta)
        {
            SetCoins(total);
            if (delta == 0 || coinDeltaLabel == null)
                return;
            coinDeltaLabel.text = delta > 0 ? "+" + Num(delta) : "-" + Num(-delta);
            coinDeltaLabel.EnableInClassList("uc-coin-delta--neg", delta < 0);
            coinDeltaTimer = 1.4f;
        }

        int shownCoins = int.MinValue;

        void SetCoins(int total)
        {
            total = Mathf.Max(0, total);
            if (coinLabel == null || total == shownCoins)
                return;
            shownCoins = total;
            coinLabel.text = Num(total);
        }

        /// <summary>Shows a short feedback toast (also used by gameplay feedback events).</summary>
        public void ShowToast(string message, Color color)
        {
            if (string.IsNullOrEmpty(message) || toastLabels[0] == null)
                return;
            // Shift older toasts down, newest on top
            for (int i = MaxToasts - 1; i > 0; i--)
            {
                toastRemaining[i] = toastRemaining[i - 1];
                toastLabels[i].text = toastLabels[i - 1].text;
                toastLabels[i].style.borderLeftColor = toastLabels[i - 1].style.borderLeftColor;
            }
            toastRemaining[0] = ToastSeconds;
            toastLabels[0].text = message;
            color.a = 1f;
            toastLabels[0].style.borderLeftColor = color;
            for (int i = 0; i < MaxToasts; i++)
                toastLabels[i].style.display = toastRemaining[i] > 0f ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ------------------------------------------------------------------ update

        void LateUpdate()
        {
            DevHud.Poll();
            if (root == null)
                return;

            float dt = Time.unscaledDeltaTime;
            UpdateToasts(dt);

            bool hidden = player == null
                || (player.Input != null && player.Input.InputMode == PlayerInputMode.UI);
            touch?.Tick(hidden);
            if (hidden != cachedHidden)
            {
                cachedHidden = hidden;
                hudLayer.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
                cueLayer.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (hidden)
                return;

            if (hotbarDirty)
                RefreshHotbar();

            // Some paths set coins without raising CoinsChanged (co-op team wallet mirror, debug credits): poll the number
            if (inventory != null)
                SetCoins(inventory.Coins);

            UpdatePrompt();
            UpdateCharge();
            UpdateBoosts();
            UpdateCues();

            if (coinDeltaTimer > 0f)
            {
                coinDeltaTimer -= dt;
                coinDeltaLabel.style.opacity = Mathf.Clamp01(coinDeltaTimer * 1.5f);
                coinDeltaLabel.style.translate = new Translate(0f, (1.4f - coinDeltaTimer) * -8f);
            }
            if (hotbarNameTimer > 0f)
            {
                hotbarNameTimer -= dt;
                hotbarItemName.style.opacity = Mathf.Clamp01(hotbarNameTimer * 2f);
            }

            slowTick -= dt;
            if (slowTick <= 0f)
            {
                slowTick = 0.25f;
                UpdateCityChips();
                UpdateEventChip();
            }
        }

        void UpdateToasts(float dt)
        {
            for (int i = 0; i < MaxToasts; i++)
            {
                if (toastRemaining[i] <= 0f)
                    continue;
                toastRemaining[i] -= dt;
                Label l = toastLabels[i];
                if (toastRemaining[i] <= 0f)
                    l.style.display = DisplayStyle.None;
                else
                    l.style.opacity = Mathf.Clamp01(toastRemaining[i] * 2.5f);
            }
        }

        void RefreshHotbar()
        {
            hotbarDirty = false;
            if (inventory == null)
                return;
            int count = inventory.SlotCount;
            EnsureSlots(count);
            IconLibrary icons = IconLibrary.Active;
            for (int i = 0; i < count; i++)
            {
                InventorySlot slot = inventory.GetSlot(i);
                VisualElement r = slotRoots[i];
                r.EnableInClassList("uc-slot--selected", i == inventory.SelectedIndex);
                r.EnableInClassList("uc-slot--empty", slot.IsEmpty);
                Sprite sprite = !slot.IsEmpty && icons != null ? icons.ForItem(slot.Item.ItemId) : null;
                slotIcons[i].style.backgroundImage = sprite != null ? new StyleBackground(sprite) : new StyleBackground(StyleKeyword.None);
                slotIcons[i].style.display = sprite != null ? DisplayStyle.Flex : DisplayStyle.None;
                string fallback = slot.IsEmpty || sprite != null ? string.Empty : Initials(slot.Item.DisplayName);
                slotFallbacks[i].text = fallback;
                slotFallbacks[i].style.color = slot.IsEmpty ? Color.white : slot.Item.AccentColor;
                slotCounts[i].text = !slot.IsEmpty && slot.Count > 1 ? "x" + Num(slot.Count) : string.Empty;
            }
        }

        static string Initials(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "?";
            return name.Length <= 2 ? name.ToUpperInvariant() : name.Substring(0, 2).ToUpperInvariant();
        }

        void UpdatePrompt()
        {
            string prompt = interactor != null ? interactor.ActivePrompt : string.Empty;
            bool touch = VirtualTouchInput.Active;
            bool hasTarget = !string.IsNullOrEmpty(prompt);
            if (hasTarget != cachedHasTarget)
            {
                cachedHasTarget = hasTarget;
                crosshair.EnableInClassList("uc-crosshair--target", hasTarget);
            }

            if (touch == cachedTouch && string.Equals(prompt, cachedPrompt, System.StringComparison.Ordinal))
                return;
            cachedPrompt = prompt ?? string.Empty;
            cachedTouch = touch;

            if (!hasTarget)
            {
                promptRow.style.display = DisplayStyle.None;
                return;
            }

            promptRow.style.display = DisplayStyle.Flex;
            int part = 0;
            int start = 0;
            string p = cachedPrompt;
            for (int i = 0; i <= p.Length && part < MaxPromptParts; i++)
            {
                bool split = i == p.Length || p[i] == '|' || (p[i] == '·' && i > 0 && p[i - 1] == ' ' && i + 1 < p.Length && p[i + 1] == ' ');
                if (!split)
                    continue;
                string segment = p.Substring(start, i - start).Trim();
                start = i + 1;
                if (segment.Length == 0)
                    continue;
                string key = string.Empty;
                string text = segment;
                if (segment[0] == '[')
                {
                    int close = segment.IndexOf(']');
                    if (close > 0)
                    {
                        key = MapKey(segment.Substring(1, close - 1).Trim(), touch);
                        text = segment.Substring(close + 1).Trim();
                    }
                }
                promptKeys[part].text = key;
                promptKeys[part].style.display = key.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                promptTexts[part].text = text;
                promptParts[part].style.display = DisplayStyle.Flex;
                part++;
            }
            for (int i = part; i < MaxPromptParts; i++)
                promptParts[i].style.display = DisplayStyle.None;
        }

        /// <summary>Keyboard hints become the on-screen button names when touch controls are shown.</summary>
        static string MapKey(string key, bool touch)
        {
            if (!touch)
                return key;
            string k = key.ToUpperInvariant();
            if (k.StartsWith("LMB HOLD")) return "HOLD GRAB";
            if (k.StartsWith("LMB")) return "GRAB";
            if (k.StartsWith("RMB")) return "DROP";
            if (k == "E" || k.StartsWith("E ") || k.StartsWith("HOLD E")) return k.StartsWith("HOLD") ? "HOLD USE" : "USE";
            if (k == "F") return "TOOL";
            if (k == "SPACE") return "JUMP";
            return key;
        }

        void UpdateCharge()
        {
            bool charging = grabber != null && grabber.IsChargingThrow;
            if (charging != cachedCharging)
            {
                cachedCharging = charging;
                chargeTrack.style.display = charging ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (charging)
                chargeFill.style.width = Length.Percent(grabber.ThrowChargeNormalized * 100f);
        }

        void UpdateBoosts()
        {
            int n = boosts != null ? boosts.ActiveBoosts.Count : 0;
            while (boostChips.Count < n)
            {
                VisualElement chip = El("uc-boost", boostRow);
                boostNames.Add(Lbl("uc-boost-name", string.Empty, chip));
                boostTimes.Add(Lbl("uc-boost-time", string.Empty, chip));
                boostBars.Add(El("uc-boost-bar", chip));
                boostChips.Add(chip);
            }
            for (int i = 0; i < boostChips.Count; i++)
            {
                if (i >= n)
                {
                    boostChips[i].style.display = DisplayStyle.None;
                    continue;
                }
                PlayerBoosts.Active b = boosts.ActiveBoosts[i];
                boostChips[i].style.display = DisplayStyle.Flex;
                if (!ReferenceEquals(boostNames[i].text, b.Label))
                    boostNames[i].text = b.Label;
                string secs = Num(Mathf.CeilToInt(b.Remaining));
                if (!ReferenceEquals(boostTimes[i].text, secs))
                    boostTimes[i].text = secs;
                boostBars[i].style.width = Length.Percent(b.Normalized * 100f);
                boostBars[i].style.backgroundColor = b.Color;
            }
        }

        void UpdateCues()
        {
            CitizenSimulationManager sim = CitizenSimulationManager.Active;
            Camera cam = Camera.main;
            IPanel panel = root.panel;
            int shown = 0;
            if (sim != null && cam != null && panel != null)
            {
                IReadOnlyList<CitizenReactionCue> cues = sim.ActiveCues;
                for (int i = 0; i < cues.Count && shown < MaxCues; i++)
                {
                    CitizenReactionCue cue = cues[i];
                    if (cue.Anchor == null || string.IsNullOrEmpty(cue.Text))
                        continue;
                    Vector3 world = cue.Anchor.position + Vector3.up * 2.15f;
                    Vector3 vp = cam.WorldToViewportPoint(world);
                    if (vp.z <= 0.5f || vp.z > 18f || vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.05f || vp.y > 1.05f)
                        continue;
                    Vector2 pos = RuntimePanelUtils.CameraTransformWorldToPanel(panel, world, cam);
                    Label l = cueLabels[shown++];
                    if (!ReferenceEquals(l.text, cue.Text))
                        l.text = cue.Text;
                    Color accent = cue.AccentColor;
                    accent.a = 1f;
                    l.style.borderBottomColor = accent;
                    l.style.left = pos.x;
                    l.style.top = pos.y;
                    l.style.opacity = Mathf.Clamp01(cue.Remaining / 0.45f);
                    l.style.display = DisplayStyle.Flex;
                }
            }
            for (int i = shown; i < MaxCues; i++)
                if (cueLabels[i].style.display != DisplayStyle.None)
                    cueLabels[i].style.display = DisplayStyle.None;
        }

        void UpdateCityChips()
        {
            CityEcosystemManager eco = CityEcosystemManager.Active;

            // Location
            CityDistrict district = eco != null ? eco.PlayerDistrict : null;
            if (district != cachedDistrict || !cachedDistrictResolved)
            {
                cachedDistrict = district;
                cachedDistrictResolved = true;
                locationLabel.text = district != null ? district.DistrictName : fallbackLocation;
            }
            float localPollution = district != null ? district.CurrentPollution : (eco != null ? eco.CityPollution : 0f);
            int band = eco == null ? -1 : localPollution <= 0.25f ? 0 : localPollution <= 0.6f ? 1 : 2;
            if (band != cachedDistrictBand)
            {
                cachedDistrictBand = band;
                locationBadge.text = band == 0 ? "CLEAN" : band == 1 ? "HAZY" : band == 2 ? "SMOGGY" : "CITY";
                locationBadge.EnableInClassList("uc-badge--clean", band == 0);
                locationBadge.EnableInClassList("uc-badge--hazy", band == 1);
                locationBadge.EnableInClassList("uc-badge--smog", band == 2);
            }

            // Air & clean
            int air = eco != null ? Mathf.RoundToInt(Mathf.Clamp01(1f - eco.CityPollution) * 100f) : -1;
            if (air != cachedAir)
            {
                cachedAir = air;
                airValueLabel.text = air < 0 ? "--%" : Num(air) + "%";
                int airBand = air < 0 ? -1 : air >= 70 ? 0 : air >= 40 ? 1 : 2;
                if (airBand != cachedAirBand)
                {
                    cachedAirBand = airBand;
                    airValueLabel.style.color = airBand == 0 ? CleanGreen : airBand == 1 ? WarnAmber : airBand == 2 ? PollutedRed : Color.white;
                }
            }

            // Smog: only when it actually affects the player
            int smogState = 0;
            if (eco != null && eco.PlayerSmogExposure >= 0.55f)
                smogState = eco.IsPlayerSmogProtected ? 2 : eco.IsPlayerSlowedBySmog ? 1 : 0;
            if (smogState != cachedSmogState)
            {
                cachedSmogState = smogState;
                smogChip.style.display = smogState == 0 ? DisplayStyle.None : DisplayStyle.Flex;
                smogChip.EnableInClassList("uc-chip--smog-safe", smogState == 2);
                smogLabel.text = smogState == 1 ? "Thick smog · wear a mask" : smogState == 2 ? "Mask on · smog filtered" : string.Empty;
            }
        }

        void UpdateEventChip()
        {
            // Same chip solo and co-op (the authority replicates the event clock to clients)
            IGameAuthority authority = GameAuthority.Current;
            float remaining = authority != null ? authority.ActiveEventTimeRemaining : 0f;
            string title = authority != null ? authority.ActiveEventTitle : string.Empty;
            bool active = remaining > 0.05f && !string.IsNullOrEmpty(title);
            eventChip.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            if (!active)
                return;
            if (!string.Equals(title, cachedEventTitle, System.StringComparison.Ordinal))
            {
                cachedEventTitle = title;
                eventTitleLabel.text = title;
            }
            int seconds = Mathf.CeilToInt(remaining);
            if (seconds != cachedEventSeconds)
            {
                cachedEventSeconds = seconds;
                eventTimeLabel.text = Num(seconds) + "s";
                eventChip.EnableInClassList("uc-chip--event-urgent", authority.ActiveEventSeverity >= 2);
            }
        }
    }
}
