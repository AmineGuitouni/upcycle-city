using System;
using UpcycleCity.Core;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// A player's Recycling Bag: finds taken out of street bins (recyclables and organic waste), carried until
    /// they are emptied into the matching sorting bin or the compost bin. Separate from the gear hotbar
    /// (<c>PlayerInventory</c>), which never holds trash.
    /// <para>The host owns every bag; each player's bag replicates to that player as one synced record.</para>
    /// </summary>
    public sealed class RecyclingBag : ISyncedStateProvider
    {
        readonly int[] slots = new int[LootCodec.MaxSlots];

        public RecyclingBag(ulong ownerClientId)
        {
            OwnerClientId = ownerClientId;
            SyncKey = KeyFor(ownerClientId);
        }

        public ulong OwnerClientId { get; }
        public string SyncKey { get; }
        public int Count { get; private set; }
        public int Capacity => BinLootTable.Active != null ? BinLootTable.Active.BagSlots : 10;
        public bool IsFull => Count >= Capacity;
        public int this[int index] => slots[index];

        /// <summary>Raised whenever the contents change (on the host or when a replicated update arrives).</summary>
        public event Action Changed;

        public static string KeyFor(ulong clientId) => "bag:" + clientId;

        public bool TryAdd(int entry)
        {
            if (entry < 0 || IsFull)
                return false;
            slots[Count++] = entry;
            Changed?.Invoke();
            return true;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= Count)
                return;
            for (int i = index; i < Count - 1; i++)
                slots[i] = slots[i + 1];
            Count--;
            Changed?.Invoke();
        }

        /// <summary>How many finds in the bag the given bin takes.</summary>
        public int CountMatching(BinLootTable table, bool organic, WasteStream stream)
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
            {
                if (table.TryGet(slots[i], out BinLootTable.Entry e) && Accepts(e, organic, stream))
                    n++;
            }
            return n;
        }

        public static bool Accepts(BinLootTable.Entry e, bool organic, WasteStream stream) =>
            e.organic == organic && (organic || e.stream == stream);

        public SyncedStateRecord CaptureSyncState()
        {
            LootCodec.Pack(slots, Count, out int i0, out int i1);
            return new SyncedStateRecord { i0 = i0, i1 = i1 };
        }

        public void ApplySyncState(in SyncedStateRecord state)
        {
            int before = Count;
            int b0 = 0, b1 = 0;
            if (before > 0)
                LootCodec.Pack(slots, before, out b0, out b1);
            Count = LootCodec.Unpack(state.i0, state.i1, slots);
            if (before != Count || b0 != state.i0 || b1 != state.i1)
                Changed?.Invoke();
        }
    }
}
