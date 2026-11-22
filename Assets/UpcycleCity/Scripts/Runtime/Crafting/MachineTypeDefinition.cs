using System;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Data-driven machine archetype asset (e.g. Manual Pulper, Pedal Plastic Melter, Workbench).
    /// Adding a new machine type requires only creating a new <see cref="MachineTypeDefinition"/> asset
    /// and registering it in <see cref="CraftingDatabase"/> — zero code changes needed.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Machine Type", fileName = "Type_New")]
    public sealed class MachineTypeDefinition : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] string typeId = "machine_type";
        [SerializeField] string displayName = "Workshop Machine";
        [SerializeField, TextArea(2, 3)] string description = "";
        [SerializeField] Color accentColor = new Color(0.18f, 0.56f, 0.54f);
        [SerializeField] Sprite icon;

        public string TypeId => typeId ?? string.Empty;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? TypeId : displayName;
        public string Description => description;
        public Color AccentColor => accentColor;
        public Sprite Icon => icon;

        /// <summary>Returns true if <paramref name="other"/> represents the same machine type.</summary>
        public bool Matches(MachineTypeDefinition other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other == null)
                return false;
            return !string.IsNullOrWhiteSpace(TypeId)
                && string.Equals(TypeId, other.TypeId, StringComparison.Ordinal);
        }

        /// <summary>Programmatic configuration helper for editor builders and unit tests.</summary>
        public void Configure(
            string id,
            string label,
            string desc = "",
            Color? color = null,
            Sprite typeIcon = null)
        {
            typeId = id ?? string.Empty;
            displayName = label ?? string.Empty;
            description = desc ?? string.Empty;
            if (color.HasValue)
                accentColor = color.Value;
            if (typeIcon != null)
                icon = typeIcon;
        }
    }
}
