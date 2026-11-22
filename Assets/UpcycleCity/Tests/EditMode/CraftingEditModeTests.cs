using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Crafting;
using UpcycleCity.Waste;

namespace UpcycleCity.Tests.EditMode
{
    /// <summary>
    /// Fast EditMode unit tests covering pure C# <see cref="CraftingRules"/>, <see cref="MaterialStockLedger"/>,
    /// <see cref="MachineTypeDefinition"/>, <see cref="CraftingDatabase"/> validation (all error classes + shipped
    /// asset &amp; network prefab hash checks), archetype-based trash mapping, per-machine hopper filtering,
    /// <see cref="MachineUpgradeHelper"/> cost formatting, and <see cref="CraftingStarterKit"/> seeding.
    /// </summary>
    public sealed class CraftingEditModeTests
    {
        readonly List<UnityEngine.Object> createdAssets = new List<UnityEngine.Object>();
        readonly List<GameObject> createdObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = createdObjects.Count - 1; i >= 0; i--)
            {
                if (createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(createdObjects[i]);
            }
            createdObjects.Clear();

            for (int i = createdAssets.Count - 1; i >= 0; i--)
            {
                if (createdAssets[i] != null)
                    UnityEngine.Object.DestroyImmediate(createdAssets[i]);
            }
            createdAssets.Clear();
        }

        T Track<T>(T obj) where T : UnityEngine.Object
        {
            createdAssets.Add(obj);
            return obj;
        }

        GameObject TrackGo(string name, bool withNetworkObject = true)
        {
            var go = new GameObject(name);
            if (withNetworkObject)
                go.AddComponent<NetworkObject>();
            createdObjects.Add(go);
            return go;
        }

        static long ReadGlobalObjectIdHash(NetworkObject netObj)
        {
            using (var so = new SerializedObject(netObj))
            {
                SerializedProperty prop = so.FindProperty("GlobalObjectIdHash");
                return prop != null ? prop.longValue : 0L;
            }
        }

        MachineTypeDefinition MakeType(string id, string label)
        {
            var t = Track(ScriptableObject.CreateInstance<MachineTypeDefinition>());
            t.Configure(id, label, "", Color.white);
            return t;
        }

        MaterialDefinition MakeMat(string id, string name, MaterialCategory cat, MaterialUnit unit = MaterialUnit.Kilograms)
        {
            var m = Track(ScriptableObject.CreateInstance<MaterialDefinition>());
            m.Configure(id, name, "", cat, unit, Color.white);
            return m;
        }

        MachineDefinition MakeMachine(
            string id,
            string name,
            MachineTypeDefinition type,
            CardCategory accepted = CardCategory.Refining,
            int maxLevel = 1,
            GameObject prefab = null,
            MachineUpgradeCost[] upgrades = null,
            WasteStream[] hopperStreams = null,
            MaterialDefinition[] hopperMaterials = null)
        {
            var m = Track(ScriptableObject.CreateInstance<MachineDefinition>());
            m.Configure(
                id, name, "", type, Color.white, 0, maxLevel,
                new[] { accepted },
                upgrades,
                prefab: prefab ?? TrackGo("MockPrefab_" + id),
                hopperStreams: hopperStreams,
                hopperMaterials: hopperMaterials);
            return m;
        }

        CraftingCard MakeCard(
            string id,
            string name,
            CardCategory cat,
            MachineTypeDefinition machType,
            int level,
            int energy,
            MaterialAmount[] inputs,
            MaterialAmount[] outputs,
            GameObject outputPrefab = null)
        {
            var c = Track(ScriptableObject.CreateInstance<CraftingCard>());
            c.Configure(id, name, "Test description.", cat, machType, level, energy, 180, 2f, inputs, outputs, null, outputPrefab);
            return c;
        }

        [Test]
        public void CraftingRules_LevelTooLow_ReturnsExactMessageAndConsumesNothing()
        {
            MachineTypeDefinition furnaceType = MakeType("crucible_furnace", "Crucible Furnace");
            MaterialDefinition cans = MakeMat("aluminum_cans", "Aluminum Cans", MaterialCategory.Trash);
            MaterialDefinition catalyst = MakeMat("catalyst", "Catalyst", MaterialCategory.Consumable);
            MaterialDefinition refinedAl = MakeMat("refined_aluminum", "Refined Aluminum", MaterialCategory.Refined);
            MachineDefinition furnace = MakeMachine("machine_furnace", "Bellows Crucible Furnace", furnaceType);

            CraftingCard card = MakeCard("al_reclaim", "Aluminum Can Reclaim", CardCategory.Refining, furnaceType,
                level: 1, energy: 25,
                inputs: new[] { new MaterialAmount(cans, 4.0f), new MaterialAmount(catalyst, 0.3f) },
                outputs: new[] { new MaterialAmount(refinedAl, 3.4f) });

            var stock = new MaterialStockLedger(initialEnergy: 100);
            stock.Add("aluminum_cans", 10f);
            stock.Add("catalyst", 2f);

            CraftingExecutionOutcome outcome = CraftingRules.TryExecute(card, furnace, machineLevel: 0, machineBusy: false, stock);

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(CraftingFailureKind.LevelTooLow, outcome.Validation.FailureKind);
            Assert.AreEqual("Requires machine level 1 (this machine is level 0)", outcome.Validation.Message);
            Assert.AreEqual(10f, stock.GetAmount("aluminum_cans"), 0.001f);
            Assert.AreEqual(2f, stock.GetAmount("catalyst"), 0.001f);
            Assert.AreEqual(100, stock.Energy);
            Assert.AreEqual(0f, stock.GetAmount("refined_aluminum"), 0.001f);
        }

        [Test]
        public void CraftingRules_OutOfRange_ReturnsExactMessageAndConsumesNothing()
        {
            MachineTypeDefinition melterType = MakeType("plastic_melter", "Plastic Melter");
            MaterialDefinition plastic = MakeMat("plastic_scrap", "Plastic Scrap", MaterialCategory.Trash);
            MaterialDefinition polymer = MakeMat("recycled_polymer", "Recycled Polymer", MaterialCategory.Refined);
            MachineDefinition melter = MakeMachine("machine_melter", "Pedal Plastic Melter", melterType);

            CraftingCard card = MakeCard("plastic_reclaim", "Plastic Thermal Reclaim", CardCategory.Refining, melterType,
                level: 0, energy: 10,
                inputs: new[] { new MaterialAmount(plastic, 4.0f) },
                outputs: new[] { new MaterialAmount(polymer, 3.2f) });

            var stock = new MaterialStockLedger(initialEnergy: 50);
            stock.Add("plastic_scrap", 5f);

            CraftingExecutionOutcome outcome = CraftingRules.TryExecute(card, melter, machineLevel: 0, machineBusy: false, stock, isPlayerInRange: false);

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(CraftingFailureKind.OutOfRange, outcome.Validation.FailureKind);
            Assert.AreEqual("Move closer to the Pedal Plastic Melter", outcome.Validation.Message);
            Assert.AreEqual(5f, stock.GetAmount("plastic_scrap"), 0.001f);
            Assert.AreEqual(50, stock.Energy);
        }

        [Test]
        public void CraftingRules_MissingInputsAndEnergy_ListsEveryShortfallAndConsumesNothing()
        {
            MachineTypeDefinition melterType = MakeType("plastic_melter", "Plastic Melter");
            MaterialDefinition plastic = MakeMat("plastic_scrap", "Plastic Scrap", MaterialCategory.Trash);
            MaterialDefinition catalyst = MakeMat("catalyst", "Catalyst", MaterialCategory.Consumable);
            MaterialDefinition polymer = MakeMat("recycled_polymer", "Recycled Polymer", MaterialCategory.Refined);
            MachineDefinition melter = MakeMachine("machine_melter", "Pedal Plastic Melter", melterType);

            CraftingCard card = MakeCard("plastic_reclaim", "Plastic Thermal Reclaim", CardCategory.Refining, melterType,
                level: 0, energy: 10,
                inputs: new[] { new MaterialAmount(plastic, 4.0f), new MaterialAmount(catalyst, 0.2f) },
                outputs: new[] { new MaterialAmount(polymer, 3.2f) });

            var stock = new MaterialStockLedger(initialEnergy: 4);
            stock.Add("plastic_scrap", 1.5f);
            stock.Add("catalyst", 0.1f);

            CraftingExecutionOutcome outcome = CraftingRules.TryExecute(card, melter, machineLevel: 0, machineBusy: false, stock);

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(CraftingFailureKind.InsufficientResources, outcome.Validation.FailureKind);
            Assert.AreEqual(3, outcome.Validation.Missing.Count, "Every missing material AND energy must be reported.");
            StringAssert.Contains("Plastic Scrap", outcome.Validation.Message);
            StringAssert.Contains("Catalyst", outcome.Validation.Message);
            StringAssert.Contains("Energy", outcome.Validation.Message);

            Assert.AreEqual(1.5f, stock.GetAmount("plastic_scrap"), 0.001f);
            Assert.AreEqual(0.1f, stock.GetAmount("catalyst"), 0.001f);
            Assert.AreEqual(4, stock.Energy);
            Assert.AreEqual(0f, stock.GetAmount("recycled_polymer"), 0.001f);
        }

        [Test]
        public void CraftingRules_ExactAmounts_SucceedsAndLeavesZeroRemainder()
        {
            MachineTypeDefinition pulperType = MakeType("pulper", "Manual Pulper");
            MaterialDefinition paper = MakeMat("paper_carton", "Paper & Carton", MaterialCategory.Trash);
            MaterialDefinition catalyst = MakeMat("catalyst", "Catalyst", MaterialCategory.Consumable);
            MaterialDefinition pulp = MakeMat("cleaned_pulp", "Cleaned Pulp", MaterialCategory.Refined);
            MachineDefinition pulper = MakeMachine("machine_pulper", "Manual Pulper", pulperType);

            CraftingCard card = MakeCard("paper_repulp", "Paper & Carton Repulp", CardCategory.Refining, pulperType,
                level: 0, energy: 10,
                inputs: new[] { new MaterialAmount(paper, 5.0f), new MaterialAmount(catalyst, 0.2f) },
                outputs: new[] { new MaterialAmount(pulp, 4.7f) });

            var stock = new MaterialStockLedger(initialEnergy: 10);
            stock.Add("paper_carton", 5.0f);
            stock.Add("catalyst", 0.2f);

            CraftingExecutionOutcome outcome = CraftingRules.TryExecute(card, pulper, machineLevel: 0, machineBusy: false, stock);

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(0f, stock.GetAmount("paper_carton"), 0.001f);
            Assert.AreEqual(0f, stock.GetAmount("catalyst"), 0.001f);
            Assert.AreEqual(0, stock.Energy);
            Assert.AreEqual(4.7f, stock.GetAmount("cleaned_pulp"), 0.001f);
        }

        [Test]
        public void CraftingRules_MultiOutputCard_CreditsAllOutputsAtomically()
        {
            MachineTypeDefinition stripperType = MakeType("tyre_stripper", "Tyre Stripper");
            MaterialDefinition wheel = MakeMat("old_wheel", "Old Wheel", MaterialCategory.Trash, MaterialUnit.Units);
            MaterialDefinition aluminum = MakeMat("refined_aluminum", "Refined Aluminum", MaterialCategory.Refined);
            MaterialDefinition polymer = MakeMat("recycled_polymer", "Recycled Polymer", MaterialCategory.Refined);
            MachineDefinition stripper = MakeMachine("machine_stripper", "Manual Tyre Stripper", stripperType);

            CraftingCard card = MakeCard("wheel_breakdown", "Old Wheel Breakdown", CardCategory.Refining, stripperType,
                level: 1, energy: 40,
                inputs: new[] { new MaterialAmount(wheel, 1.0f) },
                outputs: new[] { new MaterialAmount(aluminum, 1.8f), new MaterialAmount(polymer, 1.2f) });

            var stock = new MaterialStockLedger(initialEnergy: 60);
            stock.Add("old_wheel", 2.0f);

            CraftingExecutionOutcome outcome = CraftingRules.TryExecute(card, stripper, machineLevel: 1, machineBusy: false, stock);

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(1.0f, stock.GetAmount("old_wheel"), 0.001f);
            Assert.AreEqual(20, stock.Energy);
            Assert.AreEqual(1.8f, stock.GetAmount("refined_aluminum"), 0.001f);
            Assert.AreEqual(1.2f, stock.GetAmount("recycled_polymer"), 0.001f);
        }

        [Test]
        public void CraftingRules_BuildingCard_SucceedsAndProducesBuiltMaterial()
        {
            MachineTypeDefinition workbenchType = MakeType("workbench", "Upcycle Workbench");
            MaterialDefinition polymer = MakeMat("recycled_polymer", "Recycled Polymer", MaterialCategory.Refined);
            MaterialDefinition woodPulp = MakeMat("wood_pulp", "Wood Pulp", MaterialCategory.Refined);
            MaterialDefinition bench = MakeMat("built_park_bench", "Upcycled Park Bench", MaterialCategory.Built, MaterialUnit.Units);
            MachineDefinition workbench = MakeMachine("machine_workbench", "Upcycle Workbench", workbenchType, CardCategory.Building);

            CraftingCard card = MakeCard("build_bench", "Upcycled Park Bench", CardCategory.Building, workbenchType,
                level: 0, energy: 15,
                inputs: new[] { new MaterialAmount(polymer, 2.0f), new MaterialAmount(woodPulp, 3.0f) },
                outputs: new[] { new MaterialAmount(bench, 1.0f) });

            var stock = new MaterialStockLedger(initialEnergy: 20);
            stock.Add("recycled_polymer", 2.0f);
            stock.Add("wood_pulp", 3.0f);

            CraftingExecutionOutcome outcome = CraftingRules.TryExecute(card, workbench, machineLevel: 0, machineBusy: false, stock);

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(5, stock.Energy);
            Assert.AreEqual(1.0f, stock.GetAmount("built_park_bench"), 0.001f);
        }

        [Test]
        public void CraftingRules_BusyAndIncompatibleMachine_RefuseWithoutConsuming()
        {
            MachineTypeDefinition melterType = MakeType("plastic_melter", "Plastic Melter");
            MachineTypeDefinition pulperType = MakeType("pulper", "Manual Pulper");
            MaterialDefinition plastic = MakeMat("plastic_scrap", "Plastic Scrap", MaterialCategory.Trash);
            MaterialDefinition polymer = MakeMat("recycled_polymer", "Recycled Polymer", MaterialCategory.Refined);
            MachineDefinition melter = MakeMachine("machine_melter", "Pedal Plastic Melter", melterType);
            MachineDefinition pulper = MakeMachine("machine_pulper", "Manual Pulper", pulperType);

            CraftingCard card = MakeCard("plastic_reclaim", "Plastic Thermal Reclaim", CardCategory.Refining, melterType,
                level: 0, energy: 10,
                inputs: new[] { new MaterialAmount(plastic, 4.0f) },
                outputs: new[] { new MaterialAmount(polymer, 3.2f) });

            var stock = new MaterialStockLedger(initialEnergy: 50);
            stock.Add("plastic_scrap", 8f);

            CraftingExecutionOutcome busyRes = CraftingRules.TryExecute(card, melter, machineLevel: 0, machineBusy: true, stock);
            Assert.IsFalse(busyRes.Succeeded);
            Assert.AreEqual(CraftingFailureKind.MachineBusy, busyRes.Validation.FailureKind);

            CraftingExecutionOutcome wrongMachRes = CraftingRules.TryExecute(card, pulper, machineLevel: 0, machineBusy: false, stock);
            Assert.IsFalse(wrongMachRes.Succeeded);
            Assert.AreEqual(CraftingFailureKind.IncompatibleMachine, wrongMachRes.Validation.FailureKind);

            Assert.AreEqual(8f, stock.GetAmount("plastic_scrap"), 0.001f);
            Assert.AreEqual(50, stock.Energy);
        }

        [Test]
        public void CraftingRules_CheckOrder_LevelCheckedBeforeRangeBusyAndResources()
        {
            MachineTypeDefinition furnaceType = MakeType("crucible_furnace", "Crucible Furnace");
            MaterialDefinition cans = MakeMat("aluminum_cans", "Aluminum Cans", MaterialCategory.Trash);
            MaterialDefinition al = MakeMat("refined_aluminum", "Refined Aluminum", MaterialCategory.Refined);
            MachineDefinition furnace = MakeMachine("machine_furnace", "Bellows Crucible Furnace", furnaceType);

            CraftingCard lv1Card = MakeCard("al_reclaim", "Aluminum Can Reclaim", CardCategory.Refining, furnaceType,
                level: 1, energy: 25,
                inputs: new[] { new MaterialAmount(cans, 4.0f) },
                outputs: new[] { new MaterialAmount(al, 3.4f) });

            // Empty stock + out of range + BUSY + level 0 -> MUST report LevelTooLow first!
            var emptyStock = new MaterialStockLedger(initialEnergy: 0);
            CraftingValidationResult res = CraftingRules.Validate(lv1Card, furnace, machineLevel: 0, machineBusy: true, emptyStock, isPlayerInRange: false);
            Assert.AreEqual(CraftingFailureKind.LevelTooLow, res.FailureKind);
            Assert.AreEqual(CraftingFailureKind.LevelTooLow,
                CraftingRules.ValidateQuick(lv1Card, furnace, machineLevel: 0, machineBusy: true, emptyStock, isPlayerInRange: false));

            // Level 1 + out of range + BUSY -> MUST report OutOfRange before MachineBusy
            CraftingValidationResult res2 = CraftingRules.Validate(lv1Card, furnace, machineLevel: 1, machineBusy: true, emptyStock, isPlayerInRange: false);
            Assert.AreEqual(CraftingFailureKind.OutOfRange, res2.FailureKind);
            Assert.AreEqual(CraftingFailureKind.OutOfRange,
                CraftingRules.ValidateQuick(lv1Card, furnace, machineLevel: 1, machineBusy: true, emptyStock, isPlayerInRange: false));

            // Level 1 + in range + BUSY + empty stock -> MUST report MachineBusy before InsufficientResources
            CraftingValidationResult res3 = CraftingRules.Validate(lv1Card, furnace, machineLevel: 1, machineBusy: true, emptyStock, isPlayerInRange: true);
            Assert.AreEqual(CraftingFailureKind.MachineBusy, res3.FailureKind);
            Assert.AreEqual(CraftingFailureKind.MachineBusy,
                CraftingRules.ValidateQuick(lv1Card, furnace, machineLevel: 1, machineBusy: true, emptyStock, isPlayerInRange: true));
        }

        [Test]
        public void CraftingDatabase_ProductionAsset_ValidatesCleanlyAndAllPrefabsHaveUniqueNonZeroHashes()
        {
            CraftingDatabase db = AssetDatabase.LoadAssetAtPath<CraftingDatabase>("Assets/UpcycleCity/Resources/CraftingDatabase.asset");
            Assert.IsNotNull(db, "CraftingDatabase.asset must exist in Resources/.");

            var errors = new List<string>();
            bool ok = db.Validate(errors);
            Assert.IsTrue(ok, "CraftingDatabase.Validate failed: " + string.Join("; ", errors));

            Assert.GreaterOrEqual(db.MachineTypes.Count, 6);
            Assert.GreaterOrEqual(db.Materials.Count, 12);
            Assert.GreaterOrEqual(db.Cards.Count, 7);
            Assert.GreaterOrEqual(db.Machines.Count, 6);

            // Verify every MachinePrefab and OutputPrefab has a unique, non-zero GlobalObjectIdHash
            var seenHashes = new HashSet<long>();
            int checkedPrefabs = 0;
            for (int i = 0; i < db.Machines.Count; i++)
            {
                GameObject prefab = db.Machines[i].MachinePrefab;
                Assert.IsNotNull(prefab, $"Machine '{db.Machines[i].MachineId}' missing MachinePrefab.");
                NetworkObject netObj = prefab.GetComponent<NetworkObject>();
                Assert.IsNotNull(netObj, $"Machine prefab '{prefab.name}' missing NetworkObject.");
                long hash = ReadGlobalObjectIdHash(netObj);
                Assert.AreNotEqual(0L, hash, $"Machine prefab '{prefab.name}' has GlobalObjectIdHash == 0.");
                Assert.IsTrue(seenHashes.Add(hash), $"Duplicate GlobalObjectIdHash on '{prefab.name}'.");
                checkedPrefabs++;
            }

            for (int i = 0; i < db.Cards.Count; i++)
            {
                GameObject outPrefab = db.Cards[i].OutputPrefab;
                if (outPrefab == null)
                    continue;
                NetworkObject netObj = outPrefab.GetComponent<NetworkObject>();
                Assert.IsNotNull(netObj, $"OutputPrefab '{outPrefab.name}' missing NetworkObject.");
                long hash = ReadGlobalObjectIdHash(netObj);
                Assert.AreNotEqual(0L, hash, $"OutputPrefab '{outPrefab.name}' has GlobalObjectIdHash == 0.");
                Assert.IsTrue(seenHashes.Add(hash), $"Duplicate GlobalObjectIdHash on '{outPrefab.name}'.");
                checkedPrefabs++;
            }

            Assert.GreaterOrEqual(checkedPrefabs, 8, "Expected at least 6 machine prefabs + 2 built prefabs.");

            // Verify Paper & Carton Repulp uses data-packet value of 5.0 kg
            Assert.IsTrue(db.TryGetCard("refining_paper_carton_repulp", out CraftingCard paperCard));
            Assert.AreEqual(0, paperCard.ComplexityLevel);
            Assert.AreEqual(10, paperCard.EnergyCost);
            Assert.AreEqual(5.0f, paperCard.Inputs[0].amount, 0.001f);
            Assert.AreEqual(0.2f, paperCard.Inputs[1].amount, 0.001f);
            Assert.AreEqual(4.7f, paperCard.Outputs[0].amount, 0.001f);
            Assert.IsNotNull(paperCard.CardArtwork, "Refining card must have its Sprite artwork imported.");

            // Verify Old Wheel Breakdown (Lv1, 40 NRG, multi-output)
            Assert.IsTrue(db.TryGetCard("refining_old_wheel_breakdown", out CraftingCard wheelCard));
            Assert.AreEqual(1, wheelCard.ComplexityLevel);
            Assert.AreEqual(40, wheelCard.EnergyCost);
            Assert.AreEqual(2, wheelCard.Outputs.Length);
            Assert.IsNotNull(wheelCard.CardArtwork);

            // Verify procedural Building cards have no PNG artwork and valid networked OutputPrefab
            Assert.IsTrue(db.TryGetCard("building_upcycled_park_bench", out CraftingCard benchCard));
            Assert.IsNull(benchCard.CardArtwork, "Building cards should be data-rendered without PNG artwork.");
            Assert.IsNotNull(benchCard.OutputPrefab, "Building card must reference a spawnable prefab.");
        }

        [Test]
        public void CraftingDatabase_Validate_CatchesAllErrorClasses()
        {
            MachineTypeDefinition pulperType = MakeType("pulper", "Manual Pulper");
            MaterialDefinition m1 = MakeMat("dup_mat", "First", MaterialCategory.Trash);
            MaterialDefinition m2 = MakeMat("dup_mat", "Second", MaterialCategory.Trash);
            MaterialDefinition unregistered = MakeMat("ghost_mat", "Ghost", MaterialCategory.Refined);

            GameObject noNetObjPrefab = TrackGo("NoNetObjPrefab", withNetworkObject: false);
            GameObject badOutPrefab = TrackGo("BadOutPrefab", withNetworkObject: false);

            // Machine with invalid/duplicate upgrade target levels AND missing NetworkObject on prefab
            MachineDefinition badMach = MakeMachine(
                "m1", "Pulper", pulperType,
                maxLevel: 1,
                prefab: noNetObjPrefab,
                upgrades: new[]
                {
                    new MachineUpgradeCost { targetLevel = 1, coins = 10 },
                    new MachineUpgradeCost { targetLevel = 1, coins = 15 }, // duplicate targetLevel
                    new MachineUpgradeCost { targetLevel = 3, coins = 50 }, // > maxLevel
                });

            // Card with complexity 2 > machine maxLevel 1, unregistered input, and OutputPrefab missing NetworkObject
            CraftingCard badCard = MakeCard(
                "c1", "Bad Card", CardCategory.Refining, pulperType,
                level: 2, energy: 5,
                inputs: new[] { new MaterialAmount(unregistered, 2f) },
                outputs: new[] { new MaterialAmount(m1, 1f) },
                outputPrefab: badOutPrefab);

            var db = Track(ScriptableObject.CreateInstance<CraftingDatabase>());
            db.Configure(new[] { m1, m2 }, new[] { badCard }, new[] { badMach }, new[] { pulperType });

            var prevHook = CraftingDatabase.ExternalNetworkPrefabRegisteredCheck;
            try
            {
                CraftingDatabase.ExternalNetworkPrefabRegisteredCheck = _ => false;
                var errors = new List<string>();
                Assert.IsFalse(db.Validate(errors));

                string joined = string.Join("\n", errors);
                StringAssert.Contains("Duplicate MaterialId", joined);
                StringAssert.Contains("not registered in CraftingDatabase.Materials", joined);
                StringAssert.Contains("exceeds the highest MaxLevel", joined);
                StringAssert.Contains("missing a NetworkObject", joined);
                StringAssert.Contains("duplicate upgrade entry", joined);
                StringAssert.Contains("exceeds MaxLevel", joined);
            }
            finally
            {
                CraftingDatabase.ExternalNetworkPrefabRegisteredCheck = prevHook;
            }
        }

        [Test]
        public void CraftingDatabase_TrashMapping_ResolvesByArchetypeIdNotDisplayText_AndHopperFilteringWorks()
        {
            CraftingDatabase db = AssetDatabase.LoadAssetAtPath<CraftingDatabase>("Assets/UpcycleCity/Resources/CraftingDatabase.asset");
            Assert.IsNotNull(db);

            // Archetype ID resolution works cleanly without display text
            Assert.IsTrue(db.TryResolveTrashMaterialByArchetype("old_tyre", WasteStream.BulkyUpcyclable, out MaterialDefinition wheelMat, out float wheelYield));
            Assert.AreEqual("old_wheel", wheelMat.MaterialId);
            Assert.AreEqual(1.0f, wheelYield, 0.001f);

            Assert.IsTrue(db.TryResolveTrashMaterialByArchetype("wooden_pallet", WasteStream.BulkyUpcyclable, out MaterialDefinition woodMat, out float woodYield));
            Assert.AreEqual("wood_scrap", woodMat.MaterialId);
            Assert.Greater(woodYield, 0f);

            Assert.IsTrue(db.TryResolveTrashMaterialByArchetype("broken_cafe_chair", WasteStream.BulkyUpcyclable, out MaterialDefinition chairWoodMat, out _));
            Assert.AreEqual("wood_scrap", chairWoodMat.MaterialId);

            Assert.IsTrue(db.TryResolveTrashMaterialByArchetype("water_bottle", WasteStream.Plastic, out MaterialDefinition plasticMat, out _));
            Assert.AreEqual("plastic_scrap", plasticMat.MaterialId);

            Assert.IsTrue(db.TryResolveTrashMaterialByArchetype("crushed_can", WasteStream.Cans, out MaterialDefinition canMat, out _));
            Assert.AreEqual("aluminum_cans", canMat.MaterialId);

            // Per-machine hopper filtering (#9)
            Assert.IsTrue(db.TryGetMachine("machine_plastic_melter", out MachineDefinition melter));
            Assert.IsTrue(melter.AcceptsHopperDeposit(plasticMat, WasteStream.Plastic));
            Assert.IsFalse(melter.AcceptsHopperDeposit(canMat, WasteStream.Cans), "Melter hopper must reject Cans.");
            Assert.IsFalse(melter.AcceptsHopperDeposit(wheelMat, WasteStream.BulkyUpcyclable), "Melter hopper must reject Old Wheels.");

            Assert.IsTrue(db.TryGetMachine("machine_tyre_stripper", out MachineDefinition stripper));
            Assert.IsTrue(stripper.AcceptsHopperDeposit(wheelMat, WasteStream.BulkyUpcyclable));
            Assert.IsFalse(stripper.AcceptsHopperDeposit(woodMat, WasteStream.BulkyUpcyclable), "Tyre Stripper must reject Wood Scrap.");
            Assert.IsFalse(stripper.AcceptsHopperDeposit(plasticMat, WasteStream.Plastic));

            Assert.IsTrue(db.TryGetMachine("machine_pulper", out MachineDefinition pulper));
            Assert.IsTrue(pulper.AcceptsHopperDeposit(woodMat, WasteStream.BulkyUpcyclable));
            Assert.IsFalse(pulper.AcceptsHopperDeposit(plasticMat, WasteStream.Plastic));
        }

        [Test]
        public void MachineUpgradeHelper_FormatFullCost_ShowsCoinsAndEveryMaterial()
        {
            MaterialDefinition poly = MakeMat("recycled_polymer", "Recycled Polymer", MaterialCategory.Refined);
            MaterialDefinition al = MakeMat("refined_aluminum", "Refined Aluminum", MaterialCategory.Refined);
            var cost = new MachineUpgradeCost
            {
                targetLevel = 1,
                coins = 35,
                materials = new[]
                {
                    new MaterialAmount(poly, 2.0f),
                    new MaterialAmount(al, 1.5f),
                },
            };

            string formatted = MachineUpgradeHelper.FormatFullCost(in cost);
            StringAssert.Contains("35 coins", formatted);
            StringAssert.Contains("Recycled Polymer", formatted);
            StringAssert.Contains("Refined Aluminum", formatted);
        }

        [Test]
        public void CraftingStarterKit_SeedsLedgerCleanly()
        {
            CraftingStarterKit kit = AssetDatabase.LoadAssetAtPath<CraftingStarterKit>("Assets/UpcycleCity/Settings/Crafting/CraftingStarterKit.asset");
            Assert.IsNotNull(kit, "CraftingStarterKit.asset must exist.");

            var ledger = new MaterialStockLedger(initialEnergy: 0);
            kit.ApplyTo(ledger);

            Assert.GreaterOrEqual(ledger.Energy, 100);
            Assert.GreaterOrEqual(ledger.GetAmount("plastic_scrap"), 4.0f);
            Assert.GreaterOrEqual(ledger.GetAmount("wood_scrap"), 5.0f);
            Assert.GreaterOrEqual(ledger.GetAmount("paper_carton"), 5.0f);
            Assert.GreaterOrEqual(ledger.GetAmount("aluminum_cans"), 4.0f);
            Assert.GreaterOrEqual(ledger.GetAmount("old_wheel"), 1.0f);
            Assert.GreaterOrEqual(ledger.GetAmount("catalyst"), 1.0f);
        }

        [Test]
        public void PlacementId_DuplicateSceneMachinesGetUniqueIds_AndShippedPrefabsStayEmpty()
        {
            // 1. Duplicate CraftingMachines in the scene (simulating Ctrl+D) must regenerate a unique ID
            GameObject machGoA = TrackGo("DupMachineA", withNetworkObject: false);
            GameObject machGoB = TrackGo("DupMachineB", withNetworkObject: false);
            CraftingMachine machA = machGoA.AddComponent<CraftingMachine>();
            CraftingMachine machB = machGoB.AddComponent<CraftingMachine>();

            SetSerializedPlacementId(machA, "dup_machine_id");
            SetSerializedPlacementId(machB, "dup_machine_id");

            machB.EnsureValidScenePlacementIdInEditor();

            Assert.AreEqual("dup_machine_id", ReadSerializedPlacementId(machA), "Original machine keeps its placementId.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(ReadSerializedPlacementId(machB)), "Duplicate must get a non-empty ID.");
            Assert.AreNotEqual(ReadSerializedPlacementId(machA), ReadSerializedPlacementId(machB),
                "Duplicated CraftingMachine must regenerate a unique placementId.");

            // 2. Duplicate ManualEnergyGenerators (and cross-collision with a CraftingMachine) must regenerate unique IDs
            GameObject genGoA = TrackGo("DupGenA", withNetworkObject: false);
            GameObject genGoB = TrackGo("DupGenB", withNetworkObject: false);
            ManualEnergyGenerator genA = genGoA.AddComponent<ManualEnergyGenerator>();
            ManualEnergyGenerator genB = genGoB.AddComponent<ManualEnergyGenerator>();

            SetSerializedPlacementId(genA, "dup_machine_id"); // collides with machA!
            genA.EnsureValidScenePlacementIdInEditor();
            Assert.AreNotEqual("dup_machine_id", ReadSerializedPlacementId(genA),
                "ManualEnergyGenerator colliding with a CraftingMachine placementId must regenerate.");

            string genAId = ReadSerializedPlacementId(genA);
            SetSerializedPlacementId(genB, genAId);
            genB.EnsureValidScenePlacementIdInEditor();
            Assert.AreNotEqual(genAId, ReadSerializedPlacementId(genB),
                "Duplicated ManualEnergyGenerator must regenerate a unique placementId.");

            // 3. All shipped machine prefab ASSETS must have empty placementId and remain empty if validated
            CraftingDatabase db = AssetDatabase.LoadAssetAtPath<CraftingDatabase>("Assets/UpcycleCity/Resources/CraftingDatabase.asset");
            Assert.IsNotNull(db);
            int checkedPrefabs = 0;
            for (int i = 0; i < db.Machines.Count; i++)
            {
                GameObject prefab = db.Machines[i].MachinePrefab;
                Assert.IsNotNull(prefab);
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabAsset(prefab), $"'{prefab.name}' must be a prefab asset.");

                CraftingMachine pm = prefab.GetComponent<CraftingMachine>();
                if (pm != null)
                {
                    Assert.IsTrue(string.IsNullOrEmpty(ReadSerializedPlacementId(pm)),
                        $"Shipped prefab '{prefab.name}' must have an empty placementId.");
                    pm.EnsureValidScenePlacementIdInEditor();
                    Assert.IsTrue(string.IsNullOrEmpty(ReadSerializedPlacementId(pm)),
                        $"Validating prefab asset '{prefab.name}' must NEVER write a placementId into the asset.");
                    checkedPrefabs++;
                }

                ManualEnergyGenerator pg = prefab.GetComponent<ManualEnergyGenerator>();
                if (pg != null)
                {
                    Assert.IsTrue(string.IsNullOrEmpty(ReadSerializedPlacementId(pg)),
                        $"Shipped generator prefab '{prefab.name}' must have an empty placementId.");
                    pg.EnsureValidScenePlacementIdInEditor();
                    Assert.IsTrue(string.IsNullOrEmpty(ReadSerializedPlacementId(pg)),
                        $"Validating generator prefab asset '{prefab.name}' must NEVER write a placementId into the asset.");
                    checkedPrefabs++;
                }
            }
            Assert.GreaterOrEqual(checkedPrefabs, 6);
        }

        [Test]
        public void MachineSaveState_RoundTripsSchemaVersionActiveCardAndRemainingSeconds()
        {
            MachineTypeDefinition melterType = MakeType("plastic_melter", "Plastic Melter");
            MaterialDefinition plastic = MakeMat("plastic_scrap", "Plastic Scrap", MaterialCategory.Trash);
            MaterialDefinition polymer = MakeMat("recycled_polymer", "Recycled Polymer", MaterialCategory.Refined);
            MachineDefinition melterDef = MakeMachine("machine_plastic_melter", "Pedal Plastic Melter", melterType, maxLevel: 1);
            CraftingCard card = MakeCard("card_plastic_save", "Plastic Reclaim", CardCategory.Refining, melterType,
                level: 0, energy: 10,
                inputs: new[] { new MaterialAmount(plastic, 4f) },
                outputs: new[] { new MaterialAmount(polymer, 3.2f) });

            var db = Track(ScriptableObject.CreateInstance<CraftingDatabase>());
            db.Configure(new[] { plastic, polymer }, new[] { card }, new[] { melterDef }, new[] { melterType });

            var original = new MachineSaveState
            {
                schemaVersion = MachineSaveState.CurrentSchemaVersion,
                placementId = "save_melter_42",
                machineId = melterDef.MachineId,
                level = 1,
                position = new Vector3(4.5f, 0.25f, -9.0f),
                rotation = Quaternion.Euler(0f, 135f, 0f),
                activeCardId = card.CardId,
                remainingProcessingSeconds = 1.35f,
            };

            GameObject go = TrackGo("RestoredMelter", withNetworkObject: false);
            CraftingMachine restored = go.AddComponent<CraftingMachine>();
            restored.Configure(melterDef, db, startLevel: 0);
            restored.RestoreSaveState(in original);

            Assert.AreEqual("save_melter_42", restored.PlacementId);
            Assert.AreEqual(1, restored.Level);
            Assert.IsTrue(restored.IsProcessing);
            Assert.AreSame(card, restored.ActiveCard);
            Assert.AreEqual(1.35f, restored.ProcessRemainingSeconds, 0.01f);

            MachineSaveState recaptured = restored.CaptureSaveState();
            Assert.AreEqual(MachineSaveState.CurrentSchemaVersion, recaptured.schemaVersion);
            Assert.AreEqual(original.placementId, recaptured.placementId);
            Assert.AreEqual(original.machineId, recaptured.machineId);
            Assert.AreEqual(original.level, recaptured.level);
            Assert.AreEqual(original.activeCardId, recaptured.activeCardId);
            Assert.AreEqual(1.35f, recaptured.remainingProcessingSeconds, 0.01f);
            Assert.Less((recaptured.position - original.position).magnitude, 0.001f);
        }

        [Test]
        public void CraftingPanelSettings_UsesPhoneFirstReferenceWithoutAlteringSharedPanelSettings()
        {
            var craftingPanel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(
                "Assets/UpcycleCity/Resources/CraftingPanelSettings.asset");
            Assert.IsNotNull(craftingPanel, "Dedicated CraftingPanelSettings.asset must exist in Resources/.");
            Assert.AreEqual(UnityEngine.UIElements.PanelScaleMode.ScaleWithScreenSize, craftingPanel.scaleMode);
            Assert.AreEqual(new Vector2Int(1080, 608), craftingPanel.referenceResolution);
            Assert.AreEqual(0.5f, craftingPanel.match, 0.001f);

            var sharedPanel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(
                "Assets/UpcycleCity/UI/UpcycleCityPanelSettings.asset");
            Assert.IsNotNull(sharedPanel);
            Assert.AreEqual(new Vector2Int(1920, 1080), sharedPanel.referenceResolution,
                "Shared UpcycleCityPanelSettings.asset must remain untouched for other UIs.");
        }

        static string ReadSerializedPlacementId(UnityEngine.Object component)
        {
            using (var so = new SerializedObject(component))
                return so.FindProperty("placementId").stringValue;
        }

        static void SetSerializedPlacementId(UnityEngine.Object component, string id)
        {
            using (var so = new SerializedObject(component))
            {
                so.FindProperty("placementId").stringValue = id;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
