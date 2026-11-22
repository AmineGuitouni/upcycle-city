using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Props;
using UpcycleCity.Shops;
using UpcycleCity.Waste;

namespace UpcycleCity.Audio
{
    /// <summary>
    /// Decoupled event-driven audio router that connects gameplay systems (player locomotion &amp; footsteps,
    /// grabbing/throwing, cart stowing, sorting crates, RÉCUP buy-back, shops, tools, bins, trash physics
    /// impacts, and UI buttons) to <see cref="SoundManager"/> without scattering audio calls across gameplay scripts.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Audio/Game Audio Router")]
    public sealed class GameAudioRouter : MonoBehaviour
    {
        [Header("Distance-Based Footstep Cadence")]
        [SerializeField, Min(0.5f)] float walkStepDistance = 1.75f;
        [SerializeField, Min(0.5f)] float runStepDistance = 2.15f;
        [SerializeField, Min(0.5f)] float crouchStepDistance = 1.35f;
        [SerializeField, Min(0.1f)] float minSpeedForFootsteps = 0.55f;

        PlayerController boundPlayer;
        float playerBoundTime;
        float stepDistanceAccum;
        bool wasGroundedLastFrame;
        bool wasShopOpenLastFrame;

        // Frame-level deduplication flags so compound solo actions play one rich sound instead of stacking duplicates
        int lastDedicatedInteractFrame = -1;
        int lastCoinSoundFrame = -1;
        int lastSpendCoinsFrame = -1;
        int lastSortSoundFrame = -1;
        int lastSellSoundFrame = -1;
        int lastBinSoundFrame = -1;
        int lastFactorySoundFrame = -1;
        int lastToolSoundFrame = -1;
        int pendingEarnCoinsFrame = -1;
        int pendingSpendCoinsFrame = -1;

        readonly HashSet<UIDocument> hookedDocuments = new HashSet<UIDocument>();

        /// <summary>
        /// Raised whenever this router dispatches a sound cue (soundId, optional 3D world position, volumeScale, pitchScale).
        /// Useful for diagnostics and EditMode tests without requiring Play Mode or an active audio device.
        /// </summary>
        public event Action<SoundId, Vector3?, float, float> SoundDispatched;

        /// <summary>Optional override for multiplayer state checks (used by EditMode tests without stubbing <see cref="IGameAuthority"/>).</summary>
        public Func<bool> MultiplayerStateProvider { get; set; }

        /// <summary>Optional override for the local player's equipped item (used by EditMode tests without spawning a full PlayerController).</summary>
        public Func<IInventoryItem> EquippedItemProvider { get; set; }

        /// <summary>Optional override for the hovered <see cref="IInteractable"/> (used by EditMode tests).</summary>
        public Func<IInteractable> HoveredInteractableProvider { get; set; }

        /// <summary>Optional override for the hovered <see cref="IToolTarget"/> (used by EditMode tests).</summary>
        public Func<IToolTarget> HoveredToolTargetProvider { get; set; }

        bool IsMultiplayerSession => MultiplayerStateProvider != null ? MultiplayerStateProvider() : GameAuthority.IsMultiplayer;

        IInventoryItem CurrentEquippedItem =>
            EquippedItemProvider != null
                ? EquippedItemProvider()
                : (boundPlayer != null && boundPlayer.Inventory != null ? boundPlayer.Inventory.EquippedItem : null);

        IInteractable CurrentHoveredInteractable =>
            HoveredInteractableProvider != null
                ? HoveredInteractableProvider()
                : (boundPlayer != null && boundPlayer.Interactor != null ? boundPlayer.Interactor.HoveredInteractable : null);

        IToolTarget CurrentHoveredToolTarget =>
            HoveredToolTargetProvider != null
                ? HoveredToolTargetProvider()
                : (boundPlayer != null && boundPlayer.Interactor != null ? boundPlayer.Interactor.HoveredToolTarget : null);

        /// <summary>Maps a compact numeric <see cref="FeedbackSignal.MaterialStream"/> byte to its material impact <see cref="SoundId"/>.</summary>
        public static SoundId ResolveMaterialImpactSound(byte materialStream, SoundId fallback = SoundId.ImpactMetal)
        {
            if (materialStream == FeedbackSignal.NoMaterialStream || materialStream > (byte)WasteStream.BulkyUpcyclable)
                return fallback;

            return TrashImpactAudio.ImpactSoundForStream((WasteStream)materialStream, isBulky: false);
        }

        /// <summary>Maps a <see cref="PlayerFeedbackEvent.CartStow"/> numeric material byte to a soft impact sound.</summary>
        public static SoundId ResolveCartStowSound(byte materialStream) =>
            ResolveMaterialImpactSound(materialStream, SoundId.ImpactMetal);

        /// <summary>Routes a <see cref="FeedbackSignal"/> directly through the router's feedback handler.</summary>
        public void HandleFeedbackSignal(in FeedbackSignal signal) => OnPlayerFeedback(signal);

        /// <summary>Routes a <see cref="SortingCrate.ItemSorted"/> callback directly through the router's sort handler.</summary>
        public void HandleItemSorted(SortingCrate crate, TrashItem item, bool match, int payout) => OnItemSorted(crate, item, match, payout);

        /// <summary>Routes a <see cref="RecyclingBuyBack.ItemBought"/> callback directly through the router's buy-back handler.</summary>
        public void HandleItemBought(RecyclingBuyBack buyBack, TrashItem item, int price) => OnItemBought(buyBack, item, price);

        /// <summary>Routes a <see cref="PhysicsGrabController.Released"/> callback directly through the router's release handler.</summary>
        public void HandleObjectReleased(IGrabbable grabbable, bool thrown) => OnObjectReleased(grabbable, thrown);

        /// <summary>Routes a <see cref="PlayerInventory.CoinsChanged"/> callback directly through the router's coin handler.</summary>
        public void HandleCoinsChanged(int totalCoins, int delta) => OnCoinsChanged(totalCoins, delta);

        void OnEnable()
        {
            PlayerContext.PlayerRegistered += BindPlayer;
            PlayerContext.PlayerUnregistered += UnbindPlayer;
            SortingCrate.ItemSorted += OnItemSorted;
            RecyclingBuyBack.ItemBought += OnItemBought;
            TrashItem.Spawned += OnTrashSpawned;
            SceneManager.sceneLoaded += OnSceneLoaded;

            if (PlayerContext.Active != null)
                BindPlayer(PlayerContext.Active);

            AttachTrashAudioInScene();
            HookUiDocumentsInScene();
        }

        void OnDisable()
        {
            PlayerContext.PlayerRegistered -= BindPlayer;
            PlayerContext.PlayerUnregistered -= UnbindPlayer;
            SortingCrate.ItemSorted -= OnItemSorted;
            RecyclingBuyBack.ItemBought -= OnItemBought;
            TrashItem.Spawned -= OnTrashSpawned;
            SceneManager.sceneLoaded -= OnSceneLoaded;

            UnbindPlayer(boundPlayer);
            pendingEarnCoinsFrame = -1;
            pendingSpendCoinsFrame = -1;
            hookedDocuments.Clear();
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            hookedDocuments.Clear();
            AttachTrashAudioInScene();
            HookUiDocumentsInScene();
        }

        void Update()
        {
            TickFootsteps(Time.deltaTime);

            bool shopOpen = ShopUI.Instance != null && ShopUI.Instance.IsOpen;
            if (shopOpen && !wasShopOpenLastFrame)
                HookUiDocumentsInScene();
            wasShopOpenLastFrame = shopOpen;
        }

        void LateUpdate()
        {
            FlushPendingCoinAudio();
        }

        /// <summary>
        /// Flushes any solo wallet-delta coin cues deferred from <see cref="OnCoinsChanged"/> until end-of-frame,
        /// so gameplay actions that credit/debit coins before emitting their dedicated feedback event
        /// (<see cref="PlayerFeedbackEvent.SortCorrect"/>, <see cref="PlayerFeedbackEvent.SellTrash"/>,
        /// <see cref="PlayerFeedbackEvent.BinServiced"/>, <see cref="PlayerFeedbackEvent.FactoryRepaired"/>,
        /// <see cref="PlayerFeedbackEvent.PickupCoins"/>, <see cref="PlayerFeedbackEvent.ShopPurchase"/>)
        /// still produce a single cue regardless of callback order. Zero per-frame allocations.
        /// </summary>
        public void FlushPendingCoinAudio()
        {
            if (pendingSpendCoinsFrame != -1)
            {
                int frame = pendingSpendCoinsFrame;
                pendingSpendCoinsFrame = -1;
                if (!IsMultiplayerSession && lastSpendCoinsFrame != frame)
                {
                    lastSpendCoinsFrame = frame;
                    Dispatch2D(SoundId.SpendCoins);
                }
            }

            if (pendingEarnCoinsFrame != -1)
            {
                int frame = pendingEarnCoinsFrame;
                pendingEarnCoinsFrame = -1;
                if (!IsMultiplayerSession && lastCoinSoundFrame != frame)
                {
                    lastCoinSoundFrame = frame;
                    Dispatch2D(SoundId.EarnCoins);
                }
            }
        }

        // ---------------------------------------------------------------- Player Binding

        void BindPlayer(PlayerController player)
        {
            if (player == null || player == boundPlayer)
                return;

            UnbindPlayer(boundPlayer);
            boundPlayer = player;
            playerBoundTime = Time.unscaledTime;
            stepDistanceAccum = walkStepDistance * 0.65f; // Quick natural first step
            wasGroundedLastFrame = true;

            if (player.Feedback != null)
                player.Feedback.SignalEmitted += OnPlayerFeedback;

            if (player.Grabber != null)
            {
                player.Grabber.PickupStarted += OnPickupStarted;
                player.Grabber.Grabbed += OnObjectGrabbed;
                player.Grabber.Released += OnObjectReleased;
            }

            if (player.Inventory != null)
            {
                player.Inventory.CoinsChanged += OnCoinsChanged;
                player.Inventory.SelectedSlotChanged += OnSelectedSlotChanged;
                player.Inventory.ItemUsed += OnItemUsed;
            }
        }

        void UnbindPlayer(PlayerController player)
        {
            if (player == null || player != boundPlayer)
                return;

            if (player.Feedback != null)
                player.Feedback.SignalEmitted -= OnPlayerFeedback;

            if (player.Grabber != null)
            {
                player.Grabber.PickupStarted -= OnPickupStarted;
                player.Grabber.Grabbed -= OnObjectGrabbed;
                player.Grabber.Released -= OnObjectReleased;
            }

            if (player.Inventory != null)
            {
                player.Inventory.CoinsChanged -= OnCoinsChanged;
                player.Inventory.SelectedSlotChanged -= OnSelectedSlotChanged;
                player.Inventory.ItemUsed -= OnItemUsed;
            }

            pendingEarnCoinsFrame = -1;
            pendingSpendCoinsFrame = -1;
            boundPlayer = null;
        }

        // ---------------------------------------------------------------- Dispatch Helpers

        void Dispatch2D(SoundId id, float volumeScale = 1f, float pitchScale = 1f)
        {
            SoundDispatched?.Invoke(id, null, volumeScale, pitchScale);
            SoundManager.Play(id, volumeScale, pitchScale);
        }

        void Dispatch3D(SoundId id, Vector3 worldPosition, float volumeScale = 1f, float pitchScale = 1f)
        {
            SoundDispatched?.Invoke(id, worldPosition, volumeScale, pitchScale);
            SoundManager.PlayAt(id, worldPosition, volumeScale, pitchScale);
        }

        bool HasEquippedItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return false;
            IInventoryItem equipped = CurrentEquippedItem;
            return equipped != null && string.Equals(equipped.ItemId, itemId, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- Distance-Based Footsteps

        void TickFootsteps(float dt)
        {
            if (boundPlayer == null || boundPlayer.Motor == null)
                return;

            PlayerMotor motor = boundPlayer.Motor;
            bool grounded = motor.IsGrounded;

            if (!grounded)
            {
                wasGroundedLastFrame = false;
                stepDistanceAccum = walkStepDistance * 0.5f;
                return;
            }

            if (!wasGroundedLastFrame)
            {
                wasGroundedLastFrame = true;
                stepDistanceAccum = 0f; // Wait a full stride after landing sound
                return;
            }

            // Skip while locked in UI
            if (boundPlayer.StateMachine != null && boundPlayer.StateMachine.Current == boundPlayer.StateMachine.Locked)
            {
                stepDistanceAccum = walkStepDistance * 0.6f;
                return;
            }

            float speed = motor.HorizontalSpeed;
            if (speed < minSpeedForFootsteps)
            {
                // Prime next step so starting to walk feels responsive
                stepDistanceAccum = Mathf.Min(stepDistanceAccum, walkStepDistance * 0.65f);
                return;
            }

            stepDistanceAccum += speed * dt;

            bool isSprinting = boundPlayer.StateMachine != null && boundPlayer.StateMachine.Current == boundPlayer.StateMachine.Sprint;
            bool isCrouching = motor.IsCrouching;

            float stride = isCrouching ? crouchStepDistance : (isSprinting ? runStepDistance : walkStepDistance);
            if (stepDistanceAccum >= stride)
            {
                stepDistanceAccum -= stride;
                SoundId stepCue = isCrouching
                    ? SoundId.FootstepCrouch
                    : (isSprinting ? SoundId.FootstepRun : SoundId.FootstepWalk);

                // Slightly scale volume with speed ratio for organic momentum
                float volScale = isCrouching ? 0.85f : Mathf.Clamp(speed / 5.5f, 0.75f, 1.1f);
                Dispatch3D(stepCue, boundPlayer.transform.position, volScale);
            }
        }

        // ---------------------------------------------------------------- Player Feedback Hub Events

        void OnPlayerFeedback(FeedbackSignal signal)
        {
            switch (signal.Event)
            {
                case PlayerFeedbackEvent.Jump:
                    Dispatch2D(SoundId.Jump, Mathf.Lerp(0.7f, 1f, signal.Intensity));
                    break;

                case PlayerFeedbackEvent.Land:
                    Dispatch3D(SoundId.Land, signal.WorldPosition, Mathf.Lerp(0.42f, 1f, signal.Intensity));
                    break;

                case PlayerFeedbackEvent.Footstep:
                    Dispatch3D(SoundId.FootstepWalk, signal.WorldPosition, signal.Intensity);
                    break;

                case PlayerFeedbackEvent.PickupItem:
                    Dispatch3D(SoundId.PickupItem, signal.WorldPosition, Mathf.Lerp(0.75f, 1f, signal.Intensity));
                    break;

                case PlayerFeedbackEvent.PickupCoins:
                    if (lastCoinSoundFrame != Time.frameCount)
                    {
                        lastCoinSoundFrame = Time.frameCount;
                        pendingEarnCoinsFrame = -1;
                        Dispatch3D(SoundId.EarnCoins, signal.WorldPosition, signal.Intensity);
                    }
                    break;

                case PlayerFeedbackEvent.ShopPurchase:
                    if (lastSpendCoinsFrame != Time.frameCount)
                    {
                        lastSpendCoinsFrame = Time.frameCount;
                        pendingSpendCoinsFrame = -1;
                        Dispatch2D(SoundId.SpendCoins);
                    }
                    break;

                case PlayerFeedbackEvent.MachineUpgraded:
                    if (lastSpendCoinsFrame != Time.frameCount)
                    {
                        lastSpendCoinsFrame = Time.frameCount;
                        pendingSpendCoinsFrame = -1;
                        Dispatch2D(SoundId.SpendCoins);
                        Dispatch3D(SoundId.ImpactMetal, signal.WorldPosition, 0.78f, 1.08f);
                    }
                    break;

                case PlayerFeedbackEvent.CraftStarted:
                    Dispatch3D(SoundId.ToolUseGeneric, signal.WorldPosition, Mathf.Lerp(0.75f, 1f, signal.Intensity), 1.04f);
                    break;

                case PlayerFeedbackEvent.CraftCompleted:
                    Dispatch3D(SoundId.SortCorrect, signal.WorldPosition, Mathf.Lerp(0.8f, 1f, signal.Intensity), 1.06f);
                    break;

                case PlayerFeedbackEvent.InventoryFull:
                case PlayerFeedbackEvent.ShopPurchaseFailed:
                case PlayerFeedbackEvent.CraftFailed:
                    Dispatch2D(SoundId.InventoryFull);
                    break;

                case PlayerFeedbackEvent.SortCorrect:
                    if (lastSortSoundFrame != Time.frameCount)
                    {
                        lastSortSoundFrame = Time.frameCount;
                        lastDedicatedInteractFrame = Time.frameCount;
                        lastCoinSoundFrame = Time.frameCount;
                        pendingEarnCoinsFrame = -1;
                        Dispatch3D(SoundId.SortCorrect, signal.WorldPosition);
                        if (signal.HasMaterialStream)
                            Dispatch3D(ResolveMaterialImpactSound(signal.MaterialStream), signal.WorldPosition, 0.65f, 1.05f);
                    }
                    break;

                case PlayerFeedbackEvent.SortWrong:
                    if (lastSortSoundFrame != Time.frameCount)
                    {
                        lastSortSoundFrame = Time.frameCount;
                        lastDedicatedInteractFrame = Time.frameCount;
                        Dispatch3D(SoundId.SortWrong, signal.WorldPosition);
                    }
                    break;

                case PlayerFeedbackEvent.SellTrash:
                    if (lastSellSoundFrame != Time.frameCount)
                    {
                        lastSellSoundFrame = Time.frameCount;
                        lastDedicatedInteractFrame = Time.frameCount;
                        lastCoinSoundFrame = Time.frameCount;
                        pendingEarnCoinsFrame = -1;
                        Dispatch3D(SoundId.SellTrash, signal.WorldPosition, signal.Intensity);
                        if (signal.HasMaterialStream)
                            Dispatch3D(ResolveMaterialImpactSound(signal.MaterialStream), signal.WorldPosition, 0.55f);
                    }
                    break;

                case PlayerFeedbackEvent.BinServiced:
                    if (lastBinSoundFrame != Time.frameCount)
                    {
                        lastBinSoundFrame = Time.frameCount;
                        lastDedicatedInteractFrame = Time.frameCount;
                        lastCoinSoundFrame = Time.frameCount;
                        pendingEarnCoinsFrame = -1;
                        Dispatch3D(SoundId.BinService, signal.WorldPosition);
                    }
                    break;

                case PlayerFeedbackEvent.FactoryRepaired:
                    if (lastFactorySoundFrame != Time.frameCount)
                    {
                        lastFactorySoundFrame = Time.frameCount;
                        lastToolSoundFrame = Time.frameCount;
                        lastDedicatedInteractFrame = Time.frameCount;
                        lastCoinSoundFrame = Time.frameCount;
                        pendingEarnCoinsFrame = -1;
                        Dispatch3D(SoundId.ToolWrenchRepair, signal.WorldPosition);
                    }
                    break;

                case PlayerFeedbackEvent.CartStow:
                    Dispatch3D(
                        ResolveCartStowSound(signal.MaterialStream),
                        signal.WorldPosition,
                        Mathf.Lerp(0.35f, 0.65f, signal.Intensity),
                        0.94f);
                    break;

                case PlayerFeedbackEvent.ToolUse:
                    HandleToolUseSound(in signal);
                    break;

                case PlayerFeedbackEvent.Interact:
                    HandleInteractSound(in signal);
                    break;

                // Grab / Throw / Drop are handled with full TrashItem material context via Grabber events below.
                // FocusChanged is intentionally silent to avoid noisy crosshair chatter.
            }
        }

        void HandleToolUseSound(in FeedbackSignal signal)
        {
            IInventoryItem equipped = CurrentEquippedItem;

            // Factory repairs emit typed PlayerFeedbackEvent.FactoryRepaired upon authoritative confirmation
            // (local in solo, BroadcastFeedbackClientRpc in co-op). Suppress only the optimistic repair-tool
            // signal while aiming at a factory so non-repair tools (e.g. food/broom) still play normally.
            if (CurrentHoveredToolTarget is FactorySmokestackSource factory
                && equipped != null
                && string.Equals(equipped.ItemId, factory.RequiredToolId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (lastToolSoundFrame == Time.frameCount)
                return;
            lastToolSoundFrame = Time.frameCount;

            if (equipped != null && equipped.UseBehavior is SweepAreaItemBehavior)
            {
                Dispatch3D(SoundId.ToolBroomSweep, signal.WorldPosition, Mathf.Lerp(0.65f, 1f, signal.Intensity));
                return;
            }

            bool isWrench = equipped != null
                && string.Equals(equipped.ItemId, "tool_wrench", StringComparison.OrdinalIgnoreCase);
            if (isWrench)
            {
                Dispatch3D(SoundId.ToolWrenchRepair, signal.WorldPosition);
                return;
            }

            if (equipped != null && equipped.UseBehavior is TimedBoostItemBehavior)
            {
                Dispatch2D(SoundId.PickupItem, 0.9f, 1.12f);
                return;
            }

            Dispatch3D(SoundId.ToolUseGeneric, signal.WorldPosition, signal.Intensity);
        }

        void HandleInteractSound(in FeedbackSignal signal)
        {
            if (lastDedicatedInteractFrame == Time.frameCount)
                return;

            IInteractable hovered = CurrentHoveredInteractable;

            // Sorting, RÉCUP buy-back, and street-bin servicing emit dedicated typed feedback events
            // (SortCorrect/SortWrong, SellTrash, BinServiced) so we never double-play or play on rejected RPCs.
            if (hovered is SortingCrate || hovered is RecyclingBuyBack || hovered is OverflowingBinSource)
                return;

            if (hovered is ShoppingCart)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                Dispatch3D(SoundId.CartGrab, signal.WorldPosition);
                return;
            }

            if (hovered is ShopCounter)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                Dispatch2D(SoundId.UiClick, 0.9f, 0.96f);
                return;
            }

            if (hovered is FactorySmokestackSource factory)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                // Pressing [E] with the required tool equipped routes through RequestRepairFactory and plays
                // ToolWrenchRepair via FactoryRepaired feedback; only alert when the player lacks the required tool.
                if (!factory.IsScrubberActive && !HasEquippedItem(factory.RequiredToolId))
                    Dispatch3D(SoundId.AttentionAlert, signal.WorldPosition);
                return;
            }

            Dispatch3D(SoundId.Interact, signal.WorldPosition, signal.Intensity);
        }

        // ---------------------------------------------------------------- Grab, Throw, Drop & Inventory

        void OnPickupStarted(IGrabbable grabbable, PickupStyle style)
        {
            if (style == PickupStyle.Picker)
                Dispatch2D(SoundId.LitterPickerSnap);
        }

        void OnObjectGrabbed(IGrabbable grabbable)
        {
            Vector3 pos = grabbable != null && grabbable.Transform != null
                ? grabbable.Transform.position
                : (boundPlayer != null ? boundPlayer.transform.position : Vector3.zero);

            if (grabbable is TrashItem trash)
            {
                SoundId matSound = TrashImpactAudio.GrabSoundForStream(trash.Stream, trash.IsBulky);
                Dispatch3D(matSound, pos, 0.95f);
                Dispatch2D(SoundId.GrabGeneric, 0.40f, trash.IsBulky ? 0.85f : 1.05f);
            }
            else
            {
                Dispatch3D(SoundId.GrabGeneric, pos);
            }
        }

        void OnObjectReleased(IGrabbable grabbable, bool thrown)
        {
            // Skip release sound if it was immediately consumed by a deposit (SortingCrate/BuyBack) on the same frame
            if (lastDedicatedInteractFrame == Time.frameCount)
                return;

            bool bulky = grabbable != null && grabbable.IsBulky;
            if (thrown)
                Dispatch2D(SoundId.Throw, bulky ? 1.0f : 0.85f, bulky ? 0.86f : 1.04f);
            else
                Dispatch2D(SoundId.Drop, 0.75f, bulky ? 0.85f : 1.0f);
        }

        void OnCoinsChanged(int totalCoins, int delta)
        {
            // In co-op, both wallet credits and spends replicate to every peer via SetCoinsFromNetwork.
            // Coin audio comes exclusively from the acting player's own authoritative feedback event
            // (SortCorrect, SellTrash, BinServiced, FactoryRepaired, ShopPurchase), never from shared wallet deltas.
            if (IsMultiplayerSession)
                return;

            // In solo, defer wallet-delta cues until LateUpdate so gameplay code can credit/debit coins
            // in any order relative to dedicated feedback events without double-playing.
            if (delta < 0)
                pendingSpendCoinsFrame = Time.frameCount;
            else if (delta > 0)
                pendingEarnCoinsFrame = Time.frameCount;
        }

        void OnSelectedSlotChanged(int index, InventorySlot slot)
        {
            // Ignore initial inventory setup right when player spawns
            if (Time.unscaledTime - playerBoundTime < 0.35f)
                return;
            Dispatch2D(SoundId.HotbarSwitch);
        }

        void OnItemUsed(IInventoryItem item)
        {
            if (item == null || lastToolSoundFrame == Time.frameCount)
                return;

            if (item.UseBehavior is TimedBoostItemBehavior)
            {
                lastToolSoundFrame = Time.frameCount;
                Dispatch2D(SoundId.PickupItem, 0.9f, 1.12f);
            }
        }

        // ---------------------------------------------------------------- World Sorting & Economy Events

        void OnItemSorted(SortingCrate crate, TrashItem item, bool match, int payout)
        {
            // In co-op, ItemSorted runs host-side for every player's sort. Host and clients both receive
            // targeted SortCorrect/SortWrong feedback (with MaterialStream) via NetworkStateReplicator instead.
            if (IsMultiplayerSession || lastSortSoundFrame == Time.frameCount)
                return;

            lastDedicatedInteractFrame = Time.frameCount;
            lastSortSoundFrame = Time.frameCount;
            if (payout > 0)
            {
                lastCoinSoundFrame = Time.frameCount;
                pendingEarnCoinsFrame = -1;
            }

            Vector3 pos = crate != null ? crate.transform.position : Vector3.zero;
            if (match)
            {
                Dispatch3D(SoundId.SortCorrect, pos);
                if (item != null)
                    Dispatch3D(TrashImpactAudio.ImpactSoundForStream(item.Stream, item.IsBulky), pos, 0.65f, 1.05f);
            }
            else
            {
                Dispatch3D(SoundId.SortWrong, pos);
            }
        }

        void OnItemBought(RecyclingBuyBack buyBack, TrashItem item, int price)
        {
            // In co-op, ItemBought runs host-side for every player's sale. Host and clients both receive
            // targeted SellTrash feedback (with MaterialStream) via NetworkStateReplicator instead.
            if (IsMultiplayerSession || lastSellSoundFrame == Time.frameCount)
                return;

            lastDedicatedInteractFrame = Time.frameCount;
            lastSellSoundFrame = Time.frameCount;
            lastCoinSoundFrame = Time.frameCount;
            pendingEarnCoinsFrame = -1;

            Vector3 pos = buyBack != null ? buyBack.transform.position : Vector3.zero;
            Dispatch3D(SoundId.SellTrash, pos);
            if (item != null)
                Dispatch3D(TrashImpactAudio.ImpactSoundForStream(item.Stream, item.IsBulky), pos, 0.55f);
        }

        // ---------------------------------------------------------------- Trash Impact Attachment

        void OnTrashSpawned(TrashItem item)
        {
            if (item != null && item.GetComponent<TrashImpactAudio>() == null)
                item.gameObject.AddComponent<TrashImpactAudio>();
        }

        void AttachTrashAudioInScene()
        {
            TrashItem[] items = FindObjectsByType<TrashItem>();
            for (int i = 0; i < items.Length; i++)
                OnTrashSpawned(items[i]);
        }

        // ---------------------------------------------------------------- UI Toolkit Button Clicks

        void HookUiDocumentsInScene()
        {
            UIDocument[] docs = FindObjectsByType<UIDocument>();
            for (int i = 0; i < docs.Length; i++)
            {
                UIDocument doc = docs[i];
                if (doc == null || doc.rootVisualElement == null)
                    continue;

                if (!hookedDocuments.Add(doc))
                    continue;

                doc.rootVisualElement.RegisterCallback<PointerDownEvent>(OnUiPointerDown, TrickleDown.TrickleDown);
            }
        }

        void OnUiPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0)
                return;

            var current = evt.target as VisualElement;
            while (current != null)
            {
                if (current is Button btn && btn.enabledInHierarchy)
                {
                    // Skip Buy button click sound when SpendCoins handles purchase audio cleanly
                    if (!btn.ClassListContains("shop-buy-button"))
                        SoundManager.Play(SoundId.UiClick);
                    return;
                }
                current = current.parent;
            }
        }
    }
}
