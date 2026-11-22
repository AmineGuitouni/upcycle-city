using System;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// Packs up to 10 carry ids (0..62, stored +1 so 0 means empty) into two ints at 6 bits each.
    /// </summary>
    public static class CarryCodec
    {
        public const int MaxSlots = 10;
        const int Bits = 6;
        const int PerInt = 5;
        const int Mask = (1 << Bits) - 1;

        public static void Pack(int[] values, int count, out int i0, out int i1) => Pack(values, 0, count, out i0, out i1);

        /// <summary>Packs <paramref name="count"/> ids starting at <paramref name="offset"/> (at most <see cref="MaxSlots"/>).</summary>
        public static void Pack(int[] values, int offset, int count, out int i0, out int i1)
        {
            i0 = 0;
            i1 = 0;
            for (int k = 0; k < count && k < MaxSlots; k++)
            {
                int v = Mathf.Clamp(values[offset + k] + 1, 0, Mask);
                if (k < PerInt) i0 |= v << (k * Bits);
                else i1 |= v << ((k - PerInt) * Bits);
            }
        }

        public static int Unpack(int i0, int i1, int[] values)
        {
            int count = 0;
            for (int k = 0; k < MaxSlots; k++)
            {
                int v = k < PerInt ? (i0 >> (k * Bits)) & Mask : (i1 >> ((k - PerInt) * Bits)) & Mask;
                if (v == 0)
                    break;
                values[count++] = v - 1;
            }
            return count;
        }
    }

    /// <summary>
    /// What a player is carrying: trash picked up off the street and finds dug out of street bins (ids from
    /// <see cref="CarryItems"/>). Without a bag the player hugs up to <see cref="CarryItems.HandsCapacity"/> items in
    /// their arm; buying a Recycling Bag raises that to <see cref="CarryItems.BagCapacity"/>. Emptied at sorting bins,
    /// the compost bin or sold at RÉCUP. Separate from the gear hotbar (<c>PlayerInventory</c>), which never holds trash.
    /// <para>The host owns every bag; each player's bag (contents + bag owned) replicates as one synced record.</para>
    /// </summary>
    public sealed class RecyclingBag : ISyncedStateProvider
    {
        readonly int[] slots = new int[CarryCodec.MaxSlots];

        public RecyclingBag(ulong ownerClientId)
        {
            OwnerClientId = ownerClientId;
            SyncKey = KeyFor(ownerClientId);
        }

        public ulong OwnerClientId { get; }
        public string SyncKey { get; }
        public int Count { get; private set; }

        /// <summary>True once the player bought a Recycling Bag (before that they carry in their arms).</summary>
        public bool HasBag { get; private set; }

        /// <summary>True once the player bought a Shopping Cart (one per player; the cart itself lives in the world).</summary>
        public bool HasCart { get; private set; }

        public int Capacity => HasBag ? CarryItems.BagCapacity : CarryItems.HandsCapacity;
        public bool IsFull => Count >= Capacity;
        public int this[int index] => slots[index];

        /// <summary>Raised whenever the contents or the bag change (on the host or when a replicated update arrives).</summary>
        public event Action Changed;

        public static string KeyFor(ulong clientId) => "bag:" + clientId;

        public bool TryAdd(int id)
        {
            if (id < 0 || id > CarryItems.MaxId || IsFull)
                return false;
            slots[Count++] = id;
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

        /// <summary>Host: the player bought a bag.</summary>
        public void GrantBag()
        {
            if (HasBag)
                return;
            HasBag = true;
            Changed?.Invoke();
        }

        /// <summary>Host: the player bought a Shopping Cart (or lost it: <paramref name="owned"/> false).</summary>
        public void SetHasCart(bool owned)
        {
            if (HasCart == owned)
                return;
            HasCart = owned;
            Changed?.Invoke();
        }

        /// <summary>How many carried items the given bin takes.</summary>
        public int CountMatching(bool organic, WasteStream stream)
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
            {
                if (CarryItems.Accepts(CarryItems.Resolve(slots[i]), organic, stream))
                    n++;
            }
            return n;
        }

        public SyncedStateRecord CaptureSyncState()
        {
            CarryCodec.Pack(slots, Count, out int i0, out int i1);
            return new SyncedStateRecord { i0 = i0, i1 = i1, flags = (byte)((HasBag ? 1 : 0) | (HasCart ? 2 : 0)) };
        }

        public void ApplySyncState(in SyncedStateRecord state)
        {
            CarryCodec.Pack(slots, Count, out int b0, out int b1);
            bool hadBag = HasBag, hadCart = HasCart;
            int before = Count;
            Count = CarryCodec.Unpack(state.i0, state.i1, slots);
            HasBag = (state.flags & 1) != 0;
            HasCart = (state.flags & 2) != 0;
            if (before != Count || b0 != state.i0 || b1 != state.i1 || hadBag != HasBag || hadCart != HasCart)
                Changed?.Invoke();
        }
    }
}
