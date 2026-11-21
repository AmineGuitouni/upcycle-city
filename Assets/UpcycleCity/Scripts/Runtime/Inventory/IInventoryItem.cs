using UnityEngine;

namespace UpcycleCity.Inventory
{
    /// <summary>
    /// Polymorphic contract for any item stored in <see cref="PlayerInventory"/>
    /// (tools, consumables, placeables, upgrade parts). Trash items intentionally do NOT implement this.
    /// </summary>
    public interface IInventoryItem
    {
        string ItemId { get; }
        string DisplayName { get; }
        string Description { get; }
        ItemCategory Category { get; }
        Color AccentColor { get; }
        int MaxStack { get; }
        bool ConsumeOnUse { get; }
        float UseCooldown { get; }

        // Passive equipped stat modifiers
        float GrabReachBonus { get; }
        float ThrowForceBonus { get; }
        float MoveSpeedMultiplier { get; }
        bool GrantsSmogImmunity { get; }

        GameObject HeldVisualPrefab { get; }
        IItemUseBehavior UseBehavior { get; }
    }
}
