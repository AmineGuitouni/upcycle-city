using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Designer tuning for a buyable workshop lot (the Upcycle Garage in the Factory Yard): the lot price and the
    /// price of every machine stand inside it. Read live by <see cref="WorkshopProperty"/> on the host, so Inspector
    /// edits apply in Play Mode. Prices are resolved host-side only; clients never send a price.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Workshop Config", fileName = "WorkshopConfig")]
    public sealed class WorkshopConfig : ScriptableObject
    {
        /// <summary>One buyable machine stand. Its position in <see cref="Machines"/> is its bit in the owned mask.</summary>
        [Serializable]
        public sealed class MachineOffer
        {
            [Tooltip("Stable slot ID (e.g. 'pulper'). Never reorder the list once shipped: the index is the replicated bit.")]
            public string slotId = string.Empty;
            [Tooltip("Machine sold on this stand (name and icon are read from it).")]
            public MachineDefinition machine;
            [Tooltip("Optional short name shown on the stand sign (falls back to the machine's display name).")]
            public string displayNameOverride = string.Empty;
            [Min(0)] public int price = 60;
            [Tooltip("IconLibrary key shown on the stand when the machine has no icon (e.g. 'stream_paper', 'ui_energy').")]
            public string iconKey = string.Empty;

            public string DisplayName =>
                !string.IsNullOrWhiteSpace(displayNameOverride)
                    ? displayNameOverride
                    : machine != null ? machine.DisplayName : slotId;
        }

        /// <summary>Hard cap: the owned mask is one 32-bit int.</summary>
        public const int MaxMachines = WorkshopPropertyState.MaxMachineSlots;

        [SerializeField] string propertyId = "upcycle_garage";
        [SerializeField] string displayName = "Upcycle Garage";
        [SerializeField, Min(0)] int propertyPrice = 120;
        [Tooltip("Max distance (m) between the buyer and the sign/stand for the host to accept a purchase.")]
        [SerializeField, Min(1f)] float purchaseRange = 6f;
        [SerializeField] List<MachineOffer> machines = new List<MachineOffer>();

        public string PropertyId => string.IsNullOrWhiteSpace(propertyId) ? "workshop" : propertyId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? "Workshop" : displayName;
        public int PropertyPrice => Mathf.Max(0, propertyPrice);
        public float PurchaseRange => Mathf.Max(1f, purchaseRange);
        public IReadOnlyList<MachineOffer> Machines => machines;
        public int MachineCount => Mathf.Min(machines.Count, MaxMachines);

        public MachineOffer GetMachine(int index) =>
            index >= 0 && index < MachineCount ? machines[index] : null;

        /// <summary>Index of the stand with <paramref name="slotId"/> (-1 when missing).</summary>
        public int IndexOfSlot(string slotId)
        {
            for (int i = 0; i < MachineCount; i++)
            {
                if (machines[i] != null && string.Equals(machines[i].slotId, slotId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        /// <summary>Builder/test helper: sets the lot identity and price.</summary>
        public void ConfigureProperty(string id, string name, int price, float range = 6f)
        {
            propertyId = id;
            displayName = name;
            propertyPrice = Mathf.Max(0, price);
            purchaseRange = Mathf.Max(1f, range);
        }

        /// <summary>Builder/test helper: adds or updates the stand with <paramref name="slotId"/>, returning its index.</summary>
        public int SetMachine(string slotId, MachineDefinition machine, int price, string shortName = null, string iconKey = null, bool overwritePrice = true)
        {
            int index = IndexOfSlot(slotId);
            if (index < 0)
            {
                machines.Add(new MachineOffer
                {
                    slotId = slotId,
                    machine = machine,
                    price = Mathf.Max(0, price),
                    displayNameOverride = shortName ?? string.Empty,
                    iconKey = iconKey ?? string.Empty,
                });
                return machines.Count - 1;
            }

            MachineOffer offer = machines[index];
            if (machine != null)
                offer.machine = machine;
            if (overwritePrice)
                offer.price = Mathf.Max(0, price);
            if (shortName != null && string.IsNullOrWhiteSpace(offer.displayNameOverride))
                offer.displayNameOverride = shortName;
            if (iconKey != null && string.IsNullOrWhiteSpace(offer.iconKey))
                offer.iconKey = iconKey;
            return index;
        }
    }
}
