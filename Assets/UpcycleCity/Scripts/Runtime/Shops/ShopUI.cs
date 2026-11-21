using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UpcycleCity.Inventory;
using UpcycleCity.Interaction;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Shops
{
    /// <summary>
    /// The shop window (UI Toolkit, styled by <c>UI/Shop.uss</c>): the keeper's greeting, the player's coins,
    /// one card per offer with a Buy button, and for the RÉCUP point the prices it pays for trash.
    /// Buying spends coins and adds the item to the hotbar (refunded if the hotbar is full).
    /// While open the player is in UI input mode (free cursor); Esc, the Leave button or walking away closes it.
    /// One per scene; <see cref="ShopCounter"/>s open it through <see cref="Instance"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Shops/Shop UI")]
    public sealed class ShopUI : MonoBehaviour
    {
        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;
        [Tooltip("The window closes if the player ends up further than this from the counter.")]
        [SerializeField, Min(2f)] float closeDistance = 6f;
        [Tooltip("Example base value used to show RÉCUP prices per stream.")]
        [SerializeField, Min(1)] int sellExampleValue = 6;

        static readonly WasteStream[] SellStreams =
            { WasteStream.Paper, WasteStream.Plastic, WasteStream.Glass, WasteStream.Cans, WasteStream.BulkyUpcyclable };

        public static ShopUI Instance { get; private set; }

        UIDocument document;
        VisualElement root, window, accentBar, list, sellBox;
        Label title, keeper, greeting, coinsLabel, status;
        Button leaveButton;
        readonly List<(ShopCatalog.Offer offer, Button button, Label state)> cards = new List<(ShopCatalog.Offer, Button, Label)>();

        ShopCounter counter;
        PlayerInteractor interactor;
        PlayerInventory inventory;
        float statusTime;

        public bool IsOpen { get; private set; }

        public void Configure(PanelSettings settings, StyleSheet sheet)
        {
            panelSettings = settings;
            styleSheet = sheet;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            var docGo = new GameObject("ShopDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 50;
            docGo.SetActive(true);
            Build();
            Hide();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            Unbind();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        // ------------------------------------------------------------ building the tree

        void Build()
        {
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("shop-root");

            var backdrop = new VisualElement();
            backdrop.AddToClassList("shop-backdrop");
            root.Add(backdrop);

            window = new VisualElement();
            window.AddToClassList("shop-window");
            root.Add(window);

            var header = new VisualElement();
            header.AddToClassList("shop-header");
            window.Add(header);
            accentBar = new VisualElement();
            accentBar.AddToClassList("shop-accent");
            header.Add(accentBar);
            var titles = new VisualElement();
            titles.AddToClassList("shop-titles");
            header.Add(titles);
            title = new Label();
            title.AddToClassList("shop-title");
            titles.Add(title);
            keeper = new Label();
            keeper.AddToClassList("shop-keeper");
            titles.Add(keeper);
            var coins = new VisualElement();
            coins.AddToClassList("shop-coins");
            header.Add(coins);
            var coinIcon = new VisualElement();
            coinIcon.AddToClassList("shop-coin-icon");
            coins.Add(coinIcon);
            coinsLabel = new Label();
            coinsLabel.AddToClassList("shop-coins-label");
            coins.Add(coinsLabel);

            greeting = new Label();
            greeting.AddToClassList("shop-greeting");
            window.Add(greeting);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("shop-scroll");
            window.Add(scroll);
            list = new VisualElement();
            list.AddToClassList("shop-list");
            scroll.Add(list);
            sellBox = new VisualElement();
            sellBox.AddToClassList("shop-sell");
            scroll.Add(sellBox);

            var footer = new VisualElement();
            footer.AddToClassList("shop-footer");
            window.Add(footer);
            status = new Label();
            status.AddToClassList("shop-status");
            footer.Add(status);
            leaveButton = new Button(Close) { text = "Leave  (Esc)" };
            leaveButton.AddToClassList("shop-leave");
            footer.Add(leaveButton);
        }

        void Populate()
        {
            ShopCatalog catalog = counter.Catalog;
            accentBar.style.backgroundColor = catalog.Accent;
            title.text = catalog.ShopName.ToUpperInvariant();
            keeper.text = catalog.KeeperName;
            greeting.text = $"“{catalog.Greeting}”";
            status.text = string.Empty;

            list.Clear();
            cards.Clear();
            foreach (ShopCatalog.Offer offer in catalog.Offers)
            {
                if (offer?.item != null)
                    list.Add(BuildCard(offer));
            }
            list.style.display = cards.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            sellBox.Clear();
            sellBox.style.display = catalog.ShowsSellPrices ? DisplayStyle.Flex : DisplayStyle.None;
            if (catalog.ShowsSellPrices)
                BuildSellPrices();
        }

        VisualElement BuildCard(ShopCatalog.Offer offer)
        {
            ItemDefinition item = offer.item;
            var card = new VisualElement();
            card.AddToClassList("shop-card");

            var swatch = new Label(Initials(item.DisplayName));
            swatch.AddToClassList("shop-swatch");
            swatch.style.backgroundColor = item.AccentColor;
            card.Add(swatch);

            var info = new VisualElement();
            info.AddToClassList("shop-info");
            card.Add(info);
            var name = new Label(offer.quantity > 1 ? $"{item.DisplayName} ×{offer.quantity}" : item.DisplayName);
            name.AddToClassList("shop-item-name");
            info.Add(name);
            var desc = new Label(item.Description);
            desc.AddToClassList("shop-item-desc");
            info.Add(desc);
            string effect = EffectText(item);
            if (!string.IsNullOrEmpty(effect))
            {
                var tag = new Label(effect);
                tag.AddToClassList("shop-item-effect");
                info.Add(tag);
            }

            var buy = new VisualElement();
            buy.AddToClassList("shop-buy");
            card.Add(buy);
            var state = new Label();
            state.AddToClassList("shop-state");
            buy.Add(state);
            var button = new Button(() => Buy(offer));
            button.AddToClassList("shop-buy-button");
            var icon = new VisualElement();
            icon.AddToClassList("shop-coin-icon");
            button.Add(icon);
            var price = new Label(offer.price.ToString());
            price.AddToClassList("shop-price");
            button.Add(price);
            buy.Add(button);

            cards.Add((offer, button, state));
            return card;
        }

        void BuildSellPrices()
        {
            RecyclingBuyBack buyBack = FindBuyBack();
            var heading = new Label("WE BUY — drop your trash on the scale");
            heading.AddToClassList("shop-sell-heading");
            sellBox.Add(heading);
            foreach (WasteStream stream in SellStreams)
            {
                var row = new VisualElement();
                row.AddToClassList("shop-sell-row");
                var chip = new VisualElement();
                chip.AddToClassList("shop-sell-chip");
                chip.style.backgroundColor = WasteStreamPalette.ColorFor(stream);
                row.Add(chip);
                var name = new Label(WasteStreamPalette.LabelFor(stream));
                name.AddToClassList("shop-sell-name");
                row.Add(name);
                float rate = buyBack != null ? buyBack.RateFor(stream) : RecyclingBuyBack.Rates.Default.For(stream);
                var value = new Label($"×{rate:0.0}  ·  e.g. {Mathf.Max(1, Mathf.RoundToInt(sellExampleValue * rate))} coins");
                value.AddToClassList("shop-sell-value");
                row.Add(value);
                sellBox.Add(row);
            }
            if (buyBack != null && buyBack.ItemsBought > 0)
            {
                var total = new Label($"You have sold {buyBack.ItemsBought} items here for {buyBack.CoinsPaid} coins.");
                total.AddToClassList("shop-sell-total");
                sellBox.Add(total);
            }
        }

        RecyclingBuyBack FindBuyBack()
        {
            RecyclingBuyBack nearest = null;
            float best = float.MaxValue;
            foreach (RecyclingBuyBack candidate in FindObjectsByType<RecyclingBuyBack>())
            {
                float d = (candidate.transform.position - counter.transform.position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    nearest = candidate;
                }
            }
            return nearest;
        }

        static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "?";
            string[] words = name.Split(' ');
            return words.Length > 1 && words[1].Length > 0
                ? $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
                : char.ToUpperInvariant(words[0][0]).ToString();
        }

        static string EffectText(ItemDefinition item)
        {
            var sb = new StringBuilder();
            if (item.UseBehavior is TimedBoostItemBehavior boost)
            {
                PlayerBoosts.Effect e = boost.Effect;
                if (!Mathf.Approximately(e.moveSpeed, 1f)) Append(sb, $"Speed {Percent(e.moveSpeed)}");
                if (!Mathf.Approximately(e.jump, 1f)) Append(sb, $"Jump {Percent(e.jump)}");
                if (e.grabReach > 0f) Append(sb, $"Reach +{e.grabReach:0.#} m");
                if (e.throwForce > 0f) Append(sb, $"Throw +{e.throwForce:0.#}");
                if (e.smogImmune) Append(sb, "Smog-proof");
                Append(sb, $"{Mathf.RoundToInt(boost.Duration)}s");
            }
            else
            {
                if (item.GrabReachBonus > 0f) Append(sb, $"Reach +{item.GrabReachBonus:0.#} m");
                if (item.ThrowForceBonus > 0f) Append(sb, $"Throw +{item.ThrowForceBonus:0.#}");
                if (item.GrantsSmogImmunity) Append(sb, "Smog-proof while held");
                if (item.UseBehavior is SweepAreaItemBehavior) Append(sb, "F: sweep litter");
                if (item.UseBehavior is ToolTargetItemBehavior) Append(sb, "F: use on machines");
            }
            return sb.ToString();
        }

        static string Percent(float multiplier) => $"{(multiplier >= 1f ? "+" : "")}{Mathf.RoundToInt((multiplier - 1f) * 100f)}%";

        static void Append(StringBuilder sb, string part)
        {
            if (sb.Length > 0)
                sb.Append("  ·  ");
            sb.Append(part);
        }

        // ------------------------------------------------------------ open / close / buy

        public void Open(ShopCounter shop, PlayerInteractor by)
        {
            if (shop == null || shop.Catalog == null || by == null)
                return;

            counter = shop;
            interactor = by;
            Bind(by.Inventory);
            Populate();
            Refresh();

            root.style.display = DisplayStyle.Flex;
            IsOpen = true;
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.UI);
            if (cards.Count > 0)
                cards[0].button.Focus();
            else
                leaveButton.Focus();
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            Hide();
            Unbind();
            counter = null;
            interactor = null;
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.Gameplay);
        }

        void Hide()
        {
            IsOpen = false;
            if (root != null)
                root.style.display = DisplayStyle.None;
        }

        void Bind(PlayerInventory target)
        {
            Unbind();
            inventory = target;
            if (inventory != null)
                inventory.Changed += Refresh;
        }

        void Unbind()
        {
            if (inventory != null)
                inventory.Changed -= Refresh;
            inventory = null;
        }

        void Buy(ShopCatalog.Offer offer)
        {
            if (inventory == null || offer?.item == null)
                return;

            ItemDefinition item = offer.item;
            if (IsOwnedTool(item))
            {
                SetStatus($"You already have the {item.DisplayName}.", false);
                return;
            }
            if (!inventory.TrySpendCoins(offer.price))
            {
                SetStatus($"Not enough coins — the {item.DisplayName} costs {offer.price}.", false);
                return;
            }
            int before = inventory.CountOf(item);
            inventory.TryAddItem(item, offer.quantity);
            int added = inventory.CountOf(item) - before;
            if (added <= 0)
            {
                inventory.AddCoins(offer.price);
                SetStatus("Your hotbar is full — free a slot first.", false);
                PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.InventoryFull, counter.transform.position, 1f, "Hotbar full", Color.red);
                return;
            }

            string message = $"Bought {item.DisplayName}{(added > 1 ? $" ×{added}" : string.Empty)} · −{offer.price} Coins";
            SetStatus(message, true);
            interactor?.NotifyFeedback(message, item.AccentColor);
            PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.PickupItem, counter.transform.position, 1f, message, item.AccentColor);
            Refresh();
        }

        bool IsOwnedTool(ItemDefinition item) => item.MaxStack <= 1 && inventory != null && inventory.HasItem(item);

        void SetStatus(string text, bool good)
        {
            status.text = text;
            status.EnableInClassList("shop-status--good", good);
            status.EnableInClassList("shop-status--bad", !good);
            statusTime = 4f;
        }

        void Refresh()
        {
            if (root == null)
                return;
            int coins = inventory != null ? inventory.Coins : 0;
            coinsLabel.text = $"{coins}";
            foreach ((ShopCatalog.Offer offer, Button button, Label state) in cards)
            {
                bool owned = IsOwnedTool(offer.item);
                bool affordable = coins >= offer.price;
                button.SetEnabled(!owned && affordable);
                button.EnableInClassList("shop-buy-button--short", !owned && !affordable);
                int count = inventory != null ? inventory.CountOf(offer.item) : 0;
                state.text = owned ? "OWNED" : count > 0 ? $"In bag: {count}" : affordable ? string.Empty : $"Need {offer.price - coins} more";
            }
        }

        void Update()
        {
            if (!IsOpen)
                return;

            if (statusTime > 0f)
            {
                statusTime -= Time.unscaledDeltaTime;
                if (statusTime <= 0f)
                    status.text = string.Empty;
            }

            bool cancel = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                          || (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);
            bool tooFar = counter == null || PlayerContext.Active == null
                          || (PlayerContext.Active.transform.position - counter.transform.position).sqrMagnitude > closeDistance * closeDistance;
            if (cancel || tooFar)
                Close();
        }
    }
}
