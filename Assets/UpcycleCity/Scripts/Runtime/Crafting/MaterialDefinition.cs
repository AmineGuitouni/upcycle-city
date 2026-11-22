using System;
using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// ScriptableObject defining a recyclable, consumable, refined, or built material stored in the
    /// shared team <see cref="TeamMaterialStock"/> (never in the player's gear hotbar).
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Material Definition", fileName = "Mat_New")]
    public sealed class MaterialDefinition : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] string materialId = "material_id";
        [SerializeField] string displayName = "Material";
        [SerializeField, TextArea(2, 3)] string description = "";
        [SerializeField] MaterialCategory category = MaterialCategory.Trash;
        [SerializeField] MaterialUnit unit = MaterialUnit.Kilograms;
        [SerializeField] Color accentColor = new Color(0.18f, 0.56f, 0.54f);
        [SerializeField] Sprite icon;

        [Header("Physical Trash Source Mapping (Optional)")]
        [Tooltip("If true, physical TrashItems matching this archetype ID or waste stream automatically credit this material when deposited into compatible hoppers or sorting crates.")]
        [SerializeField] bool acceptsTrashDeposits;

        [Tooltip("Fallback WasteStream mapped to this material (ignored if an explicit Source Trash Archetype ID matches another material first).")]
        [SerializeField] WasteStream sourceWasteStream = WasteStream.Plastic;

        [Tooltip("Explicit WasteCatalog archetype IDs mapped directly to this material (e.g. 'old_tyre', 'wooden_pallet', 'broken_cafe_chair'). Never matched against display text.")]
        [SerializeField] string[] sourceTrashArchetypeIds = Array.Empty<string>();

        [Tooltip("Amount credited to Material Stock per deposited physical TrashItem.")]
        [SerializeField, Min(0.05f)] float depositYieldPerTrashItem = 1.5f;

        public string MaterialId => materialId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? materialId : displayName;
        public string Description => description;
        public MaterialCategory Category => category;
        public MaterialUnit Unit => unit;
        public Color AccentColor => accentColor;
        public Sprite Icon => icon;
        public bool AcceptsTrashDeposits => acceptsTrashDeposits;
        public WasteStream SourceWasteStream => sourceWasteStream;
        public string[] SourceTrashArchetypeIds => sourceTrashArchetypeIds ?? Array.Empty<string>();
        public float DepositYieldPerTrashItem => depositYieldPerTrashItem;

        /// <summary>Formats <paramref name="amount"/> in this material's display unit (e.g. <c>4.0 kg</c> or <c>1 unit</c>).</summary>
        public string FormatAmount(float amount) => CraftingFormat.FormatQuantity(amount, unit);

        /// <summary>Programmatic configuration helper for editor builders and unit tests.</summary>
        public void Configure(
            string id,
            string label,
            string desc,
            MaterialCategory matCategory,
            MaterialUnit matUnit,
            Color color,
            bool acceptTrash = false,
            WasteStream wasteStream = WasteStream.Plastic,
            float yieldPerItem = 1.5f,
            string[] archetypeIds = null,
            Sprite matIcon = null)
        {
            materialId = id ?? string.Empty;
            displayName = label ?? string.Empty;
            description = desc ?? string.Empty;
            category = matCategory;
            unit = matUnit;
            accentColor = color;
            acceptsTrashDeposits = acceptTrash;
            sourceWasteStream = wasteStream;
            depositYieldPerTrashItem = Mathf.Max(0.05f, yieldPerItem);
            sourceTrashArchetypeIds = archetypeIds ?? Array.Empty<string>();
            if (matIcon != null)
                icon = matIcon;
        }
    }
}
