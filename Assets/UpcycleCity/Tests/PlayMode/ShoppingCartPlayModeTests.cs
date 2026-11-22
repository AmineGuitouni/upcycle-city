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
using UpcycleCity.Inventory;
using UpcycleCity.Networking;
using UpcycleCity.Props;
using UpcycleCity.Shops;
using UpcycleCity.Waste;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>
    /// The Shopping Cart ("a moving bag") over a real host + client (loopback UnityTransport) and in offline solo:
    /// buy it at the Quincaillerie (host spawns a networked cart), the client pushes it (ownership moves to the client and
    /// the host sees it move), a second pusher is refused, picked-up trash overflows into it, items move bag ↔ cart, RÉCUP
    /// buys from it, trash thrown into the basket is stored — all decided by the host, replicated as synced records.
    /// </summary>
    public sealed class ShoppingCartPlayModeTests
    {
        static ushort nextPort = 18520;

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
            for (int i = ShoppingCart.All.Count - 1; i >= 0; i--)
            {
                if (ShoppingCart.All[i] != null)
                    UnityEngine.Object.DestroyImmediate(ShoppingCart.All[i].gameObject);
            }

            if (RecyclingBagService.Instance != null)
                UnityEngine.Object.DestroyImmediate(RecyclingBagService.Instance.gameObject);
            if (TeamMaterialStock.Instance != null)
                UnityEngine.Object.DestroyImmediate(TeamMaterialStock.Instance.gameObject);

            LocalGameAuthority.Instance.SetLocalWalletOverride(null);
            GameAuthority.ResetToLocal();
            GameAuthority.LocalClientIdResolver = null;
            CarryItems.CatalogOverride = null;
            SyncedStateRegistry.Clear();
        }

        [UnityTest]
        public IEnumerator HostAndClient_ShoppingCart_BuyPushStoreMoveSell_HostAuthoritative()
        {
            WasteCatalog catalog = LoadCatalog(out WasteCatalog.Entry bottle);
            Assert.IsNotNull(Resources.Load<GameObject>(RecyclingBagService.CartPrefabResource),
                "Resources/ShoppingCart.prefab is missing (Upcycle City → Props → Build Shopping Cart Prefab).");

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

            CreateFloor();
            // Host and client run in one process and share one physics world: freeze both copies of the client's player
            NetworkPlayerController clientCopy = null;
            GameObject playerGo = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, new Vector3(1f, 0.5f, 0f), Quaternion.identity);
            spawnedRoots.Add(playerGo);
            playerGo.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId);
            playerGo.GetComponent<NetworkPlayerController>().InitializeServerIdentity(
                2, "Pusher", NetworkCharacterAppearance.From(CharacterAppearance.Default), new Vector3(1f, 0.5f, 0f), Quaternion.identity);
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

            // ---- Buy at the Quincaillerie: host price from the catalog, team wallet, host spawns a networked cart
            ShopCatalog tools = AssetDatabase.LoadAssetAtPath<ShopCatalog>("Assets/UpcycleCity/Settings/Shops/Shop_Tools.asset");
            Assert.IsTrue(tools.TryFindOffer(CarryItems.CartUpgradeItemId, 1, out ShopCatalog.Offer cartOffer),
                "The Quincaillerie must sell the Shopping Cart (Upcycle City → Shops → Rebuild Shop Catalogs).");
            var counterGo = new GameObject("TestQuincaillerie");
            counterGo.transform.position = new Vector3(0f, 0f, 3f);
            spawnedRoots.Add(counterGo);
            counterGo.AddComponent<ShopCounter>().Configure(tools, null);

            Result poor = Request(client, RecyclingBagService.ActionBuyCart, string.Empty);
            yield return WaitUntil(() => poor.Done, 4f, "No reply to the first cart purchase.");
            Assert.IsFalse(poor.Success, "Can't buy a cart without the coins.");
            Assert.AreEqual(0, ShoppingCart.All.Count);

            host.CreditTeamCoins(cartOffer.price + 5);
            Result buy = Request(client, RecyclingBagService.ActionBuyCart, string.Empty);
            yield return WaitUntil(() => buy.Done, 4f, "No reply to buy cart.");
            Assert.IsTrue(buy.Success, buy.Message);
            StringAssert.Contains("waiting outside", buy.Message);
            Assert.AreEqual(5, hostRep.TeamCoins, "The cart is paid from the team wallet at the catalog price.");
            ShoppingCart hostCart = FindHostCart();
            Assert.IsNotNull(hostCart, "The host spawned no cart.");
            NetworkObject hostCartNet = hostCart.GetComponent<NetworkObject>();
            Assert.IsTrue(hostCartNet.IsSpawned, "The cart must be a spawned NetworkObject.");
            Assert.IsTrue(service.GetHostBag(clientId).HasCart);
            yield return WaitUntil(() => bagMirror.HasCart, 4f, "Owning a cart did not replicate to the buyer.");

            ShoppingCart clientCart = null;
            yield return WaitUntil(() => clientManager.SpawnManager.SpawnedObjects.TryGetValue(hostCartNet.NetworkObjectId, out NetworkObject o)
                && (clientCart = o.GetComponent<ShoppingCart>()) != null && clientCart.PlacementId == hostCart.PlacementId,
                5f, "The cart (and its placement id) did not spawn on the client.");
            IgnoreCopiesColliding(hostCart, clientCart, playerGo, clientCopy.gameObject);

            Result again = Request(client, RecyclingBagService.ActionBuyCart, string.Empty);
            yield return WaitUntil(() => again.Done, 4f, "No reply to the second cart purchase.");
            Assert.IsFalse(again.Success, "One cart per player.");

            // The client's view of the cart's storage (its own registry, as on a remote machine)
            var cartMirror = new CartStorage(hostCart.Storage.BaseKey);
            cartMirror.Register(clientRegistry);

            // ---- The client pushes: ownership (simulation) moves to the client and the host sees it move
            Result push = Request(client, RecyclingBagService.ActionCartPush, hostCart.SyncKey);
            yield return WaitUntil(() => push.Done, 4f, "No reply to push.");
            Assert.IsTrue(push.Success, push.Message);
            Assert.AreEqual(clientId, hostCart.PusherClientId);
            Assert.AreEqual(clientId, hostCartNet.OwnerClientId, "The host hands ownership to the pusher.");
            yield return WaitUntil(() => clientCart.GetComponent<NetworkObject>().IsOwner && clientCart.HasSimulationAuthority,
                4f, "The pushing client never became the cart's owner.");
            yield return WaitUntil(() => cartMirror.PusherClientId == clientId, 4f, "The pusher did not replicate.");
            Assert.IsFalse(hostCart.HasSimulationAuthority, "While pushed by the client the host's copy only follows.");

            // A second pusher (the host player) is refused while the client pushes
            Result steal = Request(host, RecyclingBagService.ActionCartPush, hostCart.SyncKey);
            yield return WaitUntil(() => steal.Done, 4f, "No reply to the second push.");
            Assert.IsFalse(steal.Success, "Only one pusher at a time.");
            StringAssert.Contains("Someone else", steal.Message);

            Vector3 hostBefore = hostCart.transform.position;
            Rigidbody clientBody = clientCart.GetComponent<Rigidbody>();
            Vector3 moved = clientCart.transform.position + new Vector3(2.5f, 0f, 0f);
            clientBody.position = moved;
            clientCart.transform.position = moved;
            yield return WaitUntil(() => (hostCart.transform.position - hostBefore).x > 1.5f, 4f,
                "The host did not see the client's cart move (owner-authoritative transform).");

            // ---- Moving bag: with full arms, picked-up trash goes into the pushed cart
            RecyclingBag hostBag = service.GetHostBag(clientId);
            int bottleId = CarryItems.IdForCatalogIndex(catalog.FindEntryIndex(bottle));
            for (int i = 0; i < CarryItems.HandsCapacity; i++)
                hostBag.TryAdd(bottleId);
            Assert.IsTrue(hostBag.IsFull);

            TrashItem hostTrash = host.SpawnTrash(catalog, bottle, WasteSourceKind.Citizen, new Vector3(1.5f, 0.4f, -1.5f), Vector3.zero);
            Assert.IsNotNull(hostTrash);
            ulong trashNetId = hostTrash.GetComponent<NetworkObject>().NetworkObjectId;
            NetworkedPhysicsItem clientItem = null;
            yield return WaitUntil(() => clientManager.SpawnManager.SpawnedObjects.TryGetValue(trashNetId, out NetworkObject o)
                && (clientItem = o.GetComponent<NetworkedPhysicsItem>()) != null, 4f, "Trash did not spawn on the client.");
            clientItem.OnLocalPickupStarted();
            NetworkedPhysicsItem hostItem = hostTrash.GetComponent<NetworkedPhysicsItem>();
            yield return WaitUntil(() => hostItem.HolderClientId == clientId, 4f, "The host never saw the client holding the trash.");
            Result store = Request(client, RecyclingBagService.ActionStore, string.Empty);
            yield return WaitUntil(() => store.Done, 4f, "No reply to store.");
            Assert.IsTrue(store.Success, store.Message);
            StringAssert.Contains("cart", store.Message);
            Assert.AreEqual(1, hostCart.Count, "Overflow goes into the cart.");
            Assert.AreEqual(CarryItems.HandsCapacity, hostBag.Count);
            yield return WaitUntil(() => cartMirror.Count == 1 && cartMirror[0] == bottleId, 4f, "Cart contents did not replicate.");

            // ---- Bag ↔ cart: taking out with full arms is refused; put one in, take one out
            Result takeFull = Request(client, RecyclingBagService.ActionCartTake, hostCart.SyncKey, 0, bottleId);
            yield return WaitUntil(() => takeFull.Done, 4f, "No reply to take with full arms.");
            Assert.IsFalse(takeFull.Success);

            Result put = Request(client, RecyclingBagService.ActionCartPut, hostCart.SyncKey, 0, bottleId);
            yield return WaitUntil(() => put.Done, 4f, "No reply to put.");
            Assert.IsTrue(put.Success, put.Message);
            Assert.AreEqual(2, hostCart.Count);
            Assert.AreEqual(CarryItems.HandsCapacity - 1, hostBag.Count);

            Result stalePut = Request(client, RecyclingBagService.ActionCartTake, hostCart.SyncKey, 0, CarryItems.MaxId);
            yield return WaitUntil(() => stalePut.Done, 4f, "No reply to the stale take.");
            Assert.IsFalse(stalePut.Success, "A stale (wrong expected id) move is refused.");

            Result take = Request(client, RecyclingBagService.ActionCartTake, hostCart.SyncKey, 1, bottleId);
            yield return WaitUntil(() => take.Done, 4f, "No reply to take.");
            Assert.IsTrue(take.Success, take.Message);
            Assert.AreEqual(1, hostCart.Count);
            Assert.AreEqual(CarryItems.HandsCapacity, hostBag.Count);
            yield return WaitUntil(() => cartMirror.Count == 1 && bagMirror.Count == CarryItems.HandsCapacity, 4f, "Moves did not replicate.");

            // ---- Sell from the cart at RÉCUP, then sell all (bag + cart)
            var buyBackGo = new GameObject("TestRecup");
            buyBackGo.transform.position = new Vector3(1f, 0f, 2f);
            spawnedRoots.Add(buyBackGo);
            RecyclingBuyBack buyBack = buyBackGo.AddComponent<RecyclingBuyBack>();
            int price = buyBack.PriceFor(bottle.stream, bottle.baseCoinValue);
            int coinsBefore = hostRep.TeamCoins;
            Result sellCart = Request(client, RecyclingBagService.ActionSell, buyBack.SyncKey, RecyclingBagService.CartSlotBase, bottleId);
            yield return WaitUntil(() => sellCart.Done, 4f, "No reply to sell from cart.");
            Assert.IsTrue(sellCart.Success, sellCart.Message);
            Assert.AreEqual(0, hostCart.Count);
            Assert.AreEqual(coinsBefore + price, hostRep.TeamCoins);

            hostCart.Storage.TryAdd(bottleId);
            Result sellAll = Request(client, RecyclingBagService.ActionSell, buyBack.SyncKey, RecyclingBagService.SellAll, 0);
            yield return WaitUntil(() => sellAll.Done, 4f, "No reply to sell all.");
            Assert.IsTrue(sellAll.Success, sellAll.Message);
            Assert.AreEqual(0, hostBag.Count);
            Assert.AreEqual(0, hostCart.Count, "Sell all includes the cart.");
            Assert.AreEqual(coinsBefore + price * (CarryItems.HandsCapacity + 2), hostRep.TeamCoins);
            yield return WaitUntil(() => cartMirror.Count == 0 && bagMirror.Count == 0, 4f, "Selling did not replicate.");

            // ---- Let go: ownership goes back to the server, the cart is free again
            Result release = Request(client, RecyclingBagService.ActionCartRelease, hostCart.SyncKey, 0, 0f);
            yield return WaitUntil(() => release.Done, 4f, "No reply to release.");
            Assert.IsTrue(release.Success, release.Message);
            Assert.IsFalse(hostCart.IsPushedByAnyone);
            Assert.AreEqual(NetworkManager.ServerClientId, hostCartNet.OwnerClientId, "Letting go returns ownership to the server.");
            yield return WaitUntil(() => !clientCart.GetComponent<NetworkObject>().IsOwner && cartMirror.PusherClientId == CartStorage.NoPusher,
                4f, "Release did not replicate.");
            yield return WaitUntil(() => hostCart.HasSimulationAuthority, 2f, "The host did not take the simulation back.");

            // ---- Trash thrown into the basket is stored by the host (and despawned)
            Rigidbody hostBody = hostCart.GetComponent<Rigidbody>();
            hostBody.linearVelocity = Vector3.zero;
            yield return new WaitForFixedUpdate();
            Vector3 basket = hostCart.transform.TransformPoint(new Vector3(0f, 0.8f, 0.03f));
            TrashItem tossed = host.SpawnTrash(catalog, bottle, WasteSourceKind.Citizen, basket, Vector3.down * 0.5f);
            Assert.IsNotNull(tossed);
            float until = Time.realtimeSinceStartup + 4f;
            while (hostCart.Count != 1 && Time.realtimeSinceStartup < until)
                yield return null;
            if (hostCart.Count != 1)
            {
                Transform trig = hostCart.transform.Find("CatchTrigger");
                Rigidbody tb = tossed != null ? tossed.Body : null;
                Assert.Fail($"Trash dropped into the basket was not stored. count={hostCart.Count} cart={hostCart.transform.position} kin={hostBody.isKinematic} " +
                    $"trash={(tossed != null ? tossed.transform.position.ToString() : "null")} held={(tossed != null && tossed.IsHeld)} " +
                    $"contained={(tossed != null && tossed.IsContained)} body={(tb != null ? (tb.isKinematic ? "kin" : "dyn") : "null")} " +
                    $"layer={(tossed != null ? LayerMask.LayerToName(tossed.gameObject.layer) : "-")} trig={(trig != null ? trig.position.ToString() : "none")} " +
                    $"collide={(tossed != null && trig != null ? !Physics.GetIgnoreLayerCollision(tossed.gameObject.layer, trig.gameObject.layer) : false)} " +
                    $"id={(tossed != null ? CarryItems.IdForTrash(tossed) : -9)}");
            }
            yield return WaitUntil(() => tossed == null || tossed.IsDisposed, 4f, "Stored trash was not despawned.");
            yield return WaitUntil(() => cartMirror.Count == 1, 4f, "The stored trash did not replicate.");
        }

        [UnityTest]
        public IEnumerator OfflineSolo_ShoppingCart_BuyPushMoveDepositSell()
        {
            WasteCatalog catalog = LoadCatalog(out WasteCatalog.Entry bottle);
            Assert.IsTrue(GameAuthority.IsAuthoritative);
            Assert.IsFalse(GameAuthority.IsMultiplayer);

            var walletGo = new GameObject("SoloWallet");
            spawnedRoots.Add(walletGo);
            PlayerInventory wallet = walletGo.AddComponent<PlayerInventory>();
            LocalGameAuthority.Instance.SetLocalWalletOverride(wallet);
            RecyclingBagService.EnsureInstance();
            CreateFloor();

            ShopCatalog tools = AssetDatabase.LoadAssetAtPath<ShopCatalog>("Assets/UpcycleCity/Settings/Shops/Shop_Tools.asset");
            Assert.IsTrue(tools.TryFindOffer(CarryItems.CartUpgradeItemId, 1, out ShopCatalog.Offer cartOffer));
            var counterGo = new GameObject("SoloQuincaillerie");
            counterGo.transform.position = new Vector3(0f, 0f, 3f);
            spawnedRoots.Add(counterGo);
            counterGo.AddComponent<ShopCounter>().Configure(tools, null);

            wallet.AddCoins(cartOffer.price);
            bool bought = false;
            string buyMessage = null;
            RecyclingBagService.RequestBuyCart((ok, msg) => { bought = ok; buyMessage = msg; });
            Assert.IsTrue(bought, buyMessage);
            Assert.AreEqual(0, wallet.Coins);
            Assert.AreEqual(1, ShoppingCart.All.Count);
            ShoppingCart cart = ShoppingCart.All[0];
            Assert.IsTrue(cart.HasSimulationAuthority, "Solo always simulates the cart locally.");
            Assert.IsFalse(cart.GetComponent<Rigidbody>().isKinematic);
            Assert.IsTrue(RecyclingBagService.LocalBag.HasCart);
            yield return null;

            bool pushed = false;
            RecyclingBagService.RequestPushCart(cart, (ok, _) => pushed = ok);
            Assert.IsTrue(pushed);
            Assert.AreEqual(0UL, cart.PusherClientId);

            // Bag → cart → bag
            RecyclingBag bag = RecyclingBagService.LocalBag;
            int bottleId = CarryItems.IdForCatalogIndex(catalog.FindEntryIndex(bottle));
            bag.TryAdd(bottleId);
            bag.TryAdd(bottleId);
            bool moved = false;
            RecyclingBagService.RequestPutInCart(cart, 0, bottleId, (ok, _) => moved = ok);
            Assert.IsTrue(moved);
            Assert.AreEqual(1, cart.Count);
            Assert.AreEqual(1, bag.Count);
            Assert.AreEqual(2, bag.Count + cart.Count);

            // Depositing at the right sorting bin empties the cart too
            var crateGo = new GameObject("SoloCrate");
            crateGo.transform.position = new Vector3(1.5f, 0f, 0f);
            spawnedRoots.Add(crateGo);
            SortingCrate crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(bottle.stream);
            bool deposited = false;
            RecyclingBagService.RequestDeposit(crate, (ok, _) => deposited = ok);
            Assert.IsTrue(deposited);
            Assert.AreEqual(0, cart.Count, "The sorting bin empties matching items from the cart.");
            Assert.AreEqual(0, bag.Count);

            // RÉCUP buys from the cart
            cart.Storage.TryAdd(bottleId);
            var buyBackGo = new GameObject("SoloRecup");
            buyBackGo.transform.position = new Vector3(-1.5f, 0f, 0f);
            spawnedRoots.Add(buyBackGo);
            RecyclingBuyBack buyBack = buyBackGo.AddComponent<RecyclingBuyBack>();
            int coinsBefore = wallet.Coins;
            bool sold = false;
            RecyclingBagService.RequestSell(buyBack, RecyclingBagService.CartSlotBase, bottleId, (ok, _) => sold = ok);
            Assert.IsTrue(sold);
            Assert.AreEqual(0, cart.Count);
            Assert.AreEqual(coinsBefore + buyBack.PriceFor(bottle.stream, bottle.baseCoinValue), wallet.Coins);

            bool released = false;
            RecyclingBagService.RequestReleaseCart(cart, 0f, (ok, _) => released = ok);
            Assert.IsTrue(released);
            Assert.IsFalse(cart.IsPushedByAnyone);
            yield return null;
        }

        // ------------------------------------------------------------ helpers

        static WasteCatalog LoadCatalog(out WasteCatalog.Entry bottle)
        {
            WasteCatalog catalog = AssetDatabase.LoadAssetAtPath<WasteCatalog>("Assets/UpcycleCity/Settings/Ecosystem/WasteCatalog.asset");
            Assert.IsNotNull(catalog);
            CarryItems.CatalogOverride = catalog;
            bottle = null;
            foreach (WasteCatalog.Entry e in catalog.Entries)
            {
                if (e != null && !e.isBulky && e.stream == WasteStream.Plastic) { bottle = e; break; }
            }
            Assert.IsNotNull(bottle);
            return catalog;
        }

        void CreateFloor()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "TestFloor";
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.transform.localScale = new Vector3(40f, 1f, 40f);
            floor.layer = GameLayers.Environment;
            spawnedRoots.Add(floor);
        }

        ShoppingCart FindHostCart()
        {
            foreach (ShoppingCart cart in ShoppingCart.All)
            {
                if (cart != null && cart.TryGetComponent(out NetworkObject no) && no.NetworkManager == hostManager)
                    return cart;
            }
            return null;
        }

        /// <summary>In one process the host's and the client's copies share a physics world: keep them from shoving each other.</summary>
        static void IgnoreCopiesColliding(ShoppingCart a, ShoppingCart b, GameObject playerA, GameObject playerB)
        {
            var groups = new[] { a.gameObject, b.gameObject, playerA, playerB };
            for (int i = 0; i < groups.Length; i++)
            {
                for (int j = i + 1; j < groups.Length; j++)
                {
                    foreach (Collider x in groups[i].GetComponentsInChildren<Collider>(true))
                        foreach (Collider y in groups[j].GetComponentsInChildren<Collider>(true))
                            Physics.IgnoreCollision(x, y, true);
                }
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
            hostManager = CreateNetworkManager("CartTestHostNM", settings, port, isServer: true);
            Assert.IsTrue(hostManager.StartHost(), $"Host failed to start on port {port}.");
            clientManager = CreateNetworkManager("CartTestClientNM", settings, port, isServer: false);
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
