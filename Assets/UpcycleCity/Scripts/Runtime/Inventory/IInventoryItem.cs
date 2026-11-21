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

        /// <summary>A claw tool (litter picker): quick pickups on the move, trash held at its jaws.</summary>
        bool IsGrabTool { get; }

        // Local pose correction applied to the held visual once parented to the tool anchor/fist,
        // so a tool lines up with the grip no matter where its mesh pivot sits.
        Vector3 HeldLocalPosition { get; }
        Vector3 HeldLocalEuler { get; }
        float HeldUniformScale { get; }

        IItemUseBehavior UseBehavior { get; }
    }
}
