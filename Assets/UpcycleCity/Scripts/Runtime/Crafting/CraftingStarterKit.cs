using System;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Configurable starter-kit grant (materials + team NRG + optional starting coins) applied by
    /// workshop scenes or debug buttons so testers and designers can exercise every card immediately.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Starter Kit", fileName = "CraftingStarterKit")]
    public sealed class CraftingStarterKit : ScriptableObject
    {
        [SerializeField, Min(0)] int energyAmount = 200;
        [SerializeField, Min(0)] int bonusCoins = 60;
        [SerializeField] MaterialAmount[] materials = Array.Empty<MaterialAmount>();

        public int EnergyAmount => energyAmount;
        public int BonusCoins => bonusCoins;
        public MaterialAmount[] Materials => materials ?? Array.Empty<MaterialAmount>();

        /// <summary>Applies this starter kit's materials and energy to a <see cref="MaterialStockLedger"/>.</summary>
        public void ApplyTo(MaterialStockLedger ledger)
        {
            if (ledger == null)
                return;

            if (energyAmount > 0)
                ledger.AddEnergy(energyAmount);

            if (materials == null)
                return;

            for (int i = 0; i < materials.Length; i++)
            {
                MaterialAmount entry = materials[i];
                if (entry.IsValid)
                    ledger.Add(entry.material.MaterialId, entry.amount);
            }
        }

        /// <summary>Programmatic configuration helper for editor builders and unit tests.</summary>
        public void Configure(int energy, int coins, params MaterialAmount[] entries)
        {
            energyAmount = Mathf.Max(0, energy);
            bonusCoins = Mathf.Max(0, coins);
            materials = entries ?? Array.Empty<MaterialAmount>();
        }
    }
}
