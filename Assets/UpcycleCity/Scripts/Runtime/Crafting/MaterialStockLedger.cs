using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Pure C# shared team stock ledger storing material quantities (kg or units) keyed by
    /// <see cref="MaterialDefinition.MaterialId"/> plus team Energy (NRG).
    /// Separate from <see cref="UpcycleCity.Inventory.PlayerInventory"/> so trash/materials never enter the player's gear hotbar.
    /// Unit-testable without Unity scenes.
    /// </summary>
    public sealed class MaterialStockLedger
    {
        public const float QuantityEpsilon = 0.0005f;

        readonly Dictionary<string, float> quantities = new Dictionary<string, float>(32, StringComparer.Ordinal);
        int energy;
        int maxEnergy;
        int revision;

        /// <summary>Raised whenever any material quantity or team energy changes.</summary>
        public event Action Changed;

        public int Energy => energy;
        public int MaxEnergy => maxEnergy;
        public int Revision => revision;

        public MaterialStockLedger(int initialEnergy = 0, int maxEnergyCapacity = 9999)
        {
            maxEnergy = Mathf.Max(1, maxEnergyCapacity);
            energy = Mathf.Clamp(initialEnergy, 0, maxEnergy);
        }

        /// <summary>Returns the stored quantity for <paramref name="materialId"/> (0 if absent).</summary>
        public float GetAmount(string materialId)
        {
            if (string.IsNullOrWhiteSpace(materialId))
                return 0f;
            return quantities.TryGetValue(materialId, out float value) ? value : 0f;
        }

        /// <summary>Returns the stored quantity for <paramref name="material"/> (0 if null or absent).</summary>
        public float GetAmount(MaterialDefinition material) =>
            material != null ? GetAmount(material.MaterialId) : 0f;

        /// <summary>Returns true if at least <paramref name="requiredAmount"/> is stored (with float epsilon tolerance).</summary>
        public bool HasAtLeast(string materialId, float requiredAmount)
        {
            if (requiredAmount <= 0f)
                return true;
            return GetAmount(materialId) + QuantityEpsilon >= requiredAmount;
        }

        /// <summary>Returns true if team energy is at least <paramref name="requiredEnergy"/>.</summary>
        public bool HasEnergy(int requiredEnergy) => requiredEnergy <= 0 || energy >= requiredEnergy;

        /// <summary>Credits <paramref name="delta"/> of <paramref name="materialId"/> and returns the new total.</summary>
        public float Add(string materialId, float delta)
        {
            if (string.IsNullOrWhiteSpace(materialId) || delta <= 0f)
                return GetAmount(materialId);

            float next = RoundQuantity(GetAmount(materialId) + delta);
            quantities[materialId] = next;
            NotifyChanged();
            return next;
        }

        /// <summary>Sets the exact stored quantity for <paramref name="materialId"/> (used for sync/tests).</summary>
        public void SetAmount(string materialId, float amount, bool notify = true)
        {
            if (string.IsNullOrWhiteSpace(materialId))
                return;

            float clamped = RoundQuantity(Mathf.Max(0f, amount));
            float current = GetAmount(materialId);
            if (Mathf.Abs(current - clamped) <= 0.0001f)
                return;

            if (clamped <= 0f)
                quantities.Remove(materialId);
            else
                quantities[materialId] = clamped;

            if (notify)
                NotifyChanged();
        }

        /// <summary>
        /// Attempts to deduct <paramref name="amount"/> of <paramref name="materialId"/>.
        /// Returns false without modifying stock if insufficient.
        /// </summary>
        public bool TryConsume(string materialId, float amount)
        {
            if (amount <= 0f)
                return true;
            if (!HasAtLeast(materialId, amount))
                return false;

            float next = RoundQuantity(Mathf.Max(0f, GetAmount(materialId) - amount));
            if (next <= 0f)
                quantities.Remove(materialId);
            else
                quantities[materialId] = next;

            NotifyChanged();
            return true;
        }

        /// <summary>
        /// Atomically deducts every material in <paramref name="inputs"/> and <paramref name="energyCost"/>.
        /// Returns false and modifies NOTHING if any single input or energy is short.
        /// </summary>
        public bool TryConsumeAll(IReadOnlyList<MaterialAmount> inputs, int energyCost)
        {
            if (!HasEnergy(energyCost))
                return false;

            // Aggregate duplicate material entries so two entries referencing the same material ID
            // cannot double-spend the same stock.
            Dictionary<string, float> totals = null;
            if (inputs != null && inputs.Count > 0)
            {
                totals = new Dictionary<string, float>(inputs.Count, StringComparer.Ordinal);
                for (int i = 0; i < inputs.Count; i++)
                {
                    MaterialAmount entry = inputs[i];
                    if (!entry.IsValid)
                        continue;
                    string id = entry.material.MaterialId;
                    totals.TryGetValue(id, out float existing);
                    totals[id] = existing + entry.amount;
                }

                foreach (KeyValuePair<string, float> kv in totals)
                {
                    if (!HasAtLeast(kv.Key, kv.Value))
                        return false;
                }
            }

            bool mutated = false;
            if (energyCost > 0)
            {
                energy = Mathf.Max(0, energy - energyCost);
                mutated = true;
            }

            if (totals != null)
            {
                foreach (KeyValuePair<string, float> kv in totals)
                {
                    float next = RoundQuantity(Mathf.Max(0f, GetAmount(kv.Key) - kv.Value));
                    if (next <= 0f)
                        quantities.Remove(kv.Key);
                    else
                        quantities[kv.Key] = next;
                    mutated = true;
                }
            }

            if (mutated)
                NotifyChanged();

            return true;
        }

        /// <summary>Credits all valid entries in <paramref name="outputs"/> in one revision bump.</summary>
        public void AddAll(IReadOnlyList<MaterialAmount> outputs)
        {
            if (outputs == null || outputs.Count == 0)
                return;

            bool mutated = false;
            for (int i = 0; i < outputs.Count; i++)
            {
                MaterialAmount entry = outputs[i];
                if (!entry.IsValid)
                    continue;
                string id = entry.material.MaterialId;
                quantities[id] = RoundQuantity(GetAmount(id) + entry.amount);
                mutated = true;
            }

            if (mutated)
                NotifyChanged();
        }

        /// <summary>Adds <paramref name="delta"/> team energy (clamped to <see cref="MaxEnergy"/>).</summary>
        public int AddEnergy(int delta)
        {
            if (delta <= 0)
                return energy;

            int next = Mathf.Clamp(energy + delta, 0, maxEnergy);
            if (next != energy)
            {
                energy = next;
                NotifyChanged();
            }
            return energy;
        }

        /// <summary>Sets team energy and optional max capacity directly (used for sync/tests).</summary>
        public void SetEnergy(int value, int? newMaxEnergy = null, bool notify = true)
        {
            bool changed = false;
            if (newMaxEnergy.HasValue)
            {
                int clampedMax = Mathf.Max(1, newMaxEnergy.Value);
                if (clampedMax != maxEnergy)
                {
                    maxEnergy = clampedMax;
                    changed = true;
                }
            }

            int clamped = Mathf.Clamp(value, 0, maxEnergy);
            if (clamped != energy)
            {
                energy = clamped;
                changed = true;
            }

            if (changed && notify)
                NotifyChanged();
        }

        /// <summary>Clears all stored materials and resets team energy to zero.</summary>
        public void Clear()
        {
            if (quantities.Count == 0 && energy == 0)
                return;
            quantities.Clear();
            energy = 0;
            NotifyChanged();
        }

        /// <summary>Copies all non-zero material entries into <paramref name="target"/> without allocating.</summary>
        public void CopyNonZeroEntries(List<KeyValuePair<string, float>> target)
        {
            target.Clear();
            foreach (KeyValuePair<string, float> kv in quantities)
            {
                if (kv.Value > 0f)
                    target.Add(kv);
            }
        }

        /// <summary>Notifies listeners after a batched silent update (e.g. client sync apply).</summary>
        public void RaiseChanged() => NotifyChanged();

        void NotifyChanged()
        {
            revision++;
            Changed?.Invoke();
        }

        public static float RoundQuantity(float value) => Mathf.Round(value * 1000f) / 1000f;
    }
}
