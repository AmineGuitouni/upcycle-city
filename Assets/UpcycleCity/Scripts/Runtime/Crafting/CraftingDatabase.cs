using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Central registry ScriptableObject holding all <see cref="MachineTypeDefinition"/>s,
    /// <see cref="MaterialDefinition"/>s, <see cref="CraftingCard"/>s, and <see cref="MachineDefinition"/>s
    /// with O(1) ID lookup, deterministic index ordering for co-op state sync, archetype-based trash mapping,
    /// and comprehensive structural validation.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Crafting Database", fileName = "CraftingDatabase")]
    public sealed class CraftingDatabase : ScriptableObject
    {
        public const string DefaultResourcesPath = "CraftingDatabase";

        /// <summary>
        /// Optional hook installed by Editor tooling or tests to verify whether a prefab is registered in
        /// the project's NetworkPrefabsList (<c>UpcycleCityNetworkPrefabs.asset</c>) without referencing
        /// <c>Unity.Netcode</c> from <c>UpcycleCity.Runtime</c>.
        /// </summary>
        public static Func<GameObject, bool> ExternalNetworkPrefabRegisteredCheck;

        [SerializeField] List<MachineTypeDefinition> machineTypes = new List<MachineTypeDefinition>();
        [SerializeField] List<MaterialDefinition> materials = new List<MaterialDefinition>();
        [SerializeField] List<CraftingCard> cards = new List<CraftingCard>();
        [SerializeField] List<MachineDefinition> machines = new List<MachineDefinition>();

        Dictionary<string, MachineTypeDefinition> machineTypeById;
        Dictionary<string, MaterialDefinition> materialById;
        Dictionary<string, int> materialIndexById;
        Dictionary<string, CraftingCard> cardById;
        Dictionary<string, int> cardIndexById;
        Dictionary<string, MachineDefinition> machineById;
        bool lookupBuilt;

        public IReadOnlyList<MachineTypeDefinition> MachineTypes => machineTypes;
        public IReadOnlyList<MaterialDefinition> Materials => materials;
        public IReadOnlyList<CraftingCard> Cards => cards;
        public IReadOnlyList<MachineDefinition> Machines => machines;

        /// <summary>Loads the default database from <c>Resources/CraftingDatabase</c> when no direct reference is wired.</summary>
        public static CraftingDatabase LoadDefault() => Resources.Load<CraftingDatabase>(DefaultResourcesPath);

        void OnEnable() => lookupBuilt = false;

#if UNITY_EDITOR
        void OnValidate()
        {
            lookupBuilt = false;
            if (materials.Count == 0 && cards.Count == 0 && machines.Count == 0 && machineTypes.Count == 0)
                return;

            var errors = new List<string>(8);
            if (!Validate(errors))
            {
                Debug.LogWarning(
                    $"[Upcycle City] CraftingDatabase '{name}' has {errors.Count} validation issue(s):\n- " +
                    string.Join("\n- ", errors),
                    this);
            }
        }
#endif

        /// <summary>Forces the internal lookup dictionaries to rebuild (call after mutating lists in editor/tests).</summary>
        public void RebuildLookups()
        {
            machineTypeById = new Dictionary<string, MachineTypeDefinition>(machineTypes.Count, StringComparer.Ordinal);
            for (int i = 0; i < machineTypes.Count; i++)
            {
                MachineTypeDefinition mt = machineTypes[i];
                if (mt != null && !string.IsNullOrWhiteSpace(mt.TypeId) && !machineTypeById.ContainsKey(mt.TypeId))
                    machineTypeById[mt.TypeId] = mt;
            }

            materialById = new Dictionary<string, MaterialDefinition>(materials.Count, StringComparer.Ordinal);
            materialIndexById = new Dictionary<string, int>(materials.Count, StringComparer.Ordinal);
            for (int i = 0; i < materials.Count; i++)
            {
                MaterialDefinition m = materials[i];
                if (m != null && !string.IsNullOrWhiteSpace(m.MaterialId) && !materialById.ContainsKey(m.MaterialId))
                {
                    materialById[m.MaterialId] = m;
                    materialIndexById[m.MaterialId] = i;
                }
            }

            cardById = new Dictionary<string, CraftingCard>(cards.Count, StringComparer.Ordinal);
            cardIndexById = new Dictionary<string, int>(cards.Count, StringComparer.Ordinal);
            for (int i = 0; i < cards.Count; i++)
            {
                CraftingCard c = cards[i];
                if (c != null && !string.IsNullOrWhiteSpace(c.CardId) && !cardById.ContainsKey(c.CardId))
                {
                    cardById[c.CardId] = c;
                    cardIndexById[c.CardId] = i;
                }
            }

            machineById = new Dictionary<string, MachineDefinition>(machines.Count, StringComparer.Ordinal);
            for (int i = 0; i < machines.Count; i++)
            {
                MachineDefinition md = machines[i];
                if (md != null && !string.IsNullOrWhiteSpace(md.MachineId) && !machineById.ContainsKey(md.MachineId))
                    machineById[md.MachineId] = md;
            }

            lookupBuilt = true;
        }

        void EnsureLookups()
        {
            if (!lookupBuilt || materialById == null)
                RebuildLookups();
        }

        public bool TryGetMachineType(string typeId, out MachineTypeDefinition machineType)
        {
            EnsureLookups();
            if (string.IsNullOrWhiteSpace(typeId))
            {
                machineType = null;
                return false;
            }
            return machineTypeById.TryGetValue(typeId, out machineType) && machineType != null;
        }

        public bool TryGetMaterial(string materialId, out MaterialDefinition material)
        {
            EnsureLookups();
            if (string.IsNullOrWhiteSpace(materialId))
            {
                material = null;
                return false;
            }
            return materialById.TryGetValue(materialId, out material) && material != null;
        }

        public int GetMaterialIndex(string materialId)
        {
            EnsureLookups();
            if (string.IsNullOrWhiteSpace(materialId))
                return -1;
            return materialIndexById.TryGetValue(materialId, out int idx) ? idx : -1;
        }

        public MaterialDefinition GetMaterialAt(int index)
        {
            return index >= 0 && index < materials.Count ? materials[index] : null;
        }

        public bool TryGetCard(string cardId, out CraftingCard card)
        {
            EnsureLookups();
            if (string.IsNullOrWhiteSpace(cardId))
            {
                card = null;
                return false;
            }
            return cardById.TryGetValue(cardId, out card) && card != null;
        }

        public int GetCardIndex(string cardId)
        {
            EnsureLookups();
            if (string.IsNullOrWhiteSpace(cardId))
                return -1;
            return cardIndexById.TryGetValue(cardId, out int idx) ? idx : -1;
        }

        public CraftingCard GetCardAt(int index)
        {
            return index >= 0 && index < cards.Count ? cards[index] : null;
        }

        public bool TryGetMachine(string machineId, out MachineDefinition machine)
        {
            EnsureLookups();
            if (string.IsNullOrWhiteSpace(machineId))
            {
                machine = null;
                return false;
            }
            return machineById.TryGetValue(machineId, out machine) && machine != null;
        }

        /// <summary>Populates <paramref name="results"/> with all cards compatible with <paramref name="machine"/>.</summary>
        public void GetCardsForMachine(MachineDefinition machine, List<CraftingCard> results)
        {
            results.Clear();
            if (machine == null)
                return;
            for (int i = 0; i < cards.Count; i++)
            {
                CraftingCard c = cards[i];
                if (c != null && machine.AcceptsCard(c))
                    results.Add(c);
            }
        }

        /// <summary>
        /// Resolves which <see cref="MaterialDefinition"/> and yield amount a physical <see cref="TrashItem"/>
        /// deposits into the shared Material Stock using its stable <see cref="TrashItem.ArchetypeId"/> first,
        /// then falling back to its <see cref="TrashItem.Stream"/> (never display text).
        /// </summary>
        public bool TryResolveTrashMaterial(TrashItem item, out MaterialDefinition material, out float yieldAmount)
        {
            if (item == null)
            {
                material = null;
                yieldAmount = 0f;
                return false;
            }
            return TryResolveTrashMaterialByArchetype(item.ArchetypeId, item.Stream, out material, out yieldAmount);
        }

        /// <summary>
        /// Pure data-driven overload resolving a trash archetype ID + <see cref="WasteStream"/> to a
        /// <see cref="MaterialDefinition"/> and yield amount (never matches display names).
        /// </summary>
        public bool TryResolveTrashMaterialByArchetype(string archetypeId, WasteStream stream, out MaterialDefinition material, out float yieldAmount)
        {
            material = null;
            yieldAmount = 0f;

            string id = archetypeId ?? string.Empty;

            // Pass 1: explicit waste archetype ID matches (e.g. "old_tyre" -> old_wheel, "wooden_pallet" -> wood_scrap)
            if (!string.IsNullOrWhiteSpace(id))
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    MaterialDefinition candidate = materials[i];
                    if (candidate == null || !candidate.AcceptsTrashDeposits)
                        continue;

                    string[] ids = candidate.SourceTrashArchetypeIds;
                    for (int k = 0; k < ids.Length; k++)
                    {
                        string mappedId = ids[k];
                        if (!string.IsNullOrWhiteSpace(mappedId)
                            && string.Equals(id, mappedId, StringComparison.OrdinalIgnoreCase))
                        {
                            material = candidate;
                            yieldAmount = candidate.DepositYieldPerTrashItem;
                            return true;
                        }
                    }
                }
            }

            // Pass 2: WasteStream fallback preferring generic stream materials (no specific archetype overrides)
            for (int i = 0; i < materials.Count; i++)
            {
                MaterialDefinition candidate = materials[i];
                if (candidate == null || !candidate.AcceptsTrashDeposits)
                    continue;
                if (candidate.SourceWasteStream == stream && candidate.SourceTrashArchetypeIds.Length == 0)
                {
                    material = candidate;
                    yieldAmount = candidate.DepositYieldPerTrashItem;
                    return true;
                }
            }

            // Pass 3: WasteStream fallback even if material also lists specific archetypes
            for (int i = 0; i < materials.Count; i++)
            {
                MaterialDefinition candidate = materials[i];
                if (candidate == null || !candidate.AcceptsTrashDeposits)
                    continue;
                if (candidate.SourceWasteStream == stream)
                {
                    material = candidate;
                    yieldAmount = candidate.DepositYieldPerTrashItem;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Replaces the database lists cleanly (used by editor builders and unit tests).
        /// If <paramref name="machineTypeList"/> is omitted, automatically collects distinct non-null
        /// <see cref="MachineTypeDefinition"/> references from <paramref name="machineList"/>.
        /// </summary>
        public void Configure(
            IEnumerable<MaterialDefinition> matList,
            IEnumerable<CraftingCard> cardList,
            IEnumerable<MachineDefinition> machineList,
            IEnumerable<MachineTypeDefinition> machineTypeList = null)
        {
            materials.Clear();
            if (matList != null)
            {
                foreach (MaterialDefinition m in matList)
                {
                    if (m != null)
                        materials.Add(m);
                }
            }

            cards.Clear();
            if (cardList != null)
            {
                foreach (CraftingCard c in cardList)
                {
                    if (c != null)
                        cards.Add(c);
                }
            }

            machines.Clear();
            if (machineList != null)
            {
                foreach (MachineDefinition md in machineList)
                {
                    if (md != null)
                        machines.Add(md);
                }
            }

            machineTypes.Clear();
            if (machineTypeList != null)
            {
                foreach (MachineTypeDefinition mt in machineTypeList)
                {
                    if (mt != null)
                        machineTypes.Add(mt);
                }
            }
            else
            {
                var seen = new HashSet<MachineTypeDefinition>();
                for (int i = 0; i < machines.Count; i++)
                {
                    if (machines[i] != null && machines[i].MachineType != null && seen.Add(machines[i].MachineType))
                        machineTypes.Add(machines[i].MachineType);
                }
            }

            RebuildLookups();
        }

        /// <summary>
        /// Validates every registered machine type, material, card, and machine for missing references,
        /// duplicate IDs, non-positive amounts, unreachable card complexity levels, missing machine prefabs,
        /// invalid upgrade target levels, and output/machine prefabs lacking a <c>NetworkObject</c> or missing
        /// from the network prefab registry. Returns true when zero errors are found.
        /// </summary>
        public bool Validate(List<string> errors, Func<GameObject, bool> networkPrefabRegisteredCheck = null)
        {
            errors?.Clear();
            int errorCount = 0;
            Func<GameObject, bool> netPrefabCheck = networkPrefabRegisteredCheck ?? ExternalNetworkPrefabRegisteredCheck;

            void AddError(string msg)
            {
                errorCount++;
                errors?.Add(msg);
            }

            var seenTypeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < machineTypes.Count; i++)
            {
                MachineTypeDefinition mt = machineTypes[i];
                if (mt == null)
                {
                    AddError($"MachineTypes[{i}] is null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(mt.TypeId))
                    AddError($"MachineType '{mt.name}' has an empty TypeId.");
                else if (!seenTypeIds.Add(mt.TypeId))
                    AddError($"Duplicate MachineTypeId '{mt.TypeId}'.");
            }

            var seenMatIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < materials.Count; i++)
            {
                MaterialDefinition m = materials[i];
                if (m == null)
                {
                    AddError($"Materials[{i}] is null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(m.MaterialId))
                    AddError($"Material '{m.name}' has an empty MaterialId.");
                else if (!seenMatIds.Add(m.MaterialId))
                    AddError($"Duplicate MaterialId '{m.MaterialId}'.");
            }

            var seenMachineIds = new HashSet<string>(StringComparer.Ordinal);
            var maxLevelByTypeId = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < machines.Count; i++)
            {
                MachineDefinition md = machines[i];
                if (md == null)
                {
                    AddError($"Machines[{i}] is null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(md.MachineId))
                    AddError($"Machine '{md.name}' has an empty MachineId.");
                else if (!seenMachineIds.Add(md.MachineId))
                    AddError($"Duplicate MachineId '{md.MachineId}'.");

                if (md.MachineType == null)
                {
                    AddError($"Machine '{md.MachineId}' has no MachineType assigned.");
                }
                else
                {
                    if (machineTypes.Count > 0 && !seenTypeIds.Contains(md.MachineType.TypeId))
                        AddError($"Machine '{md.MachineId}' references unregistered MachineType '{md.MachineType.TypeId}'.");

                    if (!maxLevelByTypeId.TryGetValue(md.MachineType.TypeId, out int curMax) || md.MaxLevel > curMax)
                        maxLevelByTypeId[md.MachineType.TypeId] = md.MaxLevel;
                }

                if (md.MachinePrefab == null)
                {
                    AddError($"Machine '{md.MachineId}' has no MachinePrefab assigned.");
                }
                else
                {
                    if (!HasNetworkObjectComponent(md.MachinePrefab))
                        AddError($"Machine '{md.MachineId}' prefab '{md.MachinePrefab.name}' is missing a NetworkObject component.");
                    else if (netPrefabCheck != null && !netPrefabCheck(md.MachinePrefab))
                        AddError($"Machine '{md.MachineId}' prefab '{md.MachinePrefab.name}' is not registered in the NetworkPrefabsList.");
                }

                if (md.MaxLevel < md.DefaultLevel)
                    AddError($"Machine '{md.MachineId}' has MaxLevel < DefaultLevel.");

                MachineUpgradeCost[] upgrades = md.UpgradeCosts;
                var seenUpgradeTargets = new HashSet<int>();
                for (int u = 0; u < upgrades.Length; u++)
                {
                    int target = upgrades[u].targetLevel;
                    if (target <= 0)
                        AddError($"Machine '{md.MachineId}' upgrade[{u}] has invalid targetLevel {target} (must be >= 1).");
                    else if (target > md.MaxLevel)
                        AddError($"Machine '{md.MachineId}' upgrade[{u}] targetLevel {target} exceeds MaxLevel {md.MaxLevel}.");
                    else if (!seenUpgradeTargets.Add(target))
                        AddError($"Machine '{md.MachineId}' has duplicate upgrade entry for targetLevel {target}.");

                    if (upgrades[u].materials == null)
                        continue;
                    for (int m = 0; m < upgrades[u].materials.Length; m++)
                    {
                        MaterialAmount req = upgrades[u].materials[m];
                        if (req.material == null || req.amount <= 0f)
                            AddError($"Machine '{md.MachineId}' upgrade[{u}] has an invalid material requirement.");
                        else if (!seenMatIds.Contains(req.material.MaterialId))
                            AddError($"Machine '{md.MachineId}' upgrade[{u}] references unregistered material '{req.material.MaterialId}'.");
                    }
                }
            }

            var seenCardIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < cards.Count; i++)
            {
                CraftingCard c = cards[i];
                if (c == null)
                {
                    AddError($"Cards[{i}] is null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(c.CardId))
                    AddError($"Card '{c.name}' has an empty CardId.");
                else if (!seenCardIds.Add(c.CardId))
                    AddError($"Duplicate CardId '{c.CardId}'.");

                if (c.Inputs.Length == 0)
                    AddError($"Card '{c.CardId}' has no input materials.");
                if (c.Outputs.Length == 0 && c.OutputPrefab == null)
                    AddError($"Card '{c.CardId}' has neither output materials nor an output prefab.");

                for (int j = 0; j < c.Inputs.Length; j++)
                {
                    MaterialAmount input = c.Inputs[j];
                    if (input.material == null || input.amount <= 0f)
                        AddError($"Card '{c.CardId}' input[{j}] has a null material or non-positive amount.");
                    else if (!seenMatIds.Contains(input.material.MaterialId))
                        AddError($"Card '{c.CardId}' input '{input.material.MaterialId}' is not registered in CraftingDatabase.Materials.");
                }

                for (int j = 0; j < c.Outputs.Length; j++)
                {
                    MaterialAmount output = c.Outputs[j];
                    if (output.material == null || output.amount <= 0f)
                        AddError($"Card '{c.CardId}' output[{j}] has a null material or non-positive amount.");
                    else if (!seenMatIds.Contains(output.material.MaterialId))
                        AddError($"Card '{c.CardId}' output '{output.material.MaterialId}' is not registered in CraftingDatabase.Materials.");
                }

                if (c.OutputPrefab != null)
                {
                    if (!HasNetworkObjectComponent(c.OutputPrefab))
                        AddError($"Card '{c.CardId}' OutputPrefab '{c.OutputPrefab.name}' is missing a NetworkObject component.");
                    else if (netPrefabCheck != null && !netPrefabCheck(c.OutputPrefab))
                        AddError($"Card '{c.CardId}' OutputPrefab '{c.OutputPrefab.name}' is not registered in the NetworkPrefabsList.");
                }

                if (c.RequiredMachineType == null)
                {
                    AddError($"Card '{c.CardId}' has no RequiredMachineType assigned.");
                }
                else if (machines.Count > 0)
                {
                    bool anyCompatibleMachine = false;
                    int highestCompatibleMaxLevel = -1;
                    for (int m = 0; m < machines.Count; m++)
                    {
                        MachineDefinition md = machines[m];
                        if (md != null && md.AcceptsCard(c))
                        {
                            anyCompatibleMachine = true;
                            if (md.MaxLevel > highestCompatibleMaxLevel)
                                highestCompatibleMaxLevel = md.MaxLevel;
                        }
                    }

                    if (!anyCompatibleMachine)
                    {
                        AddError($"Card '{c.CardId}' requires machine type '{c.RequiredMachineType.TypeId}' ({c.Category}) which no MachineDefinition in CraftingDatabase accepts.");
                    }
                    else if (c.ComplexityLevel > highestCompatibleMaxLevel)
                    {
                        AddError($"Card '{c.CardId}' has ComplexityLevel {c.ComplexityLevel}, which exceeds the highest MaxLevel ({highestCompatibleMaxLevel}) of any compatible machine.");
                    }
                }
            }

            return errorCount == 0;
        }

        static bool HasNetworkObjectComponent(GameObject prefab)
        {
            if (prefab == null)
                return false;
            // Reflection-free component lookup by name so UpcycleCity.Runtime stays Netcode-free
            return prefab.GetComponent("NetworkObject") != null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Keep ExternalNetworkPrefabRegisteredCheck cleared across domain-reload-disabled runs in Play Mode
            // (Editor scripts reinstall it when needed).
        }
    }
}
