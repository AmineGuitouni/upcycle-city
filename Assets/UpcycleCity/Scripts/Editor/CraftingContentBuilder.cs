using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UpcycleCity.Core;
using UpcycleCity.Crafting;
using UpcycleCity.Ecosystem;
using UpcycleCity.Networking;
using UpcycleCity.Waste;
using MaterialDefinition = UpcycleCity.Crafting.MaterialDefinition;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Idempotent Editor builder for the entire Upcycle City Recycling &amp; Crafting Card system:
    /// Machine Types, Materials (archetype-ID mapped), Built Object prefabs, Manual Machine prefabs
    /// (self-contained, networked with <see cref="NetworkMachineIdentity"/> and valid non-zero
    /// <see cref="NetworkObject.GlobalObjectIdHash"/>), Machine Definitions (with purchase prices and
    /// hopper filters), Refining &amp; Building Cards, <see cref="CraftingDatabase"/> (in <c>Resources/</c>),
    /// Starter Kit, and UI Config.
    /// <para>
    /// Preserves designer tuning on existing ScriptableObject assets when re-run; repairs missing/migrated
    /// links and ensures required asset references, prefabs, database lists, validation, and
    /// <c>UpcycleCityNetworkPrefabs.asset</c> stay in sync.
    /// </para>
    /// </summary>
    public static class CraftingContentBuilder
    {
        public const string SettingsRoot = "Assets/UpcycleCity/Settings/Crafting";
        public const string MachineTypesDir = SettingsRoot + "/MachineTypes";
        public const string MaterialsDir = SettingsRoot + "/Materials";
        public const string CardsDir = SettingsRoot + "/Cards";
        public const string MachinesSettingsDir = SettingsRoot + "/Machines";
        public const string PrefabsRoot = "Assets/UpcycleCity/Prefabs/Crafting";
        public const string MachinePrefabsDir = PrefabsRoot + "/Machines";
        public const string BuiltPrefabsDir = PrefabsRoot + "/Built";
        public const string ResourcesDir = "Assets/UpcycleCity/Resources";

        public const string DatabasePath = ResourcesDir + "/CraftingDatabase.asset";
        public const string StarterKitPath = SettingsRoot + "/CraftingStarterKit.asset";
        public const string UiConfigPath = ResourcesDir + "/CraftingUIConfig.asset";
        public const string CraftingPanelSettingsPath = ResourcesDir + "/CraftingPanelSettings.asset";

        const string WasteCatalogPath = "Assets/UpcycleCity/Settings/Ecosystem/WasteCatalog.asset";
        const string MachineArtDir = "Assets/UpcycleCity/Art/UpcycleProps/Machines/Manual";
        const string CityArtDir = "Assets/UpcycleCity/Art/UpcycleProps/City";
        const string RefiningCardsArtDir = "Assets/UpcycleCity/Art/Cards/Refining";
        const string SharedPanelSettingsPath = "Assets/UpcycleCity/UI/UpcycleCityPanelSettings.asset";
        const string CraftingUssPath = "Assets/UpcycleCity/UI/Crafting.uss";

        static readonly MethodInfo NetworkObjectOnValidateMethod =
            typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic);

        // Sorting & brand palette colors
        static readonly Color ColPaper = new Color(0.180f, 0.435f, 0.710f);        // #2E6FB5
        static readonly Color ColPlastic = new Color(0.949f, 0.710f, 0.106f);      // #F2B51B
        static readonly Color ColGlass = new Color(0.180f, 0.545f, 0.341f);        // #2E8B57
        static readonly Color ColCans = new Color(0.784f, 0.275f, 0.231f);         // #C8463B
        static readonly Color ColTeal = new Color(0.184f, 0.561f, 0.541f);         // #2F8F8A
        static readonly Color ColOrange = new Color(1.000f, 0.478f, 0.102f);       // #FF7A1A
        static readonly Color ColWood = new Color(0.663f, 0.455f, 0.310f);
        static readonly Color ColOrganic = new Color(0.471f, 0.341f, 0.180f);    // compost brown
        static readonly Color ColWheel = new Color(0.275f, 0.305f, 0.335f);
        static readonly Color ColPolymer = new Color(0.949f, 0.761f, 0.106f);      // #F2C21B
        static readonly Color ColWoodPulp = new Color(0.792f, 0.651f, 0.431f);
        static readonly Color ColCleanedPulp = new Color(0.353f, 0.659f, 0.882f);
        static readonly Color ColAluminum = new Color(0.753f, 0.792f, 0.831f);

        public sealed class Content
        {
            public CraftingDatabase database;
            public CraftingStarterKit starterKit;
            public CraftingUIConfig uiConfig;

            public GameObject pulperPrefab;
            public GameObject plasticMelterPrefab;
            public GameObject crucibleFurnacePrefab;
            public GameObject tyreStripperPrefab;
            public GameObject workbenchPrefab;
            public GameObject pedalGeneratorPrefab;

            public GameObject builtBenchPrefab;
            public GameObject builtBicyclePrefab;
        }

        [MenuItem("Upcycle City/Crafting/Rebuild Crafting Content", priority = 60)]
        public static void RebuildMenu()
        {
            Content content = EnsureAll(overwriteExistingTuning: false);
            CraftingDatabase.ExternalNetworkPrefabRegisteredCheck =
                CraftingDatabaseValidationHook.IsPrefabRegisteredInNetworkPrefabsList;

            var errors = new List<string>();
            bool valid = content.database != null && content.database.Validate(errors);
            if (!valid)
            {
                for (int i = 0; i < errors.Count; i++)
                    Debug.LogError("[Upcycle City] CraftingDatabase validation error: " + errors[i]);
            }
            else
            {
                Debug.Log(
                    $"[Upcycle City] Crafting content rebuilt & validated: " +
                    $"{content.database.MachineTypes.Count} machine types, " +
                    $"{content.database.Materials.Count} materials, " +
                    $"{content.database.Cards.Count} cards, " +
                    $"{content.database.Machines.Count} machines.");
            }
        }

        public static Content EnsureAll(bool overwriteExistingTuning = false)
        {
            EnsureFolder(MachineTypesDir);
            EnsureFolder(MaterialsDir);
            EnsureFolder(CardsDir);
            EnsureFolder(MachinesSettingsDir);
            EnsureFolder(MachinePrefabsDir);
            EnsureFolder(BuiltPrefabsDir);
            EnsureFolder(ResourcesDir);

            // 0. Ensure WasteCatalog has stable archetype IDs serialized on disk
            WasteCatalog wasteCatalog = AssetDatabase.LoadAssetAtPath<WasteCatalog>(WasteCatalogPath);
            if (wasteCatalog != null)
            {
                wasteCatalog.EnsureArchetypeIds();
                EditorUtility.SetDirty(wasteCatalog);
            }

            // 1. Data-driven Machine Types (adding a new type requires zero code changes)
            MachineTypeDefinition typePulper = EnsureMachineType("Type_Pulper", "pulper", "Manual Pulper",
                "Shredding & hydrapulping stations for cellulose and paper waste.", ColPaper, overwriteExistingTuning);
            MachineTypeDefinition typeMelter = EnsureMachineType("Type_PlasticMelter", "plastic_melter", "Plastic Melter",
                "Thermal extrusion stations for reclaiming sorted plastics.", ColPlastic, overwriteExistingTuning);
            MachineTypeDefinition typeFurnace = EnsureMachineType("Type_CrucibleFurnace", "crucible_furnace", "Crucible Furnace",
                "High-heat smelting stations for metallic scrap and cans.", ColCans, overwriteExistingTuning);
            MachineTypeDefinition typeStripper = EnsureMachineType("Type_TyreStripper", "tyre_stripper", "Tyre Stripper",
                "Mechanical separators for rubber tyres and alloy rims.", ColOrange, overwriteExistingTuning);
            MachineTypeDefinition typeWorkbench = EnsureMachineType("Type_Workbench", "workbench", "Upcycle Workbench",
                "Assembly benches for building city furniture and vehicles.", ColGlass, overwriteExistingTuning);
            MachineTypeDefinition typeGenerator = EnsureMachineType("Type_EnergyGenerator", "energy_generator", "Pedal Energy Generator",
                "Manual dynamo generators producing shared team NRG.", ColTeal, overwriteExistingTuning);

            var allMachineTypes = new List<MachineTypeDefinition>
            {
                typePulper, typeMelter, typeFurnace, typeStripper, typeWorkbench, typeGenerator,
            };

            // 2. Materials (13 total: Trash/Collectibles, Catalyst, Refined, Built — mapped by stable WasteCatalog archetype IDs)
            MaterialDefinition matPlasticScrap = EnsureMaterial("Mat_PlasticScrap", "plastic_scrap", "Plastic Scrap",
                "Sorted plastic bottles, bags, and caps ready for thermal reclaim.",
                MaterialCategory.Trash, MaterialUnit.Kilograms, ColPlastic,
                acceptTrash: true, WasteStream.Plastic, yieldPerItem: 2.0f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matWoodScrap = EnsureMaterial("Mat_WoodScrap", "wood_scrap", "Wood Scrap",
                "Salvaged wooden pallets, crates, and broken furniture.",
                MaterialCategory.Trash, MaterialUnit.Kilograms, ColWood,
                acceptTrash: true, WasteStream.BulkyUpcyclable, yieldPerItem: 2.5f,
                archetypeIds: new[] { "wooden_pallet", "broken_cafe_chair" }, overwriteExistingTuning);

            MaterialDefinition matPaperCarton = EnsureMaterial("Mat_PaperCarton", "paper_carton", "Paper & Carton",
                "Flattened cartons, coffee cups, and street paper waste.",
                MaterialCategory.Trash, MaterialUnit.Kilograms, ColPaper,
                acceptTrash: true, WasteStream.Paper, yieldPerItem: 2.5f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matAluminumCans = EnsureMaterial("Mat_AluminumCans", "aluminum_cans", "Aluminum Cans",
                "Crushed beverage cans and scrap aluminum tubing.",
                MaterialCategory.Trash, MaterialUnit.Kilograms, ColCans,
                acceptTrash: true, WasteStream.Cans, yieldPerItem: 2.0f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matOldWheel = EnsureMaterial("Mat_OldWheel", "old_wheel", "Old Wheel",
                "Discarded rubber tyre and alloy rim salvaged from the streets.",
                MaterialCategory.Trash, MaterialUnit.Units, ColWheel,
                acceptTrash: true, WasteStream.BulkyUpcyclable, yieldPerItem: 1.0f,
                archetypeIds: new[] { "old_tyre" }, overwriteExistingTuning);

            MaterialDefinition matGlassCullet = EnsureMaterial("Mat_GlassCullet", "glass_cullet", "Glass Cullet",
                "Sorted clear and tinted glass bottles collected from the city.",
                MaterialCategory.Trash, MaterialUnit.Kilograms, ColGlass,
                acceptTrash: true, WasteStream.Glass, yieldPerItem: 1.5f, archetypeIds: null, overwriteExistingTuning);

            // Organic waste dug out of street bins (BinLootTable): compost feedstock for fertiliser later on
            MaterialDefinition matOrganicWaste = EnsureMaterial("Mat_OrganicWaste", "organic_waste", "Organic Waste",
                "Food scraps, peels and coffee grounds sorted out of street bins. Compost feedstock for fertiliser.",
                MaterialCategory.Trash, MaterialUnit.Kilograms, ColOrganic,
                acceptTrash: false, WasteStream.Plastic, yieldPerItem: 0.5f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matCatalyst = EnsureMaterial("Mat_Catalyst", "catalyst", "Catalyst",
                "Industrial flux and de-inking agent used in refining recipes (available at Quincaillerie).",
                MaterialCategory.Consumable, MaterialUnit.Kilograms, ColTeal,
                acceptTrash: false, WasteStream.Plastic, yieldPerItem: 0.5f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matRecycledPolymer = EnsureMaterial("Mat_RecycledPolymer", "recycled_polymer", "Recycled Polymer",
                "Extruded thermoplastic pellets ready for moulding durable city furniture and tyres.",
                MaterialCategory.Refined, MaterialUnit.Kilograms, ColPolymer,
                acceptTrash: false, WasteStream.Plastic, yieldPerItem: 1.0f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matWoodPulp = EnsureMaterial("Mat_WoodPulp", "wood_pulp", "Wood Pulp",
                "Separated cellulose fibers pressed from reclaimed wood scrap.",
                MaterialCategory.Refined, MaterialUnit.Kilograms, ColWoodPulp,
                acceptTrash: false, WasteStream.Paper, yieldPerItem: 1.0f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matCleanedPulp = EnsureMaterial("Mat_CleanedPulp", "cleaned_pulp", "Cleaned Pulp",
                "De-inked, washed paper pulp for eco-packaging and composite boards.",
                MaterialCategory.Refined, MaterialUnit.Kilograms, ColCleanedPulp,
                acceptTrash: false, WasteStream.Paper, yieldPerItem: 1.0f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matRefinedAluminum = EnsureMaterial("Mat_RefinedAluminum", "refined_aluminum", "Refined Aluminum",
                "Smelted, slag-free aluminum ingots for lightweight frames and machine upgrades.",
                MaterialCategory.Refined, MaterialUnit.Kilograms, ColAluminum,
                acceptTrash: false, WasteStream.Cans, yieldPerItem: 1.0f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matBuiltBench = EnsureMaterial("Mat_BuiltParkBench", "built_park_bench", "Upcycled Park Bench",
                "Sturdy composite bench crafted from reclaimed polymer and pressed wood pulp.",
                MaterialCategory.Built, MaterialUnit.Units, ColGlass,
                acceptTrash: false, WasteStream.BulkyUpcyclable, yieldPerItem: 1.0f, archetypeIds: null, overwriteExistingTuning);

            MaterialDefinition matBuiltBicycle = EnsureMaterial("Mat_BuiltBicycle", "built_bicycle", "Upcycled City Bicycle",
                "Eco-commuter bicycle assembled from smelted aluminum and recycled polymer.",
                MaterialCategory.Built, MaterialUnit.Units, ColTeal,
                acceptTrash: false, WasteStream.BulkyUpcyclable, yieldPerItem: 1.0f, archetypeIds: null, overwriteExistingTuning);

            var allMaterials = new List<MaterialDefinition>
            {
                matPlasticScrap, matWoodScrap, matPaperCarton, matAluminumCans, matOldWheel, matGlassCullet,
                matCatalyst,
                matRecycledPolymer, matWoodPulp, matCleanedPulp, matRefinedAluminum,
                matBuiltBench, matBuiltBicycle,
                matOrganicWaste, // appended last so existing stock indices stay put
            };

            // 3. Built Object Networked Prefabs (with valid non-zero GlobalObjectIdHash)
            GameObject builtBenchPrefab = EnsureBuiltPrefab("Built_ParkBench", $"{CityArtDir}/park_bench.fbx", new Vector3(1.8f, 0.9f, 0.7f));
            GameObject builtBicyclePrefab = EnsureBuiltPrefab("Built_UpcycledBicycle", $"{CityArtDir}/bicycle_teal.fbx", new Vector3(1.7f, 1.1f, 0.6f));

            // 4. Machine Definitions (with purchasePrice & per-machine hopper filtering)
            MachineDefinition defPulper = EnsureMachineDef("MachineDef_ManualPulper", "machine_pulper", "Manual Pulper",
                "Hand-cranked shredder & hydrapulper for wood scrap and paper cartons.",
                typePulper, ColPaper, 0, 1,
                new[] { CardCategory.Refining },
                new[] { UpgradeCost(1, 20, new MaterialAmount(matRecycledPolymer, 2.0f)) },
                purchasePrice: 45,
                footprint: new Vector2(2f, 2f),
                hopperStreams: new[] { WasteStream.Paper },
                hopperMaterials: new[] { matWoodScrap },
                overwriteExistingTuning);

            MachineDefinition defMelter = EnsureMachineDef("MachineDef_PlasticMelter", "machine_plastic_melter", "Pedal Plastic Melter",
                "Pedal-driven thermal barrel that melts and extrudes sorted plastic waste.",
                typeMelter, ColPlastic, 0, 1,
                new[] { CardCategory.Refining },
                new[] { UpgradeCost(1, 20, new MaterialAmount(matWoodPulp, 2.0f)) },
                purchasePrice: 50,
                footprint: new Vector2(2f, 2f),
                hopperStreams: new[] { WasteStream.Plastic },
                hopperMaterials: null,
                overwriteExistingTuning);

            MachineDefinition defFurnace = EnsureMachineDef("MachineDef_CrucibleFurnace", "machine_crucible_furnace", "Bellows Crucible Furnace",
                "High-heat smelting hearth with hand-pumped forge bellows. Upgrade to Lv 1 for aluminum smelting.",
                typeFurnace, ColCans, 0, 1,
                new[] { CardCategory.Refining },
                new[] { UpgradeCost(1, 25, new MaterialAmount(matRecycledPolymer, 2.0f)) },
                purchasePrice: 70,
                footprint: new Vector2(2f, 2f),
                hopperStreams: new[] { WasteStream.Cans },
                hopperMaterials: null,
                overwriteExistingTuning);

            MachineDefinition defStripper = EnsureMachineDef("MachineDef_TyreStripper", "machine_tyre_stripper", "Manual Tyre Stripper",
                "Twin-roller shredder & thermal separator for old wheels. Upgrade to Lv 1 to break down wheels.",
                typeStripper, ColOrange, 0, 1,
                new[] { CardCategory.Refining },
                new[] { UpgradeCost(1, 25, new MaterialAmount(matRecycledPolymer, 2.0f)) },
                purchasePrice: 75,
                footprint: new Vector2(2f, 2f),
                hopperStreams: null,
                hopperMaterials: new[] { matOldWheel },
                overwriteExistingTuning);

            MachineDefinition defWorkbench = EnsureMachineDef("MachineDef_UpcycleWorkbench", "machine_workbench", "Upcycle Workbench",
                "Mechanical assembly table for turning refined materials into useful city furniture and bikes.",
                typeWorkbench, ColGlass, 0, 1,
                new[] { CardCategory.Building },
                new[] { UpgradeCost(1, 30, new MaterialAmount(matRefinedAluminum, 2.0f)) },
                purchasePrice: 65,
                footprint: new Vector2(2f, 2f),
                hopperStreams: null,
                hopperMaterials: null,
                overwriteExistingTuning);

            MachineDefinition defGenerator = EnsureMachineDef("MachineDef_PedalGenerator", "machine_pedal_generator", "Pedal Energy Generator",
                "Stationary bicycle dynamo that generates shared team NRG for workshop machines.",
                typeGenerator, ColTeal, 0, 1,
                new[] { CardCategory.Energy },
                new[] { UpgradeCost(1, 20, new MaterialAmount(matRefinedAluminum, 1.5f)) },
                purchasePrice: 40,
                footprint: new Vector2(2f, 1f),
                hopperStreams: null,
                hopperMaterials: null,
                overwriteExistingTuning);

            var allMachines = new List<MachineDefinition>
            {
                defPulper, defMelter, defFurnace, defStripper, defWorkbench, defGenerator,
            };

            // 5. Cards (5 Refining PNG cards + 2 procedural Building cards)
            CraftingCard cardPlastic = EnsureCard("Card_Refining_PlasticThermalReclaim",
                "refining_plastic_thermal_reclaim", "Plastic Thermal Reclaim",
                "Basic low-temp melt & extrusion from sorted plastic waste.",
                CardCategory.Refining, typeMelter,
                level: 0, nrg: 10, tempC: 180, duration: 2.5f,
                inputs: new[] { new MaterialAmount(matPlasticScrap, 4.0f), new MaterialAmount(matCatalyst, 0.2f) },
                outputs: new[] { new MaterialAmount(matRecycledPolymer, 3.2f) },
                artworkPath: $"{RefiningCardsArtDir}/Card_Refining_PlasticThermalReclaim.png",
                outputPrefab: null, overwriteExistingTuning);

            CraftingCard cardWood = EnsureCard("Card_Refining_WoodPulpReclaim",
                "refining_wood_pulp_reclaim", "Wood Pulp Reclaim",
                "Basic low-temp fiber separation from wood waste.",
                CardCategory.Refining, typePulper,
                level: 0, nrg: 12, tempC: 150, duration: 2.5f,
                inputs: new[] { new MaterialAmount(matWoodScrap, 5.0f), new MaterialAmount(matCatalyst, 0.2f) },
                outputs: new[] { new MaterialAmount(matWoodPulp, 4.8f) },
                artworkPath: $"{RefiningCardsArtDir}/Card_Refining_WoodPulpReclaim.png",
                outputPrefab: null, overwriteExistingTuning);

            CraftingCard cardPaper = EnsureCard("Card_Refining_PaperCartonRepulp",
                "refining_paper_carton_repulp", "Paper & Carton Repulp",
                "Low-temp hydrapulping with de-inking agent.",
                CardCategory.Refining, typePulper,
                level: 0, nrg: 10, tempC: 90, duration: 2.2f,
                inputs: new[] { new MaterialAmount(matPaperCarton, 5.0f), new MaterialAmount(matCatalyst, 0.2f) },
                outputs: new[] { new MaterialAmount(matCleanedPulp, 4.7f) },
                artworkPath: $"{RefiningCardsArtDir}/Card_Refining_PaperCartonRepulp.png",
                outputPrefab: null, overwriteExistingTuning);

            CraftingCard cardAluminum = EnsureCard("Card_Refining_AluminumCanReclaim",
                "refining_aluminum_can_reclaim", "Aluminum Can Reclaim",
                "High-heat smelting & flux skimming from crushed cans.",
                CardCategory.Refining, typeFurnace,
                level: 1, nrg: 25, tempC: 660, duration: 3.2f,
                inputs: new[] { new MaterialAmount(matAluminumCans, 4.0f), new MaterialAmount(matCatalyst, 0.3f) },
                outputs: new[] { new MaterialAmount(matRefinedAluminum, 3.4f) },
                artworkPath: $"{RefiningCardsArtDir}/Card_Refining_AluminumCanReclaim.png",
                outputPrefab: null, overwriteExistingTuning);

            CraftingCard cardWheel = EnsureCard("Card_Refining_OldWheelBreakdown",
                "refining_old_wheel_breakdown", "Old Wheel Breakdown",
                "Shredding, thermal separation & material recovery.",
                CardCategory.Refining, typeStripper,
                level: 1, nrg: 40, tempC: 500, duration: 3.5f,
                inputs: new[] { new MaterialAmount(matOldWheel, 1.0f) },
                outputs: new[] { new MaterialAmount(matRefinedAluminum, 1.8f), new MaterialAmount(matRecycledPolymer, 1.2f) },
                artworkPath: $"{RefiningCardsArtDir}/Card_Refining_OldWheelBreakdown.png",
                outputPrefab: null, overwriteExistingTuning);

            CraftingCard cardBench = EnsureCard("Card_Building_UpcycledParkBench",
                "building_upcycled_park_bench", "Upcycled Park Bench",
                "Bolted recycled-polymer slats and pressed wood-pulp seat for city parks.",
                CardCategory.Building, typeWorkbench,
                level: 0, nrg: 15, tempC: 0, duration: 2.5f,
                inputs: new[] { new MaterialAmount(matRecycledPolymer, 2.0f), new MaterialAmount(matWoodPulp, 3.0f) },
                outputs: new[] { new MaterialAmount(matBuiltBench, 1.0f) },
                artworkPath: null,
                outputPrefab: builtBenchPrefab, overwriteExistingTuning);

            CraftingCard cardBicycle = EnsureCard("Card_Building_UpcycledBicycle",
                "building_upcycled_bicycle", "Upcycled City Bicycle",
                "Lightweight smelted aluminum frame fitted with reclaimed polymer tires.",
                CardCategory.Building, typeWorkbench,
                level: 1, nrg: 30, tempC: 0, duration: 3.2f,
                inputs: new[] { new MaterialAmount(matRefinedAluminum, 3.0f), new MaterialAmount(matRecycledPolymer, 1.5f) },
                outputs: new[] { new MaterialAmount(matBuiltBicycle, 1.0f) },
                artworkPath: null,
                outputPrefab: builtBicyclePrefab, overwriteExistingTuning);

            var allCards = new List<CraftingCard>
            {
                cardPlastic, cardWood, cardPaper, cardAluminum, cardWheel, cardBench, cardBicycle,
            };

            // 6. CraftingDatabase in Resources/
            CraftingDatabase database = LoadOrCreate<CraftingDatabase>(DatabasePath, out _);
            database.Configure(allMaterials, allCards, allMachines, allMachineTypes);
            EditorUtility.SetDirty(database);

            // 7. Self-Contained Networked Machine Prefabs (with NetworkMachineIdentity + non-zero GlobalObjectIdHash)
            GameObject pulperPrefab = EnsureMachinePrefab("Machine_ManualPulper", $"{MachineArtDir}/machine_pulper.fbx", defPulper, database, isGenerator: false, hasHopper: true);
            GameObject melterPrefab = EnsureMachinePrefab("Machine_PlasticMelter", $"{MachineArtDir}/machine_plastic_melter.fbx", defMelter, database, isGenerator: false, hasHopper: true);
            GameObject furnacePrefab = EnsureMachinePrefab("Machine_CrucibleFurnace", $"{MachineArtDir}/machine_crucible_furnace.fbx", defFurnace, database, isGenerator: false, hasHopper: true);
            GameObject stripperPrefab = EnsureMachinePrefab("Machine_TyreStripper", $"{MachineArtDir}/machine_tyre_stripper.fbx", defStripper, database, isGenerator: false, hasHopper: true);
            GameObject workbenchPrefab = EnsureMachinePrefab("Machine_UpcycleWorkbench", $"{MachineArtDir}/machine_workbench.fbx", defWorkbench, database, isGenerator: false, hasHopper: false);
            GameObject generatorPrefab = EnsureMachinePrefab("Machine_PedalGenerator", $"{MachineArtDir}/machine_pedal_generator.fbx", defGenerator, database, isGenerator: true, hasHopper: false);

            WireMachinePrefabRef(defPulper, pulperPrefab);
            WireMachinePrefabRef(defMelter, melterPrefab);
            WireMachinePrefabRef(defFurnace, furnacePrefab);
            WireMachinePrefabRef(defStripper, stripperPrefab);
            WireMachinePrefabRef(defWorkbench, workbenchPrefab);
            WireMachinePrefabRef(defGenerator, generatorPrefab);

            // 8. Starter Kit
            CraftingStarterKit starterKit = LoadOrCreate<CraftingStarterKit>(StarterKitPath, out bool kitCreated);
            if (kitCreated || overwriteExistingTuning || starterKit.Materials.Length == 0)
            {
                starterKit.Configure(
                    energy: 200,
                    coins: 60,
                    new MaterialAmount(matPlasticScrap, 12.0f),
                    new MaterialAmount(matWoodScrap, 12.0f),
                    new MaterialAmount(matPaperCarton, 12.0f),
                    new MaterialAmount(matAluminumCans, 12.0f),
                    new MaterialAmount(matOldWheel, 3.0f),
                    new MaterialAmount(matCatalyst, 5.0f),
                    new MaterialAmount(matRecycledPolymer, 2.0f),
                    new MaterialAmount(matWoodPulp, 3.0f));
                EditorUtility.SetDirty(starterKit);
            }

            // 9. Dedicated phone-first Crafting PanelSettings + UI Config in Resources/
            PanelSettings craftingPanelSettings = EnsureCraftingPanelSettings();
            CraftingUIConfig uiConfig = LoadOrCreate<CraftingUIConfig>(UiConfigPath, out _);
            uiConfig.Configure(
                craftingPanelSettings,
                AssetDatabase.LoadAssetAtPath<StyleSheet>(CraftingUssPath));
            EditorUtility.SetDirty(uiConfig);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            NetworkPrefabRegistryTool.RefreshNetworkPrefabs(logSummary: false);

            return new Content
            {
                database = database,
                starterKit = starterKit,
                uiConfig = uiConfig,
                pulperPrefab = pulperPrefab,
                plasticMelterPrefab = melterPrefab,
                crucibleFurnacePrefab = furnacePrefab,
                tyreStripperPrefab = stripperPrefab,
                workbenchPrefab = workbenchPrefab,
                pedalGeneratorPrefab = generatorPrefab,
                builtBenchPrefab = builtBenchPrefab,
                builtBicyclePrefab = builtBicyclePrefab,
            };
        }

        // ------------------------------------------------------------ Asset Helpers

        static MachineTypeDefinition EnsureMachineType(
            string fileName,
            string id,
            string label,
            string desc,
            Color color,
            bool overwrite)
        {
            string path = $"{MachineTypesDir}/{fileName}.asset";
            MachineTypeDefinition asset = LoadOrCreate<MachineTypeDefinition>(path, out bool created);
            if (created || overwrite || string.IsNullOrWhiteSpace(asset.TypeId))
            {
                asset.Configure(id, label, desc, color);
                EditorUtility.SetDirty(asset);
            }
            return asset;
        }

        static MaterialDefinition EnsureMaterial(
            string fileName,
            string id,
            string label,
            string desc,
            MaterialCategory category,
            MaterialUnit unit,
            Color color,
            bool acceptTrash,
            WasteStream stream,
            float yieldPerItem,
            string[] archetypeIds,
            bool overwrite)
        {
            string path = $"{MaterialsDir}/{fileName}.asset";
            MaterialDefinition asset = LoadOrCreate<MaterialDefinition>(path, out bool created);
            if (created || overwrite)
            {
                asset.Configure(id, label, desc, category, unit, color, acceptTrash, stream, yieldPerItem, archetypeIds);
                EditorUtility.SetDirty(asset);
            }
            else if (archetypeIds != null && archetypeIds.Length > 0 && asset.SourceTrashArchetypeIds.Length == 0)
            {
                // Migrate legacy keyword-based assets to explicit WasteCatalog archetype IDs
                using (var so = new SerializedObject(asset))
                {
                    SerializedProperty prop = so.FindProperty("sourceTrashArchetypeIds");
                    prop.arraySize = archetypeIds.Length;
                    for (int i = 0; i < archetypeIds.Length; i++)
                        prop.GetArrayElementAtIndex(i).stringValue = archetypeIds[i];
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorUtility.SetDirty(asset);
            }
            return asset;
        }

        static MachineDefinition EnsureMachineDef(
            string fileName,
            string id,
            string label,
            string desc,
            MachineTypeDefinition typeDef,
            Color color,
            int defaultLevel,
            int maxLevel,
            CardCategory[] acceptedCategories,
            MachineUpgradeCost[] upgrades,
            int purchasePrice,
            Vector2 footprint,
            WasteStream[] hopperStreams,
            MaterialDefinition[] hopperMaterials,
            bool overwrite)
        {
            string path = $"{MachinesSettingsDir}/{fileName}.asset";
            MachineDefinition asset = LoadOrCreate<MachineDefinition>(path, out bool created);
            if (created || overwrite)
            {
                asset.Configure(
                    id, label, desc, typeDef, color, defaultLevel, maxLevel,
                    acceptedCategories, upgrades,
                    prefab: asset.MachinePrefab,
                    range: 6.5f,
                    footprint: footprint,
                    price: purchasePrice,
                    hopperStreams: hopperStreams,
                    hopperMaterials: hopperMaterials);
                EditorUtility.SetDirty(asset);
            }
            else
            {
                // Preserve existing tuning while migrating new fields (machineType, purchasePrice, hopper filters)
                bool dirty = false;
                using (var so = new SerializedObject(asset))
                {
                    if (asset.MachineType == null && typeDef != null)
                    {
                        so.FindProperty("machineType").objectReferenceValue = typeDef;
                        dirty = true;
                    }
                    if (asset.PurchasePrice <= 0 && purchasePrice > 0)
                    {
                        so.FindProperty("purchasePrice").intValue = purchasePrice;
                        dirty = true;
                    }
                    bool hasExistingFilters = asset.AcceptedHopperStreams.Length > 0 || asset.AcceptedHopperMaterials.Length > 0;
                    if (!hasExistingFilters)
                    {
                        if (hopperStreams != null && hopperStreams.Length > 0)
                        {
                            SerializedProperty sp = so.FindProperty("acceptedHopperStreams");
                            sp.arraySize = hopperStreams.Length;
                            for (int i = 0; i < hopperStreams.Length; i++)
                                sp.GetArrayElementAtIndex(i).enumValueIndex = (int)hopperStreams[i];
                            dirty = true;
                        }
                        if (hopperMaterials != null && hopperMaterials.Length > 0)
                        {
                            SerializedProperty mp = so.FindProperty("acceptedHopperMaterials");
                            mp.arraySize = hopperMaterials.Length;
                            for (int i = 0; i < hopperMaterials.Length; i++)
                                mp.GetArrayElementAtIndex(i).objectReferenceValue = hopperMaterials[i];
                            dirty = true;
                        }
                    }
                    if (dirty)
                        so.ApplyModifiedPropertiesWithoutUndo();
                }
                if (dirty)
                    EditorUtility.SetDirty(asset);
            }
            return asset;
        }

        static void WireMachinePrefabRef(MachineDefinition def, GameObject prefab)
        {
            if (def == null || prefab == null || def.MachinePrefab == prefab)
                return;
            using (var so = new SerializedObject(def))
            {
                so.FindProperty("machinePrefab").objectReferenceValue = prefab;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorUtility.SetDirty(def);
        }

        static CraftingCard EnsureCard(
            string fileName,
            string id,
            string label,
            string desc,
            CardCategory category,
            MachineTypeDefinition machineType,
            int level,
            int nrg,
            int tempC,
            float duration,
            MaterialAmount[] inputs,
            MaterialAmount[] outputs,
            string artworkPath,
            GameObject outputPrefab,
            bool overwrite)
        {
            string path = $"{CardsDir}/{fileName}.asset";
            CraftingCard asset = LoadOrCreate<CraftingCard>(path, out bool created);
            Sprite artwork = !string.IsNullOrEmpty(artworkPath) ? LoadCardSprite(artworkPath) : null;

            if (created || overwrite)
            {
                asset.Configure(id, label, desc, category, machineType, level, nrg, tempC, duration, inputs, outputs, artwork, outputPrefab);
                EditorUtility.SetDirty(asset);
            }
            else
            {
                // Preserve existing designer tuning, only repair missing/migrated links
                bool dirty = false;
                using (var so = new SerializedObject(asset))
                {
                    if (asset.RequiredMachineType == null && machineType != null)
                    {
                        so.FindProperty("requiredMachineType").objectReferenceValue = machineType;
                        dirty = true;
                    }
                    if (artwork != null && asset.CardArtwork == null)
                    {
                        so.FindProperty("cardArtwork").objectReferenceValue = artwork;
                        dirty = true;
                    }
                    if (outputPrefab != null && asset.OutputPrefab == null)
                    {
                        so.FindProperty("outputPrefab").objectReferenceValue = outputPrefab;
                        dirty = true;
                    }
                    if (dirty)
                        so.ApplyModifiedPropertiesWithoutUndo();
                }
                if (dirty)
                    EditorUtility.SetDirty(asset);
            }
            return asset;
        }

        static Sprite LoadCardSprite(string path)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null)
                return sprite;

            if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.mipmapEnabled = false;
                importer.maxTextureSize = 1024;
                importer.SaveAndReimport();
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
            return sprite;
        }

        static GameObject EnsureBuiltPrefab(string prefabName, string fbxPath, Vector3 fallbackSize)
        {
            string prefabPath = $"{BuiltPrefabsDir}/{prefabName}.prefab";
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);

            var root = new GameObject(prefabName);
            root.layer = GameLayers.Environment;

            if (model != null)
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
                visual.name = "Model";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
            }
            else
            {
                GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fallback.name = "Model";
                fallback.transform.SetParent(root.transform, false);
                fallback.transform.localScale = fallbackSize;
                fallback.transform.localPosition = new Vector3(0f, fallbackSize.y * 0.5f, 0f);
                Object.DestroyImmediate(fallback.GetComponent<Collider>());
            }

            Bounds bounds = ComputeLocalBounds(root, fallbackSize);
            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = bounds.center;
            col.size = bounds.size;

            GameLayers.SetLayerRecursively(root, GameLayers.Environment);
            root.AddComponent<NetworkObject>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            return FinalizeNetworkPrefabHash(saved, prefabPath);
        }

        static GameObject EnsureMachinePrefab(
            string prefabName,
            string fbxPath,
            MachineDefinition def,
            CraftingDatabase db,
            bool isGenerator,
            bool hasHopper)
        {
            string prefabPath = $"{MachinePrefabsDir}/{prefabName}.prefab";
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);

            var root = new GameObject(prefabName);
            root.layer = GameLayers.Interactable;

            if (model != null)
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
                visual.name = "Model";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
            }
            else
            {
                Debug.LogWarning($"[Upcycle City] Missing machine FBX at {fbxPath}; building placeholder cube for {prefabName}.");
                GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fallback.name = "Model";
                fallback.transform.SetParent(root.transform, false);
                fallback.transform.localScale = new Vector3(1.5f, 1.4f, 1.2f);
                fallback.transform.localPosition = new Vector3(0f, 0.7f, 0f);
                Object.DestroyImmediate(fallback.GetComponent<Collider>());
            }

            Transform[] children = root.GetComponentsInChildren<Transform>(includeInactive: true);
            Transform hopperAnchor = null;
            for (int i = 0; i < children.Length; i++)
            {
                string n = children[i].name;
                if (n == "Upgrade_Lvl1" || n.StartsWith("Upgrade_Lvl1."))
                    children[i].gameObject.SetActive(false);
                else if (n == "HopperZone" || n.StartsWith("HopperZone."))
                    hopperAnchor = children[i];
            }

            Bounds bounds = ComputeLocalBounds(root, new Vector3(1.5f, 1.5f, 1.3f));
            BoxCollider bodyCol = root.AddComponent<BoxCollider>();
            bodyCol.center = bounds.center;
            bodyCol.size = new Vector3(
                Mathf.Max(1.0f, bounds.size.x),
                Mathf.Max(1.0f, bounds.size.y),
                Mathf.Max(1.0f, bounds.size.z));

            GameLayers.SetLayerRecursively(root, GameLayers.Interactable);

            if (!isGenerator && hasHopper)
            {
                var triggerGo = new GameObject("HopperTrigger");
                triggerGo.transform.SetParent(root.transform, false);
                triggerGo.transform.localPosition = hopperAnchor != null
                    ? root.transform.InverseTransformPoint(hopperAnchor.position)
                    : new Vector3(0f, bounds.max.y + 0.1f, 0f);

                triggerGo.layer = GameLayers.TriggerZone;
                BoxCollider triggerCol = triggerGo.AddComponent<BoxCollider>();
                triggerCol.isTrigger = true;
                triggerCol.size = new Vector3(1.25f, 0.95f, 1.25f);
                triggerCol.center = new Vector3(0f, 0.18f, 0f);
            }

            root.AddComponent<CraftingMachineVisuals>();
            if (isGenerator)
            {
                ManualEnergyGenerator gen = root.AddComponent<ManualEnergyGenerator>();
                gen.Configure(def, db, nrgLvl0: 15, nrgLvl1: 25, cooldown: 1.25f);
                using (var so = new SerializedObject(gen))
                {
                    so.FindProperty("placementId").stringValue = string.Empty;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            else
            {
                CraftingMachine mach = root.AddComponent<CraftingMachine>();
                mach.Configure(def, db, startLevel: 0, hopperEnabled: hasHopper);
                using (var so = new SerializedObject(mach))
                {
                    so.FindProperty("placementId").stringValue = string.Empty;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // NetworkObject + NetworkMachineIdentity (prefab asset keeps placementId empty so every instance gets a unique ID)
            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkMachineIdentity>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            return FinalizeNetworkPrefabHash(saved, prefabPath);
        }

        /// <summary>
        /// Ensures a dedicated phone-first <see cref="PanelSettings"/> at <see cref="CraftingPanelSettingsPath"/>
        /// using a 1080x608 reference resolution (<see cref="PanelScaleMode.ScaleWithScreenSize"/>, match 0.5,
        /// sortingOrder 55) so crafting text and touch targets reach proper physical sizes on phones without
        /// changing the shared <c>UpcycleCityPanelSettings.asset</c> used by other UIs.
        /// </summary>
        public static PanelSettings EnsureCraftingPanelSettings()
        {
            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(CraftingPanelSettingsPath);
            if (panel == null)
            {
                if (AssetDatabase.LoadAssetAtPath<PanelSettings>(SharedPanelSettingsPath) != null)
                {
                    AssetDatabase.CopyAsset(SharedPanelSettingsPath, CraftingPanelSettingsPath);
                    panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(CraftingPanelSettingsPath);
                }
                if (panel == null)
                {
                    panel = ScriptableObject.CreateInstance<PanelSettings>();
                    AssetDatabase.CreateAsset(panel, CraftingPanelSettingsPath);
                }
            }

            PanelSettings shared = AssetDatabase.LoadAssetAtPath<PanelSettings>(SharedPanelSettingsPath);
            if (shared != null && panel.themeStyleSheet == null)
                panel.themeStyleSheet = shared.themeStyleSheet;

            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1080, 608);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.sortingOrder = 55;
            EditorUtility.SetDirty(panel);
            return panel;
        }

        /// <summary>
        /// Forces Netcode for GameObjects to compute and persist a valid non-zero
        /// <see cref="NetworkObject.GlobalObjectIdHash"/> after saving a prefab programmatically via
        /// <see cref="PrefabUtility.SaveAsPrefabAsset"/> (which otherwise leaves the hash at 0 until
        /// the prefab is manually opened in the Inspector).
        /// </summary>
        static GameObject FinalizeNetworkPrefabHash(GameObject savedPrefab, string prefabPath)
        {
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) ?? savedPrefab;
            if (asset == null)
                return null;

            NetworkObject netObj = asset.GetComponent<NetworkObject>();
            if (netObj == null)
                return asset;

            if (NetworkObjectOnValidateMethod != null)
                NetworkObjectOnValidateMethod.Invoke(netObj, null);

            // Defensive check: verify serialized GlobalObjectIdHash is non-zero; fall back to GlobalObjectId hash if needed
            using (var so = new SerializedObject(netObj))
            {
                SerializedProperty hashProp = so.FindProperty("GlobalObjectIdHash");
                if (hashProp != null && hashProp.longValue == 0)
                {
                    GlobalObjectId gid = GlobalObjectId.GetGlobalObjectIdSlow(netObj);
                    if (gid.identifierType != 0)
                    {
                        uint fallbackHash = ComputeStableHash32(gid.ToString());
                        if (fallbackHash == 0)
                            fallbackHash = 1u;
                        hashProp.longValue = fallbackHash;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }

            EditorUtility.SetDirty(netObj);
            EditorUtility.SetDirty(asset);
            PrefabUtility.SavePrefabAsset(asset);
            return asset;
        }

        static uint ComputeStableHash32(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        static Bounds ComputeLocalBounds(GameObject root, Vector3 fallbackSize)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
            bool hasBounds = false;
            Bounds combined = default;
            Matrix4x4 invRoot = root.transform.worldToLocalMatrix;

            for (int i = 0; i < renderers.Length; i++)
            {
                Bounds wb = renderers[i].bounds;
                if (wb.size.sqrMagnitude < 1e-5f)
                    continue;
                Vector3 center = invRoot.MultiplyPoint3x4(wb.center);
                Vector3 size = wb.size;
                var lb = new Bounds(center, size);
                if (!hasBounds)
                {
                    combined = lb;
                    hasBounds = true;
                }
                else
                {
                    combined.Encapsulate(lb);
                }
            }

            if (!hasBounds)
                return new Bounds(new Vector3(0f, fallbackSize.y * 0.5f, 0f), fallbackSize);

            return combined;
        }

        static MachineUpgradeCost UpgradeCost(int targetLevel, int coins, params MaterialAmount[] mats) => new MachineUpgradeCost
        {
            targetLevel = targetLevel,
            coins = coins,
            materials = mats,
        };

        static T LoadOrCreate<T>(string path, out bool created) where T : ScriptableObject
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                created = false;
                return existing;
            }
            T asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            created = true;
            return asset;
        }

        static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            string[] parts = assetPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
