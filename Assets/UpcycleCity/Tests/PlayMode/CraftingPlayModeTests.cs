using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using UpcycleCity.Core;
using UpcycleCity.Crafting;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Networking;
using UpcycleCity.Player;
using UpcycleCity.Shops;
using UpcycleCity.Waste;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>
    /// End-to-end PlayMode integration tests verifying the Recycling &amp; Crafting Card system in BOTH
    /// offline solo mode and real host+client co-op over loopback <see cref="UnityTransport"/> (127.0.0.1).
    /// Covers timed co-op cards, busy rejection, completion sync, upgrade cost/cap/refund, gated debug actions,
    /// stable placement IDs across movement &amp; runtime spawning, in-place CraftingUI tile updates, Card Inspector,
    /// Generator UI mode, and per-machine hopper filtering.
    /// </summary>
    public sealed class CraftingPlayModeTests
    {
        static ushort nextPort = 17910;

        readonly List<GameObject> spawnedRoots = new List<GameObject>();
        readonly List<UnityEngine.Object> createdAssets = new List<UnityEngine.Object>();
        NetworkManager hostManager;
        NetworkManager clientManager;
        NetworkManager lateClientManager;

        [TearDown]
        public void TearDown()
        {
            if (CraftingUI.Instance != null && CraftingUI.Instance.IsOpen)
                CraftingUI.Instance.Close();

            if (lateClientManager != null)
            {
                if (lateClientManager.IsListening)
                    lateClientManager.Shutdown();
                UnityEngine.Object.DestroyImmediate(lateClientManager.gameObject);
                lateClientManager = null;
            }

            if (clientManager != null)
            {
                if (clientManager.IsListening)
                    clientManager.Shutdown();
                UnityEngine.Object.DestroyImmediate(clientManager.gameObject);
                clientManager = null;
            }

            if (hostManager != null)
            {
                if (hostManager.IsListening)
                    hostManager.Shutdown();
                UnityEngine.Object.DestroyImmediate(hostManager.gameObject);
                hostManager = null;
            }

            for (int i = spawnedRoots.Count - 1; i >= 0; i--)
            {
                if (spawnedRoots[i] != null)
                    UnityEngine.Object.DestroyImmediate(spawnedRoots[i]);
            }
            spawnedRoots.Clear();

            for (int i = createdAssets.Count - 1; i >= 0; i--)
            {
                if (createdAssets[i] != null)
                    UnityEngine.Object.DestroyImmediate(createdAssets[i]);
            }
            createdAssets.Clear();

            CraftingDebugGate.SetDebugActionsAllowed(false);
            LocalGameAuthority.Instance.SetLocalWalletOverride(null);
            GameAuthority.ResetToLocal();
            SyncedStateRegistry.Clear();
            TrashHopperRegistry.Clear();
        }

        [UnityTest]
        public IEnumerator Solo_CraftingMachine_ExecutesCardOverTimerAndHonorsRange()
        {
            CraftingDatabase db = LoadDatabase();
            Assert.IsNotNull(db);
            Assert.IsTrue(db.TryGetMachine("machine_plastic_melter", out MachineDefinition melterDef));
            Assert.IsTrue(db.TryGetCard("refining_plastic_thermal_reclaim", out CraftingCard card));

            CraftingCard fastCard = ScriptableObject.CreateInstance<CraftingCard>();
            createdAssets.Add(fastCard);
            fastCard.Configure(
                "test_fast_plastic",
                card.DisplayName,
                card.Description,
                card.Category,
                card.RequiredMachineType,
                card.ComplexityLevel,
                card.EnergyCost,
                card.TemperatureCelsius,
                durationSeconds: 0.2f,
                card.Inputs,
                card.Outputs);

            CraftingDatabase testDb = ScriptableObject.CreateInstance<CraftingDatabase>();
            createdAssets.Add(testDb);
            testDb.Configure(db.Materials, new[] { fastCard }, new[] { melterDef });

            TeamMaterialStock stock = CreateStock(testDb, initialEnergy: 20);
            stock.Ledger.Add("plastic_scrap", 4.0f);
            stock.Ledger.Add("catalyst", 0.2f);

            var machGo = new GameObject("SoloMelter");
            spawnedRoots.Add(machGo);
            machGo.transform.position = Vector3.zero;
            CraftingMachine machine = machGo.AddComponent<CraftingMachine>();
            machine.Configure(melterDef, testDb, startLevel: 0);

            // 1. Out-of-range check via custom action context with distant sender position
            CustomGameActionResult farResult = GameAuthority.InvokeCustomActionLocally(
                0UL,
                CraftingMachine.ActionExecuteCard,
                machine.SyncKey,
                intArg: 0,
                floatArg: 0f,
                senderPosition: new Vector3(50f, 0f, 0f));

            Assert.IsFalse(farResult.Success);
            Assert.AreEqual($"Move closer to the {melterDef.DisplayName}", farResult.Message);
            Assert.AreEqual(4.0f, stock.GetAmount("plastic_scrap"), 0.001f);

            // 2. Valid execution starts timer, deducts inputs+energy immediately, credits output when done
            bool callbackFired = false;
            bool callbackSuccess = false;
            machine.RequestExecuteCard(fastCard, (ok, _) =>
            {
                callbackFired = true;
                callbackSuccess = ok;
            });

            Assert.IsTrue(callbackFired && callbackSuccess);
            Assert.IsTrue(machine.IsProcessing);
            Assert.AreEqual(0f, stock.GetAmount("plastic_scrap"), 0.001f);
            Assert.AreEqual(0f, stock.GetAmount("catalyst"), 0.001f);
            Assert.AreEqual(10, stock.Energy);
            Assert.AreEqual(0f, stock.GetAmount("recycled_polymer"), 0.001f, "Output should wait until timer completes.");

            yield return WaitUntil(() => !machine.IsProcessing, 2.5f, "Machine did not finish processing card.");

            Assert.AreEqual(3.2f, stock.GetAmount("recycled_polymer"), 0.001f);

            // 3. ManualEnergyGenerator cranks NRG via GameAuthority
            var genGo = new GameObject("SoloGenerator");
            spawnedRoots.Add(genGo);
            ManualEnergyGenerator gen = genGo.AddComponent<ManualEnergyGenerator>();
            gen.Configure(null, testDb, nrgLvl0: 15, nrgLvl1: 25, cooldown: 0.25f);
            int energyBefore = stock.Energy;
            bool cranked = false;
            gen.RequestCrank((ok, _) => cranked = ok);
            Assert.IsTrue(cranked);
            Assert.AreEqual(energyBefore + 15, stock.Energy);
        }

        [UnityTest]
        public IEnumerator Solo_BuildingCard_SpawnsOutputPrefabInWorldAndCreditsStock()
        {
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_workbench", out MachineDefinition benchDef));
            Assert.IsTrue(db.TryGetCard("building_upcycled_park_bench", out CraftingCard benchCard));
            Assert.IsNotNull(benchCard.OutputPrefab);

            CraftingCard instantBench = ScriptableObject.CreateInstance<CraftingCard>();
            createdAssets.Add(instantBench);
            instantBench.Configure(
                "test_instant_bench",
                benchCard.DisplayName,
                benchCard.Description,
                CardCategory.Building,
                benchDef.MachineType,
                level: 0,
                nrgCost: 15,
                tempC: 0,
                durationSeconds: 0f,
                benchCard.Inputs,
                benchCard.Outputs,
                artwork: null,
                spawnPrefab: benchCard.OutputPrefab);

            CraftingDatabase testDb = ScriptableObject.CreateInstance<CraftingDatabase>();
            createdAssets.Add(testDb);
            testDb.Configure(db.Materials, new[] { instantBench }, new[] { benchDef });

            TeamMaterialStock stock = CreateStock(testDb, initialEnergy: 30);
            stock.Ledger.Add("recycled_polymer", 2.0f);
            stock.Ledger.Add("wood_pulp", 3.0f);

            var benchMachGo = new GameObject("SoloWorkbench");
            spawnedRoots.Add(benchMachGo);
            CraftingMachine workbench = benchMachGo.AddComponent<CraftingMachine>();
            workbench.Configure(benchDef, testDb, startLevel: 0);

            GameObject spawnedObj = null;
            workbench.CardCompleted += (_, _, obj) => spawnedObj = obj;

            bool ok = false;
            workbench.RequestExecuteCard(instantBench, (success, _) => ok = success);
            yield return null;

            Assert.IsTrue(ok);
            Assert.IsNotNull(spawnedObj, "Building card must spawn its OutputPrefab in the world.");
            spawnedRoots.Add(spawnedObj);
            Assert.AreEqual(1.0f, stock.GetAmount("built_park_bench"), 0.001f);
            Assert.AreEqual(15, stock.Energy);
        }

        [UnityTest]
        public IEnumerator Solo_HopperFilteringAndSortingCrate_RejectIncompatibleAndCreditOnce()
        {
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_pulper", out MachineDefinition pulperDef));

            TeamMaterialStock stock = CreateStock(db, initialEnergy: 10);

            var invGo = new GameObject("TestInventory");
            spawnedRoots.Add(invGo);
            PlayerInventory inv = invGo.AddComponent<PlayerInventory>();

            var machGo = new GameObject("HopperPulper");
            spawnedRoots.Add(machGo);
            CraftingMachine pulper = machGo.AddComponent<CraftingMachine>();
            pulper.Configure(pulperDef, db, startLevel: 0, hopperEnabled: true);

            // 1. Incompatible item (Plastic Bottle into Pulper) MUST be rejected without disposing or crediting
            TrashItem wrongTrash = CreateTrash("Plastic Bottle", WasteStream.Plastic, "water_bottle");
            GameAuthority.Current.RequestDepositTrash(pulper, wrongTrash, null);
            yield return null;

            Assert.IsFalse(wrongTrash.IsDisposed, "Incompatible trash must NOT be disposed by hopper.");
            Assert.AreEqual(0f, stock.GetAmount("plastic_scrap"), 0.001f);

            // 2. Compatible item (Wooden Pallet via archetypeId) credits Wood Scrap exactly once
            TrashItem pallet = CreateTrash("Renamed Pallet Display", WasteStream.BulkyUpcyclable, "wooden_pallet");
            GameAuthority.Current.RequestDepositTrash(pulper, pallet, null);
            GameAuthority.Current.RequestDepositTrash(pulper, pallet, null);
            yield return null;

            Assert.IsTrue(pallet.IsDisposed);
            Assert.AreEqual(2.5f, stock.GetAmount("wood_scrap"), 0.001f);

            // 3. SortingCrate deposit credits Plastic Scrap into Material Stock AND pays coins, never putting trash in PlayerInventory
            var crateGo = new GameObject("PlasticCrate");
            spawnedRoots.Add(crateGo);
            SortingCrate crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Plastic, payOnSort: true);

            TrashItem bottle = CreateTrash("Plastic Bottle", WasteStream.Plastic, "water_bottle");
            int coinsBefore = inv.Coins;
            crate.ExecuteAuthoritativeSort(bottle, inv, null);
            yield return null;

            Assert.Greater(inv.Coins, coinsBefore, "Existing SortingCrate coin payout must remain intact.");
            Assert.AreEqual(2.0f, stock.GetAmount("plastic_scrap"), 0.001f);
            for (int i = 0; i < inv.SlotCount; i++)
                Assert.IsNull(inv.GetSlot(i).Item, "PlayerInventory gear hotbar must NEVER hold trash.");
        }

        [UnityTest]
        public IEnumerator Solo_QuincaillerieCatalystPurchase_CreditsTeamMaterialStockNotHotbar()
        {
            CraftingDatabase db = LoadDatabase();
            TeamMaterialStock stock = CreateStock(db, initialEnergy: 10);

            ShopCatalog toolsCatalog = AssetDatabase.LoadAssetAtPath<ShopCatalog>("Assets/UpcycleCity/Settings/Shops/Shop_Tools.asset");
            Assert.IsNotNull(toolsCatalog, "Shop_Tools.asset must exist.");
            Assert.IsTrue(toolsCatalog.TryFindOffer("supply_catalyst", 1, out _),
                "Quincaillerie (Shop_Tools) must sell Catalyst (supply_catalyst).");

            var invGo = new GameObject("BuyerInv");
            spawnedRoots.Add(invGo);
            PlayerInventory inv = invGo.AddComponent<PlayerInventory>();
            inv.AddCoins(50);

            ShopPurchaseResult result = default;
            bool done = false;
            GameAuthority.Current.RequestShopPurchase(
                new ShopPurchaseRequest(string.Empty, "supply_catalyst", 1, Vector3.zero, inv, toolsCatalog),
                r =>
                {
                    result = r;
                    done = true;
                });
            yield return null;

            Assert.IsTrue(done && result.Approved);
            Assert.AreEqual(1.0f, stock.GetAmount("catalyst"), 0.001f);
            for (int i = 0; i < inv.SlotCount; i++)
                Assert.IsNull(inv.GetSlot(i).Item, "Catalyst goes directly to TeamMaterialStock, not hotbar.");
        }

        [UnityTest]
        public IEnumerator Solo_Upgrade_DeductsOnceEnforcesCapAndPreservesCoinsOnMaterialShortfall()
        {
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_crucible_furnace", out MachineDefinition furnaceDef));
            Assert.IsTrue(furnaceDef.TryGetUpgradeCost(1, out MachineUpgradeCost lv1Cost));

            TeamMaterialStock stock = CreateStock(db, initialEnergy: 20);
            var invGo = new GameObject("UpgradeWallet");
            spawnedRoots.Add(invGo);
            PlayerInventory inv = invGo.AddComponent<PlayerInventory>();
            inv.AddCoins(100);
            int startingCoins = inv.Coins;
            LocalGameAuthority.Instance.SetLocalWalletOverride(inv);

            var machGo = new GameObject("UpgradeFurnace");
            spawnedRoots.Add(machGo);
            CraftingMachine furnace = machGo.AddComponent<CraftingMachine>();
            furnace.Configure(furnaceDef, db, startLevel: 0);

            // 1. Coins sufficient, materials missing -> fails and leaves coins completely untouched
            bool ok = true;
            string msg = string.Empty;
            furnace.RequestUpgrade((s, m) => { ok = s; msg = m; });
            Assert.IsFalse(ok);
            Assert.AreEqual(0, furnace.Level);
            Assert.AreEqual(startingCoins, inv.Coins, "Coins must NOT be deducted when upgrade materials are missing.");

            // 2. Add exact required materials -> upgrades cleanly once
            foreach (MaterialAmount req in lv1Cost.materials)
                stock.Ledger.Add(req.material.MaterialId, req.amount);

            furnace.RequestUpgrade((s, m) => { ok = s; msg = m; });
            Assert.IsTrue(ok, "Upgrade should succeed with coins + materials: " + msg);
            Assert.AreEqual(1, furnace.Level);
            Assert.AreEqual(startingCoins - lv1Cost.coins, inv.Coins);
            foreach (MaterialAmount req in lv1Cost.materials)
                Assert.AreEqual(0f, stock.GetAmount(req.material.MaterialId), 0.001f);

            // 3. Re-upgrading at MaxLevel -> refused and deducts nothing
            int coinsAtMax = inv.Coins;
            furnace.RequestUpgrade((s, m) => { ok = s; msg = m; });
            Assert.IsFalse(ok);
            StringAssert.Contains("max level", msg);
            Assert.AreEqual(1, furnace.Level);
            Assert.AreEqual(coinsAtMax, inv.Coins);

            yield return null;
        }

        [UnityTest]
        public IEnumerator Solo_DebugActions_RefusedWhenDisabledAndUiHidesDebugBar()
        {
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_plastic_melter", out MachineDefinition melterDef));

            // Ensure debug actions are DISABLED
            CraftingDebugGate.SetDebugActionsAllowed(false);

            TeamMaterialStock stock = CreateStock(db, initialEnergy: 10, enableDebug: false);
            var machGo = new GameObject("DebugTestMelter");
            spawnedRoots.Add(machGo);
            CraftingMachine melter = machGo.AddComponent<CraftingMachine>();
            melter.Configure(melterDef, db, startLevel: 0);

            bool ok = true;
            stock.RequestGrantStarterKit((s, _) => ok = s);
            Assert.IsFalse(ok, "GrantStarterKit must be refused when debug actions are disabled.");
            Assert.AreEqual(10, stock.Energy);

            ok = true;
            stock.RequestAddEnergy(50, (s, _) => ok = s);
            Assert.IsFalse(ok, "AddEnergy debug action must be refused when disabled.");
            Assert.AreEqual(10, stock.Energy);

            ok = true;
            stock.RequestClearStock((s, _) => ok = s);
            Assert.IsFalse(ok, "ClearStock debug action must be refused when disabled.");
            Assert.AreEqual(10, stock.Energy);

            ok = true;
            melter.RequestDebugSetLevel(1, (s, _) => ok = s);
            Assert.IsFalse(ok, "DebugSetLevel must be refused when disabled.");
            Assert.AreEqual(0, melter.Level);

            // UI must also hide the debug bar when gate is disabled
            CraftingUI ui = CreateCraftingUI(debugButtons: true);
            ui.Open(melter);
            yield return null;
            Assert.IsFalse(ui.IsDebugBarVisible, "Debug bar must stay hidden when CraftingDebugGate.IsDebugActionsAllowed is false.");
            ui.Close();
        }

        [UnityTest]
        public IEnumerator Solo_CraftingUI_PreservesCardTilesDuringProcessing_AndSupportsInspectorAndGeneratorMode()
        {
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_plastic_melter", out MachineDefinition melterDef));
            Assert.IsTrue(db.TryGetMachine("machine_pedal_generator", out MachineDefinition genDef));
            Assert.IsTrue(db.TryGetCard("refining_plastic_thermal_reclaim", out CraftingCard plasticCard));

            CraftingCard timedCard = ScriptableObject.CreateInstance<CraftingCard>();
            createdAssets.Add(timedCard);
            timedCard.Configure(
                "ui_timed_plastic",
                plasticCard.DisplayName,
                plasticCard.Description,
                plasticCard.Category,
                plasticCard.RequiredMachineType,
                level: 0,
                nrgCost: 10,
                tempC: 180,
                durationSeconds: 0.6f,
                plasticCard.Inputs,
                plasticCard.Outputs,
                plasticCard.CardArtwork);

            CraftingDatabase testDb = ScriptableObject.CreateInstance<CraftingDatabase>();
            createdAssets.Add(testDb);
            testDb.Configure(db.Materials, new[] { timedCard }, new[] { melterDef, genDef });

            TeamMaterialStock stock = CreateStock(testDb, initialEnergy: 50);
            stock.Ledger.Add("plastic_scrap", 8.0f);
            stock.Ledger.Add("catalyst", 1.0f);

            var machGo = new GameObject("UiMelter");
            spawnedRoots.Add(machGo);
            CraftingMachine melter = machGo.AddComponent<CraftingMachine>();
            melter.Configure(melterDef, testDb, startLevel: 0);

            CraftingUI ui = CreateCraftingUI(debugButtons: false);
            ui.Open(melter);
            yield return null;

            Assert.IsTrue(ui.IsOpen);
            Assert.GreaterOrEqual(ui.ActiveCardTileCount, 1);
            VisualElement tileBefore = ui.GetCardTileElement(0);
            Assert.IsNotNull(tileBefore);

            // Start processing and verify tile instances are NEVER rebuilt across frames
            ui.ExecuteSelectedCard();
            Assert.IsTrue(melter.IsProcessing);

            for (int f = 0; f < 10; f++)
            {
                yield return null;
                Assert.AreSame(tileBefore, ui.GetCardTileElement(0),
                    $"Card tile VisualElement was rebuilt on processing frame {f}!");
            }

            // Full-screen Card Inspector opens and closes cleanly
            ui.OpenCardInspector(timedCard);
            yield return null;
            Assert.IsTrue(ui.IsCardInspectorOpen);
            Assert.AreSame(timedCard, ui.InspectedCard);
            ui.CloseCardInspector();
            Assert.IsFalse(ui.IsCardInspectorOpen);

            yield return WaitUntil(() => !melter.IsProcessing, 2.5f, "UI timed card did not complete.");
            Assert.AreSame(tileBefore, ui.GetCardTileElement(0), "Card tile should stay in place even across completion.");
            ui.Close();

            // Generator mode: OpenGenerator -> crank & upgrade via UI
            var invGo = new GameObject("GenWallet");
            spawnedRoots.Add(invGo);
            PlayerInventory inv = invGo.AddComponent<PlayerInventory>();
            inv.AddCoins(50);
            LocalGameAuthority.Instance.SetLocalWalletOverride(inv);
            stock.Ledger.Add("refined_aluminum", 3.0f);

            var genGo = new GameObject("UiGenerator");
            spawnedRoots.Add(genGo);
            ManualEnergyGenerator gen = genGo.AddComponent<ManualEnergyGenerator>();
            gen.Configure(genDef, testDb, nrgLvl0: 15, nrgLvl1: 25, cooldown: 0.25f);

            ui.OpenGenerator(gen);
            yield return null;
            Assert.IsTrue(ui.IsOpen && ui.IsGeneratorMode);

            int nrgBefore = stock.Energy;
            bool cranked = false;
            gen.RequestCrank((ok, _) => cranked = ok);
            Assert.IsTrue(cranked);
            Assert.AreEqual(nrgBefore + 15, stock.Energy);

            bool upgraded = false;
            gen.RequestUpgrade((ok, _) => upgraded = ok);
            Assert.IsTrue(upgraded);
            Assert.AreEqual(1, gen.Level);
            Assert.AreEqual(25, gen.EnergyPerCrank);

            ui.Close();
        }

        [UnityTest]
        public IEnumerator Coop_HostAndClient_TimedCard_BusyRejection_CompletionSync_AndGatedDebug()
        {
            NetworkSettings settings = LoadNetworkSettings();
            CraftingDatabase db = LoadDatabase();
            Assert.IsNotNull(settings);
            Assert.IsNotNull(db);
            Assert.IsTrue(db.TryGetMachine("machine_crucible_furnace", out MachineDefinition furnaceDef));
            Assert.IsTrue(db.TryGetCard("refining_aluminum_can_reclaim", out CraftingCard lv1Card));

            // 0.5 s timed card so we observe busy state, concurrent busy rejection, and completion sync
            CraftingCard timedLv1 = ScriptableObject.CreateInstance<CraftingCard>();
            createdAssets.Add(timedLv1);
            timedLv1.Configure(
                "coop_timed_al",
                lv1Card.DisplayName,
                lv1Card.Description,
                lv1Card.Category,
                lv1Card.RequiredMachineType,
                level: 1,
                nrgCost: 25,
                tempC: 660,
                durationSeconds: 0.5f,
                lv1Card.Inputs,
                lv1Card.Outputs);

            CraftingDatabase testDb = ScriptableObject.CreateInstance<CraftingDatabase>();
            createdAssets.Add(testDb);
            testDb.Configure(db.Materials, new[] { timedLv1 }, new[] { furnaceDef });

            yield return StartHostAndClient(settings, nextPort++);

            var hostRegistry = new SyncedStateRegistryInstance();
            var clientRegistry = new SyncedStateRegistryInstance();

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.08f, initialCoins: 100);
            hostRep.SetStateRegistry(hostRegistry);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive NetworkStateReplicator.");
            clientRep.SetStateRegistry(clientRegistry);

            // Spawn lightweight player objects for host and client right beside the furnace
            SpawnTestPlayerObject(db, hostManager.LocalClientId, Vector3.zero);
            ulong remoteClientId = GetFirstRemoteClientId(hostManager);
            NetworkObject clientPlayerNo = SpawnTestPlayerObject(db, remoteClientId, new Vector3(1.5f, 0f, 0f));

            // Start with debug actions disabled so we verify client cannot cheat
            CraftingDebugGate.SetDebugActionsAllowed(false);

            TeamMaterialStock hostStock = CreateStock(testDb, initialEnergy: 80, enableDebug: false);
            hostStock.RegisterWithRegistry(hostRegistry);
            hostStock.Ledger.Add("aluminum_cans", 2.0f); // short (needs 4.0 kg)
            hostStock.Ledger.Add("catalyst", 0.1f);      // short (needs 0.3 kg)
            hostStock.Ledger.Add("recycled_polymer", 4.0f); // enough to upgrade furnace legitimately

            TeamMaterialStock clientStock = CreateIsolatedStockMirror(testDb, clientRegistry);

            var hostMachGo = new GameObject("HostFurnace");
            spawnedRoots.Add(hostMachGo);
            CraftingMachine hostMachine = hostMachGo.AddComponent<CraftingMachine>();
            hostMachine.Configure(furnaceDef, testDb, startLevel: 0, hopperEnabled: true, explicitPlacementId: "coop_furnace_01");
            hostMachine.RegisterWithRegistry(hostRegistry);

            CraftingMachine clientMachine = CreateIsolatedMachineMirror(furnaceDef, testDb, hostMachine.SyncKey, clientRegistry);

            yield return WaitUntil(() => clientStock.Energy == 80 && Mathf.Abs(clientStock.GetAmount("aluminum_cans") - 2.0f) < 0.01f,
                4f, "Initial TeamMaterialStock did not replicate to client.");

            // 1. Client tries debug cheat ActionSetLevel while disabled -> MUST be refused
            bool gotReply = false;
            bool ok = true;
            string replyMsg = string.Empty;
            clientRep.RequestCustomAction(CraftingMachine.ActionSetLevel, clientMachine.SyncKey, intArg: 1, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });
            yield return WaitUntil(() => gotReply, 4f, "Client did not receive debug-disabled rejection.");
            Assert.IsFalse(ok, "Client must NOT be able to invoke ActionSetLevel when debug gate is off.");
            Assert.AreEqual(0, hostMachine.Level);

            // 2. Client requests Lv1 card on Lv0 machine -> refused with exact level message
            gotReply = false;
            clientRep.RequestCustomAction(CraftingMachine.ActionExecuteCard, clientMachine.SyncKey, intArg: 0, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });
            yield return WaitUntil(() => gotReply, 4f, "Client did not receive Lv1 rejection reply.");
            Assert.IsFalse(ok);
            Assert.AreEqual("Requires machine level 1 (this machine is level 0)", replyMsg);

            // 3. Client legitimately upgrades furnace to Lv 1 (coins + recycled_polymer) -> replicates to client
            gotReply = false;
            clientRep.RequestCustomAction(CraftingMachine.ActionUpgrade, clientMachine.SyncKey, intArg: 1, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });
            yield return WaitUntil(() => gotReply, 4f, "Client did not receive upgrade reply.");
            Assert.IsTrue(ok, "Legitimate client upgrade failed: " + replyMsg);
            yield return WaitUntil(() => clientMachine.Level == 1, 4f, "Upgraded Lv 1 did not replicate to client.");

            // 4. Client executes while short on resources -> host refuses listing BOTH missing items and consumes NOTHING
            gotReply = false;
            clientRep.RequestCustomAction(CraftingMachine.ActionExecuteCard, clientMachine.SyncKey, intArg: 0, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });
            yield return WaitUntil(() => gotReply, 4f, "Client did not receive insufficient-resource reply.");
            Assert.IsFalse(ok);
            StringAssert.Contains("Aluminum Cans", replyMsg);
            StringAssert.Contains("Catalyst", replyMsg);
            Assert.AreEqual(2.0f, hostStock.GetAmount("aluminum_cans"), 0.001f);

            // 5. Top up stock for 2 runs, then client starts 0.5 s timed card!
            hostStock.Ledger.Add("aluminum_cans", 6.0f); // total 8.0 kg
            hostStock.Ledger.Add("catalyst", 0.5f);      // total 0.6 kg

            CraftingCard clientSyncedCompletedCard = null;
            clientMachine.CompletionSynced += (_, card) => clientSyncedCompletedCard = card;

            gotReply = false;
            clientRep.RequestCustomAction(CraftingMachine.ActionExecuteCard, clientMachine.SyncKey, intArg: 0, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });
            yield return WaitUntil(() => gotReply, 4f, "Client did not receive timed card start reply.");
            Assert.IsTrue(ok, "Expected timed card start to succeed: " + replyMsg);

            // Client mirror must see IsProcessing == true
            yield return WaitUntil(() => clientMachine.IsProcessing, 2f, "Client did not see IsProcessing=true.");

            // Second concurrent request from client while busy MUST be rejected as busy!
            gotReply = false;
            clientRep.RequestCustomAction(CraftingMachine.ActionExecuteCard, clientMachine.SyncKey, intArg: 0, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });
            yield return WaitUntil(() => gotReply, 2f, "Client did not receive busy rejection.");
            Assert.IsFalse(ok, "Second concurrent card request while processing must be rejected.");
            StringAssert.Contains("currently processing", replyMsg);

            // Wait for 0.5 s job completion -> client sees CompletionSynced + updated stock + idle state!
            yield return WaitUntil(() =>
                !hostMachine.IsProcessing
                && !clientMachine.IsProcessing
                && clientSyncedCompletedCard == timedLv1
                && Mathf.Abs(clientStock.GetAmount("refined_aluminum") - 3.4f) < 0.01f
                && clientStock.Energy == 55,
                4f, "Timed card completion and output stock did not replicate cleanly to client.");

            // Verify moving the client player far away causes co-op out-of-range rejection!
            clientPlayerNo.transform.position = new Vector3(80f, 0f, 0f);
            gotReply = false;
            clientRep.RequestCustomAction(CraftingMachine.ActionExecuteCard, clientMachine.SyncKey, intArg: 0, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });
            yield return WaitUntil(() => gotReply, 4f, "Client did not receive out-of-range reply.");
            Assert.IsFalse(ok);
            StringAssert.Contains("Move closer", replyMsg);
        }

        [UnityTest]
        public IEnumerator Coop_StableMachineIdAcrossMovement_AndRuntimeSpawnedMachineViaNetworkMachineIdentity()
        {
            NetworkSettings settings = LoadNetworkSettings();
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_plastic_melter", out MachineDefinition melterDef));
            Assert.IsNotNull(melterDef.MachinePrefab, "Melter prefab must exist.");
            Assert.IsNotNull(melterDef.MachinePrefab.GetComponent<NetworkMachineIdentity>(),
                "Machine prefab must have NetworkMachineIdentity.");

            yield return StartHostAndClient(settings, nextPort++);

            var hostRegistry = new SyncedStateRegistryInstance();
            var clientRegistry = new SyncedStateRegistryInstance();

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.08f, initialCoins: 100);
            hostRep.SetStateRegistry(hostRegistry);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive NetworkStateReplicator.");
            clientRep.SetStateRegistry(clientRegistry);

            SpawnTestPlayerObject(db, hostManager.LocalClientId, Vector3.zero);
            ulong remoteClientId = GetFirstRemoteClientId(hostManager);
            NetworkObject clientPlayerNo = SpawnTestPlayerObject(db, remoteClientId, new Vector3(1.5f, 0f, 0f));

            TeamMaterialStock hostStock = CreateStock(db, initialEnergy: 50);
            hostStock.RegisterWithRegistry(hostRegistry);
            hostStock.Ledger.Add("plastic_scrap", 8.0f);
            hostStock.Ledger.Add("catalyst", 1.0f);

            // Host spawns a machine prefab AT RUNTIME (as in future workshop build mode)
            GameObject hostSpawnedGo = UnityEngine.Object.Instantiate(melterDef.MachinePrefab, Vector3.zero, Quaternion.identity);
            spawnedRoots.Add(hostSpawnedGo);
            CraftingMachine hostMachine = hostSpawnedGo.GetComponent<CraftingMachine>();
            NetworkMachineIdentity hostNetId = hostSpawnedGo.GetComponent<NetworkMachineIdentity>();
            hostNetId.SetAuthoritativePlacementId("runtime_build_01");
            hostSpawnedGo.GetComponent<NetworkObject>().Spawn();

            // Wait for client to receive the runtime-spawned machine AND its replicated PlacementId!
            CraftingMachine clientMachine = null;
            yield return WaitUntil(() =>
            {
                NetworkMachineIdentity clientNetId = FindSpawnedBehaviour<NetworkMachineIdentity>(clientManager);
                if (clientNetId != null && clientNetId.ReplicatedPlacementId == "runtime_build_01")
                {
                    clientMachine = clientNetId.GetComponent<CraftingMachine>();
                    return clientMachine != null && clientMachine.PlacementId == "runtime_build_01";
                }
                return false;
            }, 5f, "Client did not receive runtime-spawned machine or its replicated PlacementId.");

            Assert.AreEqual(hostMachine.SyncKey, clientMachine.SyncKey,
                "Host and client runtime-spawned machines must derive the exact same stable SyncKey.");

            // Move the machine 25 m away (simulating build-mode Move) — SyncKey MUST NOT change!
            string keyBeforeMove = hostMachine.SyncKey;
            Vector3 movedPos = new Vector3(25f, 0f, 10f);
            hostMachine.transform.position = movedPos;
            clientPlayerNo.transform.position = movedPos + new Vector3(1f, 0f, 0f);
            Assert.AreEqual(keyBeforeMove, hostMachine.SyncKey, "SyncKey must be independent of world position.");

            // Save/restore round-trip check
            MachineSaveState saved = hostMachine.CaptureSaveState();
            Assert.AreEqual("runtime_build_01", saved.placementId);
            Assert.AreEqual(melterDef.MachineId, saved.machineId);

            // Client executes Plastic Thermal Reclaim card on the moved runtime-spawned machine -> host handles it!
            int cardIdx = db.GetCardIndex("refining_plastic_thermal_reclaim");
            Assert.GreaterOrEqual(cardIdx, 0);
            bool gotReply = false;
            bool ok = false;
            string replyMsg = string.Empty;
            clientRep.RequestCustomAction(CraftingMachine.ActionExecuteCard, clientMachine.SyncKey, intArg: cardIdx, onResult: (s, m) =>
            {
                ok = s;
                replyMsg = m;
                gotReply = true;
            });

            yield return WaitUntil(() => gotReply, 4f, "Client request to moved runtime-spawned machine got no reply.");
            Assert.IsTrue(ok, "Moved runtime-spawned machine failed to execute client card request: " + replyMsg);
            Assert.IsTrue(hostMachine.IsProcessing);
        }

        [UnityTest]
        public IEnumerator Coop_HostAndClient_ClientHopperDeposit_FiltersWrongTrashAndCreditsCompatibleOnce()
        {
            NetworkSettings settings = LoadNetworkSettings();
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_pulper", out MachineDefinition pulperDef));

            yield return StartHostAndClient(settings, nextPort++);

            var hostRegistry = new SyncedStateRegistryInstance();
            var clientRegistry = new SyncedStateRegistryInstance();

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.08f, initialCoins: 25);
            hostRep.SetStateRegistry(hostRegistry);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive NetworkStateReplicator.");
            clientRep.SetStateRegistry(clientRegistry);

            Vector3 pulperPos = new Vector3(2f, 0f, 0f);
            SpawnTestPlayerObject(db, hostManager.LocalClientId, pulperPos);
            ulong remoteClientId = GetFirstRemoteClientId(hostManager);
            SpawnTestPlayerObject(db, remoteClientId, pulperPos + new Vector3(1.5f, 0f, 0f));

            TeamMaterialStock hostStock = CreateStock(db, initialEnergy: 20);
            hostStock.RegisterWithRegistry(hostRegistry);
            TeamMaterialStock clientStock = CreateIsolatedStockMirror(db, clientRegistry);

            var hostMachGo = new GameObject("HostPulper");
            spawnedRoots.Add(hostMachGo);
            hostMachGo.transform.position = pulperPos;
            CraftingMachine hostMachine = hostMachGo.AddComponent<CraftingMachine>();
            hostMachine.Configure(pulperDef, db, startLevel: 0, hopperEnabled: true, explicitPlacementId: "coop_pulper_01");
            hostMachine.RegisterWithRegistry(hostRegistry);

            CraftingMachine clientMachine = CreateIsolatedMachineMirror(pulperDef, db, hostMachine.SyncKey, clientRegistry);
            clientMachine.transform.position = pulperPos;

            // Spawn a compatible Shipping Carton (WasteStream.Paper) right next to the Pulper hopper
            GameObject trashGo = UnityEngine.Object.Instantiate(settings.NetworkTrashPrefab, pulperPos + Vector3.up * 0.6f, Quaternion.identity);
            spawnedRoots.Add(trashGo);
            NetworkTrashArchetype arch = trashGo.GetComponent<NetworkTrashArchetype>();
            arch.InitializeDescriptor(NetworkTrashDescriptor.CreateFallback(
                "Shipping Carton", WasteStream.Paper, WasteSourceKind.FactoryScrap, 1f, 5, 0.5f, false, 1f, 0f, -1, "shipping_carton"));
            trashGo.GetComponent<NetworkObject>().Spawn();

            TrashItem clientTrash = null;
            yield return WaitUntil(() =>
            {
                NetworkedPhysicsItem netItem = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                if (netItem != null)
                    clientTrash = netItem.GetComponent<TrashItem>();
                return clientTrash != null;
            }, 5f, "Client did not receive spawned networked TrashItem.");

            // Client requests hopper deposit twice back-to-back -> must credit EXACTLY ONCE!
            clientRep.RequestDepositTrash(clientMachine, clientTrash, null);
            clientRep.RequestDepositTrash(clientMachine, clientTrash, null);

            yield return WaitUntil(() => hostStock.GetAmount("paper_carton") >= 2.49f,
                4f, "Host TeamMaterialStock was not credited by client hopper deposit.");

            yield return new WaitForSeconds(0.25f);
            Assert.AreEqual(2.5f, hostStock.GetAmount("paper_carton"), 0.001f, "Trash deposit must credit stock exactly once.");

            yield return WaitUntil(() => Mathf.Abs(clientStock.GetAmount("paper_carton") - 2.5f) < 0.01f,
                4f, "Credited Material Stock from hopper deposit did not sync back to client.");
        }

        [UnityTest]
        public IEnumerator Coop_RuntimeMachine_BeforeSpawnIdentity_DespawnRemovesRecordOnHostClientAndLateJoiner()
        {
            NetworkSettings settings = LoadNetworkSettings();
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_plastic_melter", out MachineDefinition melterDef));
            Assert.IsNotNull(melterDef.MachinePrefab);

            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            var hostRegistry = new SyncedStateRegistryInstance();
            var clientRegistry = new SyncedStateRegistryInstance();

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.05f, initialCoins: 50);
            hostRep.SetStateRegistry(hostRegistry);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive NetworkStateReplicator.");
            clientRep.SetStateRegistry(clientRegistry);

            // Keep a live TeamMaterialStock so we can confirm late joiners DO receive live records
            TeamMaterialStock hostStock = CreateStock(db, initialEnergy: 77);
            hostStock.RegisterWithRegistry(hostRegistry);
            uint stockHash = SyncedStateRegistry.ComputeKeyHash(hostStock.SyncKey);

            // Spawn runtime machine via IGameAuthority.SpawnNetworkedPrefab(..., beforeSpawn: ...) using IMachineIdentityReceiver only
            const string assignedPlacementId = "build_despawn_01";
            GameObject hostMachineGo = hostRep.SpawnNetworkedPrefab(
                melterDef.MachinePrefab,
                new Vector3(4f, 0f, -2f),
                Quaternion.identity,
                beforeSpawn: go =>
                {
                    IMachineIdentityReceiver receiver = go.GetComponent<IMachineIdentityReceiver>();
                    Assert.IsNotNull(receiver, "Machine prefab must implement IMachineIdentityReceiver.");
                    receiver.ApplyPlacementId(assignedPlacementId);
                    CraftingMachine m = go.GetComponent<CraftingMachine>();
                    if (m != null)
                        m.RegisterWithRegistry(hostRegistry);
                });
            Assert.IsNotNull(hostMachineGo);
            spawnedRoots.Add(hostMachineGo);

            CraftingMachine hostMachine = hostMachineGo.GetComponent<CraftingMachine>();
            Assert.AreEqual(assignedPlacementId, hostMachine.PlacementId);
            uint machineHash = SyncedStateRegistry.ComputeKeyHash(hostMachine.SyncKey);

            // Verify client received the machine with the exact pre-spawn ID and state replicated on BOTH host and client
            yield return WaitUntil(() =>
            {
                NetworkMachineIdentity clientNetId = FindSpawnedBehaviour<NetworkMachineIdentity>(clientManager);
                return clientNetId != null
                    && clientNetId.ReplicatedPlacementId == assignedPlacementId
                    && hostRep.TryGetReplicatedState(machineHash, out _)
                    && clientRep.TryGetReplicatedState(machineHash, out _);
            }, 5f, "Runtime machine state did not replicate to host and client.");

            // Despawn the runtime machine (simulating build-mode pick-up / demolish)
            hostRep.DespawnNetworkedObject(hostMachineGo);

            // Record MUST be removed from BOTH host and connected client NetworkLists
            yield return WaitUntil(() =>
                !hostRep.TryGetReplicatedState(machineHash, out _)
                && !clientRep.TryGetReplicatedState(machineHash, out _),
                4f, "Despawned machine record was not removed from host and client NetworkStateReplicator.");

            Assert.IsTrue(hostRep.TryGetReplicatedState(stockHash, out _), "Live stock record must remain on host after machine despawn.");
            Assert.IsTrue(clientRep.TryGetReplicatedState(stockHash, out _), "Live stock record must remain on client after machine despawn.");

            // Connect a late-joining 2nd client and verify it sees live stock, NOT the despawned machine!
            lateClientManager = CreateNetworkManager("CraftTestLateClientNM", settings, port, isServer: false);
            Assert.IsTrue(lateClientManager.StartClient(), "Late-joining client failed to start.");
            yield return WaitUntil(() =>
                hostManager.ConnectedClientsIds.Count == 3 && lateClientManager.IsConnectedClient,
                6f, "Late-joining client did not connect to host.");

            NetworkStateReplicator lateClientRep = null;
            yield return WaitUntil(() =>
            {
                lateClientRep = FindSpawnedBehaviour<NetworkStateReplicator>(lateClientManager);
                return lateClientRep != null && lateClientRep.IsSpawned && lateClientRep.TryGetReplicatedState(stockHash, out _);
            }, 5f, "Late-joining client did not receive NetworkStateReplicator or live stock state.");

            Assert.IsFalse(lateClientRep.TryGetReplicatedState(machineHash, out _),
                "Late-joining client must NEVER receive state records for already-despawned machines.");
        }

        [UnityTest]
        public IEnumerator Coop_CraftingUI_ClosedBeforeCrankReplyArrives_DoesNotThrow()
        {
            NetworkSettings settings = LoadNetworkSettings();
            CraftingDatabase db = LoadDatabase();
            Assert.IsTrue(db.TryGetMachine("machine_pedal_generator", out MachineDefinition genDef));

            yield return StartHostAndClient(settings, nextPort++);

            var hostRegistry = new SyncedStateRegistryInstance();
            var clientRegistry = new SyncedStateRegistryInstance();

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.05f, initialCoins: 50);
            hostRep.SetStateRegistry(hostRegistry);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive NetworkStateReplicator.");
            clientRep.SetStateRegistry(clientRegistry);

            TeamMaterialStock hostStock = CreateStock(db, initialEnergy: 20);
            hostStock.RegisterWithRegistry(hostRegistry);

            SpawnTestPlayerObject(db, hostManager.LocalClientId, Vector3.zero);
            ulong remoteClientId = GetFirstRemoteClientId(hostManager);
            SpawnTestPlayerObject(db, remoteClientId, new Vector3(1.0f, 0f, 0f));

            var genGo = new GameObject("CoopGenerator_NullCheck");
            spawnedRoots.Add(genGo);
            ManualEnergyGenerator gen = genGo.AddComponent<ManualEnergyGenerator>();
            gen.Configure(genDef, db, nrgLvl0: 15, nrgLvl1: 25, cooldown: 0.25f, explicitPlacementId: "gen_async_close_01");
            gen.RegisterWithRegistry(hostRegistry);

            // Switch active authority to clientRep so the UI request goes asynchronously over RPC
            GameAuthority.SetAuthority(clientRep);

            CraftingUI ui = CreateCraftingUI(debugButtons: false);
            ui.OpenGenerator(gen);
            yield return null;
            Assert.IsTrue(ui.IsOpen && ui.IsGeneratorMode);

            // Trigger crank click via CraftingUI, then immediately close the UI BEFORE the RPC reply lands
            var crankMethod = typeof(CraftingUI).GetMethod("OnGeneratorCrankClicked",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(crankMethod);
            crankMethod.Invoke(ui, null);
            ui.Close();
            Assert.IsFalse(ui.IsOpen);

            // Wait for the server to process the crank and send the RPC reply back -> must not throw NullReferenceException
            yield return WaitUntil(() => hostStock.Energy >= 35, 4f, "Async generator crank did not complete on host.");
            yield return null;
            yield return null;
        }

        // ------------------------------------------------------------ Helpers

        CraftingUI CreateCraftingUI(bool debugButtons)
        {
            var uiGo = new GameObject("TestCraftingUI");
            uiGo.SetActive(false);
            spawnedRoots.Add(uiGo);
            CraftingUI ui = uiGo.AddComponent<CraftingUI>();
            CraftingUIConfig cfg = CraftingUIConfig.LoadDefault();
            ui.Configure(cfg != null ? cfg.PanelSettings : null, cfg != null ? cfg.StyleSheet : null, debugButtons: debugButtons);
            uiGo.SetActive(true);
            return ui;
        }

        TeamMaterialStock CreateStock(CraftingDatabase db, int initialEnergy, bool enableDebug = false)
        {
            var go = new GameObject("TestTeamMaterialStock");
            go.SetActive(false);
            spawnedRoots.Add(go);
            TeamMaterialStock stock = go.AddComponent<TeamMaterialStock>();
            stock.Configure(db, kit: null, applyKitOnStart: false, initialEnergy: initialEnergy, energyCap: 500, enableDebugActions: enableDebug);
            go.SetActive(true);
            return stock;
        }

        TeamMaterialStock CreateIsolatedStockMirror(CraftingDatabase db, SyncedStateRegistryInstance registry)
        {
            var go = new GameObject("ClientStockMirror");
            go.SetActive(false);
            spawnedRoots.Add(go);
            TeamMaterialStock stock = go.AddComponent<TeamMaterialStock>();
            stock.ConfigureAsIsolatedMirror(db, registry);
            go.SetActive(true);
            return stock;
        }

        CraftingMachine CreateIsolatedMachineMirror(MachineDefinition def, CraftingDatabase db, string syncKey, SyncedStateRegistryInstance registry)
        {
            var go = new GameObject("ClientMachineMirror");
            go.SetActive(false);
            spawnedRoots.Add(go);
            CraftingMachine machine = go.AddComponent<CraftingMachine>();
            machine.ConfigureAsIsolatedMirror(def, db, syncKey, registry);
            go.SetActive(true);
            return machine;
        }

        TrashItem CreateTrash(string displayName, WasteStream stream, string archetypeId)
        {
            var go = new GameObject("Trash_" + displayName);
            spawnedRoots.Add(go);
            go.AddComponent<BoxCollider>();
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            TrashItem item = go.AddComponent<TrashItem>();
            item.Configure(displayName, stream, WasteSourceKind.FactoryScrap, 1f, 8, itemArchetypeId: archetypeId);
            return item;
        }

        NetworkObject SpawnTestPlayerObject(CraftingDatabase db, ulong clientId, Vector3 position)
        {
            Assert.IsTrue(db.TryGetCard("building_upcycled_park_bench", out CraftingCard benchCard));
            Assert.IsNotNull(benchCard.OutputPrefab);
            GameObject playerGo = UnityEngine.Object.Instantiate(benchCard.OutputPrefab, position, Quaternion.identity);
            playerGo.name = $"TestPlayer_{clientId}";
            spawnedRoots.Add(playerGo);
            NetworkObject netObj = playerGo.GetComponent<NetworkObject>();
            netObj.SpawnAsPlayerObject(clientId);
            return netObj;
        }

        static ulong GetFirstRemoteClientId(NetworkManager host)
        {
            foreach (ulong id in host.ConnectedClientsIds)
            {
                if (id != host.LocalClientId)
                    return id;
            }
            Assert.Fail("No remote client connected to host.");
            return 0UL;
        }

        IEnumerator StartHostAndClient(NetworkSettings settings, ushort port)
        {
            hostManager = CreateNetworkManager("CraftTestHostNM", settings, port, isServer: true);
            Assert.IsTrue(hostManager.StartHost(), $"Host failed to start on port {port}.");

            clientManager = CreateNetworkManager("CraftTestClientNM", settings, port, isServer: false);
            Assert.IsTrue(clientManager.StartClient(), $"Client failed to start targeting port {port}.");

            yield return WaitUntil(() =>
                hostManager.ConnectedClientsIds.Count == 2 && clientManager.IsConnectedClient,
                6f, "Client did not connect to Host over loopback UnityTransport.");
        }

        NetworkManager CreateNetworkManager(string name, NetworkSettings settings, ushort port, bool isServer)
        {
            var go = new GameObject(name);
            spawnedRoots.Add(go);

            var nm = go.AddComponent<NetworkManager>();
            var utp = go.AddComponent<UnityTransport>();
            if (isServer)
                utp.SetConnectionData("127.0.0.1", port, "0.0.0.0");
            else
                utp.SetConnectionData("127.0.0.1", port);

            nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = utp,
                TickRate = 30,
                EnableSceneManagement = false,
                ConnectionApproval = false,
            };

            if (settings.NetworkPrefabsList != null)
                nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(settings.NetworkPrefabsList);

            return nm;
        }

        static T FindSpawnedBehaviour<T>(NetworkManager nm) where T : NetworkBehaviour
        {
            if (nm == null || nm.SpawnManager == null)
                return null;
            foreach (NetworkObject obj in nm.SpawnManager.SpawnedObjectsList)
            {
                if (obj != null && obj.TryGetComponent(out T comp))
                    return comp;
            }
            return null;
        }

        static IEnumerator WaitUntil(Func<bool> predicate, float timeoutSeconds, string failMessage)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup >= deadline)
                    Assert.Fail(failMessage);
                yield return null;
            }
        }

        static CraftingDatabase LoadDatabase() =>
            AssetDatabase.LoadAssetAtPath<CraftingDatabase>("Assets/UpcycleCity/Resources/CraftingDatabase.asset");

        static NetworkSettings LoadNetworkSettings() =>
            AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
    }
}
