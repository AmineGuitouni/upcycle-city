using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Waste;

namespace UpcycleCity.Tests.EditMode
{
    /// <summary>Street-bin search logic: reveal order by rarity, taking, and the compact replication encoding.</summary>
    public sealed class BinSearchEditModeTests
    {
        BinLootTable table;

        // 0 organic (0.5 s), 1 common plastic (1 s), 2 rare glass (3 s)
        [SetUp]
        public void SetUp()
        {
            table = ScriptableObject.CreateInstance<BinLootTable>();
            table.SetEntries(new List<BinLootTable.Entry>
            {
                new BinLootTable.Entry { id = "peel", displayName = "Peel", organic = true, materialId = "organic_waste" },
                new BinLootTable.Entry { id = "bottle", displayName = "Bottle", stream = WasteStream.Plastic, rarity = LootRarity.Common },
                new BinLootTable.Entry { id = "jar", displayName = "Jar", stream = WasteStream.Glass, rarity = LootRarity.Rare },
            });
            table.ConfigureSlots(12, 10);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(table);

        [Test]
        public void LootCodec_RoundTripsTwelveSlots()
        {
            var values = new[] { 0, 30, 5, 1, 2, 3, 4, 7, 8, 9, 10, 29 };
            LootCodec.Pack(values, values.Length, out int i0, out int i1);
            var back = new int[LootCodec.MaxSlots];
            int count = LootCodec.Unpack(i0, i1, back);
            Assert.AreEqual(12, count);
            CollectionAssert.AreEqual(values, back);

            LootCodec.Pack(values, 3, out i0, out i1);
            Assert.AreEqual(3, LootCodec.Unpack(i0, i1, back));
            Assert.AreEqual(0, i1);
        }

        [Test]
        public void Contents_RareFindsTurnUpLast_AndRevealOverTime()
        {
            var contents = new BinContents();
            contents.Add(2, 0f, table); // rare first in roll order…
            contents.Add(1, 0f, table);
            contents.Add(0, 0f, table);

            // …but sorted so the quickest finds come first
            Assert.AreEqual(0, contents[0]);
            Assert.AreEqual(1, contents[1]);
            Assert.AreEqual(2, contents[2]);

            float organic = table.SearchSecondsFor(0), common = table.SearchSecondsFor(1), rare = table.SearchSecondsFor(2);
            Assert.Less(organic, rare);
            Assert.Less(common, rare);

            Assert.AreEqual(0, contents.RevealedCount(100f, table), "Nothing turns up until someone searches.");
            contents.SetSearching(true, 10f, table);
            Assert.AreEqual(0, contents.RevealedCount(10f + organic * 0.5f, table));
            Assert.AreEqual(1, contents.RevealedCount(10f + organic + 0.01f, table));
            Assert.AreEqual(2, contents.RevealedCount(10f + organic + common + 0.01f, table));
            Assert.AreEqual(3, contents.RevealedCount(10f + organic + common + rare + 0.01f, table));

            // Pausing keeps progress
            contents.SetSearching(false, 10f + organic + 0.01f, table);
            Assert.AreEqual(1, contents.RevealedCount(999f, table));
        }

        [Test]
        public void Contents_TakeOnlyRevealed_AndRemainingKeepTheirState()
        {
            var contents = new BinContents();
            contents.Add(0, 0f, table);
            contents.Add(1, 0f, table);
            contents.Add(2, 0f, table);
            contents.SetSearching(true, 0f, table);
            float t = table.SearchSecondsFor(0) + table.SearchSecondsFor(1) + 0.01f; // two found, the rare one not yet

            Assert.AreEqual(-1, contents.TakeAt(2, t, table), "Can't take a find that hasn't turned up.");
            Assert.AreEqual(0, contents.TakeAt(0, t, table));
            Assert.AreEqual(2, contents.Count);
            Assert.IsTrue(contents.IsRevealed(0, t, table), "The other found item stays found.");
            Assert.IsFalse(contents.IsRevealed(1, t, table), "The rare find is still hidden.");
        }

        [Test]
        public void Contents_NewFindsAfterFullSearchStartHidden()
        {
            var contents = new BinContents();
            contents.Add(1, 0f, table);
            contents.SetSearching(true, 0f, table);
            Assert.AreEqual(1, contents.RevealedCount(50f, table));
            contents.Add(2, 50f, table);
            Assert.AreEqual(1, contents.RevealedCount(50f, table), "A new find doesn't appear instantly from banked search time.");
            Assert.AreEqual(2, contents.RevealedCount(50f + table.SearchSecondsFor(2) + 0.01f, table));
        }

        [Test]
        public void Contents_CaptureApply_ReplicatesExactly()
        {
            var host = new BinContents();
            host.Add(1, 0f, table);
            host.Add(0, 0f, table);
            host.SetSearching(true, 3f, table);
            host.Capture(out float b, out float s, out int i0, out int i1);
            var record = new SyncedStateRecord { v1 = b, v2 = s, i0 = i0, i1 = i1 };

            var client = new BinContents();
            client.Apply(record.v1, record.v2, record.i0, record.i1);
            Assert.AreEqual(host.Count, client.Count);
            for (int i = 0; i < host.Count; i++)
                Assert.AreEqual(host[i], client[i]);
            Assert.AreEqual(host.RevealedCount(4.2f, table), client.RevealedCount(4.2f, table),
                "The client animates the same search from the shared clock.");
        }

        [Test]
        public void Bag_RespectsCapacityAndMatchesStreams()
        {
            var bag = new RecyclingBag(7);
            Assert.AreEqual("bag:7", bag.SyncKey);
            Assert.IsTrue(bag.TryAdd(0));
            Assert.IsTrue(bag.TryAdd(1));
            Assert.IsTrue(bag.TryAdd(2));
            Assert.AreEqual(1, bag.CountMatching(table, true, WasteStream.Plastic));
            Assert.AreEqual(1, bag.CountMatching(table, false, WasteStream.Plastic));
            Assert.AreEqual(1, bag.CountMatching(table, false, WasteStream.Glass));

            var mirror = new RecyclingBag(7);
            int changes = 0;
            mirror.Changed += () => changes++;
            mirror.ApplySyncState(bag.CaptureSyncState());
            Assert.AreEqual(3, mirror.Count);
            Assert.AreEqual(2, mirror[2]);
            Assert.AreEqual(1, changes);
            mirror.ApplySyncState(bag.CaptureSyncState());
            Assert.AreEqual(1, changes, "Re-applying the same state is not a change.");
        }

        [Test]
        public void LootTable_RollsOnlyValidIndices_AndBothKinds()
        {
            var random = new System.Random(3);
            bool organic = false, recyclable = false;
            for (int i = 0; i < 400; i++)
            {
                int e = table.Roll(random);
                Assert.That(e, Is.InRange(0, 2));
                table.TryGet(e, out BinLootTable.Entry entry);
                organic |= entry.organic;
                recyclable |= !entry.organic;
            }
            Assert.IsTrue(organic && recyclable);
        }

        [Test]
        public void ShippedLootTable_FitsTheNetworkEncoding()
        {
            BinLootTable shipped = Resources.Load<BinLootTable>(BinLootTable.ResourcePath);
            Assert.IsNotNull(shipped, "Run Upcycle City → Waste → Setup Bin Search to create Resources/BinLootTable.");
            Assert.LessOrEqual(shipped.Entries.Count, BinLootTable.MaxEntries);
            Assert.LessOrEqual(shipped.BinSlots, LootCodec.MaxSlots);
            Assert.LessOrEqual(shipped.BagSlots, LootCodec.MaxSlots);
        }
    }
}
