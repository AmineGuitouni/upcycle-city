using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Waste
{
    /// <summary>How hard a find is to come by when rummaging through a street bin.</summary>
    public enum LootRarity
    {
        Common = 0,
        Uncommon = 1,
        Rare = 2,
        Epic = 3,
    }

    /// <summary>
    /// What a street bin holds when the player searches it: mostly organic waste (future compost / fertiliser)
    /// with recyclables mixed in by rarity. Rarer finds take longer to dig out, so they turn up last.
    /// One shared asset lives at <c>Resources/BinLootTable</c> so host and clients agree on entry indices.
    /// </summary>
    [CreateAssetMenu(fileName = "BinLootTable", menuName = "Upcycle City/Waste/Bin Loot Table")]
    public sealed class BinLootTable : ScriptableObject
    {
        /// <summary>Entries are sent over the network as 5-bit indices.</summary>
        public const int MaxEntries = 31;
        public const string ResourcePath = "BinLootTable";

        [Serializable]
        public sealed class Entry
        {
            public string id = "item";
            public string displayName = "Item";
            [Tooltip("Organic waste goes to a compost bin; recyclables go to the sorting bin of their stream.")]
            public bool organic;
            public WasteStream stream = WasteStream.Plastic;
            public LootRarity rarity = LootRarity.Common;
            [Tooltip("Relative chance among entries of the same rarity (and kind).")]
            [Min(0f)] public float weight = 1f;
            [Tooltip("Team Material Stock credited when the bag is emptied at the right bin.")]
            public string materialId = "plastic_scrap";
            [Min(0f)] public float materialAmount = 1f;
            [Tooltip("Coins paid to the team when delivered.")]
            [Min(0)] public int coins = 2;
        }

        [SerializeField] List<Entry> entries = new List<Entry>();

        [Header("Rolling")]
        [Tooltip("Share of a bin's contents that is organic waste (the rest are recyclables).")]
        [SerializeField, Range(0f, 1f)] float organicShare = 0.5f;

        [Tooltip("Relative chance of each rarity for a recyclable find: Common, Uncommon, Rare, Epic.")]
        [SerializeField] float[] rarityWeights = { 60f, 28f, 10f, 2f };

        [Header("Searching")]
        [Tooltip("Seconds of rummaging to dig out one find of each rarity: Common, Uncommon, Rare, Epic.")]
        [SerializeField] float[] searchSeconds = { 0.7f, 1.4f, 2.6f, 4f };

        [Tooltip("Seconds to dig out one piece of organic waste.")]
        [SerializeField, Min(0.05f)] float organicSearchSeconds = 0.45f;

        [Tooltip("Slots in a full street bin (fill level 100%). At most 12.")]
        [SerializeField, Range(1, 12)] int binSlots = 12;

        [Tooltip("Slots in each player's Recycling Bag. At most 12.")]
        [SerializeField, Range(1, 12)] int bagSlots = 10;

        static BinLootTable active;

        public IReadOnlyList<Entry> Entries => entries;
        public int BinSlots => binSlots;
        public int BagSlots => bagSlots;

        /// <summary>The shared table (loaded once from Resources).</summary>
        public static BinLootTable Active
        {
            get
            {
                if (active == null)
                    active = Resources.Load<BinLootTable>(ResourcePath);
                return active;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => active = null;

        public bool TryGet(int index, out Entry entry)
        {
            entry = index >= 0 && index < entries.Count ? entries[index] : null;
            return entry != null;
        }

        /// <summary>Seconds of searching it takes to find entry <paramref name="index"/>.</summary>
        public float SearchSecondsFor(int index)
        {
            if (!TryGet(index, out Entry e))
                return organicSearchSeconds;
            if (e.organic)
                return organicSearchSeconds;
            int r = (int)e.rarity;
            return searchSeconds != null && r < searchSeconds.Length ? Mathf.Max(0.05f, searchSeconds[r]) : 1f;
        }

        public static Color RarityColor(LootRarity rarity)
        {
            switch (rarity)
            {
                case LootRarity.Uncommon: return new Color(0.30f, 0.78f, 0.42f);
                case LootRarity.Rare: return new Color(0.27f, 0.58f, 0.98f);
                case LootRarity.Epic: return new Color(0.74f, 0.42f, 0.98f);
                default: return new Color(0.62f, 0.66f, 0.68f);
            }
        }

        public static readonly Color OrganicColor = new Color(0.47f, 0.34f, 0.18f); // compost brown

        /// <summary>Rolls one entry index: organic waste, or a recyclable of a random rarity. -1 if the table is empty.</summary>
        public int Roll(System.Random random)
        {
            if (entries.Count == 0)
                return -1;
            bool organic = random.NextDouble() < organicShare;
            int rarity = -1;
            if (!organic)
            {
                float total = 0f;
                for (int i = 0; i < rarityWeights.Length; i++)
                    total += Mathf.Max(0f, rarityWeights[i]);
                float pick = (float)random.NextDouble() * total;
                rarity = 0;
                for (int i = 0; i < rarityWeights.Length; i++)
                {
                    pick -= Mathf.Max(0f, rarityWeights[i]);
                    if (pick <= 0f) { rarity = i; break; }
                }
            }

            // Fall back to rarer-to-commoner tiers, then anything, if a tier has no entries
            int found = PickWeighted(random, organic, rarity);
            for (int r = rarity - 1; found < 0 && r >= 0; r--)
                found = PickWeighted(random, false, r);
            if (found < 0)
                found = PickWeighted(random, !organic, -1);
            return found;
        }

        int PickWeighted(System.Random random, bool organic, int rarity)
        {
            float total = 0f;
            int limit = Mathf.Min(entries.Count, MaxEntries);
            for (int i = 0; i < limit; i++)
            {
                Entry e = entries[i];
                if (e.organic == organic && (organic || rarity < 0 || (int)e.rarity == rarity))
                    total += e.weight;
            }
            if (total <= 0f)
                return -1;
            float pick = (float)random.NextDouble() * total;
            for (int i = 0; i < limit; i++)
            {
                Entry e = entries[i];
                if (e.organic != organic || (!organic && rarity >= 0 && (int)e.rarity != rarity))
                    continue;
                pick -= e.weight;
                if (pick <= 0f)
                    return i;
            }
            return -1;
        }

        /// <summary>Editor/tests: replaces the content.</summary>
        public void SetEntries(List<Entry> newEntries) => entries = newEntries ?? new List<Entry>();

        /// <summary>Editor/tests: tuning.</summary>
        public void ConfigureSlots(int bin, int bag)
        {
            binSlots = Mathf.Clamp(bin, 1, 12);
            bagSlots = Mathf.Clamp(bag, 1, 12);
        }

        /// <summary>The default Bourguiba bin: street-food leftovers plus Tunis's everyday recyclables.</summary>
        public static List<Entry> DefaultEntries()
        {
            Entry Organic(string id, string name, float w) => new Entry
            {
                id = id, displayName = name, organic = true, rarity = LootRarity.Common, weight = w,
                materialId = "organic_waste", materialAmount = 0.5f, coins = 1,
            };
            Entry Rec(string id, string name, WasteStream s, LootRarity r, string mat, float amount, int coins, float w = 1f) => new Entry
            {
                id = id, displayName = name, stream = s, rarity = r, weight = w,
                materialId = mat, materialAmount = amount, coins = coins,
            };

            return new List<Entry>
            {
                Organic("food_scraps", "Food Scraps", 3f),
                Organic("orange_peels", "Orange Peels", 2f),
                Organic("coffee_grounds", "Coffee Grounds", 2f),
                Organic("stale_bread", "Stale Bread", 1.5f),
                Organic("melon_rinds", "Melon Rinds", 1f),

                Rec("water_bottle", "Water Bottle", WasteStream.Plastic, LootRarity.Common, "plastic_scrap", 0.6f, 2, 2f),
                Rec("newspaper", "Newspaper", WasteStream.Paper, LootRarity.Common, "paper_carton", 0.6f, 2, 1.5f),
                Rec("soda_can", "Soda Can", WasteStream.Cans, LootRarity.Common, "aluminum_cans", 0.5f, 2, 1.5f),
                Rec("kaskrout_wrap", "Kaskrout Paper", WasteStream.Paper, LootRarity.Common, "paper_carton", 0.4f, 1),

                Rec("pizza_box", "Clean Carton", WasteStream.Paper, LootRarity.Uncommon, "paper_carton", 1.2f, 4),
                Rec("harissa_tin", "Harissa Tin", WasteStream.Cans, LootRarity.Uncommon, "aluminum_cans", 1f, 4),
                Rec("glass_bottle", "Glass Bottle", WasteStream.Glass, LootRarity.Uncommon, "glass_cullet", 1f, 4),
                Rec("oil_jug", "Oil Jug", WasteStream.Plastic, LootRarity.Uncommon, "plastic_scrap", 1.2f, 4),

                Rec("jam_jar", "Intact Jam Jar", WasteStream.Glass, LootRarity.Rare, "glass_cullet", 2.5f, 9),
                Rec("alu_tray", "Aluminium Tray", WasteStream.Cans, LootRarity.Rare, "aluminum_cans", 2.5f, 9),
                Rec("crate_lid", "Plastic Crate Lid", WasteStream.Plastic, LootRarity.Rare, "plastic_scrap", 2.5f, 9),

                Rec("copper_kettle", "Dented Copper Kettle", WasteStream.Cans, LootRarity.Epic, "aluminum_cans", 5f, 20),
                Rec("perfume_flask", "Vintage Perfume Flask", WasteStream.Glass, LootRarity.Epic, "glass_cullet", 5f, 20),
            };
        }
    }
}
