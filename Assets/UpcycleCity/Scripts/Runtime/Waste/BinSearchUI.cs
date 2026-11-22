using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UpcycleCity.Ecosystem;
using UpcycleCity.Player;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// The street-bin search screen: the bin's slots on the left fill in one by one as the player rummages
    /// (common finds first, rare ones last, each with its own search time), and the player taps a find to move
    /// it into their Recycling Bag on the right. Phone-first UI Toolkit, built in code; created on first use.
    /// While open the player is in UI input mode. Esc / Back / walking away closes it (and pauses the search).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Waste/Bin Search UI")]
    public sealed class BinSearchUI : MonoBehaviour
    {
        const string PanelSettingsResource = "CraftingPanelSettings";
        const string StyleResource = "BinSearch";

        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;
        [SerializeField, Min(1f)] float closeDistance = 3.6f;

        public static BinSearchUI Instance { get; private set; }

        UIDocument document;
        VisualElement root, overlay, binGrid, bagGrid;
        Label title, binCount, bagCount, status, hint, bagBadge;
        Button closeButton, takeRecyclablesButton, takeAllButton;

        readonly List<SlotView> binSlots = new List<SlotView>();
        readonly List<SlotView> bagSlots = new List<SlotView>();
        readonly HashSet<int> pendingTakes = new HashSet<int>();

        OverflowingBinSource bin;
        int shownRevealed = -1;
        bool contentsDirty, bagDirty;
        float statusTime;

        public bool IsOpen { get; private set; }

        sealed class SlotView
        {
            public VisualElement Root, Fill, Chip, Icon;
            public Label Name, Tag;
            public int Entry = -2;
            public bool Revealed;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        public static BinSearchUI EnsureInstance()
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject("BinSearchUI");
            go.SetActive(false);
            var ui = go.AddComponent<BinSearchUI>();
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

            var docGo = new GameObject("BinSearchDocument");
            docGo.SetActive(false);
            docGo.transform.SetParent(transform, false);
            document = docGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.sortingOrder = 55;
            docGo.SetActive(true);
            Build();
            Hide();
        }

        void OnEnable()
        {
            RecyclingBagService.LocalBagChanged += OnBagChanged;
            bagDirty = true;
        }

        void OnDisable() => RecyclingBagService.LocalBagChanged -= OnBagChanged;

        void OnDestroy()
        {
            Unbind();
            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------ building

        void Build()
        {
            root = document.rootVisualElement;
            root.Clear();
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("bs-root");
            root.pickingMode = PickingMode.Ignore;

            // Bag counter, shown in play while the bag has something in it
            bagBadge = new Label();
            bagBadge.AddToClassList("bs-badge");
            bagBadge.pickingMode = PickingMode.Ignore;
            root.Add(bagBadge);

            overlay = new VisualElement();
            overlay.AddToClassList("bs-overlay");
            root.Add(overlay);

            var backdrop = new VisualElement();
            backdrop.AddToClassList("bs-backdrop");
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
            hint = new Label("Dig through it: finds turn up one by one, the rare ones take longest. Tap a find to bag it.");
            hint.AddToClassList("bs-hint");
            titles.Add(hint);
            closeButton = new Button(Close) { text = string.Empty };
            Sprite closeIcon = UpcycleCity.UI.IconLibrary.Active != null ? UpcycleCity.UI.IconLibrary.Active.Get("ui_close") : null;
            if (closeIcon != null)
            {
                var x = new VisualElement();
                x.AddToClassList("bs-close-icon");
                x.style.backgroundImage = new StyleBackground(closeIcon);
                closeButton.Add(x);
            }
            else
                closeButton.text = "✕";
            closeButton.AddToClassList("bs-close");
            header.Add(closeButton);

            var body = new VisualElement();
            body.AddToClassList("bs-body");
            window.Add(body);

            binGrid = Panel(body, "IN THE BIN", out binCount, "bs-panel-bin");
            bagGrid = Panel(body, "YOUR BAG", out bagCount, "bs-panel-bag");

            var footer = new VisualElement();
            footer.AddToClassList("bs-footer");
            window.Add(footer);
            status = new Label();
            status.AddToClassList("bs-status");
            footer.Add(status);
            takeRecyclablesButton = new Button(() => TakeAll(recyclablesOnly: true)) { text = "Take recyclables" };
            takeRecyclablesButton.AddToClassList("bs-button");
            footer.Add(takeRecyclablesButton);
            takeAllButton = new Button(() => TakeAll(recyclablesOnly: false)) { text = "Take everything" };
            takeAllButton.AddToClassList("bs-button");
            takeAllButton.AddToClassList("bs-button-alt");
            footer.Add(takeAllButton);
        }

        static VisualElement Panel(VisualElement parent, string heading, out Label counter, string extraClass)
        {
            var panel = new VisualElement();
            panel.AddToClassList("bs-panel");
            panel.AddToClassList(extraClass);
            parent.Add(panel);
            var head = new VisualElement();
            head.AddToClassList("bs-panel-head");
            panel.Add(head);
            var label = new Label(heading);
            label.AddToClassList("bs-panel-title");
            head.Add(label);
            counter = new Label();
            counter.AddToClassList("bs-panel-count");
            head.Add(counter);
            var grid = new VisualElement();
            grid.AddToClassList("bs-grid");
            panel.Add(grid);
            return grid;
        }

        SlotView MakeSlot(VisualElement grid, int index, bool inBin)
        {
            var view = new SlotView { Root = new VisualElement() };
            view.Root.AddToClassList("bs-slot");
            view.Fill = new VisualElement();
            view.Fill.AddToClassList("bs-slot-fill");
            view.Root.Add(view.Fill);
            view.Chip = new VisualElement();
            view.Chip.AddToClassList("bs-slot-chip");
            view.Root.Add(view.Chip);
            view.Icon = new VisualElement();
            view.Icon.AddToClassList("bs-slot-icon");
            view.Icon.pickingMode = PickingMode.Ignore;
            view.Root.Add(view.Icon);
            view.Name = new Label();
            view.Name.AddToClassList("bs-slot-name");
            view.Root.Add(view.Name);
            view.Tag = new Label();
            view.Tag.AddToClassList("bs-slot-tag");
            view.Root.Add(view.Tag);
            if (inBin)
                view.Root.RegisterCallback<ClickEvent>(_ => OnBinSlotClicked(index));
            grid.Add(view.Root);
            return view;
        }

        // ------------------------------------------------------------ open / close

        public void Open(OverflowingBinSource target)
        {
            if (target == null)
                return;
            if (IsOpen)
                Close();

            bin = target;
            bin.ContentsChanged += OnContentsChanged;
            RecyclingBagService.EnsureInstance();
            RecyclingBagService.RequestOpen(bin);

            pendingTakes.Clear();
            shownRevealed = -1;
            contentsDirty = bagDirty = true;
            title.text = $"SEARCHING · {bin.DisplayName.ToUpperInvariant()}";
            status.text = string.Empty;
            overlay.style.display = DisplayStyle.Flex;
            bagBadge.style.display = DisplayStyle.None;
            IsOpen = true;
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.UI);
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            if (bin != null)
                RecyclingBagService.RequestClose(bin);
            Unbind();
            Hide();
            PlayerContext.Active?.Input?.SetInputMode(PlayerInputMode.Gameplay);
        }

        void Hide()
        {
            IsOpen = false;
            if (overlay != null)
                overlay.style.display = DisplayStyle.None;
            UpdateBadge();
        }

        void UpdateBadge()
        {
            if (bagBadge == null)
                return;
            RecyclingBag bag = RecyclingBagService.LocalBag;
            bool show = !IsOpen && bag != null && bag.Count > 0;
            bagBadge.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
                bagBadge.text = $"BAG {bag.Count}/{bag.Capacity}" + (bag.IsFull ? " · FULL" : string.Empty);
        }

        void Unbind()
        {
            if (bin != null)
                bin.ContentsChanged -= OnContentsChanged;
            bin = null;
        }

        void OnContentsChanged()
        {
            contentsDirty = true;
            pendingTakes.Clear();
        }

        void OnBagChanged()
        {
            bagDirty = true;
            if (!IsOpen)
                UpdateBadge();
        }

        // ------------------------------------------------------------ taking

        void OnBinSlotClicked(int slot)
        {
            BinLootTable table = BinLootTable.Active;
            if (bin == null || table == null || slot >= bin.Contents.Count || pendingTakes.Contains(slot))
                return;
            if (!bin.Contents.IsRevealed(slot, OverflowingBinSource.SearchClock, table))
            {
                ShowStatus("Still digging for that one…");
                return;
            }
            RecyclingBag bag = RecyclingBagService.LocalBag;
            if (bag != null && bag.Count + pendingTakes.Count >= bag.Capacity)
            {
                ShowStatus("Your bag is full. Empty it at the sorting bins.");
                return;
            }
            pendingTakes.Add(slot);
            binSlots[slot].Root.AddToClassList("bs-slot-taking");
            RecyclingBagService.RequestTake(bin, slot, bin.Contents[slot], OnTakeResult);
        }

        // Highest slot first, so taking one never shifts the ones still to take
        void TakeAll(bool recyclablesOnly)
        {
            BinLootTable table = BinLootTable.Active;
            RecyclingBag bag = RecyclingBagService.LocalBag;
            if (bin == null || table == null || bag == null)
                return;
            float now = OverflowingBinSource.SearchClock;
            int revealed = bin.Contents.RevealedCount(now, table);
            int room = bag.Capacity - bag.Count - pendingTakes.Count;
            var picks = new List<int>(revealed);
            for (int i = 0; i < revealed; i++)
            {
                if (pendingTakes.Contains(i))
                    continue;
                if (recyclablesOnly && table.TryGet(bin.Contents[i], out BinLootTable.Entry e) && e.organic)
                    continue;
                picks.Add(i);
            }
            if (picks.Count == 0)
            {
                ShowStatus(revealed == 0 ? "Nothing found yet. Keep digging…" : "Nothing like that found yet.");
                return;
            }
            if (room <= 0)
            {
                ShowStatus("Your bag is full. Empty it at the sorting bins.");
                return;
            }
            // Prefer the rarest finds when the bag can't hold them all
            picks.Sort((a, b) => table.SearchSecondsFor(bin.Contents[b]).CompareTo(table.SearchSecondsFor(bin.Contents[a])));
            if (picks.Count > room)
                picks.RemoveRange(room, picks.Count - room);
            picks.Sort((a, b) => b.CompareTo(a));
            foreach (int slot in picks)
            {
                pendingTakes.Add(slot);
                binSlots[slot].Root.AddToClassList("bs-slot-taking");
                RecyclingBagService.RequestTake(bin, slot, bin.Contents[slot], OnTakeResult);
            }
        }

        void OnTakeResult(bool success, string message)
        {
            if (!IsOpen)
                return;
            if (!string.IsNullOrEmpty(message))
                ShowStatus(message);
            if (!success)
            {
                pendingTakes.Clear();
                contentsDirty = true;
            }
        }

        void ShowStatus(string text)
        {
            status.text = text;
            statusTime = 2.5f;
        }

        // ------------------------------------------------------------ refresh

        void Update()
        {
            if (!IsOpen)
                return;

            bool cancel = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                          || (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);
            bool tooFar = bin == null || PlayerContext.Active == null
                          || (PlayerContext.Active.transform.position - bin.transform.position).sqrMagnitude > closeDistance * closeDistance;
            if (cancel || tooFar)
            {
                Close();
                return;
            }

            if (statusTime > 0f)
            {
                statusTime -= Time.unscaledDeltaTime;
                if (statusTime <= 0f)
                    status.text = string.Empty;
            }
            Refresh();
        }

        void Refresh()
        {
            BinLootTable table = BinLootTable.Active;
            if (bin == null || table == null)
                return;

            float now = OverflowingBinSource.SearchClock;
            BinContents contents = bin.Contents;
            int revealed = contents.RevealedCount(now, table);
            if (contentsDirty || revealed != shownRevealed)
            {
                RebuildBin(table, contents, revealed);
                shownRevealed = revealed;
                contentsDirty = false;
            }

            // The find being dug out right now
            if (revealed < contents.Count && revealed < binSlots.Count)
            {
                float f = contents.NextFindFraction(now, table);
                binSlots[revealed].Fill.style.width = Length.Percent(f * 100f);
            }

            if (bagDirty)
            {
                RebuildBag(table);
                bagDirty = false;
            }

            takeRecyclablesButton.SetEnabled(revealed > 0);
            takeAllButton.SetEnabled(revealed > 0);
        }

        void RebuildBin(BinLootTable table, BinContents contents, int revealed)
        {
            int slots = table.BinSlots;
            while (binSlots.Count < slots)
                binSlots.Add(MakeSlot(binGrid, binSlots.Count, inBin: true));
            for (int i = 0; i < binSlots.Count; i++)
                binSlots[i].Root.style.display = i < slots ? DisplayStyle.Flex : DisplayStyle.None;

            for (int i = 0; i < slots; i++)
            {
                SlotView view = binSlots[i];
                if (i >= contents.Count)
                {
                    SetEmpty(view);
                    continue;
                }
                bool isRevealed = i < revealed;
                int entry = contents[i];
                if (isRevealed)
                {
                    bool fresh = !view.Revealed;
                    ShowFind(view, table, entry);
                    view.Root.EnableInClassList("bs-slot-taking", pendingTakes.Contains(i));
                    if (fresh)
                        Pop(view.Root);
                }
                else
                {
                    SetHidden(view, i == revealed);
                }
                view.Entry = entry;
                view.Revealed = isRevealed;
            }

            SetBinCountText(contents.Count, revealed);
            if (contents.Count == 0)
                ShowStatus("The bin is empty. Nice work!");
        }

        void SetBinCountText(int count, int revealed) =>
            binCount.text = count == 0 ? "empty" : revealed < count ? $"{revealed}/{count} found · searching…" : $"{count} found";

        void RebuildBag(BinLootTable table)
        {
            RecyclingBag bag = RecyclingBagService.LocalBag;
            int capacity = bag != null ? bag.Capacity : table.BagSlots;
            while (bagSlots.Count < capacity)
                bagSlots.Add(MakeSlot(bagGrid, bagSlots.Count, inBin: false));
            for (int i = 0; i < bagSlots.Count; i++)
            {
                SlotView view = bagSlots[i];
                view.Root.style.display = i < capacity ? DisplayStyle.Flex : DisplayStyle.None;
                if (bag != null && i < bag.Count)
                {
                    bool fresh = view.Entry != bag[i];
                    ShowFind(view, table, bag[i]);
                    view.Entry = bag[i];
                    if (fresh)
                        Pop(view.Root);
                }
                else
                {
                    SetEmpty(view);
                    view.Entry = -2;
                }
            }
            bagCount.text = bag != null ? $"{bag.Count}/{capacity}" : string.Empty;
        }

        static void ShowFind(SlotView view, BinLootTable table, int entry)
        {
            view.Root.RemoveFromClassList("bs-slot-hidden");
            view.Root.RemoveFromClassList("bs-slot-empty");
            view.Root.RemoveFromClassList("bs-slot-digging");
            view.Fill.style.width = 0f;
            if (!table.TryGet(entry, out BinLootTable.Entry e))
                return;
            Color accent = e.organic ? BinLootTable.OrganicColor : WasteStreamPalette.ColorFor(e.stream);
            Color rarity = e.organic ? BinLootTable.OrganicColor : BinLootTable.RarityColor(e.rarity);
            view.Name.text = e.displayName;
            Sprite icon = UpcycleCity.UI.IconLibrary.Active != null ? UpcycleCity.UI.IconLibrary.Active.ForLoot(e.id) : null;
            SetIcon(view, icon);
            view.Tag.text = e.organic ? "ORGANIC" : $"{WasteStreamPalette.LabelFor(e.stream)} · {e.rarity.ToString().ToUpperInvariant()}";
            view.Chip.style.backgroundColor = accent;
            view.Root.style.borderTopColor = view.Root.style.borderBottomColor =
                view.Root.style.borderLeftColor = view.Root.style.borderRightColor = rarity;
            view.Tag.style.color = rarity;
        }

        static void SetHidden(SlotView view, bool digging)
        {
            view.Root.AddToClassList("bs-slot-hidden");
            view.Root.RemoveFromClassList("bs-slot-empty");
            view.Root.RemoveFromClassList("bs-slot-taking");
            view.Root.EnableInClassList("bs-slot-digging", digging);
            view.Name.text = "?";
            SetIcon(view, null);
            view.Tag.text = digging ? "searching…" : string.Empty;
            view.Tag.style.color = StyleKeyword.Null;
            view.Chip.style.backgroundColor = StyleKeyword.Null;
            ClearBorder(view);
            if (!digging)
                view.Fill.style.width = 0f;
        }

        static void SetEmpty(SlotView view)
        {
            view.Root.AddToClassList("bs-slot-empty");
            view.Root.RemoveFromClassList("bs-slot-hidden");
            view.Root.RemoveFromClassList("bs-slot-digging");
            view.Root.RemoveFromClassList("bs-slot-taking");
            view.Name.text = string.Empty;
            view.Tag.text = string.Empty;
            view.Chip.style.backgroundColor = StyleKeyword.Null;
            view.Fill.style.width = 0f;
            SetIcon(view, null);
            ClearBorder(view);
            view.Revealed = false;
            view.Entry = -2;
        }

        static void SetIcon(SlotView view, Sprite sprite)
        {
            if (view.Icon == null)
                return;
            view.Icon.style.backgroundImage = sprite != null ? new StyleBackground(sprite) : new StyleBackground(StyleKeyword.None);
            view.Icon.style.display = sprite != null ? DisplayStyle.Flex : DisplayStyle.None;
            view.Root.EnableInClassList("bs-slot--has-icon", sprite != null);
        }

        static void ClearBorder(SlotView view)
        {
            view.Root.style.borderTopColor = view.Root.style.borderBottomColor =
                view.Root.style.borderLeftColor = view.Root.style.borderRightColor = StyleKeyword.Null;
        }

        // A quick scale pop when a find turns up or lands in the bag
        static void Pop(VisualElement element)
        {
            element.RemoveFromClassList("bs-pop");
            element.AddToClassList("bs-pop-start");
            element.schedule.Execute(() =>
            {
                element.RemoveFromClassList("bs-pop-start");
                element.AddToClassList("bs-pop");
            }).StartingIn(16);
        }
    }
}
