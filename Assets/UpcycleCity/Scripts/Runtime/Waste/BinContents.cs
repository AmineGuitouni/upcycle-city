using UnityEngine;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// Packs up to 12 small indices (0..30, stored +1 so 0 means empty) into two ints at 5 bits each, so bin
    /// contents and bags replicate in a single compact <c>SyncedStateRecord</c>.
    /// </summary>
    public static class LootCodec
    {
        public const int MaxSlots = 12;
        const int Bits = 5;
        const int PerInt = 6;
        const int Mask = (1 << Bits) - 1;

        public static void Pack(int[] values, int count, out int i0, out int i1)
        {
            i0 = 0;
            i1 = 0;
            for (int k = 0; k < count && k < MaxSlots; k++)
            {
                int v = Mathf.Clamp(values[k] + 1, 0, Mask);
                if (k < PerInt) i0 |= v << (k * Bits);
                else i1 |= v << ((k - PerInt) * Bits);
            }
        }

        /// <summary>Unpacks into <paramref name="values"/> and returns how many slots are filled (stops at the first empty).</summary>
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
    /// What is inside one street bin and how far the search through it has got. Finds are kept in the order they
    /// turn up: each takes <see cref="BinLootTable.SearchSecondsFor"/> seconds of rummaging, and new finds are slotted
    /// in by search time among the ones not found yet, so common things show up first and rare ones last.
    /// <para>
    /// Search progress is replicated as a base amount plus the shared server time the current search started, so
    /// every client animates it locally and the host only sends a change when someone starts, stops or takes a find.
    /// </para>
    /// </summary>
    public sealed class BinContents
    {
        readonly int[] items = new int[LootCodec.MaxSlots];

        public int Count { get; private set; }

        /// <summary>Seconds of searching done before <see cref="SearchStart"/>.</summary>
        public float ProgressBase { get; private set; }

        /// <summary>Server time the running search started, or -1 when nobody is searching.</summary>
        public float SearchStart { get; private set; } = -1f;

        public bool Searching => SearchStart >= 0f;

        public int this[int index] => items[index];

        public float Progress(float now) => ProgressBase + (Searching ? Mathf.Max(0f, now - SearchStart) : 0f);

        public float TotalTime(BinLootTable table)
        {
            float total = 0f;
            for (int i = 0; i < Count; i++)
                total += table.SearchSecondsFor(items[i]);
            return total;
        }

        /// <summary>Search progress at which find <paramref name="index"/> turns up.</summary>
        public float RevealTime(int index, BinLootTable table)
        {
            float t = 0f;
            for (int i = 0; i <= index && i < Count; i++)
                t += table.SearchSecondsFor(items[i]);
            return t;
        }

        public int RevealedCount(float now, BinLootTable table)
        {
            float progress = Progress(now);
            float t = 0f;
            for (int i = 0; i < Count; i++)
            {
                t += table.SearchSecondsFor(items[i]);
                if (progress + 1e-4f < t)
                    return i;
            }
            return Count;
        }

        public bool IsRevealed(int index, float now, BinLootTable table) =>
            index >= 0 && index < Count && Progress(now) + 1e-4f >= RevealTime(index, table);

        /// <summary>0..1 through digging out the next find (for the search bar).</summary>
        public float NextFindFraction(float now, BinLootTable table)
        {
            int next = RevealedCount(now, table);
            if (next >= Count)
                return 1f;
            float end = RevealTime(next, table);
            float start = end - table.SearchSecondsFor(items[next]);
            return Mathf.InverseLerp(start, end, Progress(now));
        }

        // ------------------------------------------------------------ host-side changes

        /// <summary>Starts or pauses the search (host).</summary>
        public void SetSearching(bool searching, float now, BinLootTable table)
        {
            Fold(now, table);
            SearchStart = searching ? now : -1f;
        }

        /// <summary>Adds a find not yet turned up, in search-time order among the hidden ones (host).</summary>
        public bool Add(int entry, float now, BinLootTable table)
        {
            if (Count >= LootCodec.MaxSlots || entry < 0)
                return false;
            Fold(now, table);
            int revealed = RevealedCount(now, table);
            float cost = table.SearchSecondsFor(entry);
            int at = Count;
            while (at > revealed && table.SearchSecondsFor(items[at - 1]) > cost)
                at--;
            for (int i = Count; i > at; i--)
                items[i] = items[i - 1];
            items[at] = entry;
            Count++;
            return true;
        }

        /// <summary>Takes out a find that has turned up (host). Returns its entry index, or -1.</summary>
        public int TakeAt(int index, float now, BinLootTable table)
        {
            if (!IsRevealed(index, now, table))
                return -1;
            Fold(now, table);
            int entry = items[index];
            ProgressBase = Mathf.Max(0f, ProgressBase - table.SearchSecondsFor(entry));
            for (int i = index; i < Count - 1; i++)
                items[i] = items[i + 1];
            Count--;
            return entry;
        }

        public void Clear()
        {
            Count = 0;
            ProgressBase = 0f;
        }

        // Moves running progress into the base, never past the end of what's in the bin
        void Fold(float now, BinLootTable table)
        {
            float total = TotalTime(table);
            ProgressBase = Mathf.Min(Progress(now), total);
            if (Searching)
                SearchStart = now;
        }

        // ------------------------------------------------------------ replication

        public void Capture(out float progressBase, out float searchStart, out int i0, out int i1)
        {
            progressBase = ProgressBase;
            searchStart = SearchStart;
            LootCodec.Pack(items, Count, out i0, out i1);
        }

        public void Apply(float progressBase, float searchStart, int i0, int i1)
        {
            ProgressBase = Mathf.Max(0f, progressBase);
            SearchStart = searchStart;
            Count = LootCodec.Unpack(i0, i1, items);
        }
    }
}
