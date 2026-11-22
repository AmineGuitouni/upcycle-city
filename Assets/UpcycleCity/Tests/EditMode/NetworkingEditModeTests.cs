using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Inventory;
using UpcycleCity.Networking;
using UpcycleCity.Shops;

namespace UpcycleCity.Tests.EditMode
{
    /// <summary>
    /// Fast EditMode unit tests covering the scalable networking layer's core building blocks:
    /// <see cref="SharedTeamWallet"/>, <see cref="NetworkSettings"/> validation &amp; timeout config,
    /// <see cref="NetworkCharacterAppearance"/> serialization, <see cref="SyncedStateRegistry"/> (including
    /// FNV-1a collision detection), <see cref="GameAuthority"/> (local shop catalog pricing &amp; custom action results),
    /// <see cref="CoopSessionManager.FormatFriendlyDisconnectReason"/>, <see cref="NetworkTrashArchetype.ComputeLocalBounds"/>,
    /// and automatic <see cref="NetworkPrefabsList"/> + <c>DontDestroyWithOwner</c> validation.
    /// </summary>
    public sealed class NetworkingEditModeTests
    {
        [TearDown]
        public void TearDown()
        {
            GameAuthority.ResetToLocal();
            SyncedStateRegistry.Clear();
        }

        [Test]
        public void SharedTeamWallet_CreditSpendRefundAndClamp_WorkAtomically()
        {
            var wallet = new SharedTeamWallet(20);
            int lastBalance = -1;
            int lastDelta = 0;
            int changeCount = 0;
            wallet.BalanceChanged += (bal, delta) =>
            {
                lastBalance = bal;
                lastDelta = delta;
                changeCount++;
            };

            Assert.AreEqual(20, wallet.Balance);

            wallet.Credit(15);
            Assert.AreEqual(35, wallet.Balance);
            Assert.AreEqual(35, lastBalance);
            Assert.AreEqual(15, lastDelta);
            Assert.AreEqual(1, changeCount);

            // Atomic spend succeeds when funds are sufficient
            Assert.IsTrue(wallet.TrySpend(10));
            Assert.AreEqual(25, wallet.Balance);
            Assert.AreEqual(-10, lastDelta);

            // Atomic spend fails without mutating balance or firing events when insufficient
            Assert.IsFalse(wallet.TrySpend(26));
            Assert.AreEqual(25, wallet.Balance);
            Assert.AreEqual(2, changeCount);

            // Negative spend rejected; zero spend succeeds as no-op
            Assert.IsFalse(wallet.TrySpend(-5));
            Assert.IsTrue(wallet.TrySpend(0));
            Assert.AreEqual(25, wallet.Balance);
            Assert.AreEqual(2, changeCount);

            // Refund restores coins
            wallet.Refund(10);
            Assert.AreEqual(35, wallet.Balance);

            // SetBalance clamps negative to zero
            wallet.SetBalance(-100);
            Assert.AreEqual(0, wallet.Balance);
        }

        [Test]
        public void NetworkSettings_JoinCodeValidation_NormalizesAndRejectsInvalid()
        {
            Assert.AreEqual("AB12CD", NetworkSettings.NormalizeJoinCode("  ab-12 cd "));

            Assert.IsTrue(NetworkSettings.IsValidJoinCode("ab12cd", out string normalized));
            Assert.AreEqual("AB12CD", normalized);

            Assert.IsTrue(NetworkSettings.IsValidJoinCode("WXYZ9876", out normalized));
            Assert.AreEqual("WXYZ9876", normalized);

            Assert.IsFalse(NetworkSettings.IsValidJoinCode("", out _));
            Assert.IsFalse(NetworkSettings.IsValidJoinCode("AB1", out _));
            Assert.IsFalse(NetworkSettings.IsValidJoinCode("ABCDEFGHIJKLM", out _));
            Assert.IsFalse(NetworkSettings.IsValidJoinCode("AB$12!", out _));
        }

        [Test]
        public void NetworkSettings_DirectEndpointParsing_HandlesIpPortAndErrors()
        {
            Assert.IsTrue(NetworkSettings.TryParseDirectEndpoint("127.0.0.1:7788", 7777, out string ip, out ushort port, out string err));
            Assert.AreEqual("127.0.0.1", ip);
            Assert.AreEqual(7788, port);
            Assert.IsEmpty(err);

            // Default port used when omitted
            Assert.IsTrue(NetworkSettings.TryParseDirectEndpoint("192.168.1.42", 7777, out ip, out port, out err));
            Assert.AreEqual("192.168.1.42", ip);
            Assert.AreEqual(7777, port);

            // Invalid ports and empty inputs rejected
            Assert.IsFalse(NetworkSettings.TryParseDirectEndpoint("127.0.0.1:80", 7777, out _, out _, out _));
            Assert.IsFalse(NetworkSettings.TryParseDirectEndpoint("127.0.0.1:abc", 7777, out _, out _, out _));
            Assert.IsFalse(NetworkSettings.TryParseDirectEndpoint("", 7777, out _, out _, out _));
        }

        [Test]
        public void NetworkSettings_DefaultSoloSceneIsEmptyAndDisconnectTimeoutDefaultsTo15Seconds()
        {
            var fresh = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                Assert.IsEmpty(fresh.SoloSceneName, "Default NetworkSettings.SoloSceneName should be empty so Play Solo defers to CharacterCreatorController.GameplayScene.");
                Assert.AreEqual(15f, fresh.DisconnectTimeoutSeconds, 0.01f, "Default DisconnectTimeoutSeconds should be 15s (faster than UTP's 30s default).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fresh);
            }

            var settingsAsset = AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
            Assert.IsNotNull(settingsAsset);
            Assert.IsEmpty(settingsAsset.SoloSceneName, "NetworkSettings.asset should leave soloSceneName empty by default so solo loads PlayerGym.");
            Assert.AreEqual(15f, settingsAsset.DisconnectTimeoutSeconds, 0.01f, "NetworkSettings.asset should persist 15s DisconnectTimeoutSeconds.");
        }

        [Test]
        public void NetworkCharacterAppearance_RoundTripSerialization_PreservesAllFields()
        {
            var original = new CharacterAppearance
            {
                sex = CharacterSex.Female,
                skinTone = 6,
                outfitColor = 4,
                eyeStyle = 3,
                eyeColor = 2,
                hairStyle = 7,
                hairColor = 9,
                facialHair = 1,
            };

            NetworkCharacterAppearance packed = NetworkCharacterAppearance.From(original);
            using var writer = new FastBufferWriter(64, Allocator.Temp);
            writer.WriteValueSafe(packed);

            using var reader = new FastBufferReader(writer, Allocator.Temp);
            reader.ReadValueSafe(out NetworkCharacterAppearance unpacked);

            Assert.AreEqual(packed, unpacked);
            CharacterAppearance restored = unpacked.ToAppearance();
            Assert.AreEqual(original.sex, restored.sex);
            Assert.AreEqual(original.skinTone, restored.skinTone);
            Assert.AreEqual(original.outfitColor, restored.outfitColor);
            Assert.AreEqual(original.eyeStyle, restored.eyeStyle);
            Assert.AreEqual(original.eyeColor, restored.eyeColor);
            Assert.AreEqual(original.hairStyle, restored.hairStyle);
            Assert.AreEqual(original.hairColor, restored.hairColor);
            Assert.AreEqual(original.facialHair, restored.facialHair);
        }

        [Test]
        public void SyncedStateRegistry_DeterministicHashAndProviderRouting_WorkCorrectly()
        {
            uint hashA = SyncedStateRegistry.ComputeKeyHash("district:BabBhar");
            uint hashA2 = SyncedStateRegistry.ComputeKeyHash("district:BabBhar");
            uint hashB = SyncedStateRegistry.ComputeKeyHash("district:Factory");

            Assert.AreNotEqual(0u, hashA);
            Assert.AreEqual(hashA, hashA2);
            Assert.AreNotEqual(hashA, hashB);

            string spatial1 = SyncedStateRegistry.BuildSpatialKey("bin", "Bin_01", new Vector3(1.24f, 0f, -3.51f));
            string spatial2 = SyncedStateRegistry.BuildSpatialKey("bin", "Bin_01", new Vector3(1.21f, 0f, -3.49f));
            Assert.AreEqual(spatial1, spatial2);

            var mock = new MockSyncedStateProvider("machine:Shredder_01");
            SyncedStateRegistry.Register(mock);

            Assert.IsTrue(SyncedStateRegistry.TryGet(SyncedStateRegistry.ComputeKeyHash(mock.SyncKey), out ISyncedStateProvider found));
            Assert.AreSame(mock, found);

            var incoming = new SyncedStateRecord { v0 = 0.75f, v1 = 12.5f, i0 = 4, i1 = 9, flags = 3 };
            Assert.IsTrue(SyncedStateRegistry.TryApply(SyncedStateRegistry.ComputeKeyHash(mock.SyncKey), in incoming));
            Assert.IsTrue(mock.LastApplied.Equals(incoming));
            Assert.AreEqual(1, mock.ApplyCount);

            SyncedStateRegistry.Unregister(mock);
            Assert.IsFalse(SyncedStateRegistry.TryApply(SyncedStateRegistry.ComputeKeyHash(mock.SyncKey), in incoming));
            Assert.AreEqual(1, mock.ApplyCount);
        }

        [Test]
        public void SyncedStateRegistry_HashCollision_LogsErrorAndPreservesExistingProvider()
        {
            // Verified 32-bit FNV-1a collision pair: both "costarring" and "liquid" hash to 0x5E4DAA9D
            uint hashA = SyncedStateRegistry.ComputeKeyHash("costarring");
            uint hashB = SyncedStateRegistry.ComputeKeyHash("liquid");
            Assert.AreEqual(hashA, hashB, "Test requires a verified 32-bit FNV-1a collision pair.");

            var registry = new SyncedStateRegistryInstance();
            var first = new MockSyncedStateProvider("costarring");
            var collider = new MockSyncedStateProvider("liquid");

            registry.Register(first);
            LogAssert.Expect(LogType.Error, new Regex("key hash collision.*costarring.*liquid"));
            registry.Register(collider);

            Assert.IsTrue(registry.TryGet(hashA, out ISyncedStateProvider active));
            Assert.AreSame(first, active, "Collision must NOT silently evict the existing provider.");
            Assert.AreEqual(1, registry.AllProviders.Count);
        }

        [Test]
        public void GameAuthority_DefaultsToLocal_RoutesMultiTargetCustomActionsAndCatalogShop_NoRefundApi()
        {
            GameAuthority.ResetToLocal();
            Assert.AreSame(LocalGameAuthority.Instance, GameAuthority.Current);
            Assert.IsTrue(GameAuthority.IsAuthoritative);
            Assert.IsFalse(GameAuthority.IsMultiplayer);

            // Verify N4: shop refund API was completely removed
            Assert.IsNull(typeof(IGameAuthority).GetMethod("RequestShopRefund"),
                "RequestShopRefund must be removed from IGameAuthority.");
            Assert.IsNull(typeof(ShopPurchaseResult).GetProperty("PurchaseId"),
                "PurchaseId must be removed from ShopPurchaseResult.");
            Assert.IsNull(typeof(ShopPurchaseResult).GetField("PurchaseId"),
                "PurchaseId must be removed from ShopPurchaseResult.");

            // Verify N3 + Nit: two benches under the SAME actionId coexist keyed by (actionId, targetId) and return CustomGameActionResult
            int benchACalls = 0;
            int benchBCalls = 0;
            int capturedInt = 0;
            float capturedFloat = 0f;
            Vector3? capturedPos = null;
            CustomGameActionHandler benchAHandler = (in CustomGameActionContext ctx) =>
            {
                benchACalls++;
                capturedInt = ctx.IntArg;
                capturedFloat = ctx.FloatArg;
                capturedPos = ctx.SenderPosition;
                return ctx.TargetId == "bench_A"
                    ? CustomGameActionResult.Ok("Crafted A")
                    : CustomGameActionResult.Fail("Wrong bench");
            };
            CustomGameActionHandler benchBHandler = (in CustomGameActionContext ctx) =>
            {
                benchBCalls++;
                return ctx.IntArg > 0
                    ? CustomGameActionResult.Ok("Crafted B")
                    : CustomGameActionResult.Fail("Not enough materials");
            };

            GameAuthority.RegisterCustomActionHandler("craft.bicycle", "bench_A", benchAHandler);
            GameAuthority.RegisterCustomActionHandler("craft.bicycle", "bench_B", benchBHandler);
            try
            {
                bool callbackOk = false;
                string callbackMsg = null;
                Assert.IsTrue(GameAuthority.Current.RequestCustomAction("craft.bicycle", "bench_A", 2, 1.5f, (ok, msg) =>
                {
                    callbackOk = ok;
                    callbackMsg = msg;
                }));
                Assert.AreEqual(1, benchACalls);
                Assert.AreEqual(0, benchBCalls);
                Assert.AreEqual(2, capturedInt);
                Assert.AreEqual(1.5f, capturedFloat, 0.001f);
                Assert.IsTrue(callbackOk);
                Assert.AreEqual("Crafted A", callbackMsg);

                // Rejection result callback
                Assert.IsFalse(GameAuthority.Current.RequestCustomAction("craft.bicycle", "bench_B", 0, 0f, (ok, msg) =>
                {
                    callbackOk = ok;
                    callbackMsg = msg;
                }));
                Assert.AreEqual(1, benchACalls);
                Assert.AreEqual(1, benchBCalls);
                Assert.IsFalse(callbackOk);
                Assert.AreEqual("Not enough materials", callbackMsg);

                // Duplicate registration on the exact same (actionId, targetId) logs a warning
                LogAssert.Expect(LogType.Warning, new Regex("Duplicate custom action handler.*craft\\.bicycle.*bench_A"));
                GameAuthority.RegisterCustomActionHandler("craft.bicycle", "bench_A", benchBHandler);
            }
            finally
            {
                GameAuthority.UnregisterCustomActionHandler("craft.bicycle", "bench_A", benchBHandler);
                GameAuthority.UnregisterCustomActionHandler("craft.bicycle", "bench_B", benchBHandler);
            }
            Assert.IsFalse(GameAuthority.Current.RequestCustomAction("craft.bicycle", "bench_A"));

            // Context IsWithinRange helper works cleanly with explicit position
            var nearCtx = new CustomGameActionContext(0UL, "bench_A", 0, 0f, new Vector3(1f, 0f, 0f));
            Assert.IsTrue(nearCtx.IsWithinRange(Vector3.zero, 2f));
            Assert.IsFalse(nearCtx.IsWithinRange(new Vector3(10f, 0f, 0f), 2f));

            // Catalog-resolved shop purchase (host/local only charges; no refund path)
            ShopCatalog toolsCatalog = AssetDatabase.LoadAssetAtPath<ShopCatalog>("Assets/UpcycleCity/Settings/Shops/Shop_Tools.asset");
            Assert.IsNotNull(toolsCatalog, "Shop_Tools.asset must exist.");
            Assert.Greater(toolsCatalog.Offers.Count, 0);
            ShopCatalog.Offer firstOffer = toolsCatalog.Offers[0];

            var invGo = new GameObject("EditModeInv");
            try
            {
                PlayerInventory inv = invGo.AddComponent<PlayerInventory>();
                typeof(PlayerInventory)
                    .GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(inv, null);
                inv.AddCoins(100);
                int beforeCoins = inv.Coins;

                Assert.IsTrue(inv.CanAddItem(firstOffer.item, firstOffer.quantity));

                ShopPurchaseResult result = default;
                GameAuthority.Current.RequestShopPurchase(
                    new ShopPurchaseRequest(string.Empty, firstOffer.item.ItemId, firstOffer.quantity, Vector3.zero, inv, toolsCatalog),
                    r => result = r);

                Assert.IsTrue(result.Approved);
                Assert.AreEqual(firstOffer.price, result.Price);
                Assert.AreEqual(beforeCoins - firstOffer.price, inv.Coins);

                // Unknown item rejected
                ShopPurchaseResult unknownRes = default;
                GameAuthority.Current.RequestShopPurchase(
                    new ShopPurchaseRequest(string.Empty, "does_not_exist", 1, Vector3.zero, inv, toolsCatalog),
                    r => unknownRes = r);
                Assert.IsFalse(unknownRes.Approved);
                Assert.AreEqual(beforeCoins - firstOffer.price, inv.Coins);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(invGo);
            }

            // Local city event API
            GameAuthority.Current.TriggerCityEvent("Test Surge", 15f, severity: 2);
            Assert.AreEqual("Test Surge", GameAuthority.Current.ActiveEventTitle);
            Assert.AreEqual(2, GameAuthority.Current.ActiveEventSeverity);
            Assert.Greater(GameAuthority.Current.ActiveEventTimeRemaining, 10f);
        }

        [Test]
        public void CharacterAppearanceStore_SessionOverride_DoesNotModifyPlayerPrefs()
        {
            const string prefsKey = "UpcycleCity.CharacterAppearance.v1";
            bool hadKey = PlayerPrefs.HasKey(prefsKey);
            string previousJson = hadKey ? PlayerPrefs.GetString(prefsKey) : null;

            try
            {
                var persisted = CharacterAppearance.Default;
                persisted.outfitColor = 1;
                persisted.hairStyle = 2;
                CharacterAppearanceStore.ClearSessionOverride();
                CharacterAppearanceStore.Save(persisted);
                string savedBefore = PlayerPrefs.GetString(prefsKey);

                var overrideLook = persisted;
                overrideLook.outfitColor = 6;
                overrideLook.hairStyle = 5;
                CharacterAppearanceStore.SetSessionOverride(overrideLook);

                Assert.AreEqual(6, CharacterAppearanceStore.Current.outfitColor);
                Assert.AreEqual(5, CharacterAppearanceStore.Current.hairStyle);
                Assert.AreEqual(savedBefore, PlayerPrefs.GetString(prefsKey),
                    "Session override (-coop-outfit) must never overwrite PlayerPrefs.");

                CharacterAppearanceStore.ClearSessionOverride();
                Assert.AreEqual(1, CharacterAppearanceStore.Current.outfitColor);
                Assert.AreEqual(2, CharacterAppearanceStore.Current.hairStyle);
            }
            finally
            {
                CharacterAppearanceStore.ClearSessionOverride();
                if (hadKey)
                    PlayerPrefs.SetString(prefsKey, previousJson);
                else
                    PlayerPrefs.DeleteKey(prefsKey);
            }
        }

        [Test]
        public void CoopSessionManager_FormatFriendlyDisconnectReason_MapsRawNgoAndApprovalCodes()
        {
            string hostLeft = CoopSessionManager.FormatFriendlyDisconnectReason(
                CoopSessionManager.DisconnectCodeHostLeft, CoopSessionState.InGame);
            Assert.AreEqual("Host left the co-op session.", hostLeft);

            string inProgress = CoopSessionManager.FormatFriendlyDisconnectReason(
                CoopSessionManager.DisconnectCodeGameInProgress, CoopSessionState.Connecting);
            Assert.That(inProgress, Does.Contain("already in progress"));

            string full = CoopSessionManager.FormatFriendlyDisconnectReason(
                CoopSessionManager.DisconnectCodeSessionFull, CoopSessionState.Connecting, maxPlayers: 4);
            Assert.That(full, Does.Contain("Session is full (4/4"));

            string timeout = CoopSessionManager.FormatFriendlyDisconnectReason(
                "[Disconnect Event][Client-1] Connection lost [ProtocolTimeout]", CoopSessionState.InGame);
            Assert.IsFalse(timeout.Contains("[Disconnect Event]"), "Raw NGO bracketed log must never leak to UI.");
            Assert.That(timeout, Does.Contain("timed out").IgnoreCase);

            string rawDisconnect = CoopSessionManager.FormatFriendlyDisconnectReason(
                "[Disconnect Event][Client-2] TransportFailure", CoopSessionState.Connecting);
            Assert.IsFalse(rawDisconnect.Contains("[Disconnect Event]"));
            Assert.That(rawDisconnect, Does.Contain("Could not connect"));

            string rawInGameDisconnect = CoopSessionManager.FormatFriendlyDisconnectReason(
                "[Disconnect Event][Client-2] TransportFailure", CoopSessionState.InGame);
            Assert.AreEqual("Host left or lost connection.", rawInGameDisconnect);

            string versionMismatch = CoopSessionManager.FormatFriendlyDisconnectReason(
                "NetworkConfig mismatch detected", CoopSessionState.Connecting);
            Assert.That(versionMismatch, Does.Contain("Version mismatch"));

            string hostEnded = CoopSessionManager.FormatFriendlyDisconnectReason(string.Empty, CoopSessionState.InGame);
            Assert.AreEqual("Host left or lost connection.", hostEnded);
        }

        [Test]
        public void NetworkPrefabRegistryAsset_ContainsAllCoreCoopPrefabsAndEnforcesDontDestroyWithOwner()
        {
            var settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
            Assert.IsNotNull(settings, "NetworkSettings.asset should exist after running Co-op setup.");
            Assert.IsNotNull(settings.NetworkPrefabsList, "UpcycleCityNetworkPrefabs.asset must be assigned on NetworkSettings.");
            Assert.IsNotNull(settings.NetworkPlayerPrefab);
            Assert.IsNotNull(settings.NetworkTrashPrefab);
            Assert.IsNotNull(settings.NetworkCityStatePrefab);

            Assert.IsTrue(settings.NetworkPrefabsList.Contains(settings.NetworkPlayerPrefab), "NetworkPlayer prefab must be registered.");
            Assert.IsTrue(settings.NetworkPrefabsList.Contains(settings.NetworkTrashPrefab), "NetworkTrashItem prefab must be registered.");
            Assert.IsTrue(settings.NetworkPrefabsList.Contains(settings.NetworkCityStatePrefab), "NetworkCityState prefab must be registered.");

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>("Assets/DefaultNetworkPrefabs.asset"),
                "NGO's auto-generated Assets/DefaultNetworkPrefabs.asset must stay disabled and deleted.");

            NetworkObject trashNetObj = settings.NetworkTrashPrefab.GetComponent<NetworkObject>();
            Assert.IsNotNull(trashNetObj);
            Assert.IsTrue(trashNetObj.DontDestroyWithOwner,
                "NetworkTrashItem.prefab MUST have DontDestroyWithOwner = true so disconnecting holders drop trash instead of destroying it.");

            NetworkTransform trashNetTransform = settings.NetworkTrashPrefab.GetComponent<NetworkTransform>();
            Assert.IsNotNull(trashNetTransform);
            Assert.AreEqual(NetworkTransform.AuthorityModes.Owner, trashNetTransform.AuthorityMode);
        }

        [Test]
        public void NetworkTrashArchetype_ComputeLocalBounds_IsRotationInvariant()
        {
            var root = new GameObject("RotatedTrashRoot");
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                visual.transform.SetParent(root.transform, false);
                visual.transform.localScale = new Vector3(1.0f, 0.2f, 0.2f);
                visual.transform.localPosition = new Vector3(0f, 0.1f, 0f);

                root.transform.rotation = Quaternion.identity;
                Bounds unrotated = NetworkTrashArchetype.ComputeLocalBounds(root.transform, visual);

                root.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
                Bounds rotated = NetworkTrashArchetype.ComputeLocalBounds(root.transform, visual);

                Assert.AreEqual(unrotated.size.x, rotated.size.x, 0.001f, "Local bounds width must not inflate when root is rotated.");
                Assert.AreEqual(unrotated.size.y, rotated.size.y, 0.001f);
                Assert.AreEqual(unrotated.size.z, rotated.size.z, 0.001f, "Local bounds depth must not inflate when root is rotated.");
                Assert.AreEqual(unrotated.center.y, rotated.center.y, 0.001f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        sealed class MockSyncedStateProvider : ISyncedStateProvider
        {
            public string SyncKey { get; }
            public SyncedStateRecord LastApplied { get; private set; }
            public int ApplyCount { get; private set; }

            public MockSyncedStateProvider(string key) => SyncKey = key;

            public SyncedStateRecord CaptureSyncState() => LastApplied;

            public void ApplySyncState(in SyncedStateRecord state)
            {
                LastApplied = state;
                ApplyCount++;
            }
        }
    }
}
