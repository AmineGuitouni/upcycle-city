using System;
using UnityEngine;

namespace UpcycleCity.Inventory
{
    /// <summary>
    /// Stores the player's coins, tools, and usable items in a fixed hotbar.
    /// Operates cleanly through <see cref="IInventoryItem"/> while preserving Unity Inspector
    /// ScriptableObject references. Deliberately excludes trash (handled 1-at-a-time by PhysicsGrabController).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Inventory/Player Inventory")]
    public sealed class PlayerInventory : MonoBehaviour
    {
        [Header("Currency")]
        [SerializeField, Min(0)] int startingCoins = 25;

        [Header("Hotbar")]
        [SerializeField, Range(4, 10)] int slotCount = 6;
        [SerializeField] ItemDefinition[] startingItems = Array.Empty<ItemDefinition>();

        InventorySlot[] slots;
        int selectedIndex;
        int coins;
        float nextUseTime;

        public int Coins => coins;
        public int SlotCount => slots != null ? slots.Length : slotCount;
        public int SelectedIndex => selectedIndex;
        public InventorySlot SelectedSlot => slots != null && selectedIndex >= 0 && selectedIndex < slots.Length ? slots[selectedIndex] : default;
        public IInventoryItem EquippedItem => SelectedSlot.Item;
        public bool IsUseOnCooldown => Time.time < nextUseTime;

        public event Action Changed;
        public event Action<int, int> CoinsChanged;
        public event Action<int, InventorySlot> SelectedSlotChanged;
        public event Action<IInventoryItem, int> ItemAdded;
        public event Action<IInventoryItem> ItemUsed;

        void Awake()
        {
            coins = Mathf.Max(0, startingCoins);
            slots = new InventorySlot[Mathf.Clamp(slotCount, 4, 10)];

            if (startingItems != null)
            {
                for (int i = 0; i < startingItems.Length; i++)
                {
                    if (startingItems[i] != null)
                        TryAddItem(startingItems[i], 1, silent: true);
                }
            }
        }

        public InventorySlot GetSlot(int index)
        {
            if (slots == null || index < 0 || index >= slots.Length)
                return default;
            return slots[index];
        }

        public void AddCoins(int amount)
        {
            if (amount <= 0)
                return;
            coins += amount;
            CoinsChanged?.Invoke(coins, amount);
            Changed?.Invoke();
        }

        public bool TrySpendCoins(int amount)
        {
            if (amount <= 0)
                return true;
            if (coins < amount)
                return false;
            coins -= amount;
            CoinsChanged?.Invoke(coins, -amount);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Sets the coin balance directly from the authoritative shared co-op team wallet.</summary>
        public void SetCoinsFromNetwork(int authoritativeCoins)
        {
            int clamped = Mathf.Max(0, authoritativeCoins);
            int delta = clamped - coins;
            if (delta == 0)
                return;
            coins = clamped;
            CoinsChanged?.Invoke(coins, delta);
            Changed?.Invoke();
        }

        /// <summary>Returns true if at least one unit of <paramref name="item"/> can fit in the hotbar without mutating slots.</summary>
        public bool CanAddItem(IInventoryItem item, int amount = 1)
        {
            if (item == null || amount <= 0 || slots == null)
                return false;

            if (item.MaxStack > 1)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    if (Matches(slots[i].Item, item) && slots[i].Count < item.MaxStack)
                        return true;
                }
            }

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].IsEmpty)
                    return true;
            }

            return false;
        }

        public bool TryAddItem(IInventoryItem item, int amount = 1, bool silent = false)
        {
            if (item == null || amount <= 0 || slots == null)
                return false;

            int remaining = amount;

            // Stack onto existing matching slots first
            if (item.MaxStack > 1)
            {
                for (int i = 0; i < slots.Length && remaining > 0; i++)
                {
                    if (!Matches(slots[i].Item, item) || slots[i].Count >= item.MaxStack)
                        continue;
                    int space = item.MaxStack - slots[i].Count;
                    int added = Mathf.Min(space, remaining);
                    slots[i].Count += added;
                    remaining -= added;
                }
            }

            // Fill empty slots with any remainder
            for (int i = 0; i < slots.Length && remaining > 0; i++)
            {
                if (!slots[i].IsEmpty)
                    continue;
                int added = Mathf.Min(item.MaxStack, remaining);
                slots[i].Item = item;
                slots[i].Count = added;
                remaining -= added;
            }

            int totalAdded = amount - remaining;
            if (totalAdded <= 0)
                return false;

            if (!silent)
            {
                ItemAdded?.Invoke(item, totalAdded);
                Changed?.Invoke();
                SelectedSlotChanged?.Invoke(selectedIndex, slots[selectedIndex]);
            }

            return remaining == 0;
        }

        public bool TryRemoveItem(IInventoryItem item, int amount = 1)
        {
            if (item == null || amount <= 0 || slots == null)
                return false;
            if (CountOf(item) < amount)
                return false;

            int remaining = amount;
            for (int i = slots.Length - 1; i >= 0 && remaining > 0; i--)
            {
                if (!Matches(slots[i].Item, item))
                    continue;
                int take = Mathf.Min(slots[i].Count, remaining);
                slots[i].Count -= take;
                remaining -= take;
                if (slots[i].Count <= 0)
                    slots[i].Clear();
            }

            Changed?.Invoke();
            SelectedSlotChanged?.Invoke(selectedIndex, slots[selectedIndex]);
            return true;
        }

        public int CountOf(IInventoryItem item)
        {
            if (item == null || slots == null)
                return 0;
            int total = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (Matches(slots[i].Item, item))
                    total += slots[i].Count;
            }
            return total;
        }

        public bool HasItem(IInventoryItem item) => CountOf(item) > 0;

        public void SelectSlot(int index)
        {
            if (slots == null || slots.Length == 0)
                return;
            int clamped = Mathf.Clamp(index, 0, slots.Length - 1);
            if (clamped == selectedIndex)
                return;
            selectedIndex = clamped;
            SelectedSlotChanged?.Invoke(selectedIndex, slots[selectedIndex]);
            Changed?.Invoke();
        }

        public void CycleSlot(int delta)
        {
            if (slots == null || slots.Length == 0 || delta == 0)
                return;
            int next = (selectedIndex + delta) % slots.Length;
            if (next < 0)
                next += slots.Length;
            SelectSlot(next);
        }

        /// <summary>
        /// Uses the currently equipped hotbar item. If a pluggable <see cref="IItemUseBehavior"/> is attached,
        /// executes it first and only consumes/triggers cooldown if execution succeeds.
        /// </summary>
        public bool TryUseSelected(in ItemUseContext context, out IInventoryItem usedItem)
        {
            usedItem = null;
            if (Time.time < nextUseTime)
                return false;

            InventorySlot slot = SelectedSlot;
            if (slot.IsEmpty)
                return false;

            IInventoryItem candidate = slot.Item;
            IItemUseBehavior behavior = candidate.UseBehavior;
            if (behavior != null)
            {
                if (!behavior.CanExecute(in context) || !behavior.Execute(in context))
                    return false;
            }
            else if (context.ToolTarget != null && context.ToolTarget.CanUseTool(context.Interactor, candidate))
            {
                if (!context.ToolTarget.UseTool(context.Interactor, candidate))
                    return false;
            }

            usedItem = candidate;
            CommitItemUse(usedItem);
            return true;
        }

        /// <summary>Direct convenience overload when no world context is needed.</summary>
        public bool TryUseSelected(out IInventoryItem usedItem)
        {
            usedItem = null;
            if (Time.time < nextUseTime)
                return false;

            InventorySlot slot = SelectedSlot;
            if (slot.IsEmpty)
                return false;

            usedItem = slot.Item;
            CommitItemUse(usedItem);
            return true;
        }

        void CommitItemUse(IInventoryItem usedItem)
        {
            nextUseTime = Time.time + usedItem.UseCooldown;

            if (usedItem.ConsumeOnUse)
            {
                slots[selectedIndex].Count--;
                if (slots[selectedIndex].Count <= 0)
                    slots[selectedIndex].Clear();
                SelectedSlotChanged?.Invoke(selectedIndex, slots[selectedIndex]);
            }

            ItemUsed?.Invoke(usedItem);
            Changed?.Invoke();
        }

        public void SetStartingItems(ItemDefinition[] items, int initialCoins = 25)
        {
            startingItems = items ?? Array.Empty<ItemDefinition>();
            startingCoins = Mathf.Max(0, initialCoins);
        }

        static bool Matches(IInventoryItem a, IInventoryItem b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null)
                return false;
            return string.Equals(a.ItemId, b.ItemId, StringComparison.Ordinal);
        }
    }
}
