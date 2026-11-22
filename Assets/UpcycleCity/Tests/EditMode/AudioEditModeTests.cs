using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Audio;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Shops;
using UpcycleCity.Waste;
using AudioImportProfile = UpcycleCity.Editor.Audio.AudioImportProfile;
using AudioImportRules = UpcycleCity.Editor.Audio.AudioImportRules;

namespace UpcycleCity.Tests.EditMode
{
    /// <summary>
    /// Fast EditMode unit tests covering audio import rules, cart stow &amp; release sequences,
    /// typed feedback routing, solo vs. co-op coin &amp; spend audio deduplication, real authoritative
    /// solo action paths, ref-counted ambience ducking, and scene ambience gating.
    /// </summary>
    public sealed class AudioEditModeTests
    {
        readonly List<UnityEngine.Object> createdObjects = new List<UnityEngine.Object>();
        readonly List<GameAudioRouter> enabledRouters = new List<GameAudioRouter>();
        PlayerController registeredPlayer;

        sealed class EditModeTrashDisposer : MonoBehaviour, INetworkedPhysicsGrabbable
        {
            public bool IsHeldByAnyone => false;
            public bool CanLocalGrab => true;
            public void OnLocalPickupStarted() { }
            public void OnLocalPickupCancelled() { }
            public void OnLocalReleased(Vector3 releaseVelocity, bool thrown) { }
            public bool HandleDispose() => true;
        }

        sealed class StubNonFactoryToolTarget : MonoBehaviour, IToolTarget
        {
            public Transform Transform => transform;
            public string GetToolPrompt(IInventoryItem equippedItem) => "Tighten Bolt";
            public bool CanUseTool(PlayerInteractor interactor, IInventoryItem equippedItem) => true;
            public bool UseTool(PlayerInteractor interactor, IInventoryItem equippedItem) => true;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = enabledRouters.Count - 1; i >= 0; i--)
            {
                if (enabledRouters[i] != null)
                    InvokeNonPublic(enabledRouters[i], "OnDisable");
            }
            enabledRouters.Clear();

            if (registeredPlayer != null)
            {
                PlayerContext.Unregister(registeredPlayer);
                registeredPlayer = null;
            }

            GameAuthority.ResetToLocal();
            SoundManager.ClearAmbienceDucks();
            for (int i = createdObjects.Count - 1; i >= 0; i--)
            {
                if (createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(createdObjects[i]);
            }
            createdObjects.Clear();
        }

        T Track<T>(T obj) where T : UnityEngine.Object
        {
            createdObjects.Add(obj);
            return obj;
        }

        static void InvokeNonPublic(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Expected non-public method '{methodName}' on {target.GetType().Name}.");
            method.Invoke(target, null);
        }

        static void SetNonPublicField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Expected non-public field '{fieldName}' on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        GameAudioRouter CreateRouter(bool isMultiplayer, out List<(SoundId id, Vector3? pos, float vol, float pitch)> dispatched)
        {
            var go = Track(new GameObject(isMultiplayer ? "TestRouter_Coop" : "TestRouter_Solo"));
            var router = go.AddComponent<GameAudioRouter>();
            router.MultiplayerStateProvider = () => isMultiplayer;
            var list = new List<(SoundId, Vector3?, float, float)>();
            router.SoundDispatched += (id, pos, vol, pitch) => list.Add((id, pos, vol, pitch));
            dispatched = list;
            return router;
        }

        GameAudioRouter CreateBoundSoloPlayerAndRouter(
            out PlayerInventory inventory,
            out PlayerInteractor interactor,
            out PlayerFeedbackHub feedback,
            out List<(SoundId id, Vector3? pos, float vol, float pitch)> dispatched)
        {
            if (registeredPlayer != null)
            {
                PlayerContext.Unregister(registeredPlayer);
                registeredPlayer = null;
            }

            var playerGo = Track(new GameObject("TestPlayer_SoloRealPath"));
            var player = playerGo.AddComponent<PlayerController>();
            inventory = playerGo.GetComponent<PlayerInventory>();
            interactor = playerGo.GetComponent<PlayerInteractor>();
            feedback = playerGo.GetComponent<PlayerFeedbackHub>();

            // Wire references without invoking full camera/input Awake side-effects in EditMode
            SetNonPublicField(player, "inventory", inventory);
            SetNonPublicField(player, "interactor", interactor);
            SetNonPublicField(player, "feedback", feedback);
            interactor.Initialize(null, null, inventory, null, playerGo.transform, null, feedback);

            PlayerContext.Register(player);
            registeredPlayer = player;

            GameAudioRouter router = CreateRouter(isMultiplayer: false, out dispatched);
            InvokeNonPublic(router, "OnEnable");
            enabledRouters.Add(router);
            return router;
        }

        TrashItem CreateTrash(string name, WasteStream stream, bool bulky = false, bool editModeDisposable = false)
        {
            var go = Track(new GameObject(name));
            go.AddComponent<Rigidbody>();
            if (editModeDisposable)
                go.AddComponent<EditModeTrashDisposer>();
            var trash = go.AddComponent<TrashItem>();
            trash.Configure(name, stream, WasteSourceKind.Citizen, 1f, 5, bulky);
            return trash;
        }

        ItemDefinition CreateItem(string id, string label, IItemUseBehavior behavior = null)
        {
            var def = Track(ScriptableObject.CreateInstance<ItemDefinition>());
            def.Configure(id, label, label, ItemCategory.Tool, Color.white, behavior: behavior);
            return def;
        }

        static int CountSound(List<(SoundId id, Vector3? pos, float vol, float pitch)> dispatched, SoundId target)
        {
            int count = 0;
            for (int i = 0; i < dispatched.Count; i++)
            {
                if (dispatched[i].id == target)
                    count++;
            }
            return count;
        }

        // ---------------------------------------------------------------- Import Rules

        [Test]
        public void AudioImportRules_DataDrivenFolders_ForcesPositionalSfxToMonoAndKeepsMusicAmbienceAnd2DStereo()
        {
            // Streaming stereo folders
            AudioImportProfile music = AudioImportRules.ResolveProfile("Assets/UpcycleCity/Audio/Music/background1.wav");
            Assert.IsTrue(music.ShouldApply);
            Assert.IsFalse(music.ForceToMono);
            Assert.IsTrue(music.LoadInBackground);
            Assert.IsFalse(music.PreloadAudioData);
            Assert.AreEqual(AudioClipLoadType.Streaming, music.LoadType);
            Assert.AreEqual(0.72f, music.Quality, 0.001f);

            AudioImportProfile ambience = AudioImportRules.ResolveProfile("Assets/UpcycleCity/Audio/Ambience/city_ambience.wav");
            Assert.IsTrue(ambience.ShouldApply);
            Assert.IsFalse(ambience.ForceToMono);
            Assert.AreEqual(AudioClipLoadType.Streaming, ambience.LoadType);
            Assert.AreEqual(0.65f, ambience.Quality, 0.001f);

            // Stereo 2D / UI / stingers / economy folders (including top-level /UI/)
            Assert.IsFalse(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/UI/button_click.wav"));
            Assert.IsFalse(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/UI/click.wav"));
            Assert.IsFalse(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/2D/fanfare.wav"));
            Assert.IsFalse(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Stingers/district_clean.wav"));
            Assert.IsFalse(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Economy/spending_money.mp3"));

            // Every 3D / positional SFX folder forces to mono (including /SFX/Actions/ and future folders)
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Footsteps/footstep_walk_01.wav"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Materials/plastic1.wav"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Actions/hitting.mp3"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Actions/metalClick.ogg"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Actions/recycle1.wav"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Tools/broom_sweep.wav"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Props/cart_roll.wav"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Machines/pulper_loop.wav"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/SFX/Ecosystem/fountain.wav"));
            Assert.IsTrue(AudioImportRules.ShouldForceMono(@"Assets\UpcycleCity\Audio\SFX\Actions\hitting.mp3"));

            // Explicit reachable fallback for loose clips or future folders directly under Audio/
            AudioImportProfile fallbackRoot = AudioImportRules.ResolveProfile("Assets/UpcycleCity/Audio/loose_world_clip.wav");
            Assert.IsTrue(fallbackRoot.ShouldApply);
            Assert.IsTrue(fallbackRoot.ForceToMono);
            Assert.IsTrue(AudioImportRules.ShouldForceMono("Assets/UpcycleCity/Audio/WorldProps/fountain_loop.wav"));

            // Non-managed paths are ignored
            Assert.IsFalse(AudioImportRules.ResolveProfile("Assets/ThirdParty/clip.wav").ShouldApply);
            Assert.IsFalse(AudioImportRules.ShouldForceMono(string.Empty));
            Assert.IsFalse(AudioImportRules.ShouldForceMono(null));
        }

        [Test]
        public void AudioImportRules_ReimportedActionClips_HaveForceToMonoEnabled()
        {
            string[] monoActionClips =
            {
                "Assets/UpcycleCity/Audio/SFX/Actions/hitting.mp3",
                "Assets/UpcycleCity/Audio/SFX/Actions/metalClick.ogg",
                "Assets/UpcycleCity/Audio/SFX/Actions/recycle1.wav",
                "Assets/UpcycleCity/Audio/SFX/Actions/pick.wav",
                "Assets/UpcycleCity/Audio/SFX/Actions/attention_whistle.wav",
            };

            for (int i = 0; i < monoActionClips.Length; i++)
            {
                var importer = AssetImporter.GetAtPath(monoActionClips[i]) as AudioImporter;
                Assert.IsNotNull(importer, $"Expected AudioImporter at {monoActionClips[i]}");
                Assert.IsTrue(importer.forceToMono, $"Expected forceToMono=true on {monoActionClips[i]}");
            }

            string[] stereoClips =
            {
                "Assets/UpcycleCity/Audio/Music/background1.wav",
                "Assets/UpcycleCity/Audio/Ambience/city_ambience.wav",
                "Assets/UpcycleCity/Audio/SFX/Economy/spending_money.mp3",
            };

            for (int i = 0; i < stereoClips.Length; i++)
            {
                var importer = AssetImporter.GetAtPath(stereoClips[i]) as AudioImporter;
                Assert.IsNotNull(importer, $"Expected AudioImporter at {stereoClips[i]}");
                Assert.IsFalse(importer.forceToMono, $"Expected forceToMono=false on {stereoClips[i]}");
            }
        }

        // ---------------------------------------------------------------- Cart Stow & Release Sequence

        [Test]
        public void GameAudioRouter_CartStow_UsesCompactMaterialStreamByteAndReleaseThenStowHasNoDoubleCue()
        {
            GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
            Vector3 basketPos = new Vector3(3f, 1.2f, -2f);

            // Compact numeric MaterialStream routes without any string label round-trip
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CartStow, basketPos, 0.45f, materialStream: FeedbackSignal.EncodeMaterial(WasteStream.Plastic)));
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CartStow, basketPos, 0.45f, materialStream: FeedbackSignal.EncodeMaterial(WasteStream.Glass)));
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CartStow, basketPos, 0.45f, materialStream: FeedbackSignal.EncodeMaterial(WasteStream.Paper)));
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CartStow, basketPos, 0.45f, materialStream: FeedbackSignal.EncodeMaterial(WasteStream.Cans)));
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CartStow, basketPos, 0.45f, materialStream: FeedbackSignal.EncodeMaterial(WasteStream.Paper, isBulky: true)));
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CartStow, basketPos, 0.45f, materialStream: FeedbackSignal.NoMaterialStream));

            Assert.AreEqual(6, dispatched.Count);
            Assert.AreEqual(SoundId.ImpactPlastic, dispatched[0].id);
            Assert.AreEqual(SoundId.ImpactGlass, dispatched[1].id);
            Assert.AreEqual(SoundId.ImpactPaper, dispatched[2].id);
            Assert.AreEqual(SoundId.ImpactMetal, dispatched[3].id);
            Assert.AreEqual(SoundId.ImpactBulky, dispatched[4].id);
            Assert.AreEqual(SoundId.ImpactMetal, dispatched[5].id);
            Assert.IsTrue(dispatched[0].pos.HasValue);
            Assert.AreEqual(basketPos, dispatched[0].pos.Value);
            Assert.LessOrEqual(dispatched[0].vol, 0.65f);

            // Full release-then-stow sequence: Grabber.Released + PlayerController Drop feedback + CartStow
            // Must produce exactly one Drop cue (release) + one soft material impact (stow), with zero duplicate Drops or impacts.
            dispatched.Clear();
            TrashItem bottle = CreateTrash("PlasticBottle", WasteStream.Plastic);
            router.HandleObjectReleased(bottle, thrown: false);
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.Drop, basketPos, 0.5f));
            router.HandleFeedbackSignal(new FeedbackSignal(
                PlayerFeedbackEvent.CartStow,
                basketPos,
                0.45f,
                materialStream: FeedbackSignal.EncodeMaterial(bottle.Stream, bottle.IsBulky)));

            Assert.AreEqual(2, dispatched.Count, "Release-then-stow should emit exactly one release cue and one stow impact.");
            Assert.AreEqual(SoundId.Drop, dispatched[0].id);
            Assert.AreEqual(SoundId.ImpactPlastic, dispatched[1].id);
            Assert.AreEqual(1, CountSound(dispatched, SoundId.Drop));
            Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic));

            // Throw-then-stow sequence similarly avoids duplicate cues
            dispatched.Clear();
            TrashItem jar = CreateTrash("GlassJar", WasteStream.Glass);
            router.HandleObjectReleased(jar, thrown: true);
            router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.Throw, basketPos, 1f));
            router.HandleFeedbackSignal(new FeedbackSignal(
                PlayerFeedbackEvent.CartStow,
                basketPos,
                0.45f,
                materialStream: FeedbackSignal.EncodeMaterial(jar.Stream, jar.IsBulky)));

            Assert.AreEqual(2, dispatched.Count);
            Assert.AreEqual(SoundId.Throw, dispatched[0].id);
            Assert.AreEqual(SoundId.ImpactGlass, dispatched[1].id);
        }

        // ---------------------------------------------------------------- Typed Events (No Text Matching)

        [Test]
        public void GameAudioRouter_TypedEvents_RouteWithoutMessageTextAndLayerMaterialImpact()
        {
            Vector3 stationPos = new Vector3(10f, 0.5f, -4f);

            // SortCorrect with empty text & MaterialStream layers ImpactPlastic at stationPos
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SortCorrect,
                    stationPos,
                    1f,
                    label: null,
                    materialStream: FeedbackSignal.EncodeMaterial(WasteStream.Plastic)));

                Assert.AreEqual(2, dispatched.Count);
                Assert.AreEqual(SoundId.SortCorrect, dispatched[0].id);
                Assert.AreEqual(SoundId.ImpactPlastic, dispatched[1].id);
                Assert.AreEqual(stationPos, dispatched[0].pos.Value);
                Assert.AreEqual(stationPos, dispatched[1].pos.Value);
            }

            // SortWrong with empty text
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.SortWrong, stationPos, 1f, label: string.Empty));

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.SortWrong, dispatched[0].id);
            }

            // SellTrash with empty text & MaterialStream layers ImpactMetal at stationPos
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SellTrash,
                    stationPos,
                    1f,
                    label: string.Empty,
                    materialStream: FeedbackSignal.EncodeMaterial(WasteStream.Cans)));

                Assert.AreEqual(2, dispatched.Count);
                Assert.AreEqual(SoundId.SellTrash, dispatched[0].id);
                Assert.AreEqual(SoundId.ImpactMetal, dispatched[1].id);
                Assert.AreEqual(stationPos, dispatched[1].pos.Value);
            }

            // BinServiced with empty text
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.BinServiced, stationPos, 1f, label: null));

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.BinService, dispatched[0].id);
            }

            // FactoryRepaired with empty text
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.FactoryRepaired, stationPos, 1f, label: string.Empty));

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.ToolWrenchRepair, dispatched[0].id);
            }
        }

        // ---------------------------------------------------------------- Co-op Coin, Spend & Host Action Audio

        [Test]
        public void GameAudioRouter_CoopCoinAudio_OwnSortPlaysOnceWithoutEarnCoinsInBothArrivalOrders_AndBystanderHearsNothing()
        {
            Vector3 cratePos = new Vector3(2f, 0f, 5f);
            byte plasticByte = FeedbackSignal.EncodeMaterial(WasteStream.Plastic);

            // Arrival order 1: Targeted feedback arrives BEFORE shared wallet NetworkVariable delta
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.SortCorrect, cratePos, 1f, materialStream: plasticByte));
                router.HandleCoinsChanged(totalCoins: 31, delta: 6);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SortCorrect), "Own sort must play SortCorrect once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic), "Own sort must layer material impact.");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins), "Co-op wallet delta must never play EarnCoins.");
            }

            // Arrival order 2: Shared wallet NetworkVariable delta arrives BEFORE targeted feedback
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.HandleCoinsChanged(totalCoins: 31, delta: 6);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.SortCorrect, cratePos, 1f, materialStream: plasticByte));
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SortCorrect), "Own sort must play SortCorrect once when wallet arrives first.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic), "Own sort must layer material impact when wallet arrives first.");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins), "Co-op wallet delta arriving first must never play EarnCoins.");
            }

            // Bystander: another peer earns coins -> wallet delta replicates without feedback -> completely silent
            {
                GameAudioRouter bystanderRouter = CreateRouter(isMultiplayer: true, out var dispatched);
                bystanderRouter.HandleCoinsChanged(totalCoins: 37, delta: 6);
                bystanderRouter.HandleCoinsChanged(totalCoins: 46, delta: 9);
                bystanderRouter.FlushPendingCoinAudio();

                Assert.AreEqual(0, dispatched.Count, "Bystander must hear zero cues from another player's shared wallet credits.");
            }
        }

        [Test]
        public void GameAudioRouter_CoopAndSolo_SpendCoins_SilentForBystanderSingleCueForBuyerAndOrderIndependentInSolo()
        {
            Vector3 shopPos = new Vector3(4f, 0f, -3f);

            // Co-op bystander: teammate's shop buy or crafting upgrade (TrySpendTeamCoins) replicates negative wallet delta -> silent
            {
                GameAudioRouter bystander = CreateRouter(isMultiplayer: true, out var dispatched);
                bystander.HandleCoinsChanged(totalCoins: 75, delta: -25);
                bystander.HandleCoinsChanged(totalCoins: 50, delta: -25);
                bystander.FlushPendingCoinAudio();

                Assert.AreEqual(0, dispatched.Count, "Co-op bystander must hear zero SpendCoins cues from shared wallet debits.");
            }

            // Co-op acting buyer (same frame, wallet delta arrives BEFORE ShopPurchase reply) -> exactly 1 SpendCoins, 0 PickupItem
            {
                GameAudioRouter buyer = CreateRouter(isMultiplayer: true, out var dispatched);
                buyer.HandleCoinsChanged(totalCoins: 75, delta: -25);
                buyer.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ShopPurchase, shopPos, 1f));
                buyer.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count, "Co-op buyer must hear exactly one cue.");
                Assert.AreEqual(SoundId.SpendCoins, dispatched[0].id);
                Assert.AreEqual(0, CountSound(dispatched, SoundId.PickupItem));
            }

            // Co-op acting buyer (different frames: wallet delta lands & flushes first, ShopPurchase reply arrives on a later frame)
            {
                GameAudioRouter buyer = CreateRouter(isMultiplayer: true, out var dispatched);
                buyer.HandleCoinsChanged(totalCoins: 75, delta: -25);
                buyer.FlushPendingCoinAudio();
                Assert.AreEqual(0, dispatched.Count, "Wallet debit alone must stay silent in co-op.");

                buyer.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ShopPurchase, shopPos, 1f));
                buyer.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count, "Buyer must hear SpendCoins once even when wallet and reply land on different frames.");
                Assert.AreEqual(SoundId.SpendCoins, dispatched[0].id);
                Assert.AreEqual(0, CountSound(dispatched, SoundId.PickupItem));
            }

            // Co-op acting buyer (reverse order: ShopPurchase reply arrives BEFORE wallet delta)
            {
                GameAudioRouter buyer = CreateRouter(isMultiplayer: true, out var dispatched);
                buyer.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ShopPurchase, shopPos, 1f));
                buyer.FlushPendingCoinAudio();
                buyer.HandleCoinsChanged(totalCoins: 75, delta: -25);
                buyer.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.SpendCoins, dispatched[0].id);
                Assert.AreEqual(0, CountSound(dispatched, SoundId.PickupItem));
            }

            // Solo shop buy (coin-change-FIRST order: TrySpendCoins -> ShopPurchase -> LateUpdate) -> 1 SpendCoins, 0 PickupItem
            {
                GameAudioRouter solo = CreateRouter(isMultiplayer: false, out var dispatched);
                solo.HandleCoinsChanged(totalCoins: 75, delta: -25);
                solo.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ShopPurchase, shopPos, 1f));
                solo.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.SpendCoins, dispatched[0].id);
                Assert.AreEqual(0, CountSound(dispatched, SoundId.PickupItem));
            }

            // Solo shop buy (reverse order: ShopPurchase -> TrySpendCoins -> LateUpdate) -> 1 SpendCoins, 0 PickupItem
            {
                GameAudioRouter solo = CreateRouter(isMultiplayer: false, out var dispatched);
                solo.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ShopPurchase, shopPos, 1f));
                solo.HandleCoinsChanged(totalCoins: 75, delta: -25);
                solo.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.SpendCoins, dispatched[0].id);
                Assert.AreEqual(0, CountSound(dispatched, SoundId.PickupItem));
            }

            // Solo standalone spend without ShopPurchase (e.g. crafting machine upgrade) -> LateUpdate flushes 1 SpendCoins
            {
                GameAudioRouter solo = CreateRouter(isMultiplayer: false, out var dispatched);
                solo.HandleCoinsChanged(totalCoins: 75, delta: -25);
                Assert.AreEqual(0, dispatched.Count, "Solo wallet spend is deferred until LateUpdate.");
                solo.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.SpendCoins, dispatched[0].id);
            }

            // World item pickup still plays PickupItem cleanly in both solo and co-op
            {
                GameAudioRouter coop = CreateRouter(isMultiplayer: true, out var dispatched);
                coop.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.PickupItem, shopPos, 0.9f));

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.PickupItem, dispatched[0].id);
                Assert.IsTrue(dispatched[0].pos.HasValue);
            }
        }

        [Test]
        public void GameAudioRouter_CoopHost_OwnActionPlaysOnceAndRemoteStaticCallbacksAreSilent()
        {
            var crateGo = Track(new GameObject("Crate_CoopHost"));
            var crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Plastic, payOnSort: true);
            TrashItem plastic = CreateTrash("Trash_Plastic", WasteStream.Plastic);

            var buyBackGo = Track(new GameObject("BuyBack_CoopHost"));
            var buyBack = buyBackGo.AddComponent<RecyclingBuyBack>();
            TrashItem can = CreateTrash("Trash_Can", WasteStream.Cans);

            // Remote client sorts/sells on host: host sees static ItemSorted/ItemBought + shared wallet credit, NO targeted feedback
            {
                GameAudioRouter hostRouter = CreateRouter(isMultiplayer: true, out var dispatched);
                hostRouter.HandleItemSorted(crate, plastic, match: true, payout: 6);
                hostRouter.HandleItemSorted(crate, plastic, match: false, payout: 0);
                hostRouter.HandleItemBought(buyBack, can, price: 9);
                hostRouter.HandleCoinsChanged(totalCoins: 40, delta: 15);
                hostRouter.FlushPendingCoinAudio();

                Assert.AreEqual(0, dispatched.Count,
                    "In co-op, host must not play audio from static ItemSorted/ItemBought or shared wallet credits for remote actions.");
            }

            // Host's own sort: static ItemSorted + wallet credit + SendFeedbackToClient(host) -> exactly one SortCorrect + impact
            {
                GameAudioRouter hostRouter = CreateRouter(isMultiplayer: true, out var dispatched);
                hostRouter.HandleItemSorted(crate, plastic, match: true, payout: 6);
                hostRouter.HandleCoinsChanged(totalCoins: 31, delta: 6);
                hostRouter.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SortCorrect,
                    crateGo.transform.position,
                    1f,
                    materialStream: FeedbackSignal.EncodeMaterial(plastic.Stream, plastic.IsBulky)));
                hostRouter.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SortCorrect), "Host's own sort must play SortCorrect exactly once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic), "Host's own sort must layer material impact once.");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }

            // Host's own sell: static ItemBought + wallet credit + SendFeedbackToClient(host) -> exactly one SellTrash + impact
            {
                GameAudioRouter hostRouter = CreateRouter(isMultiplayer: true, out var dispatched);
                hostRouter.HandleItemBought(buyBack, can, price: 9);
                hostRouter.HandleCoinsChanged(totalCoins: 40, delta: 9);
                hostRouter.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SellTrash,
                    buyBackGo.transform.position,
                    1f,
                    materialStream: FeedbackSignal.EncodeMaterial(can.Stream, can.IsBulky)));
                hostRouter.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SellTrash), "Host's own sell must play SellTrash exactly once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal), "Host's own sell must layer material impact once.");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }
        }

        // ---------------------------------------------------------------- Solo Event Orders & Real Authoritative Paths

        [Test]
        public void GameAudioRouter_Solo_SortSellBinAndFactory_PlayExactlyOneCueInAllEventOrdersIncludingCoinChangeFirst()
        {
            var crateGo = Track(new GameObject("Crate_Solo"));
            var crate = crateGo.AddComponent<SortingCrate>();
            crate.Configure(WasteStream.Plastic, payOnSort: true);
            TrashItem plastic = CreateTrash("Plastic_Solo", WasteStream.Plastic);

            var buyBackGo = Track(new GameObject("BuyBack_Solo"));
            var buyBack = buyBackGo.AddComponent<RecyclingBuyBack>();
            TrashItem can = CreateTrash("Can_Solo", WasteStream.Cans);

            var binGo = Track(new GameObject("Bin_Solo"));
            var bin = binGo.AddComponent<OverflowingBinSource>();

            var factoryGo = Track(new GameObject("Factory_Solo"));
            var factory = factoryGo.AddComponent<FactorySmokestackSource>();
            ItemDefinition wrench = CreateItem("tool_wrench", "Wrench", new ToolTargetItemBehavior());

            // --- Solo Sort (coin-change-FIRST, feedback-FIRST, and static-callback-FIRST) ---
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HandleCoinsChanged(31, 6);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SortCorrect, crateGo.transform.position, 1f,
                    materialStream: FeedbackSignal.EncodeMaterial(plastic.Stream)));
                router.HandleItemSorted(crate, plastic, match: true, payout: 6);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SortCorrect));
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic));
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins), "Coin-change-first solo sort must not play EarnCoins.");
            }
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SortCorrect, crateGo.transform.position, 1f,
                    materialStream: FeedbackSignal.EncodeMaterial(plastic.Stream)));
                router.HandleCoinsChanged(31, 6);
                router.HandleItemSorted(crate, plastic, match: true, payout: 6);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SortCorrect));
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic));
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HandleCoinsChanged(31, 6);
                router.HandleItemSorted(crate, plastic, match: true, payout: 6);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SortCorrect, crateGo.transform.position, 1f,
                    materialStream: FeedbackSignal.EncodeMaterial(plastic.Stream)));
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SortCorrect));
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic));
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }

            // --- Solo Sell (coin-change-FIRST, feedback-FIRST, and static-callback-FIRST) ---
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HandleCoinsChanged(34, 9);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SellTrash, buyBackGo.transform.position, 1f,
                    materialStream: FeedbackSignal.EncodeMaterial(can.Stream)));
                router.HandleItemBought(buyBack, can, price: 9);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SellTrash));
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal));
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins), "Coin-change-first solo sell must not play EarnCoins.");
            }
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SellTrash, buyBackGo.transform.position, 1f,
                    materialStream: FeedbackSignal.EncodeMaterial(can.Stream)));
                router.HandleCoinsChanged(34, 9);
                router.HandleItemBought(buyBack, can, price: 9);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SellTrash));
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal));
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HandleCoinsChanged(34, 9);
                router.HandleItemBought(buyBack, can, price: 9);
                router.HandleFeedbackSignal(new FeedbackSignal(
                    PlayerFeedbackEvent.SellTrash, buyBackGo.transform.position, 1f,
                    materialStream: FeedbackSignal.EncodeMaterial(can.Stream)));
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SellTrash));
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal));
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }

            // --- Solo Bin (coin-change-FIRST, BinServiced -> Interact, Interact -> BinServiced) ---
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HoveredInteractableProvider = () => bin;
                router.HandleCoinsChanged(39, 14);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.BinServiced, binGo.transform.position, 1f));
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.Interact, binGo.transform.position, 0.7f));
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.BinService, dispatched[0].id);
            }
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HoveredInteractableProvider = () => bin;
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.Interact, binGo.transform.position, 0.7f));
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.BinServiced, binGo.transform.position, 1f));
                router.HandleCoinsChanged(39, 14);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.BinService, dispatched[0].id);
            }

            // --- Solo Factory (coin-change-FIRST, FactoryRepaired -> ToolUse/Interact, ToolUse/Interact -> FactoryRepaired) ---
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.EquippedItemProvider = () => wrench;
                router.HoveredToolTargetProvider = () => factory;
                router.HoveredInteractableProvider = () => factory;

                router.HandleCoinsChanged(60, 35);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.FactoryRepaired, factoryGo.transform.position, 1f));
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ToolUse, factoryGo.transform.position, 1f));
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.Interact, factoryGo.transform.position, 0.7f));
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.ToolWrenchRepair, dispatched[0].id);
            }
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.EquippedItemProvider = () => wrench;
                router.HoveredToolTargetProvider = () => factory;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ToolUse, factoryGo.transform.position, 1f));
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.FactoryRepaired, factoryGo.transform.position, 1f));
                router.HandleCoinsChanged(60, 35);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.ToolWrenchRepair, dispatched[0].id);
            }
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.EquippedItemProvider = () => wrench;
                router.HoveredInteractableProvider = () => factory;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.Interact, factoryGo.transform.position, 0.7f));
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.FactoryRepaired, factoryGo.transform.position, 1f));
                router.HandleCoinsChanged(60, 35);
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.ToolWrenchRepair, dispatched[0].id);
            }

            // --- Solo standalone credit with no dedicated action -> LateUpdate flushes 1 EarnCoins ---
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                router.HandleCoinsChanged(35, 10);
                Assert.AreEqual(0, dispatched.Count, "Solo wallet credit is deferred until LateUpdate.");
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.EarnCoins, dispatched[0].id);
            }
        }

        [Test]
        public void GameAudioRouter_SoloRealAuthoritativePaths_BoundPlayerInventoryAndFeedback_PlaySingleCueWithOriginalAddCoinsOrder()
        {
            // 1. Real SortingCrate.ExecuteAuthoritativeSort (AddCoins fires BEFORE Feedback.Emit and ItemSorted)
            {
                GameAudioRouter router = CreateBoundSoloPlayerAndRouter(
                    out PlayerInventory inventory, out PlayerInteractor interactor, out _, out var dispatched);
                int startCoins = inventory.Coins;

                var crateGo = Track(new GameObject("RealCrate_Solo"));
                crateGo.transform.position = new Vector3(1f, 0f, 2f);
                var crate = crateGo.AddComponent<SortingCrate>();
                crate.Configure(WasteStream.Plastic, payOnSort: true);
                TrashItem plastic = CreateTrash("RealPlastic", WasteStream.Plastic, editModeDisposable: true);

                crate.ExecuteAuthoritativeSort(plastic, inventory, interactor);
                router.FlushPendingCoinAudio();

                Assert.Greater(inventory.Coins, startCoins, "Real sort must credit PlayerInventory.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.SortCorrect), "Real solo sort must play SortCorrect once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactPlastic), "Real solo sort must layer material impact once.");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins), "Real solo sort must not stack EarnCoins.");
                Assert.AreEqual(2, dispatched.Count);
            }

            // 2. Real RecyclingBuyBack.ExecuteAuthoritativeBuy (AddCoins fires BEFORE Feedback.Emit and ItemBought)
            {
                GameAudioRouter router = CreateBoundSoloPlayerAndRouter(
                    out PlayerInventory inventory, out PlayerInteractor interactor, out _, out var dispatched);
                int startCoins = inventory.Coins;

                var buyBackGo = Track(new GameObject("RealBuyBack_Solo"));
                buyBackGo.transform.position = new Vector3(-3f, 0f, 4f);
                var buyBack = buyBackGo.AddComponent<RecyclingBuyBack>();
                TrashItem can = CreateTrash("RealCan", WasteStream.Cans, editModeDisposable: true);

                buyBack.ExecuteAuthoritativeBuy(can, interactor);
                router.FlushPendingCoinAudio();

                Assert.Greater(inventory.Coins, startCoins, "Real buy-back must credit PlayerInventory.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.SellTrash), "Real solo sell must play SellTrash once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal), "Real solo sell must layer material impact once.");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins), "Real solo sell must not stack EarnCoins.");
                Assert.AreEqual(2, dispatched.Count);
            }

            // 3. Real OverflowingBinSource.ExecuteAuthoritativeService (AddCoins fires BEFORE Feedback.Emit)
            {
                GameAudioRouter router = CreateBoundSoloPlayerAndRouter(
                    out PlayerInventory inventory, out PlayerInteractor interactor, out _, out var dispatched);
                int startCoins = inventory.Coins;

                var binGo = Track(new GameObject("RealBin_Solo"));
                binGo.transform.position = new Vector3(5f, 0f, -2f);
                var bin = binGo.AddComponent<OverflowingBinSource>();

                bin.ExecuteAuthoritativeService(interactor);
                router.FlushPendingCoinAudio();

                Assert.Greater(inventory.Coins, startCoins, "Real bin service must credit PlayerInventory.");
                Assert.AreEqual(1, dispatched.Count, "Real solo bin service must play exactly one cue.");
                Assert.AreEqual(SoundId.BinService, dispatched[0].id);
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }

            // 4. Real FactorySmokestackSource.ExecuteAuthoritativeRepair (AddCoins fires BEFORE Feedback.Emit)
            {
                GameAudioRouter router = CreateBoundSoloPlayerAndRouter(
                    out PlayerInventory inventory, out PlayerInteractor interactor, out _, out var dispatched);
                int startCoins = inventory.Coins;

                var factoryGo = Track(new GameObject("RealFactory_Solo"));
                factoryGo.transform.position = new Vector3(-6f, 0f, -4f);
                var factory = factoryGo.AddComponent<FactorySmokestackSource>();

                factory.ExecuteAuthoritativeRepair(interactor);
                router.FlushPendingCoinAudio();

                Assert.Greater(inventory.Coins, startCoins, "Real factory repair must credit PlayerInventory.");
                Assert.AreEqual(1, dispatched.Count, "Real solo factory repair must play exactly one cue.");
                Assert.AreEqual(SoundId.ToolWrenchRepair, dispatched[0].id);
                Assert.AreEqual(0, CountSound(dispatched, SoundId.EarnCoins));
            }
        }

        // ---------------------------------------------------------------- Factory ToolUse Suppression

        [Test]
        public void GameAudioRouter_CoopToolUse_SuppressesOnlyFactoryRepairToolNotFoodBroomOrNonFactoryWrench()
        {
            var factoryGo = Track(new GameObject("Factory_ToolCheck"));
            var factory = factoryGo.AddComponent<FactorySmokestackSource>();

            var nonFactoryGo = Track(new GameObject("NonFactory_ToolTarget"));
            var nonFactoryTarget = nonFactoryGo.AddComponent<StubNonFactoryToolTarget>();

            ItemDefinition wrench = CreateItem("tool_wrench", "Wrench", new ToolTargetItemBehavior());
            ItemDefinition kaskrout = CreateItem("food_kaskrout", "Kaskrout", new TimedBoostItemBehavior());
            ItemDefinition broom = CreateItem("tool_broom", "Broom", new SweepAreaItemBehavior());

            // Wrench while aiming at factory: optimistic ToolUse is suppressed until FactoryRepaired arrives
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.EquippedItemProvider = () => wrench;
                router.HoveredToolTargetProvider = () => factory;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ToolUse, factoryGo.transform.position, 1f));
                Assert.AreEqual(0, dispatched.Count, "Optimistic wrench ToolUse on a factory should wait for FactoryRepaired.");
            }

            // Wrench while aiming at a NON-factory tool target: MUST still play ToolWrenchRepair!
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.EquippedItemProvider = () => wrench;
                router.HoveredToolTargetProvider = () => nonFactoryTarget;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ToolUse, nonFactoryGo.transform.position, 1f));
                Assert.AreEqual(1, dispatched.Count, "Wrench on a non-factory target must still play ToolWrenchRepair.");
                Assert.AreEqual(SoundId.ToolWrenchRepair, dispatched[0].id);
            }

            // Wrench with no hovered target: MUST still play ToolWrenchRepair!
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.EquippedItemProvider = () => wrench;
                router.HoveredToolTargetProvider = () => null;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ToolUse, Vector3.zero, 1f));
                Assert.AreEqual(1, dispatched.Count, "Wrench without a factory target must still play ToolWrenchRepair.");
                Assert.AreEqual(SoundId.ToolWrenchRepair, dispatched[0].id);
            }

            // Kaskrout (food consumable) while aiming at factory: MUST still play!
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.EquippedItemProvider = () => kaskrout;
                router.HoveredToolTargetProvider = () => factory;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ToolUse, factoryGo.transform.position, 0.6f));
                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.PickupItem, dispatched[0].id);
            }

            // Broom while aiming at factory: MUST still play!
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: true, out var dispatched);
                router.EquippedItemProvider = () => broom;
                router.HoveredToolTargetProvider = () => factory;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ToolUse, factoryGo.transform.position, 0.9f));
                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.ToolBroomSweep, dispatched[0].id);
            }
        }

        // ---------------------------------------------------------------- Ambience Ducking & Scene Gating

        [Test]
        public void SoundManager_AmbienceDuck_RefCountsLowestTargetAndCleansUpDestroyedOwnersAndShopDisable()
        {
            SoundManager.ClearAmbienceDucks();

            // Safe when SoundManager.Instance is absent
            Assert.IsFalse(SoundManager.HasInstance);
            Assert.AreEqual(1f, SoundManager.GetAmbienceDuckTarget(), 0.001f);
            Assert.AreEqual(0, SoundManager.ActiveAmbienceDuckCount);

            var ownerA = new object();
            var ownerB = new object();
            var ownerC = new object();

            // Null owner is a safe no-op
            SoundManager.PushAmbienceDuck(null, 0f);
            SoundManager.PopAmbienceDuck(null);
            Assert.AreEqual(0, SoundManager.ActiveAmbienceDuckCount);

            // Lowest target wins across multiple owners
            SoundManager.PushAmbienceDuck(ownerA, 0.4f);
            Assert.AreEqual(1, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0.4f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            SoundManager.PushAmbienceDuck(ownerB, 0f);
            Assert.AreEqual(2, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            SoundManager.PushAmbienceDuck(ownerC, 0.65f);
            Assert.AreEqual(3, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            // Re-pushing the same owner updates its target in place without leaking a ref-count
            SoundManager.PushAmbienceDuck(ownerA, 0.25f);
            Assert.AreEqual(3, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            // Popping lowest reveals next lowest (ownerA = 0.25)
            SoundManager.PopAmbienceDuck(ownerB);
            Assert.AreEqual(2, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0.25f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            // Popping unknown or already-popped owner is a safe no-op
            SoundManager.PopAmbienceDuck(ownerB);
            SoundManager.PopAmbienceDuck(new object());
            Assert.AreEqual(2, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0.25f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            SoundManager.PopAmbienceDuck(ownerA);
            Assert.AreEqual(1, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0.65f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            SoundManager.PopAmbienceDuck(ownerC);
            Assert.AreEqual(0, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(1f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            // Destroyed UnityEngine.Object owner automatically prunes without leaking a stuck duck
            var tempGo = new GameObject("TempDuckOwner");
            SoundManager.PushAmbienceDuck(tempGo, 0.1f);
            Assert.AreEqual(1, SoundManager.ActiveAmbienceDuckCount);
            Assert.AreEqual(0.1f, SoundManager.GetAmbienceDuckTarget(), 0.001f);

            UnityEngine.Object.DestroyImmediate(tempGo);
            Assert.AreEqual(1f, SoundManager.GetAmbienceDuckTarget(), 0.001f);
            Assert.AreEqual(0, SoundManager.ActiveAmbienceDuckCount);

            // ShopUI.OnDisable also releases any active duck lease held by that ShopUI
            var shopGo = Track(new GameObject("TestShopUI_DisableDuck"));
            var shopUi = shopGo.AddComponent<ShopUI>();
            SoundManager.PushAmbienceDuck(shopUi, 0f);
            Assert.AreEqual(1, SoundManager.ActiveAmbienceDuckCount);
            InvokeNonPublic(shopUi, "OnDisable");
            Assert.AreEqual(0, SoundManager.ActiveAmbienceDuckCount, "ShopUI.OnDisable must release its ambience duck lease.");
            Assert.AreEqual(1f, SoundManager.GetAmbienceDuckTarget(), 0.001f);
        }

        [Test]
        public void SoundLibrary_And_SoundManager_GateCityAmbienceOutOfCharacterCreatorAndAllowGameplayScenes()
        {
            var lib = AssetDatabase.LoadAssetAtPath<SoundLibrary>("Assets/UpcycleCity/Resources/UpcycleCitySoundLibrary.asset");
            Assert.IsNotNull(lib, "Default UpcycleCitySoundLibrary asset should exist.");
            Assert.IsNotNull(lib.AmbienceLoop, "Default SoundLibrary should have an AmbienceLoop clip.");

            // Serialized silent-scene list defaults to real menu scene(s) only
            // Menu scenes stay silent: the character creator and the main menu
            CollectionAssert.AreEquivalent(new[] { "CharacterCreator", "MainMenu" }, lib.AmbienceSilentScenes);
            Assert.IsFalse(lib.ShouldPlayAmbienceInScene("MainMenu"));

            // Character Creator menu scene stays silent
            Assert.IsFalse(lib.ShouldPlayAmbienceInScene("CharacterCreator"));
            Assert.IsFalse(lib.ShouldPlayAmbienceInScene("charactercreator"));
            Assert.IsFalse(lib.ShouldPlayAmbienceInScene(string.Empty));
            Assert.IsFalse(lib.ShouldPlayAmbienceInScene(null));

            Assert.IsFalse(SoundManager.ShouldPlayCityAmbienceForScene(lib, "CharacterCreator"));
            Assert.IsFalse(SoundManager.ShouldPlayCityAmbienceForScene(null, "EcosystemSandbox"));

            // Gameplay scenes (including CraftingWorkshop) allow city ambience
            Assert.IsTrue(lib.ShouldPlayAmbienceInScene("PlayerGym"));
            Assert.IsTrue(lib.ShouldPlayAmbienceInScene("EcosystemSandbox"));
            Assert.IsTrue(lib.ShouldPlayAmbienceInScene("CraftingWorkshop"));
            Assert.IsTrue(SoundManager.ShouldPlayCityAmbienceForScene(lib, "EcosystemSandbox"));
            Assert.IsTrue(SoundManager.ShouldPlayCityAmbienceForScene(lib, "CraftingWorkshop"));

            // Inside shops, city ambience defaults to muted (0)
            Assert.AreEqual(0f, lib.ShopOpenAmbienceDuck, 0.001f);
        }

        [Test]
        public void GameAudioRouter_ShopPurchaseFailed_PlaysErrorCueAndNeverASpendCue()
        {
            foreach (bool coop in new[] { false, true })
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: coop, out var dispatched);
                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.ShopPurchaseFailed, Vector3.zero, 1f));
                router.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.InventoryFull), coop ? "co-op" : "solo");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.SpendCoins), coop ? "co-op" : "solo");
                Assert.AreEqual(1, dispatched.Count, coop ? "co-op" : "solo");
            }
        }

        [Test]
        public void GameAudioRouter_CraftingFeedbackEvents_SoloAndCoop_SingleCueNoDoubleSpendAndBystanderSilent()
        {
            Vector3 machinePos = new Vector3(12f, 0.5f, -6f);

            // 1. Solo MachineUpgraded (wallet debit FIRST, then MachineUpgraded, then LateUpdate flush)
            //    -> exactly 1 SpendCoins + 1 ImpactMetal, ZERO duplicate deferred SpendCoins
            {
                GameAudioRouter solo = CreateRouter(isMultiplayer: false, out var dispatched);
                solo.HandleCoinsChanged(totalCoins: 40, delta: -25);
                solo.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.MachineUpgraded, machinePos, 1f));
                solo.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SpendCoins), "Solo MachineUpgraded (debit first) must play SpendCoins once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal), "Solo MachineUpgraded must layer metallic impact once.");
                Assert.AreEqual(2, dispatched.Count);
                Assert.AreEqual(machinePos, dispatched[1].pos.Value);
            }

            // 2. Solo MachineUpgraded (reverse order: MachineUpgraded FIRST, then wallet debit, then LateUpdate flush)
            //    -> still exactly 1 SpendCoins + 1 ImpactMetal
            {
                GameAudioRouter solo = CreateRouter(isMultiplayer: false, out var dispatched);
                solo.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.MachineUpgraded, machinePos, 1f));
                solo.HandleCoinsChanged(totalCoins: 40, delta: -25);
                solo.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SpendCoins), "Solo MachineUpgraded (feedback first) must play SpendCoins once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal));
                Assert.AreEqual(2, dispatched.Count);
            }

            // 3. Co-op acting player MachineUpgraded (wallet debit + feedback across frames) -> 1 SpendCoins + 1 ImpactMetal
            {
                GameAudioRouter actor = CreateRouter(isMultiplayer: true, out var dispatched);
                actor.HandleCoinsChanged(totalCoins: 40, delta: -25);
                actor.FlushPendingCoinAudio();
                Assert.AreEqual(0, dispatched.Count, "Co-op wallet debit alone must stay silent.");

                actor.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.MachineUpgraded, machinePos, 1f));
                actor.FlushPendingCoinAudio();

                Assert.AreEqual(1, CountSound(dispatched, SoundId.SpendCoins), "Co-op acting upgrader must hear SpendCoins once.");
                Assert.AreEqual(1, CountSound(dispatched, SoundId.ImpactMetal), "Co-op acting upgrader must hear metallic impact once.");
                Assert.AreEqual(2, dispatched.Count);
            }

            // 4. Co-op bystander (teammate upgrades machine -> only shared wallet debit replicates) -> completely silent
            {
                GameAudioRouter bystander = CreateRouter(isMultiplayer: true, out var dispatched);
                bystander.HandleCoinsChanged(totalCoins: 40, delta: -25);
                bystander.FlushPendingCoinAudio();

                Assert.AreEqual(0, dispatched.Count, "Co-op bystander must hear zero cues when a teammate upgrades a machine.");
            }

            // 5. CraftStarted, CraftFailed, CraftCompleted in both solo and co-op
            foreach (bool coop in new[] { false, true })
            {
                string mode = coop ? "co-op" : "solo";

                // CraftStarted -> ToolUseGeneric at machinePos
                {
                    GameAudioRouter router = CreateRouter(isMultiplayer: coop, out var dispatched);
                    router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CraftStarted, machinePos, 0.9f));
                    Assert.AreEqual(1, dispatched.Count, mode);
                    Assert.AreEqual(SoundId.ToolUseGeneric, dispatched[0].id, mode);
                    Assert.AreEqual(machinePos, dispatched[0].pos.Value, mode);
                }

                // CraftFailed -> InventoryFull (never SpendCoins)
                {
                    GameAudioRouter router = CreateRouter(isMultiplayer: coop, out var dispatched);
                    router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CraftFailed, machinePos, 0.85f));
                    router.FlushPendingCoinAudio();
                    Assert.AreEqual(1, dispatched.Count, mode);
                    Assert.AreEqual(SoundId.InventoryFull, dispatched[0].id, mode);
                    Assert.AreEqual(0, CountSound(dispatched, SoundId.SpendCoins), mode);
                }

                // CraftCompleted -> positive SortCorrect chime regardless of equipped tool (never broom/wrench)
                {
                    GameAudioRouter router = CreateRouter(isMultiplayer: coop, out var dispatched);
                    router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CraftCompleted, machinePos, 1f));
                    Assert.AreEqual(1, dispatched.Count, mode);
                    Assert.AreEqual(SoundId.SortCorrect, dispatched[0].id, mode);
                    Assert.AreEqual(machinePos, dispatched[0].pos.Value, mode);
                }
            }

            // 6. CraftCompleted with Broom or Wrench equipped still plays SortCorrect, never ToolBroomSweep / ToolWrenchRepair
            {
                GameAudioRouter router = CreateRouter(isMultiplayer: false, out var dispatched);
                ItemDefinition broom = CreateItem("tool_broom", "Broom", new SweepAreaItemBehavior());
                router.EquippedItemProvider = () => broom;

                router.HandleFeedbackSignal(new FeedbackSignal(PlayerFeedbackEvent.CraftCompleted, machinePos, 1f));
                Assert.AreEqual(1, dispatched.Count);
                Assert.AreEqual(SoundId.SortCorrect, dispatched[0].id, "CraftCompleted must not depend on the held tool.");
                Assert.AreEqual(0, CountSound(dispatched, SoundId.ToolBroomSweep));
            }
        }
    }
}
