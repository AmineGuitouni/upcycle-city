using UnityEngine;

namespace UpcycleCity.Inventory
{
    /// <summary>
    /// ScriptableObject data asset implementing <see cref="IInventoryItem"/> for a player tool,
    /// usable item, placeable, or upgrade part. Supports pluggable <see cref="IItemUseBehavior"/> strategies.
    /// Trash items do NOT implement IInventoryItem and can never enter PlayerInventory.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Inventory/Item Definition", fileName = "Item_New")]
    public class ItemDefinition : ScriptableObject, IInventoryItem
    {
        [Header("Identity")]
        [SerializeField] string itemId = "tool_item";
        [SerializeField] string displayName = "Tool";
        [SerializeField, TextArea(2, 3)] string description = "Player equipment.";
        [SerializeField] ItemCategory category = ItemCategory.Tool;
        [SerializeField] Color accentColor = new Color(0.18f, 0.55f, 0.34f);

        [Header("Stacking & Use")]
        [SerializeField, Min(1)] int maxStack = 1;
        [SerializeField] bool consumeOnUse;
        [SerializeField, Min(0f)] float useCooldown = 0.35f;

        [Header("Passive Stat Modifiers (When Equipped)")]
        [Tooltip("Extra raycast grab reach in metres when this tool is selected (e.g. Litter Picker).")]
        [SerializeField, Min(0f)] float grabReachBonus;

        [Tooltip("Extra throw speed bonus when tossing physics trash while this tool is selected.")]
        [SerializeField, Min(0f)] float throwForceBonus;

        [Tooltip("Optional movement speed multiplier while this item is equipped (1 = neutral).")]
        [SerializeField, Range(0.5f, 1.5f)] float moveSpeedMultiplier = 1f;

        [Tooltip("If true, grants immunity to smog slowdowns while equipped (e.g. Respirator Mask).")]
        [SerializeField] bool grantsSmogImmunity;

        [Header("Pluggable Use Behavior")]
        [SerializeReference] IItemUseBehavior useBehavior;

        [Header("Visuals")]
        [Tooltip("Optional first-person prefab shown in hand when selected.")]
        [SerializeField] GameObject heldVisualPrefab;

        public string ItemId => itemId;
        public string DisplayName => displayName;
        public string Description => description;
        public ItemCategory Category => category;
        public Color AccentColor => accentColor;
        public int MaxStack => maxStack;
        public bool ConsumeOnUse => consumeOnUse;
        public float UseCooldown => useCooldown;
        public float GrabReachBonus => grabReachBonus;
        public float ThrowForceBonus => throwForceBonus;
        public float MoveSpeedMultiplier => moveSpeedMultiplier > 0.01f ? moveSpeedMultiplier : 1f;
        public bool GrantsSmogImmunity => grantsSmogImmunity;
        public GameObject HeldVisualPrefab => heldVisualPrefab;
        public IItemUseBehavior UseBehavior => useBehavior;

        public void Configure(
            string id,
            string label,
            string desc,
            ItemCategory itemCategory,
            Color color,
            int stackLimit = 1,
            bool consume = false,
            float reachBonus = 0f,
            float throwBonus = 0f,
            float speedMultiplier = 1f,
            bool smogImmune = false,
            IItemUseBehavior behavior = null)
        {
            itemId = id;
            displayName = label;
            description = desc;
            category = itemCategory;
            accentColor = color;
            maxStack = Mathf.Max(1, stackLimit);
            consumeOnUse = consume;
            grabReachBonus = Mathf.Max(0f, reachBonus);
            throwForceBonus = Mathf.Max(0f, throwBonus);
            moveSpeedMultiplier = Mathf.Clamp(speedMultiplier, 0.5f, 1.5f);
            grantsSmogImmunity = smogImmune;
            if (behavior != null)
                useBehavior = behavior;
        }

        public void SetUseBehavior(IItemUseBehavior behavior) => useBehavior = behavior;
    }
}
