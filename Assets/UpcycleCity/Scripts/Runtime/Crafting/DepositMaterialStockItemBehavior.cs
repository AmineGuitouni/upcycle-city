using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Inventory;
using UpcycleCity.Player;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Pluggable <see cref="IItemUseBehavior"/> for shop-bought crafting consumables (such as Quincaillerie Catalyst)
    /// that credit directly into the shared team <see cref="TeamMaterialStock"/> instead of occupying the gear hotbar.
    /// </summary>
    [Serializable]
    public sealed class DepositMaterialStockItemBehavior : IItemUseBehavior, IMaterialStockCreditBehavior
    {
        [SerializeField] string materialId = "catalyst";
        [SerializeField, Min(0.05f)] float amount = 1.0f;
        [SerializeField] bool creditOnPurchase = true;

        public string MaterialId => materialId;
        public float Amount => amount;
        public bool CreditOnPurchase => creditOnPurchase;

        public DepositMaterialStockItemBehavior() { }

        public DepositMaterialStockItemBehavior(string targetMaterialId, float depositAmount, bool directOnPurchase = true)
        {
            materialId = targetMaterialId ?? string.Empty;
            amount = Mathf.Max(0.05f, depositAmount);
            creditOnPurchase = directOnPurchase;
        }

        public bool CanExecute(in ItemUseContext context) => !string.IsNullOrWhiteSpace(materialId) && amount > 0f;

        public bool Execute(in ItemUseContext context)
        {
            if (TeamMaterialStock.Instance == null || string.IsNullOrWhiteSpace(materialId))
                return false;

            if (GameAuthority.IsAuthoritative)
                TeamMaterialStock.Instance.TryAddMaterialAuthoritative(materialId, amount);

            string msg = $"+{amount:0.#} kg {context.Item?.DisplayName ?? materialId} → Team Stock";
            context.Interactor?.NotifyFeedback(msg, context.Item != null ? context.Item.AccentColor : Color.cyan);
            return true;
        }

        /// <summary>Authoritatively credits <paramref name="quantity"/> units of this item into <see cref="TeamMaterialStock"/>.</summary>
        public bool CreditAuthoritativeStock(int quantity, out string summary)
        {
            summary = string.Empty;
            if (quantity <= 0 || string.IsNullOrWhiteSpace(materialId))
                return false;

            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance();
            float total = MaterialStockLedger.RoundQuantity(amount * quantity);
            bool ok = stock.TryAddMaterialAuthoritative(materialId, total);
            if (!ok)
                return false;

            string label = materialId;
            MaterialUnit unit = MaterialUnit.Kilograms;
            if (stock.Database != null && stock.Database.TryGetMaterial(materialId, out MaterialDefinition mat))
            {
                label = mat.DisplayName;
                unit = mat.Unit;
            }
            summary = $"+{CraftingFormat.FormatQuantity(total, unit)} {label}";
            return true;
        }
    }
}
