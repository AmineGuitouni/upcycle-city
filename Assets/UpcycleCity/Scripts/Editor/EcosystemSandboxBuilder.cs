using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UpcycleCity.Core;
using UpcycleCity.Crowd;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Pollution;
using UpcycleCity.Waste;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds the dedicated Phase 2 playable scene (<c>Assets/UpcycleCity/Scenes/EcosystemSandbox.unity</c>),
    /// the data-driven <see cref="WasteCatalog"/> asset, and the Respirator Mask tool so every
    /// cause-and-effect pollution loop can be tested live in Play Mode.
    /// </summary>
    public static class EcosystemSandboxBuilder
    {
        const string ScenePath = "Assets/UpcycleCity/Scenes/EcosystemSandbox.unity";
        const string EcosystemSettingsDir = "Assets/UpcycleCity/Settings/Ecosystem";
        const string CatalogPath = EcosystemSettingsDir + "/WasteCatalog.asset";
        const string PlayerSettingsDir = "Assets/UpcycleCity/Settings/Player";
        const string PlayerPrefabPath = "Assets/UpcycleCity/Prefabs/Player/Player.prefab";

        const string ArtCity = "Assets/UpcycleCity/Art/UpcycleProps/City";
        const string ArtLitter = "Assets/UpcycleCity/Art/UpcycleProps/Litter";
        const string ArtMachines = "Assets/UpcycleCity/Art/UpcycleProps/Machines";
        const string ArtGarage = "Assets/UpcycleCity/Art/UpcycleProps/Garage";
        const string ArtKayKit = "Assets/UpcycleCity/Art/KayKitCityBits";
        const string ArtNature = "Assets/UpcycleCity/Art/QuaterniusNature";
        const string MaterialsDir = "Assets/UpcycleCity/Materials";

        [MenuItem("Upcycle City/Build Phase 2 Ecosystem Sandbox", priority = 15)]
        public static void BuildEcosystemSandbox()
        {
            ProjectLayersAndTagsSetup.EnsureAll(logWhenUnchanged: false);
            EnsureFolder("Assets/UpcycleCity/Scenes");
            EnsureFolder("Assets/UpcycleCity/Settings");
            EnsureFolder(EcosystemSettingsDir);
            EnsureFolder(PlayerSettingsDir);

            // 1. Build/update WasteCatalog & Respirator Mask item
            WasteCatalog catalog = BuildOrUpdateWasteCatalog();
            ItemDefinition respiratorMask = EnsureRespiratorMaskAsset();
            ItemDefinition wrench = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"{PlayerSettingsDir}/Item_Wrench.asset");
            ItemDefinition broom = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"{PlayerSettingsDir}/Item_Broom.asset");

            AssetDatabase.SaveAssets();

            // 2. Fresh scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Load existing PollutionLit & scene materials
            Material matPaving = LoadMat($"{MaterialsDir}/Park_Paving.mat");
            Material matGroundPaving = LoadMat($"{MaterialsDir}/Ground_Paving.mat");
            Material matLawn = LoadMat($"{MaterialsDir}/Ground_Lawn.mat");
            Material matPath = LoadMat($"{MaterialsDir}/Park_Path.mat");
            Material matMud = LoadMat($"{MaterialsDir}/Polluted_Mud.mat");
            Material matOil = LoadMat($"{MaterialsDir}/Polluted_OilPuddle.mat");
            Material matSky = LoadMat($"{MaterialsDir}/Sky_Pollution.mat");
            Material matSmoke = LoadMat($"{MaterialsDir}/ChimneySmoke.mat");
            Material matDark = CreateRuntimeMaterial("Eco_DarkSteel", new Color(0.20f, 0.22f, 0.24f), 0.28f);
            Material matRust = CreateRuntimeMaterial("Eco_IndustrialRust", new Color(0.56f, 0.29f, 0.18f), 0.2f);
            Material matChimney = CreateRuntimeMaterial("Eco_ChimneyBrick", new Color(0.45f, 0.24f, 0.18f), 0.12f);
            Material matIndicator = CreateRuntimeMaterial("Eco_Indicator", Color.white, 0.35f);
            Material matYellow = CreateRuntimeMaterial("Eco_Yellow", new Color(0.95f, 0.72f, 0.12f), 0.3f);
            Material matTeal = CreateRuntimeMaterial("Eco_Teal", new Color(0.16f, 0.62f, 0.68f), 0.3f);

            // 3. Lighting, Post-Processing Volumes & PollutionController
            var systemsRoot = new GameObject("Systems");

            var sunGo = new GameObject("Directional Sun");
            sunGo.transform.SetParent(systemsRoot.transform);
            sunGo.transform.rotation = Quaternion.Euler(42f, 125f, 0f);
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.Soft;

            if (matSky != null)
                RenderSettings.skybox = matSky;

            VolumeProfile cityGrade = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/UpcycleCity/Settings/CityGrade.asset");
            if (cityGrade != null)
            {
                var gradeGo = new GameObject("GlobalVolume_CityGrade");
                gradeGo.transform.SetParent(systemsRoot.transform);
                Volume gradeVol = gradeGo.AddComponent<Volume>();
                gradeVol.isGlobal = true;
                gradeVol.priority = 0f;
                gradeVol.weight = 1f;
                gradeVol.sharedProfile = cityGrade;
            }

            Volume smogVolume = null;
            VolumeProfile smogGrade = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/UpcycleCity/Settings/SmogGrade.asset");
            if (smogGrade != null)
            {
                var smogGo = new GameObject("GlobalVolume_SmogGrade");
                smogGo.transform.SetParent(systemsRoot.transform);
                smogVolume = smogGo.AddComponent<Volume>();
                smogVolume.isGlobal = true;
                smogVolume.priority = 1f;
                smogVolume.weight = 0.6f;
                smogVolume.sharedProfile = smogGrade;
            }

            var pollutionGo = new GameObject("PollutionController");
            pollutionGo.transform.SetParent(systemsRoot.transform);
            PollutionController pollutionController = pollutionGo.AddComponent<PollutionController>();
            PollutionProfile profile = AssetDatabase.LoadAssetAtPath<PollutionProfile>("Assets/UpcycleCity/Settings/PollutionProfile.asset");
            using (var so = new SerializedObject(pollutionController))
            {
                so.FindProperty("pollution").floatValue = 0.65f;
                so.FindProperty("transitionTime").floatValue = 2.5f;
                so.FindProperty("profile").objectReferenceValue = profile;
                so.FindProperty("sun").objectReferenceValue = sun;
                so.FindProperty("smogVolume").objectReferenceValue = smogVolume;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // CityEcosystemManager + EcosystemHUD
            var ecoGo = new GameObject("CityEcosystemManager");
            ecoGo.transform.SetParent(systemsRoot.transform);
            CityEcosystemManager ecoManager = ecoGo.AddComponent<CityEcosystemManager>();
            ecoManager.Configure(catalog, globalTrashCap: 70);
            ecoGo.AddComponent<EcosystemHUD>();

            // CrowdSystem
            var crowdSysGo = new GameObject("CrowdSystem");
            crowdSysGo.transform.SetParent(systemsRoot.transform);
            CrowdSystem crowdSystem = crowdSysGo.AddComponent<CrowdSystem>();
            RuntimeAnimatorController crowdAnim = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/UpcycleCity/Animation/CrowdAnimator.controller");
            using (var so = new SerializedObject(crowdSystem))
            {
                so.FindProperty("animatorController").objectReferenceValue = crowdAnim;
                so.FindProperty("animatedBudget").intValue = 36;
                so.FindProperty("animateDistance").floatValue = 90f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // 4. World Ground & Perimeter
            var worldRoot = new GameObject("World_Geometry");

            // Outer green lawn terrain (uses PollutionLit so grass visibly turns brown when dirty & lush when clean!)
            CreateEnvironmentBox("Ground_LawnOuter", worldRoot.transform, new Vector3(4f, -0.52f, 0f), new Vector3(110f, 1f, 84f), matLawn);
            // West Market Plaza paving
            CreateEnvironmentBox("Ground_MarketPlaza", worldRoot.transform, new Vector3(-10f, -0.49f, 0f), new Vector3(34f, 1f, 34f), matPaving);
            // Connecting Avenue between Market Plaza & Factory Yard
            CreateEnvironmentBox("Ground_ConnectingAvenue", worldRoot.transform, new Vector3(5f, -0.485f, 0f), new Vector3(18f, 1f, 10f), matPath);
            // East Factory Yard asphalt
            CreateEnvironmentBox("Ground_FactoryYard", worldRoot.transform, new Vector3(21f, -0.49f, 0f), new Vector3(30f, 1f, 32f), matGroundPaving);
            // Decorative oil/mud decals in Factory Yard & Decay Alley (dissolve away when clean!)
            GameObject oilPuddle = CreateEnvironmentBox("Polluted_OilStain_Factory", worldRoot.transform, new Vector3(18f, 0.015f, 3.5f), new Vector3(5.5f, 0.02f, 4.2f), matOil, withCollider: false);
            oilPuddle.AddComponent<PollutionVisibility>().Mode = PollutionVisibilityMode.WhenPolluted;
            GameObject mudPatch = CreateEnvironmentBox("Polluted_Mud_Alley", worldRoot.transform, new Vector3(-19f, 0.015f, 11.5f), new Vector3(6.0f, 0.02f, 5.5f), matMud, withCollider: false);
            mudPatch.AddComponent<PollutionVisibility>().Mode = PollutionVisibilityMode.WhenPolluted;

            // Low perimeter curbs so player stays inside the playable sandbox
            CreateEnvironmentBox("Curb_North", worldRoot.transform, new Vector3(4f, 0.9f, 25f), new Vector3(78f, 1.8f, 0.6f), matDark);
            CreateEnvironmentBox("Curb_South", worldRoot.transform, new Vector3(4f, 0.9f, -25f), new Vector3(78f, 1.8f, 0.6f), matDark);
            CreateEnvironmentBox("Curb_West", worldRoot.transform, new Vector3(-35f, 0.9f, 0f), new Vector3(0.6f, 1.8f, 50f), matDark);
            CreateEnvironmentBox("Curb_East", worldRoot.transform, new Vector3(43f, 0.9f, 0f), new Vector3(0.6f, 1.8f, 50f), matDark);

            // 5. DISTRICT A — MARKET PLAZA (West/Center)
            var marketRoot = new GameObject("District_A_MarketPlaza");
            marketRoot.transform.position = new Vector3(-10f, 0f, 2f);
            CityDistrict marketDistrict = marketRoot.AddComponent<CityDistrict>();
            marketDistrict.Configure("Market Plaza", radius: 22f, falloff: 7f, capacity: 14f, weight: 1.1f);
            marketDistrict.GetComponent<PollutionZone>().Pollution = 0.72f;

            // Plaza centerpiece: single stone fountain whose grime washes off naturally + real water jets/basin driven by FountainWaterResponder
            GameObject fountain = PlaceStaticProp($"{ArtCity}/fountain_dry.fbx", "Plaza_Fountain", marketRoot.transform, new Vector3(-10f, 0f, 2f), Quaternion.identity, 1.05f);
            if (fountain != null)
            {
                Material waterMat = CreateRuntimeMaterial("Eco_FountainWater", new Color(0.28f, 0.72f, 0.88f), 0.88f);

                // Smoothly rising water surface inside the basin
                GameObject waterDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                waterDisc.name = "WaterBasinSurface";
                waterDisc.transform.SetParent(fountain.transform, false);
                waterDisc.transform.localPosition = new Vector3(0f, 0.52f, 0f);
                waterDisc.transform.localScale = new Vector3(2.9f, 0.06f, 2.9f);
                Object.DestroyImmediate(waterDisc.GetComponent<Collider>());
                waterDisc.GetComponent<Renderer>().sharedMaterial = waterMat;

                // Gentle natural water spray jet from the top spout
                var sprayGo = new GameObject("FountainWaterSpray");
                sprayGo.transform.SetParent(fountain.transform, false);
                sprayGo.transform.localPosition = new Vector3(0f, 1.95f, 0f);
                ParticleSystem waterPs = sprayGo.AddComponent<ParticleSystem>();
                var wMain = waterPs.main;
                wMain.startLifetime = new ParticleSystem.MinMaxCurve(0.85f, 1.25f);
                wMain.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 2.2f);
                wMain.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.16f);
                wMain.startColor = new Color(0.75f, 0.93f, 1f, 0.72f);
                wMain.gravityModifier = 0.65f;
                wMain.maxParticles = 90;

                var wEmit = waterPs.emission;
                wEmit.rateOverTime = 26f;

                var wShape = waterPs.shape;
                wShape.shapeType = ParticleSystemShapeType.Cone;
                wShape.angle = 22f;
                wShape.radius = 0.08f;
                wShape.rotation = new Vector3(-90f, 0f, 0f);

                var wRend = sprayGo.GetComponent<ParticleSystemRenderer>();
                wRend.sharedMaterial = waterMat;

                FountainWaterResponder waterResponder = fountain.AddComponent<FountainWaterResponder>();
                waterResponder.Configure(waterPs, waterDisc.transform, threshold: 0.62f, maxRate: 28f);
                fountain.AddComponent<CitizenPointOfInterest>().Configure(CitizenPoiKind.Fountain, "Plaza Fountain", 6.0f);
            }

            // Human-scale KayKit City Buildings bordering Market Plaza (automatically PollutionLit, no oversized bases!)
            GameObject shopNW = PlaceStaticProp($"{ArtKayKit}/building_A_withoutBase.fbx", "Shop_NorthWest", marketRoot.transform, new Vector3(-22f, 0f, 16f), Quaternion.Euler(0f, 180f, 0f), 1.45f);
            GameObject shopN = PlaceStaticProp($"{ArtKayKit}/building_B_withoutBase.fbx", "Shop_North", marketRoot.transform, new Vector3(-10f, 0f, 17.5f), Quaternion.Euler(0f, 180f, 0f), 1.45f);
            GameObject cafeW = PlaceStaticProp($"{ArtKayKit}/building_C_withoutBase.fbx", "Cafe_West", marketRoot.transform, new Vector3(-25f, 0f, 4f), Quaternion.Euler(0f, 90f, 0f), 1.45f);
            GameObject bakerySW = PlaceStaticProp($"{ArtKayKit}/building_D_withoutBase.fbx", "Bakery_SouthWest", marketRoot.transform, new Vector3(-24f, 0f, -10f), Quaternion.Euler(0f, 90f, 0f), 1.45f);
            PlaceStaticProp($"{ArtKayKit}/building_E_withoutBase.fbx", "Apartments_NorthEast", marketRoot.transform, new Vector3(0f, 0f, 17.5f), Quaternion.Euler(0f, 180f, 0f), 1.4f);

            if (shopNW != null) shopNW.AddComponent<CitizenPointOfInterest>().Configure(CitizenPoiKind.ShopWindow, "Corner Market", 8.5f);
            if (shopN != null) shopN.AddComponent<CitizenPointOfInterest>().Configure(CitizenPoiKind.ShopWindow, "Main Bakery", 8.5f);
            if (cafeW != null) cafeW.AddComponent<CitizenPointOfInterest>().Configure(CitizenPoiKind.Cafe, "Street Cafe", 8.5f);
            if (bakerySW != null) bakerySW.AddComponent<CitizenPointOfInterest>().Configure(CitizenPoiKind.ShopWindow, "Bookshop", 8.5f);

            // Living Trees & Planters — PollutionLit smoothly shifts their foliage from dry brown to lush green with zero popping!
            Vector3[] treePositions =
            {
                new Vector3(-17f, 0f, 9f),
                new Vector3(-17f, 0f, -5f),
                new Vector3(-3f, 0f, 10f),
                new Vector3(-12f, 0f, -11f),
                new Vector3(-21f, 0f, -2f),
            };
            for (int i = 0; i < treePositions.Length; i++)
                PlaceStaticProp($"{ArtNature}/CommonTree_{(i % 3) + 1}.fbx", $"Tree_Living_{i}", marketRoot.transform, treePositions[i], Quaternion.Euler(0f, i * 70f, 0f), 1.25f);

            Vector3[] planterPositions =
            {
                new Vector3(-13.8f, 0f, 5.5f),
                new Vector3(-6.2f, 0f, 5.5f),
                new Vector3(-13.8f, 0f, -1.5f),
                new Vector3(-6.2f, 0f, -1.5f),
            };
            for (int i = 0; i < planterPositions.Length; i++)
                PlaceStaticProp($"{ArtCity}/flower_planter.fbx", $"Planter_Living_{i}", marketRoot.transform, planterPositions[i], Quaternion.identity, 1.0f);

            GameObject benchW = PlaceStaticProp($"{ArtCity}/park_bench.fbx", "Bench_West", marketRoot.transform, new Vector3(-15.5f, 0f, 2f), Quaternion.Euler(0f, 90f, 0f), 1.0f);
            GameObject benchN = PlaceStaticProp($"{ArtCity}/park_bench.fbx", "Bench_North", marketRoot.transform, new Vector3(-10f, 0f, 8.2f), Quaternion.Euler(0f, 180f, 0f), 1.0f);
            if (benchW != null) benchW.AddComponent<CitizenPointOfInterest>().Configure(CitizenPoiKind.Bench, "West Bench", 4.5f);
            if (benchN != null) benchN.AddComponent<CitizenPointOfInterest>().Configure(CitizenPoiKind.Bench, "North Bench", 4.5f);

            // CAUSE #1: Solid, physical walking Citizens (Tier 1 visible + Tier 2 batch background population!)
            var crowdRoot = new GameObject("Citizens_MarketPlaza");
            crowdRoot.transform.SetParent(marketRoot.transform, false);

            var plazaPathGo = new GameObject("Path_MarketPlazaLoop");
            plazaPathGo.transform.SetParent(crowdRoot.transform, false);
            CrowdPath plazaPath = plazaPathGo.AddComponent<CrowdPath>();
            plazaPath.SetPoints(new[]
            {
                new Vector3(-18.5f, 0f, 10.5f),
                new Vector3(-2.0f, 0f, 10.5f),
                new Vector3(-2.0f, 0f, -5.5f),
                new Vector3(-18.5f, 0f, -5.5f),
            }, isLoop: true);

            CrowdCharacterLibrary charLib = AssetDatabase.LoadAssetAtPath<CrowdCharacterLibrary>("Assets/UpcycleCity/Settings/CrowdCharacters.asset");

            // Always-solid walking residents (personalities & activities driven by CitizenSimulationManager)
            var activeWalkersGo = new GameObject("Spawner_ActiveCitizens");
            activeWalkersGo.transform.SetParent(crowdRoot.transform, false);
            ConfigureCrowdSpawner(activeWalkersGo.AddComponent<CrowdSpawner>(), charLib, new[] { plazaPath }, count: 14, masked: false, visibility: PollutionVisibilityMode.Always, seed: 11);

            // CAUSE #2: Two Street Bins (OverflowingBinSource) — North Bin starts overflowing right away!
            CreateStreetBin(marketRoot.transform, new Vector3(-5.0f, 0f, 8.8f), Quaternion.Euler(0f, 180f, 0f),
                "bin_plaza_north", "North Plaza Bin", marketDistrict, catalog, startingFill: 1.0f, matIndicator);
            CreateStreetBin(marketRoot.transform, new Vector3(-17.8f, 0f, -4.8f), Quaternion.Euler(0f, 35f, 0f),
                "bin_plaza_south", "Cafe Corner Bin", marketDistrict, catalog, startingFill: 0.72f, matIndicator);

            // CAUSE #4: Urban Decay Alley Corner (UrbanDecaySource)
            var decayGo = new GameObject("Source_UrbanDecay_Alley");
            decayGo.transform.SetParent(marketRoot.transform, false);
            decayGo.transform.position = new Vector3(-18.5f, 0f, 12.2f);
            PlaceStaticProp($"{ArtCity}/park_bench_old.fbx", "DecayedBench", decayGo.transform, new Vector3(-20.4f, 0f, 12.8f), Quaternion.Euler(0f, 140f, 0f), 1.0f);
            PlaceStaticProp($"{ArtCity}/pickup_rusty.fbx", "AbandonedPickup", decayGo.transform, new Vector3(-16.2f, 0f, 13.8f), Quaternion.Euler(0f, -110f, 0f), 0.95f);
            UrbanDecaySource decaySource = decayGo.AddComponent<UrbanDecaySource>();
            decaySource.Configure("alley_decay", "Neglected North Alley", marketDistrict, catalog, threshold: 0.42f, interval: 16f, radius: 2.5f, maxTrash: 6);

            // 6. DISTRICT B — INDUSTRIAL FACTORY YARD (East)
            var factoryRoot = new GameObject("District_B_FactoryYard");
            factoryRoot.transform.position = new Vector3(20f, 0f, 2f);
            CityDistrict factoryDistrict = factoryRoot.AddComponent<CityDistrict>();
            factoryDistrict.Configure("Factory Yard", radius: 22f, falloff: 7f, capacity: 20f, weight: 1.0f);
            factoryDistrict.GetComponent<PollutionZone>().Pollution = 0.92f;

            // Industrial buildings (human scale, clean footprint)
            PlaceStaticProp($"{ArtMachines}/workshop_shed.fbx", "Factory_MainHall", factoryRoot.transform, new Vector3(25.5f, 0f, 7.5f), Quaternion.Euler(0f, -90f, 0f), 1.15f);
            PlaceStaticProp($"{ArtKayKit}/building_F_withoutBase.fbx", "Factory_WarehouseSouth", factoryRoot.transform, new Vector3(26f, 0f, -10f), Quaternion.Euler(0f, -90f, 0f), 1.45f);
            PlaceStaticProp($"{ArtKayKit}/building_G_withoutBase.fbx", "Factory_OfficeNorth", factoryRoot.transform, new Vector3(15f, 0f, 17f), Quaternion.Euler(0f, 180f, 0f), 1.4f);
            PlaceStaticProp($"{ArtKayKit}/watertower.fbx", "Factory_WaterTower", factoryRoot.transform, new Vector3(31f, 0f, -1f), Quaternion.identity, 1.55f);
            PlaceStaticProp($"{ArtCity}/solar_array.fbx", "Factory_SolarArray", factoryRoot.transform, new Vector3(13f, 0f, -11f), Quaternion.Euler(0f, 35f, 0f), 1.15f);

            // CAUSE #3: Industrial Smokestack + Scrubber Repair Panel (FactorySmokestackSource)
            CreateFactorySmokestack(factoryRoot.transform, new Vector3(19.5f, 0f, 4.0f), factoryDistrict, catalog, matChimney, matDark, matIndicator, matSmoke);

            // Second overflowing industrial dumpster in Factory Yard
            CreateStreetBin(factoryRoot.transform, new Vector3(13.5f, 0f, -5.5f), Quaternion.Euler(0f, -45f, 0f),
                "bin_factory_yard", "Factory Yard Dumpster", factoryDistrict, catalog, startingFill: 1.0f, matIndicator);

            // Factory yard walking workers (always solid!)
            var factoryPathGo = new GameObject("Path_FactoryPatrol");
            factoryPathGo.transform.SetParent(factoryRoot.transform, false);
            CrowdPath factoryPath = factoryPathGo.AddComponent<CrowdPath>();
            factoryPath.SetPoints(new[]
            {
                new Vector3(10.5f, 0f, -4f),
                new Vector3(10.5f, 0f, 9f),
                new Vector3(16.5f, 0f, 9f),
                new Vector3(16.5f, 0f, -4f),
            }, isLoop: true);
            var factoryCrowdGo = new GameObject("Spawner_FactoryWorkers");
            factoryCrowdGo.transform.SetParent(factoryRoot.transform, false);
            ConfigureCrowdSpawner(factoryCrowdGo.AddComponent<CrowdSpawner>(), charLib, new[] { factoryPath }, count: 6, masked: true, visibility: PollutionVisibilityMode.Always, seed: 44);

            // 2-Tier Hybrid LOD Citizen Simulation Manager (Tier 1 visible CrowdAgents + 240 Tier 2 background batch citizens!)
            var citizenSimGo = new GameObject("CitizenSimulationManager");
            citizenSimGo.transform.SetParent(systemsRoot.transform, false);
            CitizenSimulationManager citizenSim = citizenSimGo.AddComponent<CitizenSimulationManager>();
            citizenSim.Configure(
                id: "city_citizens_hybrid",
                label: "City Residents",
                containers: new[] { activeWalkersGo.transform, factoryCrowdGo.transform },
                primaryDistrict: marketDistrict,
                catalog: catalog,
                bgPopulation: 240,
                maxPhysicalTrash: 18);

            // 7. CENTRAL DROP-OFF SORTING HUB + TOOL WORKBENCH (right ahead of player spawn!)
            var hubRoot = new GameObject("Central_UpcycleHub");

            // 4-Stream Wheelie Sorting Station + Bulky Skip Bin on the South-Center plaza edge
            PlaceStaticProp($"{ArtMachines}/sign_dropoff.fbx", "Sign_DropOff", hubRoot.transform, new Vector3(-2.5f, 0f, -11.5f), Quaternion.identity, 1.0f);
            CreateSortingBin(hubRoot.transform, new Vector3(-5.2f, 0f, -10.0f), WasteStream.Paper, $"{ArtMachines}/wheelie_paper.fbx");
            CreateSortingBin(hubRoot.transform, new Vector3(-3.4f, 0f, -10.0f), WasteStream.Plastic, $"{ArtMachines}/wheelie_plastic.fbx");
            CreateSortingBin(hubRoot.transform, new Vector3(-1.6f, 0f, -10.0f), WasteStream.Glass, $"{ArtMachines}/wheelie_glass.fbx");
            CreateSortingBin(hubRoot.transform, new Vector3(0.2f, 0f, -10.0f), WasteStream.Cans, $"{ArtMachines}/wheelie_cans.fbx");
            CreateSortingBin(hubRoot.transform, new Vector3(3.2f, 0f, -10.3f), WasteStream.BulkyUpcyclable, $"{ArtMachines}/skip_bin.fbx", triggerSize: new Vector3(2.4f, 1.5f, 2.2f));

            // Tool Supply Table right beside spawn so player can grab Wrench, Broom & Respirator Mask immediately
            PlaceStaticProp($"{ArtGarage}/workbench.fbx", "Starter_ToolBench", hubRoot.transform, new Vector3(5.8f, 0f, -4.2f), Quaternion.Euler(0f, -90f, 0f), 1.0f);
            CreatePickupPedestal(hubRoot.transform, new Vector3(4.5f, 0f, -6.0f), "Pickup_Wrench", wrench, matTeal, matDark);
            CreatePickupPedestal(hubRoot.transform, new Vector3(4.5f, 0f, -4.2f), "Pickup_Broom", broom, matRust, matDark);
            CreatePickupPedestal(hubRoot.transform, new Vector3(4.5f, 0f, -2.4f), "Pickup_RespiratorMask", respiratorMask, matYellow, matDark);

            // 8. Seed initial physical trash attributed to real causes (zero magic spawning!)
            var seededTrashRoot = new GameObject("Seeded_CauseTrash");
            SeedCauseTrash(catalog, seededTrashRoot.transform, WasteSourceKind.Citizen, new Vector3(-9.5f, 0.45f, -2.8f), 5, 3.6f);
            SeedCauseTrash(catalog, seededTrashRoot.transform, WasteSourceKind.ShopOverflow, new Vector3(-5.0f, 0.45f, 7.2f), 4, 1.9f);
            SeedCauseTrash(catalog, seededTrashRoot.transform, WasteSourceKind.UrbanDecay, new Vector3(-18.5f, 0.65f, 11.4f), 2, 1.8f);
            SeedCauseTrash(catalog, seededTrashRoot.transform, WasteSourceKind.FactoryScrap, new Vector3(16.8f, 0.50f, 2.6f), 5, 3.2f);
            SeedCauseTrash(catalog, seededTrashRoot.transform, WasteSourceKind.ShopOverflow, new Vector3(12.6f, 0.45f, -4.6f), 3, 1.5f);

            // 9. Instantiate reusable Player.prefab with clear vista of Market Plaza, Tools & Factory Smokestack
            GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (playerPrefab == null)
            {
                Debug.LogWarning("[Upcycle City] Player.prefab not found — building Phase 1 Gym first to generate Player.prefab.");
                PlayerGymBuilder.BuildPlayerGym();
                playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            }

            if (playerPrefab != null)
            {
                GameObject playerGo = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
                playerGo.transform.position = new Vector3(0.5f, 0.1f, -6.2f);
                playerGo.transform.rotation = Quaternion.Euler(0f, -15f, 0f);

                // Customize HUD badge & disable duplicate right-hand controls box so EcosystemHUD sits cleanly
                PlayerGymHUD hud = playerGo.GetComponent<PlayerGymHUD>();
                if (hud != null)
                {
                    using (var so = new SerializedObject(hud))
                    {
                        so.FindProperty("stageTitle").stringValue = "PHASE 2 · ECOSYSTEM";
                        so.FindProperty("showControlsLegend").boolValue = false;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }

            // 10. Save scene & add to Build Settings
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[Upcycle City] Built Phase 2 Living Ecosystem Sandbox scene at {ScenePath}");
        }

        static WasteCatalog BuildOrUpdateWasteCatalog()
        {
            WasteCatalog catalog = AssetDatabase.LoadAssetAtPath<WasteCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<WasteCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            var entries = new List<WasteCatalog.Entry>
            {
                // Citizen litter (light, tossed while walking)
                MakeCatalogEntry("Paper Cup", WasteStream.Paper, WasteSourceFlags.Citizen | WasteSourceFlags.ShopOverflow, $"{ArtLitter}/cup_paper.fbx", 1.15f, 0.9f, 5, 0.25f),
                MakeCatalogEntry("Red Coffee Cup", WasteStream.Paper, WasteSourceFlags.Citizen, $"{ArtLitter}/cup_paper_red.fbx", 1.15f, 0.9f, 5, 0.25f),
                MakeCatalogEntry("Newspaper", WasteStream.Paper, WasteSourceFlags.Citizen | WasteSourceFlags.ShopOverflow, $"{ArtLitter}/newspaper.fbx", 1.05f, 1.0f, 6, 0.35f),
                MakeCatalogEntry("Snack Wrapper", WasteStream.Plastic, WasteSourceFlags.Citizen, $"{ArtLitter}/wrapper_yellow.fbx", 1.25f, 0.85f, 5, 0.20f),
                MakeCatalogEntry("Candy Wrapper", WasteStream.Plastic, WasteSourceFlags.Citizen, $"{ArtLitter}/wrapper_red.fbx", 1.25f, 0.85f, 5, 0.20f),
                MakeCatalogEntry("Soda Can", WasteStream.Cans, WasteSourceFlags.Citizen | WasteSourceFlags.ShopOverflow, $"{ArtLitter}/can.fbx", 1.2f, 1.0f, 7, 0.30f),

                // Shop / Street Bin Overflow
                MakeCatalogEntry("Plastic Bottle", WasteStream.Plastic, WasteSourceFlags.ShopOverflow | WasteSourceFlags.Citizen, $"{ArtLitter}/bottle_blue.fbx", 1.15f, 1.1f, 6, 0.35f),
                MakeCatalogEntry("2L Plastic Jug", WasteStream.Plastic, WasteSourceFlags.ShopOverflow, $"{ArtLitter}/bottle_2l.fbx", 1.05f, 1.3f, 8, 0.55f),
                MakeCatalogEntry("Trash Bag", WasteStream.Plastic, WasteSourceFlags.ShopOverflow, $"{ArtLitter}/bag_black.fbx", 1.0f, 1.6f, 9, 1.2f),
                MakeCatalogEntry("Pizza Box", WasteStream.Paper, WasteSourceFlags.ShopOverflow, $"{ArtLitter}/pizza_box.fbx", 1.0f, 1.2f, 7, 0.45f),
                MakeCatalogEntry("Takeaway Box", WasteStream.Paper, WasteSourceFlags.ShopOverflow, $"{ArtLitter}/takeaway_box.fbx", 1.1f, 1.0f, 6, 0.35f),
                MakeCatalogEntry("Green Glass Bottle", WasteStream.Glass, WasteSourceFlags.ShopOverflow | WasteSourceFlags.Citizen, $"{ArtLitter}/bottle_glass_green.fbx", 1.15f, 1.15f, 8, 0.65f),
                MakeCatalogEntry("Brown Glass Bottle", WasteStream.Glass, WasteSourceFlags.ShopOverflow, $"{ArtLitter}/bottle_glass_brown.fbx", 1.15f, 1.15f, 8, 0.65f),

                // Industrial Factory Scrap
                MakeCatalogEntry("Crushed Can", WasteStream.Cans, WasteSourceFlags.FactoryScrap, $"{ArtLitter}/can_crushed.fbx", 1.25f, 1.25f, 8, 0.45f),
                MakeCatalogEntry("Scrap Pipe", WasteStream.Cans, WasteSourceFlags.FactoryScrap, $"{ArtLitter}/pipe.fbx", 1.0f, 1.65f, 11, 2.2f),
                MakeCatalogEntry("Shipping Carton", WasteStream.Paper, WasteSourceFlags.FactoryScrap | WasteSourceFlags.ShopOverflow, $"{ArtLitter}/cardboard_box.fbx", 1.0f, 1.4f, 8, 0.85f),
                MakeCatalogEntry("Industrial Bottle", WasteStream.Glass, WasteSourceFlags.FactoryScrap, $"{ArtLitter}/bottle_clear.fbx", 1.15f, 1.2f, 9, 0.7f),

                // Urban Decay (Bulky objects)
                MakeCatalogEntry("Old Tyre", WasteStream.BulkyUpcyclable, WasteSourceFlags.UrbanDecay, $"{ArtLitter}/tyre.fbx", 1.0f, 2.6f, 18, 7.5f, bulky: true, carrySpeed: 0.72f, holdOffset: 0.42f),
                MakeCatalogEntry("Broken TV", WasteStream.BulkyUpcyclable, WasteSourceFlags.UrbanDecay, $"{ArtLitter}/tv.fbx", 0.95f, 2.8f, 22, 8.5f, bulky: true, carrySpeed: 0.68f, holdOffset: 0.48f),
                MakeCatalogEntry("Old Washer", WasteStream.BulkyUpcyclable, WasteSourceFlags.UrbanDecay, $"{ArtLitter}/washer.fbx", 0.85f, 3.0f, 25, 9.5f, bulky: true, carrySpeed: 0.65f, holdOffset: 0.55f),
            };

            catalog.SetEntries(entries);
            EditorUtility.SetDirty(catalog);
            return catalog;
        }

        static WasteCatalog.Entry MakeCatalogEntry(
            string name,
            WasteStream stream,
            WasteSourceFlags flags,
            string fbxPath,
            float scale,
            float weight,
            int coins,
            float mass,
            bool bulky = false,
            float carrySpeed = 1f,
            float holdOffset = 0f)
        {
            return new WasteCatalog.Entry
            {
                displayName = name,
                stream = stream,
                allowedSources = flags,
                visualPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath),
                visualScale = scale,
                pollutionWeight = weight,
                baseCoinValue = coins,
                mass = mass,
                isBulky = bulky,
                carrySpeedMultiplier = carrySpeed,
                holdDistanceOffset = holdOffset,
            };
        }

        static ItemDefinition EnsureRespiratorMaskAsset()
        {
            const string path = PlayerSettingsDir + "/Item_RespiratorMask.asset";
            ItemDefinition item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (item == null)
            {
                item = ScriptableObject.CreateInstance<ItemDefinition>();
                AssetDatabase.CreateAsset(item, path);
            }

            item.Configure(
                id: "tool_respirator",
                label: "Respirator Mask",
                desc: "Filters toxic factory smog while equipped, preventing smog movement slowdown.",
                itemCategory: ItemCategory.Tool,
                color: new Color(0.16f, 0.68f, 0.72f),
                stackLimit: 1,
                consume: false,
                reachBonus: 0f,
                throwBonus: 0f,
                speedMultiplier: 1f,
                smogImmune: true,
                behavior: null);

            EditorUtility.SetDirty(item);
            return item;
        }

        static void CreateStreetBin(
            Transform parent,
            Vector3 position,
            Quaternion rotation,
            string id,
            string label,
            CityDistrict district,
            WasteCatalog catalog,
            float startingFill,
            Material indicatorMat)
        {
            var binGo = new GameObject($"StreetBin_{id}");
            binGo.transform.SetParent(parent, false);
            binGo.transform.SetPositionAndRotation(position, rotation);

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>($"{ArtCity}/street_bins.fbx");
            if (model != null)
            {
                GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(model, binGo.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
            }
            else
            {
                GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fallback.transform.SetParent(binGo.transform, false);
                fallback.transform.localPosition = new Vector3(0f, 0.55f, 0f);
                fallback.transform.localScale = new Vector3(0.9f, 1.1f, 0.7f);
            }

            // Solid interaction collider
            BoxCollider col = binGo.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 0.65f, 0f);
            col.size = new Vector3(1.55f, 1.3f, 0.95f);

            // Spill anchor slightly in front and above bin top
            var spillAnchor = new GameObject("SpillAnchor").transform;
            spillAnchor.SetParent(binGo.transform, false);
            spillAnchor.localPosition = new Vector3(0f, 1.25f, 0.65f);

            // Visual fill-level status pole & rising bar
            var gaugeBase = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gaugeBase.name = "FillGauge_Bar";
            gaugeBase.transform.SetParent(binGo.transform, false);
            gaugeBase.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            gaugeBase.transform.localScale = new Vector3(0.28f, 0.75f, 0.28f);
            Object.DestroyImmediate(gaugeBase.GetComponent<Collider>());
            Renderer indicator = gaugeBase.GetComponent<Renderer>();
            indicator.sharedMaterial = indicatorMat;

            OverflowingBinSource source = binGo.AddComponent<OverflowingBinSource>();
            source.Configure(id, label, district, catalog, startingFill, spillAnchor, indicator, gaugeBase.transform);

            binGo.AddComponent<FocusHighlight>();
            GameLayers.SetLayerRecursively(binGo, GameLayers.Interactable);
            binGo.tag = GameTags.PollutionSource;
        }

        static void CreateFactorySmokestack(
            Transform parent,
            Vector3 position,
            CityDistrict district,
            WasteCatalog catalog,
            Material chimneyMat,
            Material steelMat,
            Material beaconMat,
            Material smokeMat)
        {
            var root = new GameObject("Factory_Smokestack_Complex");
            root.transform.SetParent(parent, false);
            root.transform.position = position;

            // Brick/steel chimney tower (Environment layer)
            GameObject baseBlock = CreateEnvironmentBox("Chimney_Base", root.transform, position + new Vector3(0f, 1.2f, 0f), new Vector3(2.8f, 2.4f, 2.8f), steelMat);
            baseBlock.transform.SetParent(root.transform, true);

            GameObject stackCyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stackCyl.name = "Chimney_Stack";
            stackCyl.transform.SetParent(root.transform, false);
            stackCyl.transform.localPosition = new Vector3(0f, 6.8f, 0f);
            stackCyl.transform.localScale = new Vector3(1.65f, 5.2f, 1.65f);
            stackCyl.GetComponent<Renderer>().sharedMaterial = chimneyMat;
            GameLayers.SetLayerRecursively(stackCyl, GameLayers.Environment);
            stackCyl.tag = GameTags.Environment;

            GameObject rim = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            rim.name = "Chimney_Rim";
            rim.transform.SetParent(root.transform, false);
            rim.transform.localPosition = new Vector3(0f, 11.9f, 0f);
            rim.transform.localScale = new Vector3(2.05f, 0.35f, 2.05f);
            rim.GetComponent<Renderer>().sharedMaterial = steelMat;
            GameLayers.SetLayerRecursively(rim, GameLayers.Environment);

            // Billowing chimney smoke ParticleSystem
            var smokeGo = new GameObject("Chimney_SmokeFX");
            smokeGo.transform.SetParent(root.transform, false);
            smokeGo.transform.localPosition = new Vector3(0f, 12.2f, 0f);
            ParticleSystem ps = smokeGo.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4.5f, 6.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.2f, 3.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(2.4f, 4.6f);
            main.startColor = new Color(0.18f, 0.17f, 0.15f, 0.85f);
            main.gravityModifier = -0.06f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 140;

            var emission = ps.emission;
            emission.rateOverTime = 16f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.7f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2.2f));

            var psRenderer = smokeGo.GetComponent<ParticleSystemRenderer>();
            if (smokeMat != null)
                psRenderer.sharedMaterial = smokeMat;

            // Scrap chute anchor pointing toward the yard
            var chute = new GameObject("ScrapChutePoint").transform;
            chute.SetParent(root.transform, false);
            chute.localPosition = new Vector3(-1.85f, 1.4f, -0.5f);
            chute.localRotation = Quaternion.Euler(0f, -90f, 0f);

            // Scrubber Repair Control Panel (IToolTarget + IInteractable!)
            var panelGo = new GameObject("Scrubber_ControlPanel");
            panelGo.transform.SetParent(root.transform, false);
            panelGo.transform.localPosition = new Vector3(-2.1f, 0f, 0.7f);
            panelGo.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);

            GameObject cabinetAsset = AssetDatabase.LoadAssetAtPath<GameObject>($"{ArtMachines}/electrical_cabinet.fbx");
            if (cabinetAsset != null)
            {
                GameObject cabinet = (GameObject)PrefabUtility.InstantiatePrefab(cabinetAsset, panelGo.transform);
                cabinet.name = "Visual";
                cabinet.transform.localPosition = Vector3.zero;
                cabinet.transform.localRotation = Quaternion.identity;
            }
            else
            {
                GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fallback.transform.SetParent(panelGo.transform, false);
                fallback.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                fallback.transform.localScale = new Vector3(0.9f, 1.8f, 0.6f);
            }

            BoxCollider panelCol = panelGo.AddComponent<BoxCollider>();
            panelCol.center = new Vector3(0f, 0.95f, 0f);
            panelCol.size = new Vector3(1.1f, 1.9f, 0.85f);

            // Status beacon cube on top of cabinet (flashes red → emerald green on Wrench repair)
            GameObject beacon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            beacon.name = "StatusBeacon";
            beacon.transform.SetParent(panelGo.transform, false);
            beacon.transform.localPosition = new Vector3(0f, 2.15f, 0f);
            beacon.transform.localScale = Vector3.one * 0.42f;
            Object.DestroyImmediate(beacon.GetComponent<Collider>());
            Renderer beaconRend = beacon.GetComponent<Renderer>();
            beaconRend.sharedMaterial = beaconMat;

            FactorySmokestackSource source = panelGo.AddComponent<FactorySmokestackSource>();
            source.Configure(
                id: "factory_smokestack_1",
                label: "Factory Smokestack",
                targetDistrict: district,
                catalog: catalog,
                smoke: ps,
                chute: chute,
                beacon: beaconRend,
                unfilteredLoad: 16.5f,
                filteredLoad: 1.8f);

            panelGo.AddComponent<FocusHighlight>();
            GameLayers.SetLayerRecursively(panelGo, GameLayers.Interactable);
            panelGo.tag = GameTags.PollutionSource;
        }

        static void CreateSortingBin(Transform parent, Vector3 position, WasteStream stream, string modelPath, Vector3? triggerSize = null)
        {
            var go = new GameObject($"SortingBin_{stream}");
            go.transform.SetParent(parent, false);
            go.transform.position = position;

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (model != null)
            {
                GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(model, go.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
            }

            // Solid base collider
            BoxCollider solid = go.AddComponent<BoxCollider>();
            Vector3 tSize = triggerSize ?? new Vector3(1.15f, 1.1f, 1.15f);
            solid.center = new Vector3(0f, 0.42f, 0f);
            solid.size = new Vector3(tSize.x * 0.85f, 0.84f, tSize.z * 0.85f);

            // Trigger catch zone (accepts thrown/dropped trash!)
            var catchGo = new GameObject("CatchTrigger");
            catchGo.transform.SetParent(go.transform, false);
            catchGo.transform.localPosition = new Vector3(0f, 0.88f, 0f);
            BoxCollider trigger = catchGo.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = tSize;
            GameLayers.SetLayerRecursively(catchGo, GameLayers.TriggerZone);

            SortingCrate crate = go.AddComponent<SortingCrate>();
            crate.Configure(stream, payOnSort: true);
            go.AddComponent<FocusHighlight>();

            GameLayers.SetLayerRecursively(go, GameLayers.Interactable);
            GameLayers.SetLayerRecursively(catchGo, GameLayers.TriggerZone);
            go.tag = GameTags.SortingCrate;
        }

        static void CreatePickupPedestal(Transform parent, Vector3 position, string name, ItemDefinition item, Material accentMat, Material baseMat)
        {
            if (item == null)
                return;

            var pedestal = CreateEnvironmentBox($"{name}_Stand", parent, position + new Vector3(0f, 0.42f, 0f), new Vector3(0.75f, 0.84f, 0.75f), baseMat);

            var pickupGo = new GameObject(name);
            pickupGo.transform.SetParent(parent, false);
            pickupGo.transform.position = position + new Vector3(0f, 1.18f, 0f);

            GameObject orb = GameObject.CreatePrimitive(PrimitiveType.Cube);
            orb.name = "Visual";
            orb.transform.SetParent(pickupGo.transform, false);
            orb.transform.localScale = new Vector3(0.36f, 0.36f, 0.36f);
            orb.transform.localRotation = Quaternion.Euler(22f, 35f, 15f);
            Object.DestroyImmediate(orb.GetComponent<Collider>());
            orb.GetComponent<Renderer>().sharedMaterial = accentMat;

            SphereCollider col = pickupGo.AddComponent<SphereCollider>();
            col.radius = 0.42f;

            WorldPickupItem pickup = pickupGo.AddComponent<WorldPickupItem>();
            pickup.ConfigureItem(item, 1);

            pickupGo.AddComponent<FocusHighlight>();
            GameLayers.SetLayerRecursively(pickupGo, GameLayers.Pickup);
            pickupGo.tag = GameTags.Pickup;
        }

        static void SeedCauseTrash(WasteCatalog catalog, Transform parent, WasteSourceKind sourceKind, Vector3 center, int count, float spreadRadius)
        {
            if (catalog == null)
                return;

            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)Mathf.Max(1, count)) * Mathf.PI * 2f + 0.4f;
                float dist = Mathf.Lerp(0.6f, spreadRadius, (i % 3 + 1) / 3f);
                Vector3 pos = center + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
                catalog.SpawnTrash(sourceKind, pos, Vector3.zero, parent);
            }
        }

        static void ConfigureCrowdSpawner(
            CrowdSpawner spawner,
            CrowdCharacterLibrary library,
            CrowdPath[] paths,
            int count,
            bool masked,
            PollutionVisibilityMode visibility,
            int seed)
        {
            using (var so = new SerializedObject(spawner))
            {
                so.FindProperty("characters").objectReferenceValue = library;
                SerializedProperty pathsProp = so.FindProperty("paths");
                pathsProp.arraySize = paths.Length;
                for (int i = 0; i < paths.Length; i++)
                    pathsProp.GetArrayElementAtIndex(i).objectReferenceValue = paths[i];
                so.FindProperty("count").intValue = count;
                so.FindProperty("masked").boolValue = masked;
                so.FindProperty("visibility").enumValueIndex = (int)visibility;
                so.FindProperty("seed").intValue = seed;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static GameObject PlaceStaticProp(string assetPath, string name, Transform parent, Vector3 worldPos, Quaternion rotation, float scale)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return null;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            instance.name = name;
            instance.transform.SetPositionAndRotation(worldPos, rotation);
            instance.transform.localScale = Vector3.one * scale;

            // Ensure a solid collider on environment buildings/props if none exists
            if (instance.GetComponentInChildren<Collider>(true) == null)
            {
                Renderer[] rends = instance.GetComponentsInChildren<Renderer>(true);
                if (rends.Length > 0)
                {
                    Bounds b = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++)
                        b.Encapsulate(rends[i].bounds);

                    BoxCollider box = instance.AddComponent<BoxCollider>();
                    box.center = instance.transform.InverseTransformPoint(b.center);
                    Vector3 lossy = instance.transform.lossyScale;
                    box.size = new Vector3(
                        b.size.x / Mathf.Max(0.01f, Mathf.Abs(lossy.x)),
                        b.size.y / Mathf.Max(0.01f, Mathf.Abs(lossy.y)),
                        b.size.z / Mathf.Max(0.01f, Mathf.Abs(lossy.z)));
                }
            }

            GameLayers.SetLayerRecursively(instance, GameLayers.Environment);
            instance.tag = GameTags.Environment;
            return instance;
        }

        static GameObject CreateEnvironmentBox(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, bool withCollider = true)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = scale;
            if (mat != null)
                go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!withCollider)
                Object.DestroyImmediate(go.GetComponent<Collider>());
            GameLayers.SetLayerRecursively(go, GameLayers.Environment);
            go.tag = GameTags.Environment;
            return go;
        }

        static Material LoadMat(string path) => AssetDatabase.LoadAssetAtPath<Material>(path);

        static Material CreateRuntimeMaterial(string matName, Color color, float smoothness)
        {
            const string dir = "Assets/UpcycleCity/Materials/Ecosystem";
            EnsureFolder(dir);
            string path = $"{dir}/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(urpLit);
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetColor("_BaseColor", color);
            mat.SetColor("_Color", color);
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
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

        static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].path == scenePath)
                    return;
            }
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
