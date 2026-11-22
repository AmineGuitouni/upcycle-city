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
using UpcycleCity.Ecosystem;
using UpcycleCity.Networking;
using UpcycleCity.Shops;
using UpcycleCity.Waste;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>
    /// Street-bin searching over a real host + client (loopback UnityTransport) and in offline solo:
    /// the client opens a bin, sees finds turn up from replicated state, takes one into its own replicated
    /// Recycling Bag, empties it at the right bin for team coins and material stock — all decided by the host.
    /// </summary>
    public sealed class BinSearchPlayModeTests
    {
        static ushort nextPort = 18410;

        readonly List<GameObject> spawnedRoots = new List<GameObject>();
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

            if (RecyclingBagService.Instance != null)
                UnityEngine.Object.DestroyImmediate(RecyclingBagService.Instance.gameObject);
            if (TeamMaterialStock.Instance != null)
                UnityEngine.Object.DestroyImmediate(TeamMaterialStock.Instance.gameObject);
            if (BinSearchUI.Instance != null)
                UnityEngine.Object.DestroyImmediate(BinSearchUI.Instance.gameObject);

            GameAuthority.ResetToLocal();
            GameAuthority.LocalClientIdResolver = null;
            CarryItems.CatalogOverride = null;
            SyncedStateRegistry.Clear();
        }

        [UnityTest]
        public IEnumerator HostAndClient_BinSearch_ClientSearchesTakesAndDeposits_HostAuthoritative()
        {
            BinLootTable table = BinLootTable.Active;
            Assert.IsNotNull(table, "Resources/BinLootTable is missing (Upcycle City → Waste → Setup Bin Search).");

            NetworkSettings settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
            yield return StartHostAndClient(settings, nextPort++);

            // Host replicates the real scene registry; the client mirrors into its own registry (as a remote machine would)
            var clientRegistry = new SyncedStateRegistryInstance();
            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 0);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawned<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive the replicator.");
            clientRep.SetStateRegistry(clientRegistry);
            IGameAuthority client = clientRep;
            Assert.IsFalse(client.IsAuthoritative, "The client must not be authoritative.");
            ulong clientId = clientManager.LocalClientId;

            RecyclingBagService service = RecyclingBagService.EnsureInstance();

            // Ground for the client's player to stand on (it has physics; without a floor it falls out of range)
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "TestFloor";
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.transform.localScale = new Vector3(40f, 1f, 40f);
            floor.layer = GameLayers.Environment;
            spawnedRoots.Add(floor);

            // Host: a half-full bin next to the client's player, and a far one
            OverflowingBinSource bin = CreateBin("TestBin_Near", Vector3.zero, 0.5f);
            OverflowingBinSource farBin = CreateBin("TestBin_Far", new Vector3(60f, 0f, 0f), 0.5f);
            GameObject playerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(1f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(playerGo);
            playerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId);
            playerGo.GetComponent<NetworkPlayerController>().InitializeServerIdentity(
                2, "Searcher", NetworkCharacterAppearance.From(CharacterAppearance.Default), new Vector3(1f, 0.5f, 0f), Quaternion.identity);

            // Host and client run in one process, so the host's and the client's copies of this player share one physics
            // world and would shove each other apart: freeze both so the server-side range check sees a stable position
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

            yield return WaitUntil(() => bin.Contents.Count >= table.BinSlots / 2, 3f, "Host did not fill the bin with finds.");

            // Client mirrors: the bin's contents and its own bag
            var binMirror = new BinMirror(bin.SyncKey);
            var bagMirror = new RecyclingBag(clientId);
            clientRegistry.Register(binMirror);
            clientRegistry.Register(bagMirror);
            yield return WaitUntil(() => binMirror.Contents.Count == bin.Contents.Count && binMirror.Contents.Count > 0,
                4f, "Bin contents did not replicate to the client.");
            for (int i = 0; i < bin.Contents.Count; i++)
                Assert.AreEqual(bin.Contents[i], binMirror.Contents[i]);

            // Too far: the host rejects using the server-side player position
            Result far = Request(client, RecyclingBagService.ActionOpen, farBin.SyncKey);
            yield return WaitUntil(() => far.Done, 4f, "No reply to the far-bin request.");
            Assert.IsFalse(far.Success);
            Assert.IsFalse(farBin.Contents.Searching);

            // Open: the host starts the search and the client sees it running
            Result open = Request(client, RecyclingBagService.ActionOpen, bin.SyncKey);
            yield return WaitUntil(() => open.Done, 4f, "No reply to open.");
            Assert.IsTrue(open.Success, open.Message);
            Assert.IsTrue(bin.Contents.Searching);
            yield return WaitUntil(() => binMirror.Contents.Searching, 4f, "Search state did not replicate.");

            // Finds turn up on the client from replicated state + the shared server clock
            yield return WaitUntil(() => binMirror.Contents.RevealedCount((float)client.ServerTime, table) >= 1,
                6f, "No find turned up on the client.");

            // A stale request (wrong expected item) and a hidden find are refused
            int last = binMirror.Contents.Count - 1;
            bool lastHidden = !bin.Contents.IsRevealed(last, (float)hostRep.ServerTime, table);
            Result stale = Request(client, RecyclingBagService.ActionTake, bin.SyncKey, 0, BinLootTable.MaxEntries + 5);
            yield return WaitUntil(() => stale.Done, 4f, "No reply to the stale take.");
            Assert.IsFalse(stale.Success);
            if (lastHidden)
            {
                Result hidden = Request(client, RecyclingBagService.ActionTake, bin.SyncKey, last, binMirror.Contents[last]);
                yield return WaitUntil(() => hidden.Done, 4f, "No reply to the hidden take.");
                Assert.IsFalse(hidden.Success, "A find that hasn't turned up must not be takeable.");
            }
            Assert.AreEqual(0, service.GetHostBag(clientId).Count);

            // Take the first find into the client's bag
            int countBefore = bin.Contents.Count;
            int entry = binMirror.Contents[0];
            Result take = Request(client, RecyclingBagService.ActionTake, bin.SyncKey, 0, entry);
            yield return WaitUntil(() => take.Done, 4f, "No reply to take.");
            Assert.IsTrue(take.Success, take.Message);
            Assert.AreEqual(countBefore - 1, bin.Contents.Count);
            RecyclingBag hostBag = service.GetHostBag(clientId);
            Assert.AreEqual(1, hostBag.Count);
            Assert.AreEqual(entry, hostBag[0]);
            Assert.AreEqual(0, service.GetHostBag(0).Count, "The host's own bag is untouched.");
            yield return WaitUntil(() => bagMirror.Count == 1 && bagMirror[0] == entry, 4f, "The bag did not replicate to its owner.");
            yield return WaitUntil(() => binMirror.Contents.Count == countBefore - 1, 4f, "The take did not replicate to the bin mirror.");
            yield return new WaitForSeconds(0.5f);
            Assert.AreEqual(countBefore - 1, bin.Contents.Count, "Passive fill must not instantly replace a find that was just taken.");

            // Empty the bag at the right bin: coins + material stock on the host
            table.TryGet(entry, out BinLootTable.Entry found);
            ISyncedStateProvider target = found.organic
                ? (ISyncedStateProvider)CreateCompost(new Vector3(2f, 0f, 0f))
                : CreateCrate(new Vector3(2f, 0f, 0f), found.stream);
            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance();
            float stockBefore = stock.GetAmount(found.materialId);
            int coinsBefore = hostRep.TeamCoins;

            Result deposit = Request(client, RecyclingBagService.ActionDeposit, target.SyncKey);
            yield return WaitUntil(() => deposit.Done, 4f, "No reply to deposit.");
            Assert.IsTrue(deposit.Success, deposit.Message);
            Assert.AreEqual(0, hostBag.Count);
            Assert.AreEqual(coinsBefore + found.coins, hostRep.TeamCoins, "Delivered finds pay the shared team wallet.");
            Assert.Greater(stock.GetAmount(found.materialId), stockBefore, "Delivered finds credit the Team Material Stock.");
            yield return WaitUntil(() => bagMirror.Count == 0, 4f, "The emptied bag did not replicate.");

            // Depositing again with nothing matching is refused
            Result again = Request(client, RecyclingBagService.ActionDeposit, target.SyncKey);
            yield return WaitUntil(() => again.Done, 4f, "No reply to the empty deposit.");
            Assert.IsFalse(again.Success);

            // Close pauses the search
            Result close = Request(client, RecyclingBagService.ActionClose, bin.SyncKey);
            yield return WaitUntil(() => close.Done, 4f, "No reply to close.");
            Assert.IsFalse(bin.Contents.Searching);
            yield return WaitUntil(() => !binMirror.Contents.Searching, 4f, "Pausing did not replicate.");
        }

        [UnityTest]
        public IEnumerator HostAndClient_Carry_StoreHeldTrash_HandsLimit_SellBuyBagAndThrow()
        {
            WasteCatalog catalog = AssetDatabase.LoadAssetAtPath<WasteCatalog>("Assets/UpcycleCity/Settings/Ecosystem/WasteCatalog.asset");
            Assert.IsNotNull(catalog);
            CarryItems.CatalogOverride = catalog;
            WasteCatalog.Entry bottle = null;
            foreach (WasteCatalog.Entry e in catalog.Entries)
            {
                if (e != null && !e.isBulky && e.stream == WasteStream.Plastic) { bottle = e; break; }
            }
            Assert.IsNotNull(bottle);

            NetworkSettings settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
            yield return StartHostAndClient(settings, nextPort++);

            var clientRegistry = new SyncedStateRegistryInstance();
            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 0);
            repGo.GetComponent<NetworkObject>().Spawn();
            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawned<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive the replicator.");
            clientRep.SetStateRegistry(clientRegistry);
            IGameAuthority host = hostRep, client = clientRep;
            ulong clientId = clientManager.LocalClientId;
            RecyclingBagService service = RecyclingBagService.EnsureInstance();

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.transform.localScale = new Vector3(40f, 1f, 40f);
            spawnedRoots.Add(floor);
            GameObject playerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(1f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(playerGo);
            playerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId);
            playerGo.GetComponent<NetworkPlayerController>().InitializeServerIdentity(
                2, "Carrier", NetworkCharacterAppearance.From(CharacterAppearance.Default), new Vector3(1f, 0.5f, 0f), Quaternion.identity);
            NetworkPlayerController clientCopy = null;
            yield return WaitUntil(() =>
            {
                foreach (NetworkObject obj in clientManager.SpawnManager.SpawnedObjectsList)
                    if (obj != null && obj.TryGetComponent(out NetworkPlayerController p) && p.OwnerClientId == clientId)
                        clientCopy = p;
                return clientCopy != null;
            }, 5f, "The client's player did not spawn on the client.");
            foreach (Rigidbody body in new[] { playerGo.GetComponent<Rigidbody>(), clientCopy.GetComponent<Rigidbody>() })
                if (body != null) body.isKinematic = true;

            var bagMirror = new RecyclingBag(clientId);
            clientRegistry.Register(bagMirror);

            // Hands hold 3: pick up (hold) and store three pieces of street trash; the fourth is refused
            for (int n = 0; n < 4; n++)
            {
                TrashItem hostTrash = host.SpawnTrash(catalog, bottle, WasteSourceKind.Citizen, new Vector3(1.5f, 0.4f, 0f), Vector3.zero);
                Assert.IsNotNull(hostTrash);
                ulong netId = hostTrash.GetComponent<NetworkObject>().NetworkObjectId;
                NetworkedPhysicsItem clientItem = null;
                yield return WaitUntil(() => clientManager.SpawnManager.SpawnedObjects.TryGetValue(netId, out NetworkObject o)
                    && (clientItem = o.GetComponent<NetworkedPhysicsItem>()) != null, 4f, "Trash did not spawn on the client.");
                clientItem.OnLocalPickupStarted();
                NetworkedPhysicsItem hostItem = hostTrash.GetComponent<NetworkedPhysicsItem>();
                yield return WaitUntil(() => hostItem.HolderClientId == clientId, 4f, "The host never saw the client holding the trash.");

                Result store = Request(client, RecyclingBagService.ActionStore, string.Empty);
                yield return WaitUntil(() => store.Done, 4f, "No reply to store.");
                if (n < CarryItems.HandsCapacity)
                {
                    Assert.IsTrue(store.Success, store.Message);
                    yield return WaitUntil(() => hostTrash == null || !hostItem.IsSpawned, 4f, "Stored trash was not despawned.");
                }
                else
                {
                    Assert.IsFalse(store.Success, "Hands hold only 3.");
                    StringAssert.Contains("arms are full", store.Message);
                    hostItem.GetComponent<TrashItem>().Dispose();
                }
            }
            RecyclingBag hostBag = service.GetHostBag(clientId);
            Assert.AreEqual(CarryItems.HandsCapacity, hostBag.Count);
            int carriedId = hostBag[0];
            Assert.IsTrue(CarryItems.IsTrashId(carriedId));
            yield return WaitUntil(() => bagMirror.Count == CarryItems.HandsCapacity, 4f, "The carry did not replicate to its owner.");

            // Sell one at RÉCUP: the host prices it, the team is paid
            var buyBackGo = new GameObject("TestRecup");
            buyBackGo.transform.position = new Vector3(2f, 0f, 0f);
            spawnedRoots.Add(buyBackGo);
            RecyclingBuyBack buyBack = buyBackGo.AddComponent<RecyclingBuyBack>();
            int expected = buyBack.PriceFor(bottle.stream, bottle.baseCoinValue);
            int coinsBefore = hostRep.TeamCoins;
            Result sell = Request(client, RecyclingBagService.ActionSell, buyBack.SyncKey, 0, carriedId);
            yield return WaitUntil(() => sell.Done, 4f, "No reply to sell.");
            Assert.IsTrue(sell.Success, sell.Message);
            Assert.AreEqual(coinsBefore + expected, hostRep.TeamCoins);
            Assert.AreEqual(2, hostBag.Count);

            // Stale sell (wrong expected id) is refused; sell all empties the rest
            Result stale = Request(client, RecyclingBagService.ActionSell, buyBack.SyncKey, 0, CarryItems.MaxId);
            yield return WaitUntil(() => stale.Done, 4f, "No reply to the stale sell.");
            Assert.IsFalse(stale.Success);
            Result sellAll = Request(client, RecyclingBagService.ActionSell, buyBack.SyncKey, RecyclingBagService.SellAll, 0);
            yield return WaitUntil(() => sellAll.Done, 4f, "No reply to sell all.");
            Assert.IsTrue(sellAll.Success, sellAll.Message);
            Assert.AreEqual(0, hostBag.Count);
            Assert.AreEqual(coinsBefore + expected * 3, hostRep.TeamCoins);
            yield return WaitUntil(() => bagMirror.Count == 0, 4f, "The emptied carry did not replicate.");

            // Buy the Recycling Bag at the Quincaillerie: host price, team wallet, capacity 10
            ShopCatalog tools = AssetDatabase.LoadAssetAtPath<ShopCatalog>("Assets/UpcycleCity/Settings/Shops/Shop_Tools.asset");
            Assert.IsTrue(tools.TryFindOffer(CarryItems.BagUpgradeItemId, 1, out ShopCatalog.Offer bagOffer), "The Quincaillerie must sell the bag.");
            var counterGo = new GameObject("TestQuincaillerie");
            counterGo.transform.position = new Vector3(0f, 0f, 2f);
            spawnedRoots.Add(counterGo);
            counterGo.AddComponent<ShopCounter>().Configure(tools, null);

            Result poor = Request(client, RecyclingBagService.ActionBuyBag, string.Empty);
            yield return WaitUntil(() => poor.Done, 4f, "No reply to the first bag purchase.");
            if (hostRep.TeamCoins < bagOffer.price)
                Assert.IsFalse(poor.Success, "Can't buy without the coins.");
            host.CreditTeamCoins(bagOffer.price);
            int beforeBag = hostRep.TeamCoins;
            Result buy = poor.Success ? poor : Request(client, RecyclingBagService.ActionBuyBag, string.Empty);
            yield return WaitUntil(() => buy.Done, 4f, "No reply to buy bag.");
            Assert.IsTrue(buy.Success, buy.Message);
            Assert.IsTrue(hostBag.HasBag);
            Assert.AreEqual(CarryItems.BagCapacity, hostBag.Capacity);
            if (!ReferenceEquals(buy, poor))
                Assert.AreEqual(beforeBag - bagOffer.price, hostRep.TeamCoins);
            yield return WaitUntil(() => bagMirror.HasBag, 4f, "Owning the bag did not replicate.");
            Result again = Request(client, RecyclingBagService.ActionBuyBag, string.Empty);
            yield return WaitUntil(() => again.Done, 4f, "No reply to the second bag purchase.");
            Assert.IsFalse(again.Success, "Only one bag.");

            // Throw an item back out: the host spawns real trash and the carry shrinks
            hostBag.TryAdd(carriedId);
            int trashBefore = UnityEngine.Object.FindObjectsByType<TrashItem>(FindObjectsInactive.Exclude).Length;
            Result toss = Request(client, RecyclingBagService.ActionThrow, string.Empty, 0, carriedId);
            yield return WaitUntil(() => toss.Done, 4f, "No reply to throw.");
            Assert.IsTrue(toss.Success, toss.Message);
            Assert.AreEqual(0, hostBag.Count);
            Assert.Greater(UnityEngine.Object.FindObjectsByType<TrashItem>(FindObjectsInactive.Exclude).Length, trashBefore,
                "Throwing out spawns the item back into the world.");
        }

        [UnityTest]
        public IEnumerator OfflineSolo_BinSearch_WorksWithoutNetworking()
        {
            BinLootTable table = BinLootTable.Active;
            Assert.IsNotNull(table);
            Assert.IsTrue(GameAuthority.IsAuthoritative);
            Assert.IsFalse(GameAuthority.IsMultiplayer);

            RecyclingBagService.EnsureInstance();
            OverflowingBinSource bin = CreateBin("SoloBin", Vector3.zero, 0.6f);
            yield return WaitUntil(() => bin.Contents.Count > 0, 3f, "The bin was not filled.");

            RecyclingBagService.RequestOpen(bin);
            Assert.IsTrue(bin.Contents.Searching);
            yield return WaitUntil(() => bin.Contents.RevealedCount(OverflowingBinSource.SearchClock, table) >= 1, 6f, "Nothing turned up.");

            int entry = bin.Contents[0];
            bool ok = false;
            RecyclingBagService.RequestTake(bin, 0, entry, (success, _) => ok = success);
            Assert.IsTrue(ok);
            RecyclingBag bag = RecyclingBagService.LocalBag;
            Assert.AreEqual(1, bag.Count);
            Assert.AreEqual(entry, bag[0]);

            table.TryGet(entry, out BinLootTable.Entry found);
            ISyncedStateProvider target = found.organic
                ? (ISyncedStateProvider)CreateCompost(new Vector3(2f, 0f, 0f))
                : CreateCrate(new Vector3(2f, 0f, 0f), found.stream);
            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance();
            float before = stock.GetAmount(found.materialId);
            bool deposited = false;
            RecyclingBagService.RequestDeposit(target, (success, _) => deposited = success);
            Assert.IsTrue(deposited);
            Assert.AreEqual(0, bag.Count);
            Assert.Greater(stock.GetAmount(found.materialId), before);

            RecyclingBagService.RequestClose(bin);
            Assert.IsFalse(bin.Contents.Searching);
        }

        // ------------------------------------------------------------ helpers

        OverflowingBinSource CreateBin(string name, Vector3 position, float fill)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            spawnedRoots.Add(go);
            var bin = go.AddComponent<OverflowingBinSource>();
            bin.Configure(name.ToLowerInvariant(), name, null, null, fill, null);
            return bin;
        }

        SortingCrate CreateCrate(Vector3 position, WasteStream stream)
        {
            var go = new GameObject($"TestCrate_{stream}");
            go.transform.position = position;
            spawnedRoots.Add(go);
            var crate = go.AddComponent<SortingCrate>();
            crate.Configure(stream);
            return crate;
        }

        CompostBin CreateCompost(Vector3 position)
        {
            var go = new GameObject("TestCompost");
            go.transform.position = position;
            spawnedRoots.Add(go);
            return go.AddComponent<CompostBin>();
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

        /// <summary>What a remote client holds for a bin: the replicated contents only.</summary>
        sealed class BinMirror : ISyncedStateProvider
        {
            public BinMirror(string key) => SyncKey = key;
            public string SyncKey { get; }
            public BinContents Contents { get; } = new BinContents();
            public SyncedStateRecord CaptureSyncState() => default;
            public void ApplySyncState(in SyncedStateRecord state) => Contents.Apply(state.v1, state.v2, state.i0, state.i1);
        }

        IEnumerator StartHostAndClient(NetworkSettings settings, ushort port)
        {
            hostManager = CreateNetworkManager("BinTestHostNM", settings, port, isServer: true);
            Assert.IsTrue(hostManager.StartHost(), $"Host failed to start on port {port}.");
            clientManager = CreateNetworkManager("BinTestClientNM", settings, port, isServer: false);
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
                if (Time.realtimeSinceStartup >= deadline)
                    Assert.Fail(failMessage);
                yield return null;
            }
        }
    }
}
