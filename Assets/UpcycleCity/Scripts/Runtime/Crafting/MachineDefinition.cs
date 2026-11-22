using System;
using System.Text;
using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// ScriptableObject describing a manual or future automated workshop machine: its data-driven
    /// <see cref="MachineTypeDefinition"/>, level bounds, accepted card categories, hopper input filters,
    /// interaction range, upgrade costs, purchase price, grid footprint, and self-contained networked prefab.
    /// Forward-compatible with a purchasable workshop build/placement mode (grid snap, rotate, move, pick up).
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Machine Definition", fileName = "Machine_New")]
    public sealed class MachineDefinition : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] string machineId = "machine_id";
        [SerializeField] string displayName = "Manual Machine";
        [SerializeField, TextArea(2, 3)] string description = "";
        [Tooltip("Data-driven machine type asset matched against CraftingCard.RequiredMachineType.")]
        [SerializeField] MachineTypeDefinition machineType;
        [SerializeField] Color accentColor = new Color(0.18f, 0.56f, 0.54f);
        [SerializeField] Sprite icon;

        [Header("Capabilities & Levels")]
        [SerializeField, Min(0)] int defaultLevel;
        [SerializeField, Min(0)] int maxLevel = 1;
        [SerializeField] CardCategory[] acceptedCardCategories = { CardCategory.Refining };
        [SerializeField, Min(1.5f)] float interactRange = 6.5f;
        [SerializeField] MachineUpgradeCost[] upgradeCosts = Array.Empty<MachineUpgradeCost>();

        [Header("Hopper Intake Filtering")]
        [Tooltip("Waste streams accepted by this machine's physical hopper (e.g. Paper for Pulper, Plastic for Melter). Leave both lists empty if this machine has no trash hopper.")]
        [SerializeField] WasteStream[] acceptedHopperStreams = Array.Empty<WasteStream>();

        [Tooltip("Optional explicit materials accepted by this machine's physical hopper (takes effect alongside Accepted Hopper Streams, e.g. Old Tyre on Tyre Stripper or Wood Scrap on Workbench).")]
        [SerializeField] MaterialDefinition[] acceptedHopperMaterials = Array.Empty<MaterialDefinition>();

        [Header("Workshop Economy & Build-Mode Placement")]
        [Tooltip("Purchase price in shared team coins when buying this machine in the future workshop build mode.")]
        [SerializeField, Min(0)] int purchasePrice = 50;

        [Tooltip("Self-contained networked prefab (with NetworkObject + NetworkMachineIdentity) spawnable via GameAuthority.Current.SpawnNetworkedPrefab.")]
        [SerializeField] GameObject machinePrefab;

        [Tooltip("Placement footprint in metres (width X, depth Z) on the workshop build grid (snapped in 1.0 m grid cells, rotatable in 90° steps).")]
        [SerializeField] Vector2 placementFootprint = new Vector2(2f, 2f);

        public string MachineId => machineId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? machineId : displayName;
        public string Description => description;
        public MachineTypeDefinition MachineType => machineType;
        public Color AccentColor => accentColor;
        public Sprite Icon => icon;
        public int DefaultLevel => defaultLevel;
        public int MaxLevel => Mathf.Max(defaultLevel, maxLevel);
        public CardCategory[] AcceptedCardCategories => acceptedCardCategories ?? Array.Empty<CardCategory>();
        public float InteractRange => interactRange;
        public MachineUpgradeCost[] UpgradeCosts => upgradeCosts ?? Array.Empty<MachineUpgradeCost>();
        public WasteStream[] AcceptedHopperStreams => acceptedHopperStreams ?? Array.Empty<WasteStream>();
        public MaterialDefinition[] AcceptedHopperMaterials => acceptedHopperMaterials ?? Array.Empty<MaterialDefinition>();
        public int PurchasePrice => purchasePrice;
        public GameObject MachinePrefab => machinePrefab;
        public Vector2 PlacementFootprint => placementFootprint;

        /// <summary>Returns true if this machine definition accepts <paramref name="card"/>.</summary>
        public bool AcceptsCard(CraftingCard card)
        {
            if (card == null || machineType == null || card.RequiredMachineType == null)
                return false;
            if (!machineType.Matches(card.RequiredMachineType))
                return false;

            if (acceptedCardCategories == null || acceptedCardCategories.Length == 0)
                return true;

            for (int i = 0; i < acceptedCardCategories.Length; i++)
            {
                if (acceptedCardCategories[i] == card.Category)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if this machine's physical hopper accepts a trash item whose resolved material is
        /// <paramref name="resolvedMaterial"/> and waste stream is <paramref name="itemStream"/>.
        /// </summary>
        public bool AcceptsHopperDeposit(MaterialDefinition resolvedMaterial, WasteStream itemStream)
        {
            bool hasMatFilter = acceptedHopperMaterials != null && acceptedHopperMaterials.Length > 0;
            bool hasStreamFilter = acceptedHopperStreams != null && acceptedHopperStreams.Length > 0;
            if (!hasMatFilter && !hasStreamFilter)
                return false;

            if (hasMatFilter && resolvedMaterial != null)
            {
                for (int i = 0; i < acceptedHopperMaterials.Length; i++)
                {
                    MaterialDefinition allowed = acceptedHopperMaterials[i];
                    if (allowed != null && (ReferenceEquals(allowed, resolvedMaterial)
                        || string.Equals(allowed.MaterialId, resolvedMaterial.MaterialId, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }

            if (hasStreamFilter)
            {
                for (int i = 0; i < acceptedHopperStreams.Length; i++)
                {
                    if (acceptedHopperStreams[i] == itemStream)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Builds a human-readable list of inputs accepted by this machine's hopper (for rejection feedback).</summary>
        public string DescribeAcceptedHopperInputs()
        {
            var sb = new StringBuilder(64);
            if (acceptedHopperStreams != null)
            {
                for (int i = 0; i < acceptedHopperStreams.Length; i++)
                {
                    if (sb.Length > 0)
                        sb.Append(", ");
                    sb.Append(WasteStreamPalette.LabelFor(acceptedHopperStreams[i]));
                }
            }

            if (acceptedHopperMaterials != null)
            {
                for (int i = 0; i < acceptedHopperMaterials.Length; i++)
                {
                    MaterialDefinition mat = acceptedHopperMaterials[i];
                    if (mat == null)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(", ");
                    sb.Append(mat.DisplayName);
                }
            }

            return sb.Length > 0 ? sb.ToString() : "no physical trash";
        }

        /// <summary>Finds the configured <see cref="MachineUpgradeCost"/> to reach <paramref name="nextLevel"/>.</summary>
        public bool TryGetUpgradeCost(int nextLevel, out MachineUpgradeCost cost)
        {
            if (upgradeCosts != null)
            {
                for (int i = 0; i < upgradeCosts.Length; i++)
                {
                    if (upgradeCosts[i].targetLevel == nextLevel)
                    {
                        cost = upgradeCosts[i];
                        return true;
                    }
                }
            }

            cost = default;
            return false;
        }

        /// <summary>Programmatic configuration helper for editor builders and unit tests.</summary>
        public void Configure(
            string id,
            string label,
            string desc,
            MachineTypeDefinition typeDef,
            Color color,
            int initialLevel,
            int maxLvl,
            CardCategory[] acceptedCategories,
            MachineUpgradeCost[] upgrades,
            GameObject prefab = null,
            float range = 6.5f,
            Vector2? footprint = null,
            int price = 50,
            WasteStream[] hopperStreams = null,
            MaterialDefinition[] hopperMaterials = null)
        {
            machineId = id ?? string.Empty;
            displayName = label ?? string.Empty;
            description = desc ?? string.Empty;
            machineType = typeDef;
            accentColor = color;
            defaultLevel = Mathf.Max(0, initialLevel);
            maxLevel = Mathf.Max(defaultLevel, maxLvl);
            acceptedCardCategories = acceptedCategories ?? Array.Empty<CardCategory>();
            upgradeCosts = upgrades ?? Array.Empty<MachineUpgradeCost>();
            if (prefab != null)
                machinePrefab = prefab;
            interactRange = Mathf.Max(1.5f, range);
            if (footprint.HasValue)
                placementFootprint = footprint.Value;
            purchasePrice = Mathf.Max(0, price);
            acceptedHopperStreams = hopperStreams ?? Array.Empty<WasteStream>();
            acceptedHopperMaterials = hopperMaterials ?? Array.Empty<MaterialDefinition>();
        }
    }
}
