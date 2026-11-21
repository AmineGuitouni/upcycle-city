using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UpcycleCity.Core;
using UpcycleCity.Demo;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Pollution;
using UpcycleCity.Waste;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds the dedicated Phase 1 test scene (Assets/UpcycleCity/Scenes/PlayerGym.unity)
    /// and reusable Player.prefab to test FSM arcade physics movement, keyed modifiers,
    /// pluggable item behaviors, tool/currency inventory, and 1-at-a-time physics trash grabbing.
    /// </summary>
    public static class PlayerGymBuilder
    {
        const string ScenePath = "Assets/UpcycleCity/Scenes/PlayerGym.unity";
        const string PlayerPrefabFolder = "Assets/UpcycleCity/Prefabs/Player";
        const string PlayerPrefabPath = "Assets/UpcycleCity/Prefabs/Player/Player.prefab";
        const string SettingsRoot = "Assets/UpcycleCity/Settings/Player";
        const string PropsGarage = "Assets/UpcycleCity/Art/UpcycleProps/Garage";
        const string PropsClip = "Assets/UpcycleCity/Art/UpcycleProps/Clip";

        [MenuItem("Upcycle City/Build Player Gym Scene")]
        public static void BuildPlayerGym()
        {
            ProjectLayersAndTagsSetup.EnsureAll();
            EnsureFolder(SettingsRoot);
            EnsureFolder(PlayerPrefabFolder);
            EnsureFolder("Assets/UpcycleCity/Scenes");

            // 1. Create / update ScriptableObject assets
            PlayerConfig config = CreateOrLoadAsset<PlayerConfig>($"{SettingsRoot}/PlayerConfig.asset");

            ItemDefinition litterPicker = CreateOrLoadAsset<ItemDefinition>($"{SettingsRoot}/Item_LitterPicker.asset");
            litterPicker.Configure(
                "tool_litter_picker",
                "Litter Picker",
                "Snaps litter up in a blink without stopping (+1.6 m reach, harder throws). By hand you have to bend down and stand still.",
                ItemCategory.Tool,
                new Color(1f, 0.48f, 0.1f),
                stackLimit: 1,
                consume: false,
                reachBonus: 1.6f,
                throwBonus: 1.5f);
            // angled across the view towards the ground, so it reads as a long reacher rather than a stub
            litterPicker.SetGrabTool(true,
                AssetDatabase.LoadAssetAtPath<GameObject>("Assets/UpcycleCity/Art/UpcycleProps/Tools/litter_picker.fbx"),
                new Vector3(-4f, -22f, 0f));
            EditorUtility.SetDirty(litterPicker);

            ItemDefinition wrench = CreateOrLoadAsset<ItemDefinition>($"{SettingsRoot}/Item_Wrench.asset");
            wrench.Configure(
                "tool_wrench",
                "Wrench",
                "Clears machine jams and repairs upcycled parts.",
                ItemCategory.Tool,
                new Color(0.24f, 0.54f, 0.78f),
                stackLimit: 1,
                consume: false,
                behavior: new ToolTargetItemBehavior("Aim at a jammed machine to repair it."));
            EditorUtility.SetDirty(wrench);

            ItemDefinition broom = CreateOrLoadAsset<ItemDefinition>($"{SettingsRoot}/Item_Broom.asset");
            broom.Configure(
                "tool_broom",
                "Broom",
                "Sweeps nearby street litter forward in an arc.",
                ItemCategory.Tool,
                new Color(0.71f, 0.34f, 0.18f),
                stackLimit: 1,
                consume: false,
                behavior: new SweepAreaItemBehavior(radius: 2.6f, distance: 2.0f, impulse: 5.5f));
            EditorUtility.SetDirty(broom);

            ItemDefinition paintTin = CreateOrLoadAsset<ItemDefinition>($"{SettingsRoot}/Item_PaintTin.asset");
            paintTin.Configure(
                "usable_paint_teal",
                "Teal Paint",
                "Usable paint tin for finishing upcycled benches.",
                ItemCategory.Usable,
                new Color(0.18f, 0.56f, 0.54f),
                stackLimit: 5,
                consume: true,
                behavior: new ToolTargetItemBehavior("Aim at an upcycled bench to paint it."));
            EditorUtility.SetDirty(paintTin);

            AssetDatabase.SaveAssets();

            // 2. Create a fresh clean scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Materials
            Material floorMat = CreateGymMaterial("Gym_Floor", new Color(0.46f, 0.49f, 0.51f), 0.12f);
            Material accentMat = CreateGymMaterial("Gym_AccentTeal", new Color(0.184f, 0.561f, 0.541f), 0.25f);
            Material greenMat = CreateGymMaterial("Gym_Green", new Color(0.180f, 0.545f, 0.341f), 0.25f);
            Material yellowMat = CreateGymMaterial("Gym_Yellow", new Color(0.949f, 0.761f, 0.106f), 0.3f);
            Material rustMat = CreateGymMaterial("Gym_Rust", new Color(0.706f, 0.337f, 0.180f), 0.2f);
            Material darkMat = CreateGymMaterial("Gym_DarkGrey", new Color(0.227f, 0.247f, 0.267f), 0.2f);
            Material wallMat = CreateGymMaterial("Gym_Wall", new Color(0.76f, 0.75f, 0.72f), 0.12f);

            // 3. Lighting + Clean PollutionController (so Pollution Lit props render vibrant & crisp)
            var envRoot = new GameObject("Environment");
            var sunGo = new GameObject("Directional Sun");
            sunGo.transform.SetParent(envRoot.transform);
            sunGo.transform.rotation = Quaternion.Euler(46f, 130f, 0f);
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.65f;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.shadows = LightShadows.Soft;

            var pollutionGo = new GameObject("PollutionController");
            pollutionGo.transform.SetParent(envRoot.transform);
            PollutionController pollutionController = pollutionGo.AddComponent<PollutionController>();
            PollutionProfile profile = AssetDatabase.LoadAssetAtPath<PollutionProfile>("Assets/UpcycleCity/Settings/PollutionProfile.asset");
            using (var so = new SerializedObject(pollutionController))
            {
                so.FindProperty("pollution").floatValue = 0f;
                so.FindProperty("profile").objectReferenceValue = profile;
                so.FindProperty("sun").objectReferenceValue = sun;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            Material skyMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/UpcycleCity/Materials/Sky_Pollution.mat");
            if (skyMat != null)
                RenderSettings.skybox = skyMat;

            // Main floor + perimeter curbs
            CreateBox("Ground", envRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(64f, 1f, 64f), floorMat);
            CreateBox("Wall_North", envRoot.transform, new Vector3(0f, 1.5f, 32f), new Vector3(64f, 3f, 0.6f), wallMat);
            CreateBox("Wall_South", envRoot.transform, new Vector3(0f, 1.5f, -32f), new Vector3(64f, 3f, 0.6f), wallMat);
            CreateBox("Wall_East", envRoot.transform, new Vector3(32f, 1.5f, 0f), new Vector3(0.6f, 3f, 64f), wallMat);
            CreateBox("Wall_West", envRoot.transform, new Vector3(-32f, 1.5f, 0f), new Vector3(0.6f, 3f, 64f), wallMat);

            // 4. ZONE 1: Movement & Arcade Physics Course (West side, X = -14 .. -4)
            var moveZone = new GameObject("Zone_1_MovementCourse");
            CreateZoneBanner(moveZone.transform, new Vector3(-11f, 3.2f, 6f), "MOVEMENT & ARCADE PHYSICS", accentMat);

            // Gentle 20-deg climbable ramp + upper landing
            CreateBox("Ramp_20deg", moveZone.transform, new Vector3(-15f, 0.85f, 4f), new Vector3(3.6f, 0.3f, 6.2f), accentMat, new Vector3(-20f, 0f, 0f));
            CreateBox("Landing_Top", moveZone.transform, new Vector3(-15f, 1.0f, 9.2f), new Vector3(4.2f, 2.0f, 4.4f), darkMat);

            // Steep 38-deg climbable ramp
            CreateBox("Ramp_38deg", moveZone.transform, new Vector3(-9.5f, 1.05f, 4.2f), new Vector3(3.0f, 0.3f, 4.2f), greenMat, new Vector3(-38f, 0f, 0f));
            CreateBox("Landing_Steep", moveZone.transform, new Vector3(-9.5f, 1.2f, 8.2f), new Vector3(3.4f, 2.4f, 3.2f), darkMat);

            // Unwalkable 60-deg slope test (should slide/block)
            CreateBox("Slope_60deg_Unwalkable", moveZone.transform, new Vector3(-5.2f, 1.15f, 4.2f), new Vector3(2.4f, 0.3f, 3.2f), rustMat, new Vector3(-60f, 0f, 0f));

            // Crouch tunnel (ceiling underside at Y = 1.38m: forces crouch & prevents standing inside)
            CreateBox("CrouchTunnel_LeftWall", moveZone.transform, new Vector3(-16.8f, 1.1f, -6f), new Vector3(0.4f, 2.2f, 7.5f), darkMat);
            CreateBox("CrouchTunnel_RightWall", moveZone.transform, new Vector3(-13.2f, 1.1f, -6f), new Vector3(0.4f, 2.2f, 7.5f), darkMat);
            CreateBox("CrouchTunnel_Roof", moveZone.transform, new Vector3(-15f, 1.63f, -6f), new Vector3(4.0f, 0.5f, 7.5f), yellowMat);
            CreateZoneBanner(moveZone.transform, new Vector3(-15f, 2.5f, -2.2f), "CROUCH TUNNEL (1.38m)", yellowMat);

            // Jump & Coyote-Time Platform Course
            CreateBox("JumpPad_1", moveZone.transform, new Vector3(-15f, 0.45f, 14.5f), new Vector3(3.0f, 0.9f, 3.0f), greenMat);
            CreateBox("JumpPad_2", moveZone.transform, new Vector3(-11f, 0.9f, 17.5f), new Vector3(2.8f, 1.8f, 2.8f), accentMat);
            CreateBox("JumpPad_3", moveZone.transform, new Vector3(-6.2f, 1.25f, 19.2f), new Vector3(2.8f, 2.5f, 2.8f), yellowMat);
            CreateBox("JumpPad_4", moveZone.transform, new Vector3(-1.5f, 1.6f, 17.8f), new Vector3(3.2f, 3.2f, 3.2f), greenMat);

            // Sprint runway with distance markers
            CreateBox("SprintRunway", moveZone.transform, new Vector3(-6f, 0.01f, -12f), new Vector3(3.6f, 0.02f, 18f), darkMat);
            for (int i = 0; i < 6; i++)
                CreateBox($"SprintStripe_{i}", moveZone.transform, new Vector3(-6f, 0.025f, -20f + i * 3.2f), new Vector3(3.2f, 0.02f, 0.35f), yellowMat);

            // 5. ZONE 2: Inventory & Pickups Bay (East side, X = +7 .. +14)
            var invZone = new GameObject("Zone_2_InventoryPickups");
            CreateZoneBanner(invZone.transform, new Vector3(9.5f, 3.1f, 4.5f), "INVENTORY: TOOLS & COINS ONLY", greenMat);

            PlaceProp($"{PropsGarage}/workbench.fbx", "Workbench_Pickups", invZone.transform, new Vector3(10.5f, 0f, 4.5f), Quaternion.Euler(0f, -90f, 0f), addBoxCollider: true);

            // Pedestals with Tools, Paint Tin & Coin Pouches
            CreatePickupPedestal(invZone.transform, new Vector3(7.5f, 0f, 7.2f), "Pickup_LitterPicker", litterPicker, 1, 0, yellowMat, null, darkMat);
            CreatePickupPedestal(invZone.transform, new Vector3(7.5f, 0f, 4.5f), "Pickup_Wrench", wrench, 1, 0, accentMat, null, darkMat);
            CreatePickupPedestal(invZone.transform, new Vector3(7.5f, 0f, 1.8f), "Pickup_Broom", broom, 1, 0, rustMat, null, darkMat);
            CreatePickupPedestal(invZone.transform, new Vector3(7.5f, 0f, -0.9f), "Pickup_PaintTin", paintTin, 2, 0, accentMat, $"{PropsClip}/paint_tin_teal.fbx", darkMat);
            CreatePickupPedestal(invZone.transform, new Vector3(10.5f, 0f, 0.4f), "Pickup_Coins_25", null, 1, 25, yellowMat, null, darkMat);
            CreatePickupPedestal(invZone.transform, new Vector3(10.5f, 0f, -2.0f), "Pickup_Coins_50", null, 1, 50, yellowMat, null, darkMat);

            // 6. ZONE 3: Physics Trash Grab & 4-Stream Sorting Arena (Center-North, Z = +6 .. +15)
            var trashZone = new GameObject("Zone_3_PhysicsTrashAndSorting");
            CreateZoneBanner(trashZone.transform, new Vector3(1.5f, 3.4f, 11.5f), "PHYSICS TRASH (1 AT A TIME) & SORTING", rustMat);

            // 4 Color-Coded Sorting Crates
            CreateSortingCrate(trashZone.transform, new Vector3(-1.5f, 0f, 10.5f), WasteStream.Paper, $"{PropsGarage}/crate_paper.fbx");
            CreateSortingCrate(trashZone.transform, new Vector3(0.5f, 0f, 10.5f), WasteStream.Plastic, $"{PropsGarage}/crate_plastic.fbx");
            CreateSortingCrate(trashZone.transform, new Vector3(2.5f, 0f, 10.5f), WasteStream.Glass, $"{PropsGarage}/crate_glass.fbx");
            CreateSortingCrate(trashZone.transform, new Vector3(4.5f, 0f, 10.5f), WasteStream.Cans, $"{PropsGarage}/crate_cans.fbx");

            // Sorting table nearby for visual context
            PlaceProp($"{PropsGarage}/sorting_table.fbx", "SortingTable", trashZone.transform, new Vector3(1.5f, 0f, 7.6f), Quaternion.identity, addBoxCollider: true);

            // Trash spawn center & physical TrashItems
            var spawnCenter = new GameObject("TrashSpawnCenter").transform;
            spawnCenter.SetParent(trashZone.transform);
            spawnCenter.position = new Vector3(1.5f, 0f, 4.6f);

            var spawnEntries = new[]
            {
                MakeSpawnEntry("Newspaper", WasteStream.Paper, $"{PropsGarage}/item_newspapers.fbx", new Vector3(0.32f, 0.14f, 0.24f), 0.45f, 6, false, 1f, 0f),
                MakeSpawnEntry("Cardboard Box", WasteStream.Paper, $"{PropsGarage}/item_card_box_small.fbx", new Vector3(0.36f, 0.26f, 0.32f), 0.6f, 7, false, 0.96f, 0.08f),
                MakeSpawnEntry("PET Bottle", WasteStream.Plastic, $"{PropsGarage}/item_pet_blue.fbx", new Vector3(0.16f, 0.30f, 0.16f), 0.35f, 6, false, 1f, 0f),
                MakeSpawnEntry("Green PET Bottle", WasteStream.Plastic, $"{PropsGarage}/item_pet_green.fbx", new Vector3(0.16f, 0.30f, 0.16f), 0.35f, 6, false, 1f, 0f),
                MakeSpawnEntry("Glass Bottle", WasteStream.Glass, $"{PropsGarage}/item_beer_green.fbx", new Vector3(0.15f, 0.30f, 0.15f), 0.7f, 8, false, 1f, 0f),
                MakeSpawnEntry("Glass Jar", WasteStream.Glass, $"{PropsGarage}/item_jar.fbx", new Vector3(0.18f, 0.22f, 0.18f), 0.65f, 8, false, 1f, 0f),
                MakeSpawnEntry("Soda Can", WasteStream.Cans, $"{PropsGarage}/item_can_red.fbx", new Vector3(0.14f, 0.20f, 0.14f), 0.3f, 7, false, 1f, 0f),
                MakeSpawnEntry("Blue Soda Can", WasteStream.Cans, $"{PropsGarage}/item_can_blue.fbx", new Vector3(0.14f, 0.20f, 0.14f), 0.3f, 7, false, 1f, 0f),
                MakeSpawnEntry("Broken Chair", WasteStream.BulkyUpcyclable, $"{PropsClip}/broken_chair.fbx", new Vector3(0.55f, 0.85f, 0.55f), 6.5f, 18, true, 0.72f, 0.45f),
                MakeSpawnEntry("Old Car Tyre", WasteStream.BulkyUpcyclable, $"{PropsGarage}/tyre_car.fbx", new Vector3(0.68f, 0.28f, 0.68f), 8.0f, 16, true, 0.70f, 0.45f),
            };

            // Place initial physical trash items neatly in the arena
            PlaceInitialTrash(trashZone.transform, spawnEntries, new Vector3(1.5f, 0f, 4.6f));

            // Respawn button pedestal
            var respawnPedestal = CreateBox("TrashRespawner_Button", trashZone.transform, new Vector3(6.2f, 0.55f, 10.5f), new Vector3(0.65f, 1.1f, 0.65f), darkMat);
            CreateBox("ButtonCap", respawnPedestal.transform, new Vector3(0f, 0.6f, 0f), new Vector3(0.45f, 0.12f, 0.45f), greenMat);
            respawnPedestal.tag = GameTags.Interactable;
            respawnPedestal.AddComponent<FocusHighlight>();
            GameLayers.SetLayerRecursively(respawnPedestal, GameLayers.Interactable);
            GymTrashRespawner respawner = respawnPedestal.AddComponent<GymTrashRespawner>();
            respawner.Configure(spawnCenter, spawnEntries);

            // 7. Build / update reusable Player.prefab and instantiate into scene
            GameObject playerPrefab = BuildOrUpdatePlayerPrefab(config, new ItemDefinition[] { litterPicker });
            GameObject playerGo = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            playerGo.transform.position = new Vector3(0f, 0.1f, -3.5f);
            playerGo.transform.rotation = Quaternion.Euler(0f, 8f, 0f);

            // Save scene & add to build settings
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[Upcycle City] Built reusable {PlayerPrefabPath} and Player Gym test scene at {ScenePath}");
        }

        static GameObject BuildOrUpdatePlayerPrefab(PlayerConfig config, ItemDefinition[] startingItems)
        {
            GameObject tempRig = CreatePlayerRig(config, startingItems);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(tempRig, PlayerPrefabPath);
            Object.DestroyImmediate(tempRig);
            return prefab;
        }

        static GameObject CreatePlayerRig(PlayerConfig config, ItemDefinition[] startingItems)
        {
            var playerGo = new GameObject("Player");
            playerGo.tag = GameTags.Player;

            playerGo.AddComponent<Rigidbody>();
            playerGo.AddComponent<CapsuleCollider>();

            InputActionAsset inputAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/InputSystem_Actions.inputactions");
            PlayerInput playerInput = playerGo.AddComponent<PlayerInput>();
            playerInput.actions = inputAsset;
            playerInput.defaultActionMap = "Player";
            playerInput.notificationBehavior = PlayerNotifications.InvokeCSharpEvents;

            PlayerInputReader input = playerGo.AddComponent<PlayerInputReader>();
            if (inputAsset != null)
                input.SetFallbackActionsAsset(inputAsset);

            PlayerMotor motor = playerGo.AddComponent<PlayerMotor>();
            motor.SetGroundMask(GameLayers.GroundMask);
            PlayerCameraController camCtrl = playerGo.AddComponent<PlayerCameraController>();

            PlayerInventory inventory = playerGo.AddComponent<PlayerInventory>();
            inventory.SetStartingItems(startingItems, initialCoins: 25);

            PhysicsGrabController grabber = playerGo.AddComponent<PhysicsGrabController>();
            grabber.SetObstacleMask(GameLayers.ObstacleMask);

            PlayerInteractor interactor = playerGo.AddComponent<PlayerInteractor>();
            interactor.SetInteractMask(GameLayers.InteractMask);

            playerGo.AddComponent<PlayerFeedbackHub>();

            var pivotGo = new GameObject("CameraPivot");
            pivotGo.transform.SetParent(playerGo.transform, false);
            pivotGo.transform.localPosition = new Vector3(0f, config.standEyeHeight, 0f);

            var camGo = new GameObject("PlayerCamera");
            camGo.tag = GameTags.MainCamera;
            camGo.transform.SetParent(pivotGo.transform, false);
            Camera cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.08f;
            cam.farClipPlane = 300f;
            camGo.AddComponent<AudioListener>();

            var toolAnchor = new GameObject("EquippedToolAnchor");
            toolAnchor.transform.SetParent(camGo.transform, false);
            toolAnchor.transform.localPosition = new Vector3(0.32f, -0.26f, 0.52f);

            PlayerController controller = playerGo.AddComponent<PlayerController>();
            controller.SetConfig(config);
            controller.SetCameraReferences(pivotGo.transform, cam, toolAnchor.transform);
            playerGo.AddComponent<PlayerGymHUD>();
            PlayerCharacterSetup.Apply(playerGo, CharacterCreatorBuilder.CreateOrFillLibrary());

            // Wire camera controller serialized fields too
            using (var so = new SerializedObject(camCtrl))
            {
                so.FindProperty("cameraPivot").objectReferenceValue = pivotGo.transform;
                so.FindProperty("playerCamera").objectReferenceValue = cam;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            GameLayers.SetLayerRecursively(playerGo, GameLayers.Player);
            return playerGo;
        }

        static void CreateSortingCrate(Transform parent, Vector3 pos, WasteStream stream, string fbxPath)
        {
            var root = new GameObject($"SortingCrate_{WasteStreamPalette.LabelFor(stream)}");
            root.tag = GameTags.SortingCrate;
            root.transform.SetParent(parent, false);
            root.transform.position = pos;

            GameObject model = PlaceProp(fbxPath, "Visual", root.transform, Vector3.zero, Quaternion.identity, addBoxCollider: false);
            if (model == null)
            {
                Material mat = CreateGymMaterial($"Crate_{stream}", WasteStreamPalette.ColorFor(stream), 0.25f);
                CreateBox("FallbackCrate", root.transform, new Vector3(0f, 0.25f, 0f), new Vector3(0.75f, 0.5f, 0.55f), mat);
            }
            else
            {
                // Slightly scale up crates for fun arcade tossing targets
                model.transform.localScale = Vector3.one * 1.45f;
            }

            // Solid base collider on Interactable layer + generous top catch trigger on TriggerZone layer
            var baseCol = root.AddComponent<BoxCollider>();
            baseCol.size = new Vector3(0.95f, 0.55f, 0.72f);
            baseCol.center = new Vector3(0f, 0.275f, 0f);

            var triggerGo = new GameObject("CatchTrigger");
            triggerGo.transform.SetParent(root.transform, false);
            var trigger = triggerGo.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(0.92f, 0.65f, 0.72f);
            trigger.center = new Vector3(0f, 0.48f, 0f);

            SortingCrate crate = root.AddComponent<SortingCrate>();
            crate.Configure(stream, payOnSort: true);
            root.AddComponent<FocusHighlight>();

            // Overhead color marker bar + readable label
            Material colorMat = CreateGymMaterial($"Sign_{stream}", WasteStreamPalette.ColorFor(stream), 0.3f);
            CreateBox("SignBar", root.transform, new Vector3(0f, 1.25f, 0.25f), new Vector3(1.1f, 0.28f, 0.08f), colorMat);
            CreateWorldText(root.transform, WasteStreamPalette.LabelFor(stream), new Vector3(0f, 1.25f, 0.19f), 0.055f);

            GameLayers.SetLayerRecursively(root, GameLayers.Interactable);
            triggerGo.layer = GameLayers.TriggerZone;
        }

        static void CreatePickupPedestal(
            Transform parent,
            Vector3 pos,
            string name,
            ItemDefinition item,
            int quantity,
            int coins,
            Material visualMat,
            string optionalFbxPath,
            Material pedestalMat)
        {
            CreateBox($"{name}_Stand", parent, pos + new Vector3(0f, 0.45f, 0f), new Vector3(0.75f, 0.9f, 0.75f), pedestalMat);

            var pickupGo = new GameObject(name);
            pickupGo.tag = GameTags.Pickup;
            pickupGo.transform.SetParent(parent, false);
            pickupGo.transform.position = pos + new Vector3(0f, 1.25f, 0f);

            GameObject model = !string.IsNullOrEmpty(optionalFbxPath)
                ? PlaceProp(optionalFbxPath, "Visual", pickupGo.transform, Vector3.zero, Quaternion.identity, addBoxCollider: false)
                : null;

            if (model == null)
            {
                PrimitiveType prim = coins > 0 ? PrimitiveType.Cylinder : PrimitiveType.Cube;
                var primGo = GameObject.CreatePrimitive(prim);
                Object.DestroyImmediate(primGo.GetComponent<Collider>());
                primGo.transform.SetParent(pickupGo.transform, false);
                primGo.transform.localScale = coins > 0
                    ? new Vector3(0.32f, 0.07f, 0.32f)
                    : (item != null && item.GrabReachBonus > 0f ? new Vector3(0.1f, 0.1f, 0.65f) : new Vector3(0.28f, 0.28f, 0.28f));
                primGo.GetComponent<Renderer>().sharedMaterial = visualMat;
            }

            SphereCollider col = pickupGo.AddComponent<SphereCollider>();
            col.radius = 0.36f;
            col.isTrigger = true;

            WorldPickupItem pickup = pickupGo.AddComponent<WorldPickupItem>();
            if (coins > 0)
                pickup.ConfigureCoins(coins);
            else
                pickup.ConfigureItem(item, quantity);

            pickupGo.AddComponent<FocusHighlight>();
            GameLayers.SetLayerRecursively(pickupGo, GameLayers.Pickup);
        }

        static void PlaceInitialTrash(Transform parent, GymTrashRespawner.SpawnEntry[] entries, Vector3 center)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                int row = i / 5;
                int col = i % 5;
                Vector3 pos = center + new Vector3((col - 2) * 1.15f, 0.45f, (row - 0.5f) * 1.45f);
                if (entry.isBulky)
                    pos += new Vector3(0f, 0.2f, -1.8f);

                var go = new GameObject($"Trash_{entry.displayName}");
                go.tag = GameTags.Trash;
                go.transform.SetParent(parent, false);
                go.transform.position = pos;

                if (entry.visualPrefab != null)
                {
                    GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(entry.visualPrefab, go.transform);
                    visual.transform.localPosition = Vector3.zero;
                    visual.transform.localRotation = Quaternion.identity;
                }

                BoxCollider box = go.AddComponent<BoxCollider>();
                box.size = entry.colliderSize;
                box.center = new Vector3(0f, entry.colliderSize.y * 0.5f, 0f);

                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.mass = entry.mass;

                TrashItem item = go.AddComponent<TrashItem>();
                item.Configure(
                    entry.displayName,
                    entry.stream,
                    WasteSourceKind.Initial,
                    entry.isBulky ? 2.5f : 1f,
                    entry.coinValue,
                    entry.isBulky,
                    entry.carrySpeedMultiplier,
                    entry.holdOffset);

                go.AddComponent<FocusHighlight>();
                GameLayers.SetLayerRecursively(go, GameLayers.Grabbable);
            }
        }

        static GymTrashRespawner.SpawnEntry MakeSpawnEntry(
            string name,
            WasteStream stream,
            string fbxPath,
            Vector3 colliderSize,
            float mass,
            int coinValue,
            bool bulky,
            float carrySpeed,
            float holdOffset)
        {
            return new GymTrashRespawner.SpawnEntry
            {
                displayName = name,
                stream = stream,
                visualPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath),
                colliderSize = colliderSize,
                mass = mass,
                coinValue = coinValue,
                isBulky = bulky,
                carrySpeedMultiplier = carrySpeed,
                holdOffset = holdOffset,
            };
        }

        static GameObject PlaceProp(string fbxPath, string name, Transform parent, Vector3 localPos, Quaternion localRot, bool addBoxCollider)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (prefab == null)
                return null;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            instance.name = name;
            instance.tag = GameTags.Environment;
            instance.transform.localPosition = localPos;
            instance.transform.localRotation = localRot;

            if (addBoxCollider)
            {
                Bounds bounds = CalculateLocalBounds(instance);
                BoxCollider col = instance.AddComponent<BoxCollider>();
                col.center = bounds.center;
                col.size = bounds.size;
            }
            GameLayers.SetLayerRecursively(instance, GameLayers.Environment);
            return instance;
        }

        static Bounds CalculateLocalBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(Vector3.up * 0.5f, Vector3.one);

            Bounds world = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                world.Encapsulate(renderers[i].bounds);

            Vector3 localCenter = root.transform.InverseTransformPoint(world.center);
            return new Bounds(localCenter, world.size);
        }

        static GameObject CreateBox(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat, Vector3 euler = default)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.tag = GameTags.Environment;
            go.layer = GameLayers.Environment;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            if (mat != null)
                go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static void CreateZoneBanner(Transform parent, Vector3 pos, string text, Material mat)
        {
            var go = new GameObject($"Banner_{text}");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var bar = CreateBox("Bar", go.transform, Vector3.zero, new Vector3(5.6f, 0.55f, 0.14f), mat);
            Object.DestroyImmediate(bar.GetComponent<Collider>());
            CreateWorldText(go.transform, text, new Vector3(0f, 0f, -0.09f), 0.07f);
        }

        static void CreateWorldText(Transform parent, string text, Vector3 localPos, float charSize)
        {
            var textGo = new GameObject("Label");
            textGo.transform.SetParent(parent, false);
            textGo.transform.localPosition = localPos;
            TextMesh tm = textGo.AddComponent<TextMesh>();
            tm.text = text;
            tm.fontSize = 48;
            tm.characterSize = charSize;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontStyle = FontStyle.Bold;
            tm.color = Color.white;
        }

        static Material CreateGymMaterial(string name, Color color, float smoothness)
        {
            string matFolder = "Assets/UpcycleCity/Materials/Gym";
            EnsureFolder(matFolder);
            string path = $"{matFolder}/{name}.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.SetColor("_BaseColor", color);
                existing.SetFloat("_Smoothness", smoothness);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static T CreateOrLoadAsset<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
                return asset;

            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        static void EnsureFolder(string assetFolderPath)
        {
            string[] parts = assetFolderPath.Split('/');
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
            var existing = EditorBuildSettings.scenes;
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i].path == scenePath)
                    return;
            }
            var updated = new EditorBuildSettingsScene[existing.Length + 1];
            existing.CopyTo(updated, 0);
            updated[existing.Length] = new EditorBuildSettingsScene(scenePath, true);
            EditorBuildSettings.scenes = updated;
        }
    }
}
