using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UpcycleCity.Player;
using UpcycleCity.Props;
using UpcycleCity.Shops;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// The player's carry screen: what they hold in their arms (or their Recycling Bag). Tab / B / Select / the touch
    /// BAG button opens it anywhere: pick an item to see it and throw it back out. At RÉCUP it opens in sell mode:
    /// pick an item, see its price, confirm — or sell everything RÉCUP buys. Every change is a host request
    /// (<see cref="RecyclingBagService"/>); the grid redraws from the replicated bag. Local-only UI, built in code with
    /// the bin-search styles (Resources/BinSearch.uss).
    /// <para>With a Shopping Cart pushed or within reach a second panel lists the cart (CART n/24): move items bag ↔ cart,
    /// throw them out of the cart, and at RÉCUP sell them (Sell all includes the cart).</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Waste/Carry Bag UI")]
    public sealed class CarryBagUI : MonoBehaviour
    {
        const string PanelSettingsResource = "CraftingPanelSettings";
        const string StyleResource = "BinSearch";

        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;
        [SerializeField, Min(1f)] float sellCloseDistance = 4f;

        public static CarryBagUI Instance { get; private set; }

        UIDocument document;
        VisualElement root, overlay, grid, cartGrid, cartSection, detailIcon, detailChip;
        Label title, hint, countLabel, cartCountLabel, detailName, detailTag, detailPrice, status;
        Button closeButton, actionButton, moveButton, sellAllButton;
        readonly List<Slot> slots = new List<Slot>();
        readonly List<Slot> cartSlots = new List<Slot>();
        ShoppingCart shownCart;
        bool selectedInCart;

        RecyclingBuyBack buyBack;
        int selected = -1;
        int selectedId = -1;
        bool dirty, busy;
        float statusTime;

        public bool IsOpen { get; private set; }
        bool SellMode => buyBack != null;

        sealed class Slot
        {
            public VisualElement Root, Chip, Icon;
            public Label Name, Tag;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        public static CarryBagUI EnsureInstance()
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject("CarryBagUI");
            go.SetActive(false);
            var ui = go.AddComponent<CarryBagUI>();
            go.SetActive(true);
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
            if (panelSettings == null)
                panelSettings = Resources.Load<PanelSettings>(PanelSettingsResource);
            if (styleSheet == null)
                styleSheet = Resources.Load<StyleSheet>(StyleResource);

            var docGo = new GameObject("CarryBagDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 56;
            docGo.SetActive(true);
            Build();
            Hide();
        }

        void OnEnable()
        {
            RecyclingBagService.LocalBagChanged += OnBagChanged;
            ShoppingCart.LocalCartChanged += OnBagChanged;
        }

        void OnDisable()
        {
            RecyclingBagService.LocalBagChanged -= OnBagChanged;
            ShoppingCart.LocalCartChanged -= OnBagChanged;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void OnBagChanged() => dirty = true;

        // ------------------------------------------------------------ building

        void Build()
        {
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("bs-root");
            root.pickingMode = PickingMode.Ignore;

            overlay = new VisualElement();
            overlay.AddToClassList("bs-overlay");
            root.Add(overlay);
            var backdrop = new VisualElement();
            backdrop.AddToClassList("bs-backdrop");
            backdrop.RegisterCallback<ClickEvent>(_ => Close());
            overlay.Add(backdrop);

            var window = new VisualElement();
            window.AddToClassList("bs-window");
            overlay.Add(window);

            var header = new VisualElement();
            header.AddToClassList("bs-header");
            window.Add(header);
            var titles = new VisualElement();
            titles.AddToClassList("bs-titles");
            header.Add(titles);
            title = new Label();
            title.AddToClassList("bs-title");
            titles.Add(title);
            hint = new Label();
            hint.AddToClassList("bs-hint");
            titles.Add(hint);
            closeButton = new Button(Close) { text = "✕" };
            closeButton.AddToClassList("bs-close");
            header.Add(closeButton);

            var body = new VisualElement();
            body.AddToClassList("bs-body");
            window.Add(body);

            // Left: what you carry (and the cart, when there's one), scrolling on small screens
            var leftPanel = new ScrollView(ScrollViewMode.Vertical);
            leftPanel.AddToClassList("bs-panel");
            leftPanel.AddToClassList("bs-panel-bin");
            leftPanel.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(leftPanel);
            VisualElement left = leftPanel.contentContainer;
            var head = new VisualElement();
            head.AddToClassList("bs-panel-head");
            left.Add(head);
            var heading = new Label("CARRYING");
            heading.AddToClassList("bs-panel-title");
            head.Add(heading);
            countLabel = new Label();
            countLabel.AddToClassList("bs-panel-count");
            head.Add(countLabel);
            grid = new VisualElement();
            grid.AddToClassList("bs-grid");
            left.Add(grid);

            cartSection = new VisualElement();
            cartSection.style.marginTop = 10;
            left.Add(cartSection);
            var cartHead = new VisualElement();
            cartHead.AddToClassList("bs-panel-head");
            cartSection.Add(cartHead);
            var cartHeading = new Label("CART");
            cartHeading.AddToClassList("bs-panel-title");
            cartHead.Add(cartHeading);
            cartCountLabel = new Label();
            cartCountLabel.AddToClassList("bs-panel-count");
            cartHead.Add(cartCountLabel);
            cartGrid = new VisualElement();
            cartGrid.AddToClassList("bs-grid");
            cartSection.Add(cartGrid);

            // Right: the selected item
            var right = new VisualElement();
            right.AddToClassList("bs-panel");
            right.AddToClassList("bs-panel-bag");
            right.style.alignItems = Align.Center;
            right.style.justifyContent = Justify.Center;
            body.Add(right);
            var iconFrame = new VisualElement();
            iconFrame.AddToClassList("bs-slot");
            iconFrame.style.width = 120;
            iconFrame.style.height = 120;
            iconFrame.style.alignItems = Align.Center;
            iconFrame.style.justifyContent = Justify.Center;
            right.Add(iconFrame);
            detailChip = new VisualElement();
            detailChip.AddToClassList("bs-slot-chip");
            iconFrame.Add(detailChip);
            detailIcon = new VisualElement();
            detailIcon.style.width = 92;
            detailIcon.style.height = 92;
            detailIcon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            iconFrame.Add(detailIcon);
            detailName = new Label();
            detailName.AddToClassList("bs-title");
            detailName.style.fontSize = 22;
            detailName.style.marginTop = 12;
            detailName.style.unityTextAlign = TextAnchor.MiddleCenter;
            detailName.style.whiteSpace = WhiteSpace.Normal;
            right.Add(detailName);
            detailTag = new Label();
            detailTag.AddToClassList("bs-hint");
            detailTag.style.unityTextAlign = TextAnchor.MiddleCenter;
            right.Add(detailTag);
            detailPrice = new Label();
            detailPrice.AddToClassList("bs-panel-title");
            detailPrice.style.fontSize = 22;
            detailPrice.style.marginTop = 8;
            right.Add(detailPrice);
            actionButton = new Button(OnAction);
            actionButton.AddToClassList("bs-button");
            actionButton.style.marginTop = 14;
            actionButton.style.marginLeft = 0;
            actionButton.style.minWidth = 200;
            right.Add(actionButton);
            moveButton = new Button(OnMove);
            moveButton.AddToClassList("bs-button");
            moveButton.AddToClassList("bs-button-alt");
            moveButton.style.marginTop = 8;
            moveButton.style.marginLeft = 0;
            moveButton.style.minWidth = 200;
            right.Add(moveButton);

            var footer = new VisualElement();
            footer.AddToClassList("bs-footer");
            window.Add(footer);
            status = new Label();
            status.AddToClassList("bs-status");
            footer.Add(status);
            sellAllButton = new Button(OnSellAll) { text = "Sell all" };
            sellAllButton.AddToClassList("bs-button");
            sellAllButton.AddToClassList("bs-button-alt");
            footer.Add(sellAllButton);
        }

        Slot MakeSlot(int index, bool inCart = false)
        {
            var slot = new Slot { Root = new VisualElement() };
            slot.Root.AddToClassList("bs-slot");
            if (inCart)
            {
                // 24 places: smaller tiles, four to a row
                slot.Root.style.width = new Length(23f, LengthUnit.Percent);
                slot.Root.style.height = 66;
            }
            slot.Chip = new VisualElement();
            slot.Chip.AddToClassList("bs-slot-chip");
            slot.Root.Add(slot.Chip);
            slot.Icon = new VisualElement();
            slot.Icon.AddToClassList("bs-slot-icon");
            slot.Icon.pickingMode = PickingMode.Ignore;
            slot.Root.Add(slot.Icon);
            slot.Name = new Label();
            slot.Name.AddToClassList("bs-slot-name");
            slot.Root.Add(slot.Name);
            slot.Tag = new Label();
            slot.Tag.AddToClassList("bs-slot-tag");
            slot.Root.Add(slot.Tag);
            slot.Root.RegisterCallback<ClickEvent>(_ => Select(index, inCart));
            (inCart ? cartGrid : grid).Add(slot.Root);
            return slot;
        }

        // ------------------------------------------------------------ open / close

        /// <summary>Opens the carry screen. With a <paramref name="sellAt"/> RÉCUP scale it opens in sell mode.</summary>
        public void Open(RecyclingBuyBack sellAt = null)
        {
            RecyclingBagService.EnsureInstance();
            buyBack = sellAt;
            selected = -1;
            selectedId = -1;
            selectedInCart = false;
            busy = false;
            dirty = true;
            status.text = string.Empty;
            overlay.style.display = DisplayStyle.Flex;
            IsOpen = true;
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.UI);
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            Hide();
            buyBack = null;
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.Gameplay);
        }

        void Hide()
        {
            IsOpen = false;
            if (overlay != null)
                overlay.style.display = DisplayStyle.None;
        }

        // ------------------------------------------------------------ actions

        void Select(int index, bool inCart)
        {
            if (busy)
                return;
            if (inCart)
            {
                CartStorage storage = shownCart != null ? shownCart.Storage : null;
                if (storage == null || index >= storage.Count)
                    return;
                selectedId = storage[index];
            }
            else
            {
                RecyclingBag bag = RecyclingBagService.LocalBag;
                if (bag == null || index >= bag.Count)
                    return;
                selectedId = bag[index];
            }
            selected = index;
            selectedInCart = inCart;
            dirty = true;
        }

        /// <summary>True when the selection still points at the same item (in the bag or the cart).</summary>
        bool SelectionValid()
        {
            if (selected < 0)
                return false;
            if (selectedInCart)
            {
                CartStorage storage = shownCart != null ? shownCart.Storage : null;
                return storage != null && selected < storage.Count && storage[selected] == selectedId;
            }
            RecyclingBag bag = RecyclingBagService.LocalBag;
            return bag != null && selected < bag.Count && bag[selected] == selectedId;
        }

        void OnAction()
        {
            if (busy || !SelectionValid())
                return;
            busy = true;
            int slot = selectedInCart ? RecyclingBagService.CartSlotBase + selected : selected;
            if (SellMode)
                RecyclingBagService.RequestSell(buyBack, slot, selectedId, OnResult);
            else
                RecyclingBagService.RequestThrow(slot, selectedId, OnResult);
        }

        void OnMove()
        {
            if (busy || shownCart == null || !SelectionValid())
                return;
            busy = true;
            if (selectedInCart)
                RecyclingBagService.RequestTakeFromCart(shownCart, selected, selectedId, OnResult);
            else
                RecyclingBagService.RequestPutInCart(shownCart, selected, selectedId, OnResult);
        }

        void OnSellAll()
        {
            if (!SellMode || busy)
                return;
            busy = true;
            RecyclingBagService.RequestSell(buyBack, RecyclingBagService.SellAll, 0, OnResult);
        }

        void OnResult(bool ok, string message)
        {
            busy = false;
            if (ok)
            {
                selected = -1;
                selectedId = -1;
            }
            ShowStatus(message);
            dirty = true;
        }

        void ShowStatus(string text)
        {
            if (status == null)
                return;
            status.text = text ?? string.Empty;
            statusTime = 2.5f;
        }

        // ------------------------------------------------------------ per frame

        void Update()
        {
            PlayerController player = PlayerContext.Active;
            if (!IsOpen)
            {
                // Tab / B / Select / touch BAG opens it during gameplay; with trash in hand it stores that first
                if (player != null && player.Input != null && player.Input.InputMode == PlayerInputMode.Gameplay
                    && player.Input.BagPressedThisFrame && !OtherScreenOpen())
                {
                    if (player.Grabber != null && player.Grabber.HeldObject is TrashItem held && !held.IsBulky)
                        RecyclingBagService.RequestStoreHeld((ok, msg) => { if (!ok) player.Interactor?.NotifyFeedback(msg, new Color(0.95f, 0.55f, 0.2f)); });
                    else
                        Open();
                }
                return;
            }

            bool cancel = (Keyboard.current != null && (Keyboard.current.escapeKey.wasPressedThisFrame
                              || Keyboard.current.tabKey.wasPressedThisFrame || Keyboard.current.bKey.wasPressedThisFrame))
                          || (Gamepad.current != null && (Gamepad.current.buttonEast.wasPressedThisFrame || Gamepad.current.selectButton.wasPressedThisFrame));
            bool tooFar = SellMode && (player == null
                          || (player.transform.position - buyBack.transform.position).sqrMagnitude > sellCloseDistance * sellCloseDistance);
            if (cancel || tooFar)
            {
                Close();
                return;
            }

            if (ShoppingCart.FindForLocal() != shownCart)
                dirty = true;
            if (statusTime > 0f)
            {
                statusTime -= Time.unscaledDeltaTime;
                if (statusTime <= 0f)
                    status.text = string.Empty;
            }
            if (dirty)
                Refresh();
        }

        static bool OtherScreenOpen() =>
            (BinSearchUI.Instance != null && BinSearchUI.Instance.IsOpen)
            || (ShopUI.Instance != null && ShopUI.Instance.IsOpen);

        void Refresh()
        {
            dirty = false;
            RecyclingBag bag = RecyclingBagService.LocalBag;
            int capacity = bag != null ? bag.Capacity : CarryItems.HandsCapacity;
            int count = bag != null ? bag.Count : 0;
            bool hasBag = bag != null && bag.HasBag;

            title.text = SellMode ? "RÉCUP · SELL YOUR FINDS" : hasBag ? "YOUR BAG" : "YOUR ARMS";
            hint.text = SellMode
                ? "Pick an item to see what RÉCUP pays, then confirm. Organic waste goes to the compost bin."
                : hasBag
                    ? "Everything you picked up. Pick an item to throw it back out."
                    : "You hug what you pick up: room for 3. Buy a Recycling Bag at the Quincaillerie to carry 10.";
            countLabel.text = $"{count}/{capacity}";

            shownCart = ShoppingCart.FindForLocal();
            CartStorage cart = shownCart != null ? shownCart.Storage : null;

            // A stale selection (the item moved or went) is dropped
            if (selected >= 0 && !SelectionValid())
            {
                selected = -1;
                selectedId = -1;
                selectedInCart = false;
            }
            if (cart != null && !SellMode)
                hint.text = "Pick an item to move it between you and the cart, or throw it out.";

            while (slots.Count < capacity)
                slots.Add(MakeSlot(slots.Count));
            int sellAllCoins = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                slot.Root.style.display = i < capacity ? DisplayStyle.Flex : DisplayStyle.None;
                bool filled = bag != null && i < count;
                slot.Root.EnableInClassList("bs-slot-empty", !filled);
                slot.Root.EnableInClassList("bs-slot-taking", busy && !selectedInCart && i == selected);
                if (!filled)
                {
                    slot.Name.text = slot.Tag.text = string.Empty;
                    slot.Icon.style.display = DisplayStyle.None;
                    slot.Chip.style.backgroundColor = StyleKeyword.Null;
                    SetBorder(slot.Root, StyleKeyword.Null);
                    continue;
                }
                CarryItems.Info info = CarryItems.Resolve(bag[i]);
                Sprite icon = CarryItems.IconFor(bag[i]);
                slot.Name.text = info.Name;
                slot.Tag.text = info.Organic ? "ORGANIC" : WasteStreamPalette.LabelFor(info.Stream);
                slot.Icon.style.backgroundImage = icon != null ? new StyleBackground(icon) : new StyleBackground(StyleKeyword.None);
                slot.Icon.style.display = icon != null ? DisplayStyle.Flex : DisplayStyle.None;
                slot.Root.EnableInClassList("bs-slot--has-icon", icon != null);
                Color accent = CarryItems.AccentFor(info);
                slot.Chip.style.backgroundColor = accent;
                SetBorder(slot.Root, !selectedInCart && i == selected ? new StyleColor(new Color(0.95f, 0.76f, 0.11f)) : new StyleColor(accent));
                if (SellMode && info.Valid && !info.Organic)
                    sellAllCoins += buyBack.PriceFor(info.Stream, info.BaseValue);
            }

            // The cart panel
            cartSection.style.display = cart != null ? DisplayStyle.Flex : DisplayStyle.None;
            int cartCount = cart != null ? cart.Count : 0;
            if (cart != null)
            {
                cartCountLabel.text = (shownCart == ShoppingCart.LocalPushed ? "PUSHING · " : string.Empty) + $"{cartCount}/{cart.Capacity}";
                while (cartSlots.Count < cart.Capacity)
                    cartSlots.Add(MakeSlot(cartSlots.Count, inCart: true));
                for (int i = 0; i < cartSlots.Count; i++)
                {
                    Slot slot = cartSlots[i];
                    bool filled = i < cartCount;
                    slot.Root.EnableInClassList("bs-slot-empty", !filled);
                    slot.Root.EnableInClassList("bs-slot-taking", busy && selectedInCart && i == selected);
                    if (!filled)
                    {
                        slot.Name.text = slot.Tag.text = string.Empty;
                        slot.Icon.style.display = DisplayStyle.None;
                        slot.Chip.style.backgroundColor = StyleKeyword.Null;
                        SetBorder(slot.Root, StyleKeyword.Null);
                        continue;
                    }
                    CarryItems.Info info = CarryItems.Resolve(cart[i]);
                    Sprite icon = CarryItems.IconFor(cart[i]);
                    slot.Name.text = info.Name;
                    slot.Tag.text = info.Organic ? "ORGANIC" : WasteStreamPalette.LabelFor(info.Stream);
                    slot.Icon.style.backgroundImage = icon != null ? new StyleBackground(icon) : new StyleBackground(StyleKeyword.None);
                    slot.Icon.style.display = icon != null ? DisplayStyle.Flex : DisplayStyle.None;
                    slot.Root.EnableInClassList("bs-slot--has-icon", icon != null);
                    Color accent = CarryItems.AccentFor(info);
                    slot.Chip.style.backgroundColor = accent;
                    SetBorder(slot.Root, selectedInCart && i == selected ? new StyleColor(new Color(0.95f, 0.76f, 0.11f)) : new StyleColor(accent));
                    if (SellMode && info.Valid && !info.Organic)
                        sellAllCoins += buyBack.PriceFor(info.Stream, info.BaseValue);
                }
            }

            RefreshDetail(bag);
            sellAllButton.style.display = SellMode ? DisplayStyle.Flex : DisplayStyle.None;
            sellAllButton.text = sellAllCoins > 0 ? $"Sell all · +{sellAllCoins}" : "Sell all";
            sellAllButton.SetEnabled(!busy && sellAllCoins > 0);
            if (count == 0 && cartCount == 0 && string.IsNullOrEmpty(status.text))
                status.text = hasBag ? "Your bag is empty." : "Your arms are empty. Pick up some trash!";
        }

        void RefreshDetail(RecyclingBag bag)
        {
            bool any = SelectionValid();
            detailIcon.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            actionButton.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            bool canMove = any && shownCart != null && shownCart.Storage != null && !SellMode;
            moveButton.style.display = canMove ? DisplayStyle.Flex : DisplayStyle.None;
            if (canMove)
            {
                moveButton.text = selectedInCart ? (bag != null && bag.HasBag ? "Move to bag" : "Take in arms") : "Put in cart";
                bool room = selectedInCart ? bag != null && !bag.IsFull : !shownCart.Storage.IsFull;
                moveButton.SetEnabled(!busy && room);
            }
            if (!any)
            {
                detailName.text = "Pick an item";
                detailTag.text = SellMode ? "to see its price" : "to throw it out";
                detailPrice.text = string.Empty;
                detailChip.style.backgroundColor = StyleKeyword.Null;
                return;
            }

            CarryItems.Info info = CarryItems.Resolve(selectedId);
            Sprite icon = CarryItems.IconFor(selectedId);
            detailIcon.style.backgroundImage = icon != null ? new StyleBackground(icon) : new StyleBackground(StyleKeyword.None);
            detailChip.style.backgroundColor = CarryItems.AccentFor(info);
            detailName.text = info.Name;
            detailTag.text = (info.Organic ? "Organic waste" : $"{WasteStreamPalette.LabelFor(info.Stream)}")
                + (selectedInCart ? "  ·  in the cart" : string.Empty);

            if (SellMode)
            {
                if (info.Organic)
                {
                    detailPrice.text = "RÉCUP doesn't buy this";
                    actionButton.text = "Compost bin only";
                    actionButton.SetEnabled(false);
                }
                else
                {
                    int price = buyBack.PriceFor(info.Stream, info.BaseValue);
                    detailPrice.text = $"Sells for {price} coins";
                    actionButton.text = $"Sell · +{price}";
                    actionButton.SetEnabled(!busy);
                }
            }
            else
            {
                detailPrice.text = string.Empty;
                actionButton.text = "Throw out";
                actionButton.SetEnabled(!busy);
            }
        }

        static void SetBorder(VisualElement element, StyleColor color)
        {
            element.style.borderTopColor = element.style.borderBottomColor =
                element.style.borderLeftColor = element.style.borderRightColor = color;
        }
    }
}
