using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Crafting;

namespace UpcycleCity.Tests.EditMode
{
    /// <summary>
    /// Workshop purchase: the replicated state codec (<see cref="WorkshopPropertyState"/>) and the shipped
    /// <see cref="WorkshopConfig"/> (Upcycle Garage price + six machine stands).
    /// </summary>
    public sealed class WorkshopEditModeTests
    {
        [Test]
        public void StateCodec_RoundTripsOwnedFlagAndMachineMask()
        {
            var cases = new[]
            {
                new WorkshopPropertyState(false, 0),
                new WorkshopPropertyState(true, 0),
                new WorkshopPropertyState(true, 0b101),
                new WorkshopPropertyState(true, WorkshopPropertyState.MaskFor(6)),
                new WorkshopPropertyState(false, 1 << 30),
            };
            foreach (WorkshopPropertyState original in cases)
            {
                SyncedStateRecord record = original.Encode();
                WorkshopPropertyState decoded = WorkshopPropertyState.Decode(in record);
                Assert.AreEqual(original, decoded, $"Round trip failed for {original}");
            }
        }

        [Test]
        public void StateCodec_UsesOnlyFlagsAndI0_SoDeltasStayTiny()
        {
            SyncedStateRecord record = new WorkshopPropertyState(true, 0b11).Encode();
            Assert.AreEqual(1, record.flags);
            Assert.AreEqual(0b11, record.i0);
            Assert.AreEqual(0, record.i1);
            Assert.AreEqual(0f, record.v0);
            Assert.AreEqual(0f, record.v1);

            // Unchanged state compares equal, so the replicator sends nothing
            Assert.IsTrue(record.ApproximatelyEquals(new WorkshopPropertyState(true, 0b11).Encode()));
            Assert.IsFalse(record.ApproximatelyEquals(new WorkshopPropertyState(true, 0b111).Encode()));
        }

        [Test]
        public void State_WithMachine_SetsOnlyThatBit_AndIgnoresOutOfRange()
        {
            WorkshopPropertyState s = new WorkshopPropertyState(true, 0).WithMachine(2);
            Assert.IsTrue(s.HasMachine(2));
            Assert.IsFalse(s.HasMachine(0));
            Assert.IsFalse(s.HasMachine(1));
            Assert.AreEqual(s, s.WithMachine(-1));
            Assert.AreEqual(s, s.WithMachine(WorkshopPropertyState.MaxMachineSlots));
            Assert.IsFalse(s.HasMachine(-1));
            Assert.IsFalse(s.WithOwned(false).Owned);
            Assert.IsTrue(s.WithOwned(false).HasMachine(2), "Owned flag and mask are independent.");
            Assert.AreEqual(0b111111, WorkshopPropertyState.MaskFor(6));
            Assert.AreEqual(0, WorkshopPropertyState.MaskFor(0));
        }

        [Test]
        public void ShippedWorkshopConfig_SellsTheGarageAndSixMachines()
        {
            var config = AssetDatabase.LoadAssetAtPath<WorkshopConfig>("Assets/UpcycleCity/Settings/Crafting/WorkshopConfig.asset");
            Assert.IsNotNull(config, "Run Upcycle City → Crafting → Setup Workshop Purchase In Open Scene (creates WorkshopConfig.asset).");
            Assert.AreEqual("Upcycle Garage", config.DisplayName);
            Assert.Greater(config.PropertyPrice, 0);
            Assert.AreEqual(6, config.MachineCount);
            for (int i = 0; i < config.MachineCount; i++)
            {
                WorkshopConfig.MachineOffer offer = config.GetMachine(i);
                Assert.IsNotNull(offer.machine, $"Stand '{offer.slotId}' has no MachineDefinition.");
                Assert.Greater(offer.price, 0, $"Stand '{offer.slotId}' is free.");
                Assert.AreEqual(i, config.IndexOfSlot(offer.slotId), $"Duplicate slot id '{offer.slotId}'.");
            }
            Assert.IsNull(config.GetMachine(-1));
            Assert.IsNull(config.GetMachine(config.MachineCount));
        }
    }
}
