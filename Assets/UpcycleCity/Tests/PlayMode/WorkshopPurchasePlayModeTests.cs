using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Crafting;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Networking;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>
    /// "Buy the workshop, then buy each machine" over a real host + client (loopback UnityTransport) and in solo:
    /// the host decides every purchase with team coins, the owned flag + machine mask replicate (incl. late join),
    /// and a machine on an unbought stand stays hidden and refuses use until it is bought.
    /// </summary>
    public sealed class WorkshopPurchasePlayModeTests
    {
        static ushort nextPort = 18520;

        const int PropertyPrice = 50;
        const int GeneratorPrice = 30;
        const int PulperPrice = 40;

        readonly List<GameObject> spawnedRoots = new List<GameObject>();
        readonly List<UnityEngine.Object> createdAssets = new List<UnityEngine.Object>();
        NetworkManager hostManager;
        NetworkManager clientManager;

        [TearDown]
        public void TearDown()
        {
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

            if (TeamMaterialStock.Instance != null)
                UnityEngine.Object.DestroyImmediate(TeamMaterialStock.Instance.gameObject);
            LocalGameAuthority.Instance.SetLocalWalletOverride(null);
            GameAuthority.ResetToLocal();
            GameAuthority.LocalClientIdResolver = null;
            SyncedStateRegistry.Clear();
            TrashHopperRegistry.Clear();
        }

        [UnityTest]
        public IEnumerator HostAndClient_ClientBuysGarageThenMachine_HostAuthoritativeAndReplicated()
        {
            NetworkSettings settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
            Assert.IsNotNull(settings);
            yield return StartHostAndClient(settings, nextPort++);

            var clientRegistry = new SyncedStateRegistryInstance();
            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 0);
            repGo.GetComponent<NetworkObject>().Spawn();
            IGameAuthority host = hostRep;

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawned<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive the replicator.");
            clientRep.SetStateRegistry(clientRegistry);
            IGameAuthority client = clientRep;
            Assert.IsFalse(client.IsAuthoritative);
            ulong clientId = clientManager.LocalClientId;

            // The client's player stands next to the lot (server-side range checks use its rig position)
            yield return SpawnFrozenClientPlayer(settings, clientId);

            // Host: the real lot with its machines (registered in the scene registry)
            WorkshopConfig config = CreateConfig("net_garage");
            Lot hostLot = CreateLot("Host", config, mirrorRegistry: null);
            Assert.IsTrue(hostLot.generatorLock.IsLocked, "Machines start hidden on an unbought lot.");
            Assert.IsFalse(hostLot.generatorRenderer.enabled);

            // Client: what a remote phone holds — a mirror lot fed only by replicated state
            Lot clientLot = CreateLot("Client", config, mirrorRegistry: clientRegistry);
            yield return WaitUntil(() => clientLot.property.State.Equals(hostLot.property.State), 3f, "Lot state did not reach the client.");

            // 1. A machine stand is refused before the lot is bought (even with coins)
            host.CreditTeamCoins(PropertyPrice + GeneratorPrice);
            yield return WaitUntil(() => client.TeamCoins == PropertyPrice + GeneratorPrice, 3f, "Team coins did not replicate.");
            Result early = Request(client, WorkshopProperty.ActionBuyMachine, hostLot.property.SyncKey, 0);
            yield return WaitUntil(() => early.Done, 4f, "No reply to the early stand purchase.");
            Assert.IsFalse(early.Success, "Stands are locked until the lot is owned.");
            StringAssert.Contains("first", early.Message);
            Assert.AreEqual(PropertyPrice + GeneratorPrice, host.TeamCoins, "A refused purchase spends nothing.");

            // 2. The locked machine refuses use on the host too (not just hidden)
            Result lockedCrank = Request(client, ManualEnergyGenerator.ActionCrank, hostLot.generator.SyncKey);
            yield return WaitUntil(() => lockedCrank.Done, 4f, "No reply to the locked crank.");
            Assert.IsFalse(lockedCrank.Success, "An unbought machine must refuse requests.");

            // 3. The client buys the lot: the host spends team coins, everyone sees it owned
            Result buyLot = Request(client, WorkshopProperty.ActionBuyProperty, hostLot.property.SyncKey);
            yield return WaitUntil(() => buyLot.Done, 4f, "No reply to the lot purchase.");
            Assert.IsTrue(buyLot.Success, buyLot.Message);
            Assert.IsTrue(hostLot.property.IsOwned);
            Assert.AreEqual(GeneratorPrice, host.TeamCoins);
            yield return WaitUntil(() => clientLot.property.IsOwned, 3f, "Ownership did not replicate.");
            yield return WaitUntil(() => client.TeamCoins == GeneratorPrice, 3f, "The spend did not replicate.");
            Assert.IsFalse(hostLot.cordon.activeSelf, "The cordon comes down once owned.");
            Assert.IsFalse(clientLot.cordon.activeSelf);
            Assert.IsTrue(clientLot.generatorLock.IsLocked, "Owning the lot does not hand out machines.");

            // 4. Double purchase of the lot is refused
            Result again = Request(client, WorkshopProperty.ActionBuyProperty, hostLot.property.SyncKey);
            yield return WaitUntil(() => again.Done, 4f, "No reply to the second lot purchase.");
            Assert.IsFalse(again.Success);
            Assert.AreEqual(GeneratorPrice, host.TeamCoins);

            // 5. Not enough coins for the Pulper stand (40 > 30): refused, nothing changes
            Result poor = Request(client, WorkshopProperty.ActionBuyMachine, hostLot.property.SyncKey, 1);
            yield return WaitUntil(() => poor.Done, 4f, "No reply to the unaffordable stand.");
            Assert.IsFalse(poor.Success);
            StringAssert.Contains("Not enough coins (need 10 more)", poor.Message);
            Assert.IsFalse(hostLot.property.IsMachineOwned(1));
            Assert.AreEqual(GeneratorPrice, host.TeamCoins);

            // 6. Buy the generator: it appears and works on both
            Result buyGen = Request(client, WorkshopProperty.ActionBuyMachine, hostLot.property.SyncKey, 0);
            yield return WaitUntil(() => buyGen.Done, 4f, "No reply to the generator purchase.");
            Assert.IsTrue(buyGen.Success, buyGen.Message);
            Assert.AreEqual(0, host.TeamCoins);
            Assert.IsTrue(hostLot.property.IsMachineOwned(0));
            Assert.IsFalse(hostLot.generatorLock.IsLocked);
            Assert.IsTrue(hostLot.generatorRenderer.enabled, "The bought machine is visible on the host.");
            Assert.IsTrue(hostLot.generatorCollider.enabled, "The bought machine is interactable on the host.");
            Assert.IsFalse(hostLot.generatorStand.IsVisualShown(), "The empty stand hides once its machine is installed.");
            yield return WaitUntil(() => !clientLot.generatorLock.IsLocked, 3f, "The machine did not unlock on the client.");
            Assert.IsTrue(clientLot.generatorRenderer.enabled, "The bought machine is visible on the client.");
            Assert.IsTrue(clientLot.generatorCollider.enabled);
            Assert.IsFalse(clientLot.generatorStand.IsVisualShown());
            Assert.IsTrue(clientLot.pulperLock.IsLocked, "Only the bought machine unlocks.");

            Result crank = Request(client, ManualEnergyGenerator.ActionCrank, hostLot.generator.SyncKey);
            yield return WaitUntil(() => crank.Done, 4f, "No reply to the crank.");
            Assert.IsTrue(crank.Success, crank.Message);

            // 7. Buying the same machine twice is refused even with coins to spare
            host.CreditTeamCoins(100);
            Result twice = Request(client, WorkshopProperty.ActionBuyMachine, hostLot.property.SyncKey, 0);
            yield return WaitUntil(() => twice.Done, 4f, "No reply to the duplicate machine purchase.");
            Assert.IsFalse(twice.Success);
            Assert.AreEqual(100, host.TeamCoins);

            // 8. Out of range: a stand purchase from far away is refused by the host
            hostLot.pulperStand.transform.position += new Vector3(40f, 0f, 0f);
            Result far = Request(client, WorkshopProperty.ActionBuyMachine, hostLot.property.SyncKey, 1);
            yield return WaitUntil(() => far.Done, 4f, "No reply to the far stand purchase.");
            Assert.IsFalse(far.Success);
            Assert.AreEqual(100, host.TeamCoins);

            // 9. A late joiner (fresh mirror registered now) gets the current state as a baseline
            Lot lateLot = CreateLot("Late", config, mirrorRegistry: clientRegistry, register: false);
            Assert.IsTrue(lateLot.generatorLock.IsLocked);
            lateLot.root.SetActive(true);
            yield return WaitUntil(() => lateLot.property.IsOwned && !lateLot.generatorLock.IsLocked, 3f, "A late joiner did not receive the lot state.");
            Assert.IsTrue(lateLot.pulperLock.IsLocked);
        }

        [UnityTest]
        public IEnumerator Solo_BuyGarageThenMachine_SamePathAsCoop()
        {
            Assert.IsTrue(GameAuthority.IsAuthoritative);
            Assert.IsFalse(GameAuthority.IsMultiplayer);

            var walletGo = new GameObject("SoloWallet");
            spawnedRoots.Add(walletGo);
            PlayerInventory wallet = walletGo.AddComponent<PlayerInventory>();
            LocalGameAuthority.Instance.SetLocalWalletOverride(wallet);
            Assert.AreEqual(0, GameAuthority.Current.TeamCoins, "Runs start with nothing.");

            WorkshopConfig config = CreateConfig("solo_garage");
            Lot lot = CreateLot("Solo", config, mirrorRegistry: null);
            yield return null;

            // Prompts in the PlayerInteractor style
            StringAssert.Contains($"need {PropertyPrice} more", lot.sign.InteractionPrompt);
            StringAssert.Contains("first", lot.pulperStand.InteractionPrompt);

            bool ok = true;
            string msg = string.Empty;
            lot.property.RequestBuyProperty((s, m) => { ok = s; msg = m; });
            Assert.IsFalse(ok, "No coins, no garage.");
            StringAssert.Contains($"Not enough coins (need {PropertyPrice} more)", msg);

            wallet.AddCoins(PropertyPrice + GeneratorPrice);
            Assert.AreEqual($"[E] Buy Test Garage · {PropertyPrice} coins", lot.sign.InteractionPrompt);

            lot.property.RequestBuyMachine(0, (s, m) => { ok = s; msg = m; });
            Assert.IsFalse(ok, "Stands are locked until the garage is owned.");

            // A locked crafting machine refuses cards and upgrades on the authority
            bool cardOk = true;
            lot.pulper.RequestUpgrade((s, m) => { cardOk = s; msg = m; });
            Assert.IsFalse(cardOk);
            StringAssert.Contains("first", msg);
            Assert.IsTrue(lot.pulper.IsWorkshopLocked);

            lot.property.RequestBuyProperty((s, m) => { ok = s; msg = m; });
            Assert.IsTrue(ok, msg);
            Assert.IsTrue(lot.property.IsOwned);
            Assert.AreEqual(GeneratorPrice, wallet.Coins);
            Assert.IsFalse(lot.sign.CanInteract(null));
            Assert.AreEqual(string.Empty, lot.sign.InteractionPrompt);
            Assert.AreEqual($"[E] Buy Generator · {GeneratorPrice} coins", lot.generatorStand.InteractionPrompt);
            StringAssert.Contains("Not enough coins (need 10 more)", lot.pulperStand.InteractionPrompt);

            lot.property.RequestBuyMachine(1, (s, m) => { ok = s; msg = m; });
            Assert.IsFalse(ok, "40 > 30 coins.");
            Assert.AreEqual(GeneratorPrice, wallet.Coins);

            lot.property.RequestBuyMachine(0, (s, m) => { ok = s; msg = m; });
            Assert.IsTrue(ok, msg);
            Assert.AreEqual(0, wallet.Coins);
            Assert.IsFalse(lot.generatorLock.IsLocked);
            Assert.IsTrue(lot.generatorRenderer.enabled);
            Assert.IsFalse(lot.generatorStand.IsVisualShown());

            bool cranked = false;
            lot.generator.RequestCrank((s, m) => { cranked = s; msg = m; });
            Assert.IsTrue(cranked, msg);

            wallet.AddCoins(100);
            lot.property.RequestBuyMachine(0, (s, m) => { ok = s; msg = m; });
            Assert.IsFalse(ok, "Each machine is bought once.");
            Assert.AreEqual(100, wallet.Coins);

            lot.property.RequestBuyMachine(1, (s, m) => { ok = s; msg = m; });
            Assert.IsTrue(ok, msg);
            Assert.IsFalse(lot.pulper.IsWorkshopLocked);
            Assert.AreEqual(100 - PulperPrice, wallet.Coins);
        }

        // ------------------------------------------------------------ helpers

        sealed class Lot
        {
            public GameObject root;
            public WorkshopProperty property;
            public WorkshopSaleSign sign;
            public GameObject cordon;
            public WorkshopMachineStand generatorStand, pulperStand;
            public ManualEnergyGenerator generator;
            public CraftingMachine pulper;
            public WorkshopMachineLock generatorLock, pulperLock;
            public Renderer generatorRenderer;
            public Collider generatorCollider;
        }

        WorkshopConfig CreateConfig(string id)
        {
            CraftingDatabase db = CraftingDatabase.LoadDefault();
            Assert.IsNotNull(db);
            Assert.IsTrue(db.TryGetMachine("machine_pulper", out MachineDefinition pulperDef));
            var config = ScriptableObject.CreateInstance<WorkshopConfig>();
            createdAssets.Add(config);
            config.ConfigureProperty(id, "Test Garage", PropertyPrice, 6f);
            config.SetMachine("generator", null, GeneratorPrice, "Generator");
            config.SetMachine("pulper", pulperDef, PulperPrice, "Pulper");
            return config;
        }

        Lot CreateLot(string label, WorkshopConfig config, SyncedStateRegistryInstance mirrorRegistry, bool register = true)
        {
            var lot = new Lot();
            lot.root = new GameObject($"Lot_{label}");
            lot.root.SetActive(false);
            spawnedRoots.Add(lot.root);
            lot.root.transform.position = Vector3.zero;

            // Generator (on its own stand) — lock first so the station caches it in Awake
            GameObject genGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            genGo.name = $"{label}_Generator";
            genGo.transform.SetParent(lot.root.transform, false);
            genGo.transform.position = new Vector3(2f, 0.5f, -1f);
            genGo.layer = GameLayers.Interactable;
            lot.generatorLock = genGo.AddComponent<WorkshopMachineLock>();
            lot.generator = genGo.AddComponent<ManualEnergyGenerator>();
            lot.generatorRenderer = genGo.GetComponent<Renderer>();
            lot.generatorCollider = genGo.GetComponent<Collider>();

            CraftingDatabase db = CraftingDatabase.LoadDefault();
            GameObject pulperGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pulperGo.name = $"{label}_Pulper";
            pulperGo.transform.SetParent(lot.root.transform, false);
            pulperGo.transform.position = new Vector3(2f, 0.5f, 2f);
            lot.pulperLock = pulperGo.AddComponent<WorkshopMachineLock>();
            lot.pulper = pulperGo.AddComponent<CraftingMachine>();
            lot.pulper.Configure(config.GetMachine(1).machine, db, startLevel: 0, hopperEnabled: false);

            lot.generatorStand = CreateStand($"{label}_Stand_Gen", lot.root.transform, 0, lot.generatorLock, new Vector3(2f, 0f, -1f));
            lot.pulperStand = CreateStand($"{label}_Stand_Pulper", lot.root.transform, 1, lot.pulperLock, new Vector3(2f, 0f, 2f));

            var signGo = new GameObject($"{label}_Sign");
            signGo.transform.SetParent(lot.root.transform, false);
            signGo.transform.position = new Vector3(2f, 0f, 0.5f);
            lot.sign = signGo.AddComponent<WorkshopSaleSign>();
            lot.sign.Configure(null, null, null, signGo.AddComponent<BoxCollider>());

            lot.cordon = new GameObject($"{label}_Cordon");
            lot.cordon.transform.SetParent(lot.root.transform, false);

            var propGo = new GameObject($"{label}_Property");
            propGo.transform.SetParent(lot.root.transform, false);
            lot.property = propGo.AddComponent<WorkshopProperty>();
            lot.property.Configure(config, lot.sign, new[] { lot.generatorStand, lot.pulperStand }, lockedOnly: new[] { lot.cordon });
            if (mirrorRegistry != null)
                lot.property.ConfigureAsIsolatedMirror(mirrorRegistry);

            if (register)
                lot.root.SetActive(true);
            return lot;
        }

        static WorkshopMachineStand CreateStand(string name, Transform parent, int index, WorkshopMachineLock machineLock, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var visual = new GameObject("Visual");
            visual.transform.SetParent(go.transform, false);
            var stand = go.AddComponent<WorkshopMachineStand>();
            stand.Configure(null, index, machineLock, visual, go.AddComponent<BoxCollider>(), null, null, null);
            return stand;
        }

        IEnumerator SpawnFrozenClientPlayer(NetworkSettings settings, ulong clientId)
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "TestFloor";
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.transform.localScale = new Vector3(40f, 1f, 40f);
            floor.layer = GameLayers.Environment;
            spawnedRoots.Add(floor);

            GameObject playerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(0f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(playerGo);
            playerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId);
            playerGo.GetComponent<NetworkPlayerController>().InitializeServerIdentity(
                2, "Buyer", NetworkCharacterAppearance.From(CharacterAppearance.Default), new Vector3(0f, 0.5f, 0f), Quaternion.identity);

            // In-process host + client share one physics world: freeze both copies so range checks see a stable position
            NetworkPlayerController clientCopy = null;
            yield return WaitUntil(() =>
            {
                foreach (NetworkObject obj in clientManager.SpawnManager.SpawnedObjectsList)
                {
                    if (obj != null && obj.TryGetComponent(out NetworkPlayerController p) && p.OwnerClientId == clientId)
                        clientCopy = p;
                }
                return clientCopy != null;
            }, 5f, "The client's player did not spawn on the client.");
            foreach (Rigidbody body in new[] { playerGo.GetComponent<Rigidbody>(), clientCopy.GetComponent<Rigidbody>() })
            {
                if (body != null)
                    body.isKinematic = true;
            }
        }

        sealed class Result
        {
            public bool Done, Success;
            public string Message;
        }

        static Result Request(IGameAuthority authority, string action, string target, int intArg = 0, float floatArg = 0f)
        {
            var result = new Result();
            authority.RequestCustomAction(action, target, intArg, floatArg, (ok, msg) =>
            {
                result.Done = true;
                result.Success = ok;
                result.Message = msg;
            });
            return result;
        }

        IEnumerator StartHostAndClient(NetworkSettings settings, ushort port)
        {
            hostManager = CreateNetworkManager("WorkshopTestHostNM", settings, port, isServer: true);
            Assert.IsTrue(hostManager.StartHost(), $"Host failed to start on port {port}.");
            clientManager = CreateNetworkManager("WorkshopTestClientNM", settings, port, isServer: false);
            Assert.IsTrue(clientManager.StartClient(), $"Client failed to start on port {port}.");
            yield return WaitUntil(() => hostManager.ConnectedClientsIds.Count == 2 && clientManager.IsConnectedClient,
                6f, "Client did not connect to the host over loopback.");
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

        static T FindSpawned<T>(NetworkManager nm) where T : NetworkBehaviour
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
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail(failMessage);
                yield return null;
            }
        }
    }
}
