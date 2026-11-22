using UnityEngine;
using UpcycleCity.Crafting;
using UpcycleCity.Ecosystem;
using UpcycleCity.UI;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// One id space for everything a player can carry in their <see cref="RecyclingBag"/>:
    /// <list type="bullet">
    /// <item>0–31: finds dug out of street bins (<see cref="BinLootTable"/> entry index).</item>
    /// <item>32–62: street trash picked up off the ground (<see cref="WasteCatalog"/> entry index + 32).</item>
    /// </list>
    /// Host and clients resolve ids from the same shared assets, so only the small id is replicated.
    /// </summary>
    public static class CarryItems
    {
        public const int TrashOffset = 32;
        public const int MaxId = 62;

        /// <summary>Hotbar/shop item that upgrades a player's carry from their hands to a Recycling Bag.</summary>
        public const string BagUpgradeItemId = "upgrade_recycling_bag";

        /// <summary>Quincaillerie item that buys a pushable Shopping Cart (spawned in the world by the host).</summary>
        public const string CartUpgradeItemId = "upgrade_shopping_cart";

        public const int HandsCapacity = 3;
        public const int BagCapacity = 10;

        /// <summary>Items a Shopping Cart holds: a moving bag (host-authoritative, replicated in <see cref="CartStorage"/>).</summary>
        public const int CartCapacity = 24;

        /// <summary>What a carried item is, resolved from its id.</summary>
        public struct Info
        {
            public bool Valid;
            public string Name;
            public bool Organic;
            public WasteStream Stream;
            public int BaseValue;      // coins before the RÉCUP stream rate
            public string MaterialId;  // Team Material Stock credited at a sorting/compost bin
            public float MaterialAmount;
            public string IconKey;     // for IconLibrary
            public bool IsTrash;
            public int CatalogIndex;   // WasteCatalog index for trash (and for respawning anything thrown out)
            public LootRarity Rarity;
        }

        /// <summary>The waste catalog trash ids index into: the city's, or <see cref="CatalogOverride"/> (scenes/tests without a city).</summary>
        public static WasteCatalog Catalog =>
            CatalogOverride != null ? CatalogOverride
            : CityEcosystemManager.Active != null ? CityEcosystemManager.Active.DefaultCatalog : null;

        /// <summary>Optional catalog used when no <see cref="CityEcosystemManager"/> is in the scene.</summary>
        public static WasteCatalog CatalogOverride { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => CatalogOverride = null;

        public static bool IsTrashId(int id) => id >= TrashOffset && id <= MaxId;

        public static int IdForLoot(int lootIndex) => lootIndex >= 0 && lootIndex < TrashOffset ? lootIndex : -1;

        public static int IdForCatalogIndex(int catalogIndex) =>
            catalogIndex >= 0 && TrashOffset + catalogIndex <= MaxId ? TrashOffset + catalogIndex : -1;

        /// <summary>The carry id of a piece of street trash (by its archetype), or -1 if it can't be carried.</summary>
        public static int IdForTrash(TrashItem item)
        {
            WasteCatalog catalog = Catalog;
            if (item == null || catalog == null || item.IsBulky)
                return -1;
            string archetype = item.ArchetypeId;
            for (int i = 0; i < catalog.Entries.Count; i++)
            {
                WasteCatalog.Entry e = catalog.Entries[i];
                if (e != null && string.Equals(e.ResolvedArchetypeId, archetype, System.StringComparison.Ordinal))
                    return e.isBulky ? -1 : IdForCatalogIndex(i);
            }
            return -1;
        }

        public static Info Resolve(int id)
        {
            var info = new Info { CatalogIndex = -1 };
            if (IsTrashId(id))
            {
                WasteCatalog catalog = Catalog;
                int index = id - TrashOffset;
                if (catalog == null || index >= catalog.Entries.Count || catalog.Entries[index] == null)
                    return info;
                WasteCatalog.Entry e = catalog.Entries[index];
                info.Valid = true;
                info.IsTrash = true;
                info.Name = e.displayName;
                info.Stream = e.stream;
                info.BaseValue = e.baseCoinValue;
                info.IconKey = e.ResolvedArchetypeId;
                info.CatalogIndex = index;
                info.Rarity = LootRarity.Common;
                ResolveTrashMaterial(e, ref info);
                return info;
            }

            BinLootTable table = BinLootTable.Active;
            if (table == null || !table.TryGet(id, out BinLootTable.Entry loot))
                return info;
            info.Valid = true;
            info.Name = loot.displayName;
            info.Organic = loot.organic;
            info.Stream = loot.stream;
            info.BaseValue = loot.coins;
            info.MaterialId = loot.materialId;
            info.MaterialAmount = loot.materialAmount;
            info.IconKey = loot.id;
            info.Rarity = loot.rarity;
            info.CatalogIndex = CatalogIndexFor(loot);
            return info;
        }

        public static Sprite IconFor(int id)
        {
            IconLibrary icons = IconLibrary.Active;
            if (icons == null)
                return null;
            Info info = Resolve(id);
            if (!info.Valid)
                return null;
            return info.IsTrash ? icons.ForTrash(info.IconKey) : icons.ForLoot(info.IconKey);
        }

        public static Color AccentFor(in Info info) =>
            info.Organic ? BinLootTable.OrganicColor : WasteStreamPalette.ColorFor(info.Stream);

        /// <summary>True when this item belongs in the given bin: organic → compost, recyclables → their stream.</summary>
        public static bool Accepts(in Info info, bool organicBin, WasteStream stream) =>
            info.Valid && info.Organic == organicBin && (organicBin || info.Stream == stream);

        static void ResolveTrashMaterial(WasteCatalog.Entry e, ref Info info)
        {
            CraftingDatabase db = CraftingDatabase.LoadDefault();
            if (db != null && db.TryResolveTrashMaterialByArchetype(e.ResolvedArchetypeId, e.stream, out MaterialDefinition material, out float yield))
            {
                info.MaterialId = material.MaterialId;
                info.MaterialAmount = yield;
            }
        }

        // A bin find thrown back out lands as the matching street trash (same archetype, else same stream; organic as a bag)
        static int CatalogIndexFor(BinLootTable.Entry loot)
        {
            WasteCatalog catalog = Catalog;
            if (catalog == null)
                return -1;
            int sameStream = -1, bag = -1;
            for (int i = 0; i < catalog.Entries.Count; i++)
            {
                WasteCatalog.Entry e = catalog.Entries[i];
                if (e == null || e.isBulky)
                    continue;
                if (string.Equals(e.ResolvedArchetypeId, loot.id, System.StringComparison.Ordinal))
                    return i;
                if (sameStream < 0 && !loot.organic && e.stream == loot.stream)
                    sameStream = i;
                if (bag < 0 && e.ResolvedArchetypeId == "rubbish_bag")
                    bag = i;
            }
            return loot.organic ? bag : sameStream >= 0 ? sameStream : bag;
        }
    }
}
