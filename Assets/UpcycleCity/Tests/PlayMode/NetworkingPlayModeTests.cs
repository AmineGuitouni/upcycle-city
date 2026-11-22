using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Inventory;
using UpcycleCity.Networking;
using UpcycleCity.Player;
using UpcycleCity.Shops;
using UpcycleCity.Waste;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>
    /// End-to-end PlayMode integration tests verifying real host + client networking over loopback
    /// <see cref="UnityTransport"/> (127.0.0.1), security/validation hardening, trigger vs held trash,
    /// disconnect-while-holding survival, README recipe APIs, session flow + late-join rejection,
    /// and offline solo play with zero Netcode components.
    /// </summary>
    public sealed class NetworkingPlayModeTests
    {
        static ushort nextPort = 17810;

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

            if (CoopSessionManager.Instance != null)
            {
                NetworkManager smNm = CoopSessionManager.Instance.GetComponent<NetworkManager>();
                if (smNm != null && smNm.IsListening)
                    smNm.Shutdown();
                UnityEngine.Object.DestroyImmediate(CoopSessionManager.Instance.gameObject);
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

            GameAuthority.ResetToLocal();
            SyncedStateRegistry.Clear();
        }

        [UnityTest]
        public IEnumerator HostAndClient_ConnectSyncStateRegistryApplyAndWallet()
        {
            NetworkSettings settings = LoadNetworkSettings();
            Assert.IsNotNull(settings, "NetworkSettings.asset must exist.");

            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            Assert.IsTrue(hostManager.IsHost);
            Assert.IsTrue(clientManager.IsConnectedClient);
            Assert.AreEqual(2, hostManager.ConnectedClientsIds.Count);

            // Use isolated host and client registries so the client exercises real ApplySyncState() in-process
            var hostRegistry = new SyncedStateRegistryInstance();
            var clientRegistry = new SyncedStateRegistryInstance();

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 25);
            hostRep.SetStateRegistry(hostRegistry);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive NetworkStateReplicator.");
            clientRep.SetStateRegistry(clientRegistry);

            // 1. Register paired ISyncedStateProviders in host and client registries and verify real client ApplySyncState
            var hostMachine = new TestMachineProvider("machine:TestShredder_01");
            var clientMachine = new TestMachineProvider("machine:TestShredder_01");
            hostMachine.SetState(0.65f, 42f, 7, 1);
            hostRegistry.Register(hostMachine);
            clientRegistry.Register(clientMachine);

            yield return WaitUntil(() =>
                clientMachine.ApplyCount >= 1
                && Mathf.Abs(clientMachine.Current.v0 - 0.65f) < 0.01f
                && clientMachine.Current.i0 == 7,
                4f, "Client ISyncedStateProvider.ApplySyncState was not called with initial replicated state.");

            int appliesAfterInitial = clientMachine.ApplyCount;
            hostMachine.SetState(0.95f, 88f, 12, 3);
            yield return WaitUntil(() =>
                clientMachine.ApplyCount > appliesAfterInitial
                && Mathf.Abs(clientMachine.Current.v0 - 0.95f) < 0.01f
                && clientMachine.Current.i0 == 12
                && clientMachine.Current.flags == 3,
                4f, "Client ISyncedStateProvider.ApplySyncState did not receive delta update.");

            // 2. Host-authoritative server-timestamped city event replicates and ticks locally on client
            hostRep.TriggerCityEvent("Street Food Market", 10f, severity: 2);
            yield return WaitUntil(() =>
                clientRep.ActiveEventTitle == "Street Food Market"
                && clientRep.ActiveEventTimeRemaining > 6f
                && clientRep.ActiveEventSeverity == 2,
                4f, "City event warning did not replicate to client.");

            float remainingBefore = clientRep.ActiveEventTimeRemaining;
            string cachedTitleRef = clientRep.ActiveEventTitle;
            yield return new WaitForSeconds(0.35f);
            Assert.Less(clientRep.ActiveEventTimeRemaining, remainingBefore,
                "Client countdown must advance locally from ServerTime without per-frame NetworkVariable writes.");
            Assert.AreSame(cachedTitleRef, clientRep.ActiveEventTitle,
                "ActiveEventTitle must return cached string with zero per-frame allocations.");
        }

        [UnityTest]
        public IEnumerator HostAndClient_ShopSecurity_CatalogPriceResolutionAndNoRefundPath()
        {
            // Verify N4: no refund RPC or interface method exists
            Assert.IsNull(typeof(IGameAuthority).GetMethod("RequestShopRefund"));
            Assert.IsNull(typeof(NetworkStateReplicator).GetMethod(
                "ShopRefundServerRpc", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));

            NetworkSettings settings = LoadNetworkSettings();
            ShopCatalog toolsAsset = LoadToolsCatalog();
            Assert.IsNotNull(toolsAsset);
            Assert.Greater(toolsAsset.Offers.Count, 1);

            ItemDefinition broomItem = toolsAsset.Offers[0].item;
            ItemDefinition wrenchItem = toolsAsset.Offers[1].item;
            Assert.IsNotNull(broomItem);
            Assert.IsNotNull(wrenchItem);

            // Build a deterministic catalog with a valid 10-coin offer, an unaffordable 50-coin offer, and an invalid 0-coin offer
            ShopCatalog testCatalog = ScriptableObject.CreateInstance<ShopCatalog>();
            createdAssets.Add(testCatalog);
            testCatalog.Configure("Test Quincaillerie", "Mounir", "Ahla", Color.green, false, new[]
            {
                new ShopCatalog.Offer { item = broomItem, price = 10, quantity = 1 },
                new ShopCatalog.Offer { item = wrenchItem, price = 50, quantity = 1 },
                new ShopCatalog.Offer { item = broomItem, price = 0, quantity = 3 },
            });

            var counterGo = new GameObject("TestShopCounter");
            spawnedRoots.Add(counterGo);
            counterGo.transform.position = Vector3.zero;
            ShopCounter counter = counterGo.AddComponent<ShopCounter>();
            counter.Configure(testCatalog, null);

            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 25);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive NetworkStateReplicator.");

            // 1. Valid purchase resolves authoritative 10-coin price from server ShopCounter (no price sent from client)
            ShopPurchaseResult res = default;
            bool done = false;
            clientRep.RequestShopPurchase(
                new ShopPurchaseRequest(counter.ShopKey, broomItem.ItemId, 1, counter.transform.position, null),
                r => { res = r; done = true; });
            yield return WaitUntil(() => done, 4f, "Purchase callback did not fire.");
            Assert.IsTrue(res.Approved);
            Assert.AreEqual(10, res.Price);
            Assert.AreEqual(15, res.RemainingCoins);
            yield return WaitUntil(() => clientRep.TeamCoins == 15 && hostRep.TeamCoins == 15, 4f, "Team coins did not sync.");

            // 2. Over-budget item rejected atomically
            done = false;
            clientRep.RequestShopPurchase(
                new ShopPurchaseRequest(counter.ShopKey, wrenchItem.ItemId, 1, counter.transform.position, null),
                r => { res = r; done = true; });
            yield return WaitUntil(() => done, 4f, "Over-budget callback did not fire.");
            Assert.IsFalse(res.Approved);
            Assert.AreEqual(15, hostRep.TeamCoins);

            // 3. Unknown item ID rejected
            done = false;
            clientRep.RequestShopPurchase(
                new ShopPurchaseRequest(counter.ShopKey, "forged_free_item", 1, counter.transform.position, null),
                r => { res = r; done = true; });
            yield return WaitUntil(() => done, 4f, "Unknown item callback did not fire.");
            Assert.IsFalse(res.Approved);
            Assert.AreEqual(15, hostRep.TeamCoins);

            // 4. Zero-price catalog offer rejected
            done = false;
            clientRep.RequestShopPurchase(
                new ShopPurchaseRequest(counter.ShopKey, broomItem.ItemId, 3, counter.transform.position, null),
                r => { res = r; done = true; });
            yield return WaitUntil(() => done, 4f, "Zero-price callback did not fire.");
            Assert.IsFalse(res.Approved);
            Assert.AreEqual(15, hostRep.TeamCoins);
        }

        [UnityTest]
        public IEnumerator HostAndClient_NetworkedPhysicsItem_OwnershipMutualExclusionAndSortPayout()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 25);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive replicator.");

            // Spawn a networked trash item on host via NetworkTrashArchetype descriptor (zero pre-spawn warnings)
            GameObject trashGo = UnityEngine.Object.Instantiate(settings.NetworkTrashPrefab, new Vector3(0f, 1.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(trashGo);
            NetworkTrashArchetype archetype = trashGo.GetComponent<NetworkTrashArchetype>();
            archetype.InitializeDescriptor(NetworkTrashDescriptor.CreateFallback(
                "Plastic Bottle", WasteStream.Plastic, WasteSourceKind.Citizen, 0.25f, 8));
            trashGo.GetComponent<NetworkObject>().Spawn();

            NetworkedPhysicsItem hostItem = trashGo.GetComponent<NetworkedPhysicsItem>();
            NetworkedPhysicsItem clientItem = null;
            yield return WaitUntil(() =>
            {
                clientItem = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                return clientItem != null && clientItem.IsSpawned;
            }, 5f, "Client did not receive spawned NetworkTrashItem.");

            TrashItem clientTrashCheck = clientItem.GetComponent<TrashItem>();
            Assert.AreEqual(WasteStream.Plastic, clientTrashCheck.Stream, "Client trash stream should replicate from host.");
            Assert.AreEqual("Plastic Bottle", clientTrashCheck.DisplayName, "Client trash name should replicate from host.");

            // Client requests grab -> host validates and transfers ownership to client
            Assert.IsTrue(clientItem.CanLocalGrab);
            clientItem.OnLocalPickupStarted();

            yield return WaitUntil(() =>
                hostItem.HolderClientId == clientManager.LocalClientId
                && clientItem.HolderClientId == clientManager.LocalClientId
                && clientItem.IsOwner,
                4f, "Client did not receive grab ownership from host.");

            // Mutual exclusion: host cannot steal item while client is holding it
            Assert.IsFalse(hostItem.CanLocalGrab);
            hostItem.OnLocalPickupStarted();
            yield return new WaitForSeconds(0.25f);
            Assert.AreEqual(clientManager.LocalClientId, hostItem.HolderClientId, "Second player must not steal a held item.");

            // Client moves the held item near the crate and releases it -> ownership returns to server
            clientItem.transform.position = new Vector3(4.5f, 1f, 0f);
            clientItem.OnLocalReleased(new Vector3(0f, 0.2f, 0f), thrown: false);

            yield return WaitUntil(() =>
                hostItem.HolderClientId == NetworkedPhysicsItem.NoHolder
                && hostItem.IsOwner,
                4f, "Item ownership did not return to server after release.");

            var crateGo = new GameObject("TestPlasticCrate");
            spawnedRoots.Add(crateGo);
            crateGo.transform.position = new Vector3(5f, 0.5f, 0f);
            SortingCrate crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Plastic, payOnSort: true);

            TrashItem clientTrash = clientItem.GetComponent<TrashItem>();
            clientRep.RequestSortTrash(crate, clientTrash, null);

            yield return WaitUntil(() =>
                hostRep.TeamCoins > 25
                && clientRep.TeamCoins == hostRep.TeamCoins
                && crate.CorrectCount == 1
                && (hostItem == null || !hostItem.IsSpawned),
                5f, "Authoritative sort did not reward team wallet and despawn trash across host + client.");
        }

        [UnityTest]
        public IEnumerator HostAndClient_PassiveTriggerSortAndSell_FeedbackRoutedToThrower()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 25);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive replicator.");

            var hostFeedbacks = new List<string>();
            var clientFeedbacks = new List<string>();
            hostRep.LocalFeedbackReceived += (msg, _, _) => hostFeedbacks.Add(msg);
            clientRep.LocalFeedbackReceived += (msg, _, _) => clientFeedbacks.Add(msg);

            // 1. Client grabs & throws a Plastic Bottle, then host's SortingCrate trigger runs (interactor == null)
            var crateGo = new GameObject("ThrowerFeedbackCrate");
            spawnedRoots.Add(crateGo);
            crateGo.transform.position = new Vector3(4f, 0.5f, 0f);
            SortingCrate crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Plastic, payOnSort: true);

            GameObject trashGo = UnityEngine.Object.Instantiate(settings.NetworkTrashPrefab, new Vector3(3.8f, 1.0f, 0f), Quaternion.identity);
            spawnedRoots.Add(trashGo);
            trashGo.GetComponent<NetworkTrashArchetype>().InitializeDescriptor(NetworkTrashDescriptor.CreateFallback(
                "Thrown Bottle", WasteStream.Plastic, WasteSourceKind.Citizen, 0.25f, 8));
            trashGo.GetComponent<NetworkObject>().Spawn();

            NetworkedPhysicsItem hostItem = trashGo.GetComponent<NetworkedPhysicsItem>();
            NetworkedPhysicsItem clientItem = null;
            yield return WaitUntil(() =>
            {
                clientItem = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                return clientItem != null && clientItem.IsSpawned;
            }, 5f, "Client did not receive bottle.");

            clientItem.OnLocalPickupStarted();
            yield return WaitUntil(() => hostItem.HolderClientId == clientManager.LocalClientId, 4f, "Client grab failed.");

            clientItem.transform.position = new Vector3(4f, 0.8f, 0f);
            clientItem.OnLocalReleased(new Vector3(0.2f, 0.1f, 0f), thrown: true);
            yield return WaitUntil(() =>
                !hostItem.IsHeldByAnyone && hostItem.LastHolderClientId == clientManager.LocalClientId,
                4f, "Release did not preserve LastHolderClientId on server.");

            // Passive trigger on host passes interactor == null
            hostRep.RequestSortTrash(crate, hostItem.GetComponent<TrashItem>(), null);
            yield return WaitUntil(() => clientFeedbacks.Count == 1, 4f,
                "Passive sort feedback did not route to the client who threw the trash.");
            Assert.AreEqual(0, hostFeedbacks.Count, "Host must NOT receive feedback for a client-thrown bottle.");
            Assert.That(clientFeedbacks[0], Does.Contain("Plastic").IgnoreCase);

            // 2. Client grabs & throws a Glass Jar into RecyclingBuyBack trigger -> feedback goes to client, NOT host
            var buyBackGo = new GameObject("ThrowerFeedbackBuyBack");
            spawnedRoots.Add(buyBackGo);
            buyBackGo.transform.position = new Vector3(-4f, 0.5f, 0f);
            RecyclingBuyBack buyBack = buyBackGo.AddComponent<RecyclingBuyBack>();
            buyBack.Configure("RÉCUP scale", RecyclingBuyBack.Rates.Default);

            GameObject trashGo2 = UnityEngine.Object.Instantiate(settings.NetworkTrashPrefab, new Vector3(-3.8f, 1.0f, 0f), Quaternion.identity);
            spawnedRoots.Add(trashGo2);
            trashGo2.GetComponent<NetworkTrashArchetype>().InitializeDescriptor(NetworkTrashDescriptor.CreateFallback(
                "Thrown Jar", WasteStream.Glass, WasteSourceKind.Citizen, 0.3f, 10));
            trashGo2.GetComponent<NetworkObject>().Spawn();

            NetworkedPhysicsItem hostItem2 = trashGo2.GetComponent<NetworkedPhysicsItem>();
            NetworkedPhysicsItem clientItem2 = null;
            yield return WaitUntil(() =>
            {
                clientItem2 = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                return clientItem2 != null && clientItem2.IsSpawned;
            }, 5f, "Client did not receive jar.");

            clientItem2.OnLocalPickupStarted();
            yield return WaitUntil(() => hostItem2.HolderClientId == clientManager.LocalClientId, 4f, "Client grab of jar failed.");

            clientItem2.transform.position = new Vector3(-4f, 0.8f, 0f);
            clientItem2.OnLocalReleased(new Vector3(0.1f, 0.1f, 0f), thrown: true);
            yield return WaitUntil(() =>
                !hostItem2.IsHeldByAnyone && hostItem2.LastHolderClientId == clientManager.LocalClientId,
                4f, "Release of jar did not preserve LastHolderClientId.");

            hostRep.RequestSellTrash(buyBack, hostItem2.GetComponent<TrashItem>(), null);
            yield return WaitUntil(() => clientFeedbacks.Count == 2, 4f,
                "Passive buy-back feedback did not route to the client who threw the trash.");
            Assert.AreEqual(0, hostFeedbacks.Count, "Host must NOT receive buy-back feedback for a client-thrown jar.");
            Assert.That(clientFeedbacks[1], Does.Contain("Thrown Jar"));
        }

        [UnityTest]
        public IEnumerator HostAndClient_TriggerVsHeldItem_HostDoesNotConsumeClientHeldTrashAndDespawnClearsGrabber()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 25);
            repGo.GetComponent<NetworkObject>().Spawn();

            yield return WaitUntil(() =>
            {
                NetworkStateReplicator cr = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return cr != null && cr.IsSpawned;
            }, 5f, "Client did not receive replicator.");

            // Create a SortingCrate with a physics trigger volume on host
            var crateGo = new GameObject("TriggerCrate");
            spawnedRoots.Add(crateGo);
            crateGo.transform.position = new Vector3(4f, 1f, 0f);
            BoxCollider trigger = crateGo.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(3f, 3f, 3f);
            SortingCrate crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Plastic, payOnSort: true);

            // Spawn trash away from the crate first
            GameObject trashGo = UnityEngine.Object.Instantiate(settings.NetworkTrashPrefab, new Vector3(0f, 1f, 0f), Quaternion.identity);
            spawnedRoots.Add(trashGo);
            trashGo.GetComponent<NetworkTrashArchetype>().InitializeDescriptor(NetworkTrashDescriptor.CreateFallback(
                "Held Bottle", WasteStream.Plastic, WasteSourceKind.Citizen, 0.2f, 8));
            trashGo.GetComponent<NetworkObject>().Spawn();

            NetworkedPhysicsItem hostItem = trashGo.GetComponent<NetworkedPhysicsItem>();
            TrashItem hostTrash = trashGo.GetComponent<TrashItem>();
            NetworkedPhysicsItem clientItem = null;
            yield return WaitUntil(() =>
            {
                clientItem = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                return clientItem != null && clientItem.IsSpawned;
            }, 5f, "Client did not receive trash.");

            // Set up a client-side PhysicsGrabController holding the item via the real BeginPickup path
            var grabRig = new GameObject("ClientGrabRig");
            spawnedRoots.Add(grabRig);
            var camGo = new GameObject("GrabCam");
            camGo.transform.SetParent(grabRig.transform, false);
            PlayerConfig config = ScriptableObject.CreateInstance<PlayerConfig>();
            createdAssets.Add(config);
            config.handPickupDuration = 0.05f;
            config.handPickupGrabPoint = 0.5f;
            PhysicsGrabController grabber = grabRig.AddComponent<PhysicsGrabController>();
            grabber.Initialize(config, null, null, camGo.transform, null);

            TrashItem clientTrash = clientItem.GetComponent<TrashItem>();
            Assert.IsTrue(grabber.BeginPickup(clientTrash), "Client grabber should start picking up free trash.");

            yield return WaitUntil(() =>
                grabber.IsHolding
                && hostItem.HolderClientId == clientManager.LocalClientId
                && hostTrash.IsHeld,
                4f, "Client did not finish holding trash or host TrashItem.IsHeld did not reflect remote holder.");

            // Move the held item directly into the host's SortingCrate trigger volume
            grabRig.transform.position = crateGo.transform.position;
            clientItem.transform.position = crateGo.transform.position;
            hostItem.transform.position = crateGo.transform.position;
            yield return new WaitForFixedUpdate();
            yield return new WaitForSeconds(0.35f);

            // Host trigger MUST NOT sort or despawn trash while held by the client
            Assert.AreEqual(0, crate.CorrectCount, "Host SortingCrate trigger must not consume trash while held by a client.");
            Assert.IsTrue(hostItem != null && hostItem.IsSpawned, "Client-held trash must remain spawned.");
            Assert.AreEqual(25, hostRep.TeamCoins);

            // Now force-despawn the held item on the host and verify client PhysicsGrabController clears cleanly without MissingReferenceException
            hostItem.NetworkObject.Despawn(true);
            yield return WaitUntil(() => clientItem == null || !clientItem.IsSpawned, 4f, "Client trash did not despawn.");
            yield return null;
            yield return new WaitForFixedUpdate();

            Assert.IsFalse(grabber.IsHolding, "PhysicsGrabController.IsHolding must return false cleanly after held item despawns.");
            Assert.IsNull(grabber.HeldObject, "PhysicsGrabController.HeldObject must be null after held item despawns.");
        }

        [UnityTest]
        public IEnumerator HostAndClient_DisconnectWhileHolding_RealCoopSessionManager_ItemSurvivesAndReturnsToServer()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;

            // Drive the REAL CoopSessionManager host (zero hand-wired OnClientDisconnectCallback)
            var sessionGo = new GameObject("RealCoopSessionHost");
            sessionGo.SetActive(false);
            CoopSessionManager sm = sessionGo.AddComponent<CoopSessionManager>();
            sm.Configure(settings);
            sessionGo.SetActive(true);

            Task<(bool success, string error)> hostTask = sm.HostCoopAsync(CoopTransportMode.Direct, "127.0.0.1", port);
            yield return WaitUntil(() => hostTask.IsCompleted, 5f, "CoopSessionManager.HostCoopAsync did not complete.");
            Assert.IsTrue(hostTask.Result.success, $"HostCoopAsync failed: {hostTask.Result.error}");
            Assert.AreEqual(CoopSessionState.Lobby, sm.State);

            NetworkManager smHostManager = sm.GetComponent<NetworkManager>();
            Assert.IsNotNull(smHostManager);

            // Connect a real client NetworkManager to CoopSessionManager's host
            clientManager = CreateNetworkManager("RealDisconnectClientNM", settings, port, isServer: false);
            clientManager.NetworkConfig.EnableSceneManagement = true;
            clientManager.NetworkConfig.ConnectionApproval = true;
            Assert.IsTrue(clientManager.StartClient(), $"Client failed to start targeting port {port}.");

            yield return WaitUntil(() =>
                smHostManager.ConnectedClientsIds.Count == 2 && clientManager.IsConnectedClient,
                6f, "Client did not connect to CoopSessionManager host.");

            GameObject trashGo = UnityEngine.Object.Instantiate(settings.NetworkTrashPrefab, new Vector3(1f, 1.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(trashGo);
            trashGo.GetComponent<NetworkTrashArchetype>().InitializeDescriptor(NetworkTrashDescriptor.CreateFallback(
                "Glass Jar", WasteStream.Glass, WasteSourceKind.Citizen, 0.3f, 10));
            NetworkObject hostNetObj = trashGo.GetComponent<NetworkObject>();
            Assert.IsTrue(hostNetObj.DontDestroyWithOwner, "DontDestroyWithOwner must be true on NetworkedPhysicsItem.");
            hostNetObj.Spawn();

            NetworkedPhysicsItem hostItem = trashGo.GetComponent<NetworkedPhysicsItem>();
            NetworkedPhysicsItem clientItem = null;
            yield return WaitUntil(() =>
            {
                clientItem = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                return clientItem != null && clientItem.IsSpawned;
            }, 5f, "Client did not receive NetworkTrashItem.");

            // Client grabs the item (gains NGO ownership)
            clientItem.OnLocalPickupStarted();
            yield return WaitUntil(() =>
                hostItem.HolderClientId == clientManager.LocalClientId
                && hostNetObj.OwnerClientId == clientManager.LocalClientId,
                4f, "Client did not gain ownership of trash.");

            // Client abruptly disconnects while holding -> CoopSessionManager's real OnClientDisconnected must release it
            clientManager.Shutdown();
            yield return WaitUntil(() => smHostManager.ConnectedClientsIds.Count == 1, 5f, "Host did not detect client disconnect.");
            yield return null;

            // Item MUST still exist & be spawned on host, with server ownership and NoHolder
            Assert.IsTrue(hostItem != null && hostNetObj != null && hostNetObj.IsSpawned,
                "Trash held by a disconnecting client must NOT be destroyed.");
            Assert.AreEqual(NetworkedPhysicsItem.NoHolder, hostItem.HolderClientId);
            Assert.IsFalse(hostItem.IsHeldByAnyone);
            Assert.AreEqual(smHostManager.LocalClientId, hostNetObj.OwnerClientId);
        }

        [UnityTest]
        public IEnumerator HostAndClient_RpcAbuseRejection_HolderRangeReleaseAndToolValidation()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ShopCatalog toolsAsset = LoadToolsCatalog();
            ItemDefinition wrenchDef = null;
            for (int i = 0; i < toolsAsset.Offers.Count; i++)
            {
                if (toolsAsset.Offers[i]?.item != null && toolsAsset.Offers[i].item.ItemId == "tool_wrench")
                {
                    wrenchDef = toolsAsset.Offers[i].item;
                    break;
                }
            }
            Assert.IsNotNull(wrenchDef, "tool_wrench ItemDefinition must exist in Shop_Tools.asset.");

            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 25);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive replicator.");

            // Spawn host & client NetworkPlayers so server range & equipped-tool checks run with real player objects
            GameObject hostPlayerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(0f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(hostPlayerGo);
            hostPlayerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(hostManager.LocalClientId);
            hostPlayerGo.GetComponent<NetworkPlayerController>().InitializeServerIdentity(
                1, "Host", NetworkCharacterAppearance.From(CharacterAppearance.Default), new Vector3(0f, 0.5f, 0f), Quaternion.identity);

            GameObject hostSideClientPlayerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(30f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(hostSideClientPlayerGo);
            hostSideClientPlayerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientManager.LocalClientId);
            hostSideClientPlayerGo.GetComponent<NetworkPlayerController>().InitializeServerIdentity(
                2, "Client", NetworkCharacterAppearance.From(CharacterAppearance.Default), new Vector3(30f, 0.5f, 0f), Quaternion.identity);

            NetworkPlayerController clientOwnPlayer = null;
            yield return WaitUntil(() =>
            {
                foreach (NetworkObject obj in clientManager.SpawnManager.SpawnedObjectsList)
                {
                    if (obj != null && obj.TryGetComponent(out NetworkPlayerController npc) && npc.IsOwner)
                    {
                        clientOwnPlayer = npc;
                        return true;
                    }
                }
                return false;
            }, 5f, "Client did not receive its NetworkPlayer.");

            // Spawn trash next to host (0, 1, 0) & crate next to host (1, 0.5, 0)
            GameObject trashGo = UnityEngine.Object.Instantiate(settings.NetworkTrashPrefab, new Vector3(0.5f, 1f, 0f), Quaternion.identity);
            spawnedRoots.Add(trashGo);
            trashGo.GetComponent<NetworkTrashArchetype>().InitializeDescriptor(NetworkTrashDescriptor.CreateFallback(
                "Plastic Bottle", WasteStream.Plastic, WasteSourceKind.Citizen, 0.25f, 8));
            trashGo.GetComponent<NetworkObject>().Spawn();

            NetworkedPhysicsItem hostItem = trashGo.GetComponent<NetworkedPhysicsItem>();
            NetworkedPhysicsItem clientItem = null;
            yield return WaitUntil(() =>
            {
                clientItem = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                return clientItem != null && clientItem.IsSpawned;
            }, 5f, "Client did not receive trash.");

            var crateGo = new GameObject("AbuseTestCrate");
            spawnedRoots.Add(crateGo);
            crateGo.transform.position = new Vector3(1f, 0.5f, 0f);
            SortingCrate crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Plastic, payOnSort: true);

            // Abuse 1: Non-holder calls ReleaseServerRpc on a FREE item trying to teleport it to (99, 99, 99) -> REJECTED
            Vector3 posBefore = hostItem.transform.position;
            clientItem.transform.position = new Vector3(99f, 99f, 99f);
            clientItem.OnLocalReleased(new Vector3(50f, 50f, 50f), thrown: true);
            yield return new WaitForSeconds(0.25f);
            Assert.Less(Vector3.Distance(hostItem.transform.position, posBefore), 1.5f,
                "Non-holder ReleaseServerRpc must NOT teleport a free item.");

            // Abuse 2: Client is 30m away and tries to sort free item at crate -> REJECTED by range check
            clientRep.RequestSortTrash(crate, clientItem.GetComponent<TrashItem>(), null);
            yield return new WaitForSeconds(0.25f);
            Assert.AreEqual(0, crate.CorrectCount, "Out-of-range client must not sort trash.");
            Assert.IsTrue(hostItem.IsSpawned);

            // Abuse 3: Host holds the item; client moves next to crate and tries to sort host-held item -> REJECTED
            hostItem.OnLocalPickupStarted();
            yield return WaitUntil(() => hostItem.HolderClientId == hostManager.LocalClientId, 3f, "Host grab failed.");

            TeleportPlayer(clientOwnPlayer, new Vector3(1.5f, 0.5f, 0f));
            yield return WaitUntil(() =>
                Vector3.Distance(hostSideClientPlayerGo.transform.position, new Vector3(1.5f, 0.5f, 0f)) < 1.5f,
                4f, "Client move to crate did not replicate to host.");
            clientRep.RequestSortTrash(crate, clientItem.GetComponent<TrashItem>(), null);
            yield return new WaitForSeconds(0.25f);
            Assert.AreEqual(0, crate.CorrectCount, "Client must NOT sort an item held by the host.");
            Assert.IsTrue(hostItem.IsSpawned);

            // Abuse 4: Factory repair without wrench -> REJECTED; with wrench out of range -> REJECTED; with wrench in range -> SUCCEEDS
            var factoryGo = new GameObject("AbuseTestFactory");
            spawnedRoots.Add(factoryGo);
            factoryGo.transform.position = new Vector3(2f, 0f, 0f);
            FactorySmokestackSource factory = factoryGo.AddComponent<FactorySmokestackSource>();
            factory.Configure("factory_test", "Test Factory", null, null, null, null, null);

            // Client is in range (1.5, 0.5, 0) but does NOT have tool_wrench equipped
            clientRep.RequestRepairFactory(factory, null);
            yield return new WaitForSeconds(0.25f);
            Assert.IsFalse(factory.IsScrubberActive, "Client without equipped wrench must NOT repair factory.");

            // Equip wrench on client player and move OUT of range (40m away)
            PlayerInventory clientInv = clientOwnPlayer.LocalController.Inventory;
            Assert.IsTrue(clientInv.TryAddItem(wrenchDef, 1, silent: true));
            for (int s = 0; s < clientInv.SlotCount; s++)
            {
                if (clientInv.GetSlot(s).Item != null && clientInv.GetSlot(s).Item.ItemId == "tool_wrench")
                {
                    clientInv.SelectSlot(s);
                    break;
                }
            }
            yield return WaitUntil(() =>
                hostSideClientPlayerGo.GetComponent<NetworkPlayerController>().EquippedItemId == "tool_wrench",
                4f, "Equipped wrench ID did not replicate to host.");

            TeleportPlayer(clientOwnPlayer, new Vector3(40f, 0.5f, 0f));
            yield return WaitUntil(() =>
                Vector3.Distance(hostSideClientPlayerGo.transform.position, factory.transform.position) > 25f,
                4f, "Client out-of-range teleport did not replicate to host.");

            clientRep.RequestRepairFactory(factory, null);
            yield return new WaitForSeconds(0.25f);
            Assert.IsFalse(factory.IsScrubberActive, "Out-of-range client with wrench must NOT repair factory.");

            // Move client IN range with wrench equipped -> SUCCEEDS!
            TeleportPlayer(clientOwnPlayer, new Vector3(2.5f, 0.5f, 0f));
            yield return WaitUntil(() =>
                Vector3.Distance(hostSideClientPlayerGo.transform.position, factory.transform.position) < 3f,
                4f, "Client in-range teleport did not replicate to host.");
            clientRep.RequestRepairFactory(factory, null);
            yield return WaitUntil(() => factory.IsScrubberActive && hostRep.TeamCoins > 25,
                4f, "In-range client with equipped wrench should repair factory and award team coins.");
        }

        [UnityTest]
        public IEnumerator HostAndClient_ReadmeRecipeApis_HostOnlySpawnClientRejectTwoBenchCustomActionAndCityEvent()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            var hostRegistry = new SyncedStateRegistryInstance();
            var clientRegistry = new SyncedStateRegistryInstance();

            GameObject repGo = UnityEngine.Object.Instantiate(settings.NetworkCityStatePrefab);
            spawnedRoots.Add(repGo);
            NetworkStateReplicator hostRep = repGo.GetComponent<NetworkStateReplicator>();
            hostRep.Configure(settings.NetworkTrashPrefab, syncInterval: 0.1f, initialCoins: 25);
            hostRep.SetStateRegistry(hostRegistry);
            repGo.GetComponent<NetworkObject>().Spawn();

            NetworkStateReplicator clientRep = null;
            yield return WaitUntil(() =>
            {
                clientRep = FindSpawnedBehaviour<NetworkStateReplicator>(clientManager);
                return clientRep != null && clientRep.IsSpawned;
            }, 5f, "Client did not receive replicator.");
            clientRep.SetStateRegistry(clientRegistry);

            // Spawn a real client player at (1, 0.5, 0) so CustomGameActionContext.SenderPosition is verified server-side
            GameObject hostSideClientPlayerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(1f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(hostSideClientPlayerGo);
            hostSideClientPlayerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientManager.LocalClientId);
            hostSideClientPlayerGo.GetComponent<NetworkPlayerController>().InitializeServerIdentity(
                2, "Crafter", NetworkCharacterAppearance.From(CharacterAppearance.Default), new Vector3(1f, 0.5f, 0f), Quaternion.identity);

            // Recipe 1 (N1): Host SpawnNetworkedPrefab + DespawnNetworkedObject work; client calls log Error and return null/false
            IGameAuthority hostAuthority = hostRep;
            IGameAuthority clientAuthority = clientRep;

            LogAssert.Expect(LogType.Error, new Regex("SpawnNetworkedPrefab.*host-only"));
            GameObject clientRejectedSpawn = clientAuthority.SpawnNetworkedPrefab(
                settings.NetworkTrashPrefab,
                new Vector3(0f, 1.5f, 0f),
                Quaternion.identity);
            Assert.IsNull(clientRejectedSpawn, "Non-host client SpawnNetworkedPrefab must return null.");
            yield return new WaitForSeconds(0.15f);
            Assert.IsNull(FindSpawnedBehaviour<NetworkedPhysicsItem>(hostManager),
                "Client SpawnNetworkedPrefab must never spawn anything on host.");

            GameObject spawned = hostAuthority.SpawnNetworkedPrefab(
                settings.NetworkTrashPrefab,
                new Vector3(0f, 1.5f, 0f),
                Quaternion.identity);
            Assert.IsNotNull(spawned);
            spawnedRoots.Add(spawned);

            NetworkedPhysicsItem clientSpawnedItem = null;
            yield return WaitUntil(() =>
            {
                clientSpawnedItem = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                return clientSpawnedItem != null && clientSpawnedItem.IsSpawned;
            }, 5f, "Recipe 1 host SpawnNetworkedPrefab did not replicate to client.");

            // Non-host client DespawnNetworkedObject must log Error, return false, and leave host object intact
            LogAssert.Expect(LogType.Error, new Regex("DespawnNetworkedObject.*host-only"));
            Assert.IsFalse(clientAuthority.DespawnNetworkedObject(clientSpawnedItem.gameObject));
            yield return new WaitForSeconds(0.15f);
            Assert.IsTrue(spawned != null && spawned.GetComponent<NetworkObject>().IsSpawned,
                "Client DespawnNetworkedObject must not despawn host object.");

            // Recipe 1 Nit: passing a non-networked parent logs a warning and spawns at root;
            // passing a spawned NetworkObject parent calls TrySetParent cleanly
            var nonNetParent = new GameObject("NonNetworkedParent");
            spawnedRoots.Add(nonNetParent);
            LogAssert.Expect(LogType.Warning, new Regex("SpawnNetworkedPrefab.*parent.*NetworkObject"));
            GameObject spawnedAtRoot = hostAuthority.SpawnNetworkedPrefab(
                settings.NetworkTrashPrefab,
                new Vector3(0.5f, 1.5f, 0f),
                Quaternion.identity,
                nonNetParent.transform);
            Assert.IsNotNull(spawnedAtRoot);
            spawnedRoots.Add(spawnedAtRoot);
            Assert.IsNull(spawnedAtRoot.transform.parent, "Non-networked parent must spawn at scene root.");
            Assert.IsTrue(hostAuthority.DespawnNetworkedObject(spawnedAtRoot));

            GameObject spawnedUnderNetParent = hostAuthority.SpawnNetworkedPrefab(
                settings.NetworkTrashPrefab,
                new Vector3(0.5f, 2f, 0f),
                Quaternion.identity,
                hostRep.transform);
            Assert.IsNotNull(spawnedUnderNetParent);
            spawnedRoots.Add(spawnedUnderNetParent);
            Assert.AreSame(hostRep.transform, spawnedUnderNetParent.transform.parent,
                "Spawned NetworkObject parent must parent via NetworkObject.TrySetParent.");
            Assert.IsTrue(hostAuthority.DespawnNetworkedObject(spawnedUnderNetParent));

            Assert.IsTrue(hostAuthority.DespawnNetworkedObject(spawned));
            yield return WaitUntil(() => clientSpawnedItem == null || !clientSpawnedItem.IsSpawned,
                4f, "Recipe 1 host DespawnNetworkedObject did not despawn item on client.");

            // Recipe 2: ISyncedStateProvider
            var hostCompactor = new TestMachineProvider("compactor:compactor_01");
            var clientCompactor = new TestMachineProvider("compactor:compactor_01");
            hostCompactor.SetState(0.8f, 0f, 3, 1);
            hostRegistry.Register(hostCompactor);
            clientRegistry.Register(clientCompactor);

            yield return WaitUntil(() =>
                clientCompactor.ApplyCount >= 1
                && Mathf.Abs(clientCompactor.Current.v0 - 0.8f) < 0.01f
                && clientCompactor.Current.i0 == 3,
                4f, "Recipe 2 ISyncedStateProvider did not replicate to client.");

            // Recipe 3 (N1 + N3 + Result RPC): Two crafting benches share actionId "craft_bicycle" via distinct targetIds + range check + host spawn + onResult callback
            Vector3 benchAPos = new Vector3(0f, 0.5f, 0f);
            Vector3 benchBPos = new Vector3(25f, 0.5f, 0f);
            int benchAInvocations = 0;
            int benchBInvocations = 0;
            bool benchBRejectedRange = false;
            ulong customSender = ulong.MaxValue;
            GameObject craftedOnHost = null;

            CustomGameActionHandler benchAHandler = (in CustomGameActionContext ctx) =>
            {
                benchAInvocations++;
                customSender = ctx.SenderClientId;
                if (!ctx.IsWithinRange(benchAPos, 4.5f) || ctx.IntArg != 2)
                    return CustomGameActionResult.Fail("Invalid recipe or out of range.");

                craftedOnHost = hostAuthority.SpawnNetworkedPrefab(
                    settings.NetworkTrashPrefab,
                    benchAPos + Vector3.up,
                    Quaternion.identity);
                if (craftedOnHost != null)
                    spawnedRoots.Add(craftedOnHost);
                return craftedOnHost != null
                    ? CustomGameActionResult.Ok("Crafted Bicycle!")
                    : CustomGameActionResult.Fail("Spawn failed.");
            };

            CustomGameActionHandler benchBHandler = (in CustomGameActionContext ctx) =>
            {
                benchBInvocations++;
                if (!ctx.IsWithinRange(benchBPos, 4.5f))
                {
                    benchBRejectedRange = true;
                    return CustomGameActionResult.Fail("Not enough materials or too far.");
                }
                return CustomGameActionResult.Ok("Crafted at B");
            };

            GameAuthority.RegisterCustomActionHandler("craft_bicycle", "bench_A", benchAHandler);
            GameAuthority.RegisterCustomActionHandler("craft_bicycle", "bench_B", benchBHandler);
            try
            {
                // Request far bench_B -> routed ONLY to bench_B, rejected by server-verified SenderPosition range check, sends failure message back to client!
                bool benchBResultReceived = false;
                bool benchBSuccess = true;
                string benchBMessage = null;
                clientAuthority.RequestCustomAction("craft_bicycle", "bench_B", intArg: 2, onResult: (ok, msg) =>
                {
                    benchBResultReceived = true;
                    benchBSuccess = ok;
                    benchBMessage = msg;
                });
                yield return WaitUntil(() => benchBInvocations == 1 && benchBRejectedRange && benchBResultReceived,
                    4f, "bench_B handler did not run, reject out-of-range client, or return onResult callback.");
                Assert.AreEqual(0, benchAInvocations, "bench_A must not fire when bench_B is targeted.");
                Assert.IsNull(craftedOnHost);
                Assert.IsFalse(benchBSuccess);
                Assert.AreEqual("Not enough materials or too far.", benchBMessage);

                // Request nearby bench_A -> routed ONLY to bench_A, passes range check, spawns item on host & returns success message to client!
                bool benchAResultReceived = false;
                bool benchASuccess = false;
                string benchAMessage = null;
                clientAuthority.RequestCustomAction("craft_bicycle", "bench_A", intArg: 2, onResult: (ok, msg) =>
                {
                    benchAResultReceived = true;
                    benchASuccess = ok;
                    benchAMessage = msg;
                });
                yield return WaitUntil(() => benchAInvocations == 1 && craftedOnHost != null && benchAResultReceived,
                    4f, "bench_A handler did not spawn crafted item on host or invoke client onResult callback.");
                Assert.AreEqual(clientManager.LocalClientId, customSender);
                Assert.AreEqual(1, benchBInvocations);
                Assert.IsTrue(benchASuccess);
                Assert.AreEqual("Crafted Bicycle!", benchAMessage);

                NetworkedPhysicsItem clientCrafted = null;
                yield return WaitUntil(() =>
                {
                    clientCrafted = FindSpawnedBehaviour<NetworkedPhysicsItem>(clientManager);
                    return clientCrafted != null && clientCrafted.IsSpawned;
                }, 4f, "Host-spawned crafted item from custom action did not replicate to client.");
            }
            finally
            {
                GameAuthority.UnregisterCustomActionHandler("craft_bicycle", "bench_A", benchAHandler);
                GameAuthority.UnregisterCustomActionHandler("craft_bicycle", "bench_B", benchBHandler);
            }

            // Recipe 4: IGameAuthority.TriggerCityEvent
            hostAuthority.TriggerCityEvent("Street Food Market Surge", 20f, severity: 1);
            yield return WaitUntil(() =>
                clientRep.ActiveEventTitle == "Street Food Market Surge"
                && clientRep.ActiveEventTimeRemaining > 15f
                && clientRep.ActiveEventSeverity == 1,
                4f, "Recipe 4 TriggerCityEvent did not replicate to client.");
        }

        [UnityTest]
        public IEnumerator HostAndClient_NetworkPlayer_SpawnsReplicatesAppearanceAndIsolatesRemoteCamera()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;
            yield return StartHostAndClient(settings, port);

            var hostLook = new CharacterAppearance
            {
                sex = CharacterSex.Male,
                skinTone = 3,
                outfitColor = 5,
                eyeStyle = 2,
                eyeColor = 1,
                hairStyle = 4,
                hairColor = 2,
                facialHair = 1,
            };

            GameObject hostPlayerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(-2f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(hostPlayerGo);
            hostPlayerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(hostManager.LocalClientId);
            NetworkPlayerController hostOwnPlayer = hostPlayerGo.GetComponent<NetworkPlayerController>();
            hostOwnPlayer.InitializeServerIdentity(1, "Amine", NetworkCharacterAppearance.From(hostLook));

            GameObject remoteClientPlayerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(2f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(remoteClientPlayerGo);
            remoteClientPlayerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientManager.LocalClientId);
            NetworkPlayerController hostSideClientProxy = remoteClientPlayerGo.GetComponent<NetworkPlayerController>();
            hostSideClientProxy.InitializeServerIdentity(2, "Maya", NetworkCharacterAppearance.From(CharacterAppearance.Default));

            NetworkPlayerController clientOwnPlayer = null;
            NetworkPlayerController clientSideHostProxy = null;
            yield return WaitUntil(() =>
            {
                clientOwnPlayer = null;
                clientSideHostProxy = null;
                foreach (NetworkObject obj in clientManager.SpawnManager.SpawnedObjectsList)
                {
                    if (obj != null && obj.TryGetComponent(out NetworkPlayerController npc))
                    {
                        if (npc.IsOwner) clientOwnPlayer = npc;
                        else clientSideHostProxy = npc;
                    }
                }
                return clientOwnPlayer != null && clientSideHostProxy != null
                    && clientSideHostProxy.DisplayLabel == "P1 · Amine"
                    && clientOwnPlayer.DisplayLabel == "P2 · Maya"
                    && clientSideHostProxy.CurrentAppearance.outfitColor == 5
                    && clientSideHostProxy.CurrentAppearance.hairStyle == 4;
            }, 5f, "Client did not receive both NetworkPlayer instances with replicated labels & appearance.");

            // Verify N5: cached DisplayLabel and SlotTag return identical string references (no per-frame allocation)
            Assert.AreSame(clientSideHostProxy.DisplayLabel, clientSideHostProxy.DisplayLabel);
            Assert.AreSame(clientSideHostProxy.SlotTag, clientSideHostProxy.SlotTag);

            foreach (Camera cam in clientSideHostProxy.GetComponentsInChildren<Camera>(true))
                Assert.IsFalse(cam.enabled, "Remote player proxy Camera must be disabled.");
            foreach (AudioListener listener in clientSideHostProxy.GetComponentsInChildren<AudioListener>(true))
                Assert.IsFalse(listener.enabled, "Remote player proxy AudioListener must be disabled.");

            foreach (Camera cam in hostSideClientProxy.GetComponentsInChildren<Camera>(true))
                Assert.IsFalse(cam.enabled, "Host's remote client proxy Camera must be disabled.");
            foreach (AudioListener listener in hostSideClientProxy.GetComponentsInChildren<AudioListener>(true))
                Assert.IsFalse(listener.enabled, "Host's remote client proxy AudioListener must be disabled.");
        }

        [UnityTest]
        public IEnumerator CoopMenuController_LateJoinRejectionAndCancel_UpdatesMenuStateBannerAndHidesCreator()
        {
            SceneManager.LoadScene("CharacterCreator", LoadSceneMode.Single);
            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "CharacterCreator"
                && CoopSessionManager.Instance != null
                && UnityEngine.Object.FindAnyObjectByType<CoopMenuController>() != null
                && UnityEngine.Object.FindAnyObjectByType<CharacterCreatorController>() != null,
                6f, "CharacterCreator scene, CoopSessionManager, CoopMenuController, or CharacterCreatorController did not load.");

            CoopSessionManager sm = CoopSessionManager.Instance;
            CoopMenuController menu = UnityEngine.Object.FindAnyObjectByType<CoopMenuController>();
            CharacterCreatorController creator = UnityEngine.Object.FindAnyObjectByType<CharacterCreatorController>();
            NetworkSettings settings = LoadNetworkSettings();

            // 1. Start a separate host NetworkManager that rejects connections with GAME_IN_PROGRESS
            ushort port = nextPort++;
            hostManager = CreateNetworkManager("RejectingHostNM", settings, port, isServer: true);
            hostManager.NetworkConfig.EnableSceneManagement = true;
            hostManager.NetworkConfig.ConnectionApproval = true;
            hostManager.ConnectionApprovalCallback = (req, resp) =>
            {
                bool isHostSelf = req.ClientNetworkId == hostManager.LocalClientId && hostManager.IsHost;
                resp.CreatePlayerObject = false;
                if (!isHostSelf)
                {
                    resp.Approved = false;
                    resp.Reason = CoopSessionManager.DisconnectCodeGameInProgress;
                    return;
                }
                resp.Approved = true;
            };
            Assert.IsTrue(hostManager.StartHost(), $"Rejecting host failed to start on port {port}.");

            // 2. Open Join screen and submit Direct IP -> must enter Connecting (with Cancel), NEVER stuck on Lobby
            menu.OpenJoinInput(CoopTransportMode.Direct);
            Assert.AreEqual("JoinInput", menu.CurrentScreenName);
            Assert.IsFalse(creator.IsCreatorPanelVisible, "Character Creator panel must be hidden while Join screen is open.");

            menu.SubmitJoin($"127.0.0.1:{port}", CoopTransportMode.Direct);
            Assert.AreEqual("Connecting", menu.CurrentScreenName,
                "Join submission must show Connecting screen while awaiting approval, never jumping straight to Lobby.");
            Assert.IsTrue(menu.IsCancelButtonVisible, "Cancel button must be visible on Connecting screen.");
            Assert.IsFalse(creator.IsCreatorPanelVisible);

            // 3. Wait for rejection -> must transition to JoinInput with friendly error banner (NOT Lobby!)
            yield return WaitUntil(() =>
                sm.State == CoopSessionState.Offline
                && menu.CurrentScreenName == "JoinInput"
                && menu.IsBannerVisible,
                6f, $"Menu did not return to JoinInput with error banner after rejection (screen={menu.CurrentScreenName}, state={sm.State}).");

            Assert.That(menu.BannerText, Does.Contain("already in progress"),
                $"Expected friendly 'already in progress' banner text, got: '{menu.BannerText}'");
            Assert.IsFalse(creator.IsCreatorPanelVisible,
                "Character Creator panel must remain hidden underneath JoinInput after rejection.");

            // 4. Test Cancel button during Connecting against a closed port
            hostManager.Shutdown();
            yield return null;

            ushort unusedPort = nextPort++;
            menu.SubmitJoin($"127.0.0.1:{unusedPort}", CoopTransportMode.Direct);
            Assert.AreEqual("Connecting", menu.CurrentScreenName);
            Assert.IsTrue(menu.IsCancelButtonVisible);

            menu.CancelConnecting();
            yield return null;
            Assert.AreEqual(CoopSessionState.Offline, sm.State);
            Assert.AreEqual("JoinInput", menu.CurrentScreenName);
            Assert.IsFalse(menu.IsBannerVisible, "Manual Cancel should not show an error banner.");
        }

        [UnityTest]
        public IEnumerator HostAndClient_CoopSessionManager_FullSessionFlowLateJoinRejectionAndLeaveToMenu()
        {
            // 1. Load CharacterCreator menu scene with CoopSessionManager
            SceneManager.LoadScene("CharacterCreator", LoadSceneMode.Single);
            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "CharacterCreator"
                && CoopSessionManager.Instance != null,
                6f, "CharacterCreator scene or CoopSessionManager did not load.");

            CoopSessionManager sm = CoopSessionManager.Instance;
            ushort port = nextPort++;

            // 2. Host Direct Co-op -> enters Lobby
            Task<(bool success, string error)> hostTask = sm.HostCoopAsync(CoopTransportMode.Direct, "127.0.0.1", port);
            yield return WaitUntil(() => hostTask.IsCompleted, 5f, "HostCoopAsync did not complete.");
            Assert.IsTrue(hostTask.Result.success, $"HostCoopAsync failed: {hostTask.Result.error}");
            Assert.AreEqual(CoopSessionState.Lobby, sm.State);

            // 3. Start gameplay -> NGO SceneManager loads EcosystemSandbox and spawns NetworkStateReplicator
            Assert.IsTrue(sm.StartCoopGameplay(), "StartCoopGameplay should start scene transition.");
            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "EcosystemSandbox"
                && sm.State == CoopSessionState.InGame
                && NetworkStateReplicator.Active != null
                && NetworkStateReplicator.Active.IsSpawned,
                10f, "Co-op session did not transition to EcosystemSandbox InGame with NetworkStateReplicator.");

            // 4. Late-joining client MUST be rejected by connection approval with GAME_IN_PROGRESS
            NetworkSettings settings = LoadNetworkSettings();
            var lateGo = new GameObject("LateJoinClientNM");
            spawnedRoots.Add(lateGo);
            NetworkManager lateClient = lateGo.AddComponent<NetworkManager>();
            UnityTransport lateUtp = lateGo.AddComponent<UnityTransport>();
            lateUtp.SetConnectionData("127.0.0.1", port);
            lateClient.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = lateUtp,
                TickRate = settings.TickRate,
                EnableSceneManagement = true,
                ConnectionApproval = true,
                PlayerPrefab = settings.NetworkPlayerPrefab,
            };
            if (settings.NetworkPrefabsList != null)
                lateClient.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(settings.NetworkPrefabsList);

            bool lateDisconnected = false;
            string rawRejectReason = null;
            lateClient.OnClientDisconnectCallback += _ =>
            {
                lateDisconnected = true;
                rawRejectReason = lateClient.DisconnectReason;
            };

            Assert.IsTrue(lateClient.StartClient());
            yield return WaitUntil(() => lateDisconnected, 6f, "Late-joining client was not rejected by host.");
            Assert.IsFalse(lateClient.IsConnectedClient);
            Assert.AreEqual(CoopSessionManager.DisconnectCodeGameInProgress, rawRejectReason,
                "Host connection approval must reject mid-game late joins with GAME_IN_PROGRESS.");

            string friendly = CoopSessionManager.FormatFriendlyDisconnectReason(rawRejectReason, CoopSessionState.Connecting);
            Assert.That(friendly, Does.Contain("already in progress"));

            if (lateClient.IsListening)
                lateClient.Shutdown();
            UnityEngine.Object.DestroyImmediate(lateGo);
            spawnedRoots.Remove(lateGo);

            // 5. LeaveSession cleanly shuts down, restores LocalGameAuthority, and returns to CharacterCreator WITHOUT menu/creator overlap
            sm.LeaveSession("Finished test session.");
            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "CharacterCreator"
                && sm.State == CoopSessionState.Offline
                && ReferenceEquals(GameAuthority.Current, LocalGameAuthority.Instance)
                && UnityEngine.Object.FindAnyObjectByType<CoopMenuController>() != null,
                8f, "LeaveSession did not cleanly return to CharacterCreator and LocalGameAuthority.");
            yield return null;

            CoopMenuController menuAfterLeave = UnityEngine.Object.FindAnyObjectByType<CoopMenuController>();
            CharacterCreatorController creatorAfterLeave = UnityEngine.Object.FindAnyObjectByType<CharacterCreatorController>();
            Assert.AreEqual("ModeSelect", menuAfterLeave.CurrentScreenName);
            Assert.IsTrue(menuAfterLeave.IsBannerVisible);
            Assert.AreEqual("Finished test session.", menuAfterLeave.BannerText);
            Assert.IsNotNull(creatorAfterLeave);
            Assert.IsFalse(creatorAfterLeave.IsCreatorPanelVisible,
                "CharacterCreator panel must stay hidden when ModeSelect opens after disconnect/leave (Nit 1).");
        }

        [UnityTest]
        public IEnumerator CoopSessionManager_PauseAndResume_CopyCodeAndTimeout_KeepsAliveAndRecoversDeadSession()
        {
            SceneManager.LoadScene("CharacterCreator", LoadSceneMode.Single);
            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "CharacterCreator"
                && CoopSessionManager.Instance != null
                && UnityEngine.Object.FindAnyObjectByType<CoopMenuController>() != null,
                6f, "CharacterCreator scene did not load.");

            CoopSessionManager sm = CoopSessionManager.Instance;
            CoopMenuController menu = UnityEngine.Object.FindAnyObjectByType<CoopMenuController>();
            CoopInGameHud hud = sm.GetComponent<CoopInGameHud>();
            Assert.IsNotNull(hud, "CoopInGameHud must exist on CoopSessionManager.");

            ushort port = nextPort++;
            Task<(bool success, string error)> hostTask = sm.HostCoopAsync(CoopTransportMode.Direct, "127.0.0.1", port);
            yield return WaitUntil(() => hostTask.IsCompleted, 5f, "HostCoopAsync did not complete.");
            Assert.IsTrue(hostTask.Result.success, $"HostCoopAsync failed: {hostTask.Result.error}");
            Assert.AreEqual(CoopSessionState.Lobby, sm.State);

            // Verify m3: NetworkSettings.DisconnectTimeoutSeconds (15s) applied to UnityTransport
            NetworkManager hostNm = sm.GetComponent<NetworkManager>();
            UnityTransport hostUtp = sm.GetComponent<UnityTransport>();
            Assert.AreEqual(15000, hostUtp.DisconnectTimeoutMS, "UnityTransport.DisconnectTimeoutMS must match 15s NetworkSettings.");
            Assert.AreEqual(1000, hostUtp.HeartbeatTimeoutMS, "UnityTransport.HeartbeatTimeoutMS must be scaled to 1000ms.");

            // Verify M1 Copy button in Lobby copies ShareableSessionCode to clipboard and shows "Copied!"
            yield return null;
            Assert.IsTrue(menu.IsCopyCodeButtonVisible, "Lobby Copy code button must be visible.");
            Assert.AreEqual("Copy", menu.CopyCodeButtonText);
            Assert.IsTrue(menu.CopySessionCode());
            Assert.AreEqual($"127.0.0.1:{port}", GUIUtility.systemCopyBuffer);
            Assert.AreEqual("Copied!", menu.CopyCodeButtonText);

            // Connect a real client before entering gameplay so we verify client survives host pause/resume too
            NetworkSettings settings = LoadNetworkSettings();
            clientManager = CreateNetworkManager("PauseResumeClientNM", settings, port, isServer: false);
            clientManager.NetworkConfig.EnableSceneManagement = true;
            clientManager.NetworkConfig.ConnectionApproval = true;
            Assert.IsTrue(clientManager.StartClient());
            yield return WaitUntil(() => hostNm.ConnectedClientsIds.Count == 2 && clientManager.IsConnectedClient,
                6f, "Client did not connect before pause test.");

            // Start gameplay -> EcosystemSandbox
            Assert.IsTrue(sm.StartCoopGameplay());
            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "EcosystemSandbox"
                && sm.State == CoopSessionState.InGame
                && NetworkStateReplicator.Active != null
                && NetworkStateReplicator.Active.IsSpawned,
                10f, "Co-op did not reach EcosystemSandbox InGame.");
            yield return null;

            // Verify M1 Copy button in CoopInGameHud copies code and displays "Copied!"
            GUIUtility.systemCopyBuffer = string.Empty;
            Assert.IsTrue(hud.IsCopyButtonVisible, "In-game HUD Copy button must be visible.");
            Assert.AreEqual("Copy", hud.CopyButtonText);
            Assert.IsTrue(hud.CopySessionCode());
            Assert.AreEqual($"127.0.0.1:{port}", GUIUtility.systemCopyBuffer);
            Assert.AreEqual("Copied!", hud.CopyButtonText);

            // M1 Case 1: Host pauses & resumes while session is alive -> MUST stay InGame with client still connected
            MethodInfo onPause = typeof(CoopSessionManager).GetMethod("OnApplicationPause", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(onPause);
            onPause.Invoke(sm, new object[] { true });
            yield return null;
            Assert.IsTrue(hostNm.IsListening, "OnApplicationPause(true) must NOT tear down Netcode.");
            Assert.AreEqual(CoopSessionState.InGame, sm.State);

            onPause.Invoke(sm, new object[] { false });
            yield return null;
            Assert.IsTrue(hostNm.IsListening, "Host must remain listening after pause -> resume alive.");
            Assert.AreEqual(CoopSessionState.InGame, sm.State);
            Assert.AreEqual("EcosystemSandbox", SceneManager.GetActiveScene().name);
            Assert.IsTrue(clientManager.IsConnectedClient, "Connected client must remain connected across brief host pause.");
            Assert.IsTrue(NetworkStateReplicator.Active != null && NetworkStateReplicator.Active.IsSpawned);

            // Clean up test client before simulating OS killing host NGO while paused
            clientManager.Shutdown();
            yield return WaitUntil(() => hostNm.ConnectedClientsIds.Count == 1, 4f, "Client did not disconnect.");

            // M1 Case 2: Session dies while paused -> OnApplicationPause(false) must route through LeaveSession to CharacterCreator with banner (never soft-locked!)
            onPause.Invoke(sm, new object[] { true });
            hostNm.Shutdown();
            yield return WaitUntil(() => !hostNm.IsListening, 4f, "Host NetworkManager did not shut down.");
            onPause.Invoke(sm, new object[] { false });

            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "CharacterCreator"
                && sm.State == CoopSessionState.Offline
                && UnityEngine.Object.FindAnyObjectByType<CoopMenuController>() != null,
                8f, "Dead session after resume did not return cleanly to CharacterCreator menu.");
            yield return null;

            CoopMenuController menuAfterDeadResume = UnityEngine.Object.FindAnyObjectByType<CoopMenuController>();
            Assert.AreEqual("ModeSelect", menuAfterDeadResume.CurrentScreenName);
            Assert.IsTrue(menuAfterDeadResume.IsBannerVisible, "Menu banner must explain connection lost while in background.");
            Assert.That(menuAfterDeadResume.BannerText, Does.Contain("background").IgnoreCase);

            // M1 Case 3: Client pauses & resumes while host is alive -> client stays connected in Lobby
            ushort clientPausePort = nextPort++;
            hostManager = CreateNetworkManager("ClientPauseHostNM", settings, clientPausePort, isServer: true);
            hostManager.NetworkConfig.EnableSceneManagement = true;
            hostManager.NetworkConfig.ConnectionApproval = true;
            hostManager.ConnectionApprovalCallback = (_, resp) => { resp.CreatePlayerObject = false; resp.Approved = true; };
            Assert.IsTrue(hostManager.StartHost());

            Task<(bool success, string error)> joinTask = sm.JoinCoopAsync(CoopTransportMode.Direct, $"127.0.0.1:{clientPausePort}");
            yield return WaitUntil(() => joinTask.IsCompleted && sm.State == CoopSessionState.Lobby,
                6f, "Client CoopSessionManager did not join host lobby.");

            onPause.Invoke(sm, new object[] { true });
            yield return null;
            Assert.IsTrue(hostNm.IsListening && hostNm.IsConnectedClient, "Client pause must not tear down Netcode.");
            onPause.Invoke(sm, new object[] { false });
            yield return null;
            Assert.IsTrue(hostNm.IsListening && hostNm.IsConnectedClient, "Client must stay connected after pause -> resume alive.");
            Assert.AreEqual(CoopSessionState.Lobby, sm.State);
        }

        [UnityTest]
        public IEnumerator CoopSessionManager_CancelDuringInFlightConnect_NeverStartsHostOrClient()
        {
            SceneManager.LoadScene("CharacterCreator", LoadSceneMode.Single);
            yield return WaitUntil(() =>
                SceneManager.GetActiveScene().name == "CharacterCreator"
                && CoopSessionManager.Instance != null,
                6f, "CharacterCreator scene did not load.");

            CoopSessionManager sm = CoopSessionManager.Instance;
            NetworkManager nm = sm.GetComponent<NetworkManager>();
            ushort port = nextPort++;

            // 1. Cancel mid-flight HostCoopAsync (simulating slow UGS/Relay or transport startup)
            var hostHookGate = new TaskCompletionSource<bool>();
            bool hostHookCanceled = false;
            sm.PreConnectAsyncHook = async ct =>
            {
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { hostHookCanceled = true; }
                await hostHookGate.Task;
            };

            Task<(bool success, string error)> hostTask = sm.HostCoopAsync(CoopTransportMode.Direct, "127.0.0.1", port);
            Assert.AreEqual(CoopSessionState.Connecting, sm.State);

            // User taps Cancel while await is still pending
            sm.LeaveSession();
            hostHookGate.TrySetResult(true);

            yield return WaitUntil(() => hostTask.IsCompleted, 4f, "Canceled HostCoopAsync did not finish.");
            yield return null;

            Assert.IsTrue(hostHookCanceled, "CancellationToken must be signaled on Cancel.");
            Assert.IsFalse(hostTask.Result.success, "Canceled HostCoopAsync must return success=false.");
            Assert.AreEqual(CoopSessionState.Offline, sm.State);
            Assert.IsFalse(nm.IsListening, "StartHost() MUST NOT run after mid-flight Cancel.");

            // 2. Cancel mid-flight JoinCoopAsync even if a late async result completes ignoring CancellationToken
            var joinHookGate = new TaskCompletionSource<bool>();
            sm.PreConnectAsyncHook = _ => joinHookGate.Task;

            Task<(bool success, string error)> joinTask = sm.JoinCoopAsync(CoopTransportMode.Direct, $"127.0.0.1:{port}");
            Assert.AreEqual(CoopSessionState.Connecting, sm.State);

            sm.LeaveSession();
            joinHookGate.TrySetResult(true); // Late completion after Cancel!

            yield return WaitUntil(() => joinTask.IsCompleted, 4f, "Canceled JoinCoopAsync did not finish.");
            yield return null;

            Assert.IsFalse(joinTask.Result.success, "Canceled JoinCoopAsync must return success=false.");
            Assert.AreEqual(CoopSessionState.Offline, sm.State);
            Assert.IsFalse(nm.IsListening, "StartClient() MUST NOT run after mid-flight Cancel.");

            sm.PreConnectAsyncHook = null;
        }

        [UnityTest]
        public IEnumerator CoopSessionManager_HostLeaveSession_FlushesHostLeftReasonToClient()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;

            var sessionGo = new GameObject("FlushHostLeftSessionHost");
            sessionGo.SetActive(false);
            CoopSessionManager sm = sessionGo.AddComponent<CoopSessionManager>();
            sm.Configure(settings);
            sessionGo.SetActive(true);

            Task<(bool success, string error)> hostTask = sm.HostCoopAsync(CoopTransportMode.Direct, "127.0.0.1", port);
            yield return WaitUntil(() => hostTask.IsCompleted, 5f, "HostCoopAsync did not complete.");
            Assert.IsTrue(hostTask.Result.success);

            NetworkManager smHostNm = sm.GetComponent<NetworkManager>();
            clientManager = CreateNetworkManager("HostLeftReceiveClientNM", settings, port, isServer: false);
            clientManager.NetworkConfig.EnableSceneManagement = true;
            clientManager.NetworkConfig.ConnectionApproval = true;

            bool clientDisconnected = false;
            string receivedReason = null;
            clientManager.OnClientDisconnectCallback += id =>
            {
                if (id == clientManager.LocalClientId)
                {
                    clientDisconnected = true;
                    receivedReason = clientManager.DisconnectReason;
                }
            };

            Assert.IsTrue(clientManager.StartClient());
            yield return WaitUntil(() => smHostNm.ConnectedClientsIds.Count == 2 && clientManager.IsConnectedClient,
                6f, "Client did not connect to host.");

            // Host leaves explicitly -> must flush HOST_LEFT before Shutdown so client gets exact reason!
            sm.LeaveSession();

            yield return WaitUntil(() => clientDisconnected, 5f, "Client did not receive disconnect from host.");
            Assert.AreEqual(CoopSessionManager.DisconnectCodeHostLeft, receivedReason,
                "Host LeaveSession must flush HOST_LEFT to connected clients before shutting down transport.");
            Assert.AreEqual("Host left the co-op session.",
                CoopSessionManager.FormatFriendlyDisconnectReason(receivedReason, CoopSessionState.Lobby));
        }

        [UnityTest]
        public IEnumerator CoopSessionManager_LeaveThenImmediateHost_WaitsForTeardownAndHostsCleanly()
        {
            NetworkSettings settings = LoadNetworkSettings();
            ushort port = nextPort++;

            var sessionGo = new GameObject("LeaveThenHostSession");
            sessionGo.SetActive(false);
            CoopSessionManager sm = sessionGo.AddComponent<CoopSessionManager>();
            sm.Configure(settings);
            sessionGo.SetActive(true);

            Task<(bool success, string error)> firstHost = sm.HostCoopAsync(CoopTransportMode.Direct, "127.0.0.1", port);
            yield return WaitUntil(() => firstHost.IsCompleted, 5f, "First HostCoopAsync did not complete.");
            Assert.IsTrue(firstHost.Result.success, firstHost.Result.error);

            NetworkManager smHostNm = sm.GetComponent<NetworkManager>();
            clientManager = CreateNetworkManager("LeaveThenHostClientNM", settings, port, isServer: false);
            clientManager.NetworkConfig.EnableSceneManagement = true;
            clientManager.NetworkConfig.ConnectionApproval = true;
            Assert.IsTrue(clientManager.StartClient());
            yield return WaitUntil(() => smHostNm.ConnectedClientsIds.Count == 2 && clientManager.IsConnectedClient,
                6f, "Client did not connect to host.");

            // Leave (which flushes HOST_LEFT for ~120 ms) and tap Host again in the same frame,
            // exactly what a player does from the menu.
            sm.LeaveSession();
            Assert.IsTrue(sm.IsLeaving, "LeaveSession with a connected client must report an in-flight teardown.");
            Task<(bool success, string error)> secondHost = sm.HostCoopAsync(CoopTransportMode.Direct, "127.0.0.1", port);

            yield return WaitUntil(() => secondHost.IsCompleted, 8f, "Second HostCoopAsync did not complete.");
            Assert.IsTrue(secondHost.Result.success,
                "Hosting right after Leave must wait for the old teardown instead of failing: " + secondHost.Result.error);
            Assert.IsFalse(sm.IsLeaving);

            // The finished old leave must not reset the new session afterwards.
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.AreEqual(CoopSessionState.Lobby, sm.State, "Old LeaveSession must not reset the new hosting attempt.");
            Assert.IsTrue(smHostNm.IsListening && smHostNm.IsHost);
        }

        [UnityTest]
        public IEnumerator OfflineSolo_LocalGameAuthorityAndSortingWorkWithoutNetworkManager()
        {
            Assert.IsNull(hostManager);
            Assert.IsNull(clientManager);
            GameAuthority.ResetToLocal();

            Assert.IsTrue(GameAuthority.IsAuthoritative);
            Assert.IsFalse(GameAuthority.IsMultiplayer);

            var playerGo = new GameObject("SoloInventoryHost");
            spawnedRoots.Add(playerGo);
            PlayerInventory inv = playerGo.AddComponent<PlayerInventory>();
            inv.AddCoins(50);
            int startingCoins = inv.Coins;

            var crateGo = new GameObject("SoloPaperCrate");
            spawnedRoots.Add(crateGo);
            SortingCrate crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Paper, payOnSort: true);

            var trashGo = new GameObject("SoloPaperCup");
            spawnedRoots.Add(trashGo);
            trashGo.AddComponent<BoxCollider>();
            trashGo.AddComponent<Rigidbody>();
            TrashItem item = trashGo.AddComponent<TrashItem>();
            item.Configure("Paper Cup", WasteStream.Paper, WasteSourceKind.Citizen, 0.2f, 5);

            GameAuthority.Current.RequestSortTrash(crate, item, null);
            yield return null;

            Assert.AreEqual(1, crate.CorrectCount);
            Assert.IsTrue(item == null, "Sorted offline trash should be destroyed locally.");

            // Verify catalog-resolved solo shop spend via LocalGameAuthority
            ShopCatalog toolsCatalog = LoadToolsCatalog();
            ShopCatalog.Offer offer = toolsCatalog.Offers[0];
            Assert.IsTrue(inv.CanAddItem(offer.item, offer.quantity));

            bool purchaseDone = false;
            ShopPurchaseResult res = default;
            GameAuthority.Current.RequestShopPurchase(
                new ShopPurchaseRequest(string.Empty, offer.item.ItemId, offer.quantity, Vector3.zero, inv, toolsCatalog),
                r =>
                {
                    res = r;
                    purchaseDone = true;
                });

            Assert.IsTrue(purchaseDone);
            Assert.IsTrue(res.Approved);
            Assert.AreEqual(offer.price, res.Price);
            Assert.AreEqual(startingCoins - offer.price, inv.Coins);
        }

        // ---------------------------------------------------------------- Helpers

        IEnumerator StartHostAndClient(NetworkSettings settings, ushort port)
        {
            hostManager = CreateNetworkManager("TestHostNM", settings, port, isServer: true);
            Assert.IsTrue(hostManager.StartHost(), $"Host failed to start on port {port}.");

            clientManager = CreateNetworkManager("TestClientNM", settings, port, isServer: false);
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

        static void TeleportPlayer(NetworkPlayerController player, Vector3 worldPos)
        {
            player.transform.position = worldPos;
            Rigidbody rb = player.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.position = worldPos;
                if (!rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            NetworkTransform nt = player.GetComponent<NetworkTransform>();
            if (nt != null && player.IsSpawned)
                nt.Teleport(worldPos, player.transform.rotation, player.transform.localScale);
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

        static NetworkSettings LoadNetworkSettings()
        {
            return AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
        }

        static ShopCatalog LoadToolsCatalog()
        {
            return AssetDatabase.LoadAssetAtPath<ShopCatalog>("Assets/UpcycleCity/Settings/Shops/Shop_Tools.asset");
        }

        sealed class TestMachineProvider : ISyncedStateProvider
        {
            SyncedStateRecord state;
            public string SyncKey { get; }
            public SyncedStateRecord Current => state;
            public int ApplyCount { get; private set; }

            public TestMachineProvider(string key) => SyncKey = key;

            public void SetState(float v0, float v1, int i0, byte flags)
            {
                state = new SyncedStateRecord { v0 = v0, v1 = v1, i0 = i0, flags = flags };
            }

            public SyncedStateRecord CaptureSyncState() => state;

            public void ApplySyncState(in SyncedStateRecord incoming)
            {
                state = incoming;
                ApplyCount++;
            }
        }
    }
}
