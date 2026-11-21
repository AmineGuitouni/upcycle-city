using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
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
    /// grabbing/throwing, sorting crates, RÉCUP buy-back, shops, tools, bins, trash physics impacts, and UI buttons)
    /// to <see cref="SoundManager"/> without modifying any gameplay script.
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

        // Frame-level deduplication flags so compound actions play one rich sound instead of stacking duplicates
        int lastDedicatedInteractFrame = -1;
        int lastCoinSoundFrame = -1;
        int lastSpendCoinsFrame = -1;
        int lastSortSoundFrame = -1;
        int lastToolSoundFrame = -1;

        readonly HashSet<UIDocument> hookedDocuments = new HashSet<UIDocument>();

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

            boundPlayer = null;
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
                SoundManager.PlayAt(stepCue, boundPlayer.transform.position, volScale);
            }
        }

        // ---------------------------------------------------------------- Player Feedback Hub Events

        void OnPlayerFeedback(FeedbackSignal signal)
        {
            switch (signal.Event)
            {
                case PlayerFeedbackEvent.Jump:
                    SoundManager.Play(SoundId.Jump, Mathf.Lerp(0.7f, 1f, signal.Intensity));
                    break;

                case PlayerFeedbackEvent.Land:
                    SoundManager.PlayAt(SoundId.Land, signal.WorldPosition, Mathf.Lerp(0.42f, 1f, signal.Intensity));
                    break;

                case PlayerFeedbackEvent.Footstep:
                    SoundManager.PlayAt(SoundId.FootstepWalk, signal.WorldPosition, signal.Intensity);
                    break;

                case PlayerFeedbackEvent.PickupItem:
                    // When buying in ShopUI, CoinsChanged already plays SpendCoins on this same frame
                    if (lastSpendCoinsFrame != Time.frameCount)
                        SoundManager.PlayAt(SoundId.PickupItem, signal.WorldPosition, Mathf.Lerp(0.75f, 1f, signal.Intensity));
                    break;

                case PlayerFeedbackEvent.PickupCoins:
                    if (lastCoinSoundFrame != Time.frameCount)
                    {
                        lastCoinSoundFrame = Time.frameCount;
                        SoundManager.PlayAt(SoundId.EarnCoins, signal.WorldPosition, signal.Intensity);
                    }
                    break;

                case PlayerFeedbackEvent.InventoryFull:
                    SoundManager.Play(SoundId.InventoryFull);
                    break;

                case PlayerFeedbackEvent.SortCorrect:
                    if (lastSortSoundFrame != Time.frameCount)
                    {
                        lastSortSoundFrame = Time.frameCount;
                        SoundManager.PlayAt(SoundId.SortCorrect, signal.WorldPosition);
                    }
                    break;

                case PlayerFeedbackEvent.SortWrong:
                    if (lastSortSoundFrame != Time.frameCount)
                    {
                        lastSortSoundFrame = Time.frameCount;
                        SoundManager.PlayAt(SoundId.SortWrong, signal.WorldPosition);
                    }
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
            if (lastToolSoundFrame == Time.frameCount)
                return;
            lastToolSoundFrame = Time.frameCount;

            IInventoryItem equipped = boundPlayer != null && boundPlayer.Inventory != null
                ? boundPlayer.Inventory.EquippedItem
                : null;

            string label = signal.Label ?? string.Empty;

            if (equipped != null && equipped.UseBehavior is SweepAreaItemBehavior
                || label.IndexOf("Swept", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("Broom", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SoundManager.PlayAt(SoundId.ToolBroomSweep, signal.WorldPosition, Mathf.Lerp(0.65f, 1f, signal.Intensity));
                return;
            }

            if ((equipped != null && string.Equals(equipped.ItemId, "tool_wrench", StringComparison.OrdinalIgnoreCase))
                || label.IndexOf("Wrench", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("Scrubber", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("Smokestack", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SoundManager.PlayAt(SoundId.ToolWrenchRepair, signal.WorldPosition);
                return;
            }

            if (equipped != null && equipped.UseBehavior is TimedBoostItemBehavior)
            {
                SoundManager.Play(SoundId.PickupItem, 0.9f, 1.12f);
                return;
            }

            SoundManager.PlayAt(SoundId.ToolUseGeneric, signal.WorldPosition, signal.Intensity);
        }

        void HandleInteractSound(in FeedbackSignal signal)
        {
            if (lastDedicatedInteractFrame == Time.frameCount)
                return;

            string label = signal.Label ?? string.Empty;
            if (label.IndexOf("Serviced", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("Cleared overflowing", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                SoundManager.PlayAt(SoundId.BinService, signal.WorldPosition);
                return;
            }

            IInteractable hovered = boundPlayer != null && boundPlayer.Interactor != null
                ? boundPlayer.Interactor.HoveredInteractable
                : null;

            if (hovered is SortingCrate || hovered is RecyclingBuyBack)
                return;

            if (hovered is OverflowingBinSource)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                SoundManager.PlayAt(SoundId.BinService, signal.WorldPosition);
                return;
            }

            if (hovered is ShoppingCart)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                SoundManager.PlayAt(SoundId.CartGrab, signal.WorldPosition);
                return;
            }

            if (hovered is ShopCounter)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                SoundManager.Play(SoundId.UiClick, 0.9f, 0.96f);
                return;
            }

            if (hovered is FactorySmokestackSource factory)
            {
                lastDedicatedInteractFrame = Time.frameCount;
                SoundManager.PlayAt(factory.IsScrubberActive ? SoundId.ToolWrenchRepair : SoundId.AttentionAlert, signal.WorldPosition);
                return;
            }

            SoundManager.PlayAt(SoundId.Interact, signal.WorldPosition, signal.Intensity);
        }

        // ---------------------------------------------------------------- Grab, Throw, Drop & Inventory

        void OnPickupStarted(IGrabbable grabbable, PickupStyle style)
        {
            if (style == PickupStyle.Picker)
                SoundManager.Play(SoundId.LitterPickerSnap);
        }

        void OnObjectGrabbed(IGrabbable grabbable)
        {
            Vector3 pos = grabbable != null && grabbable.Transform != null
                ? grabbable.Transform.position
                : (boundPlayer != null ? boundPlayer.transform.position : Vector3.zero);

            if (grabbable is TrashItem trash)
            {
                SoundId matSound = TrashImpactAudio.GrabSoundForStream(trash.Stream, trash.IsBulky);
                SoundManager.PlayAt(matSound, pos, 0.95f);
                SoundManager.Play(SoundId.GrabGeneric, 0.40f, trash.IsBulky ? 0.85f : 1.05f);
            }
            else
            {
                SoundManager.PlayAt(SoundId.GrabGeneric, pos);
            }
        }

        void OnObjectReleased(IGrabbable grabbable, bool thrown)
        {
            // Skip release sound if it was immediately consumed by a deposit (SortingCrate/BuyBack) on the same frame
            if (lastDedicatedInteractFrame == Time.frameCount)
                return;

            bool bulky = grabbable != null && grabbable.IsBulky;
            if (thrown)
                SoundManager.Play(SoundId.Throw, bulky ? 1.0f : 0.85f, bulky ? 0.86f : 1.04f);
            else
                SoundManager.Play(SoundId.Drop, 0.75f, bulky ? 0.85f : 1.0f);
        }

        void OnCoinsChanged(int totalCoins, int delta)
        {
            if (delta < 0)
            {
                lastSpendCoinsFrame = Time.frameCount;
                SoundManager.Play(SoundId.SpendCoins);
            }
            else if (delta > 0 && lastCoinSoundFrame != Time.frameCount)
            {
                lastCoinSoundFrame = Time.frameCount;
                SoundManager.Play(SoundId.EarnCoins);
            }
        }

        void OnSelectedSlotChanged(int index, InventorySlot slot)
        {
            // Ignore initial inventory setup right when player spawns
            if (Time.unscaledTime - playerBoundTime < 0.35f)
                return;
            SoundManager.Play(SoundId.HotbarSwitch);
        }

        void OnItemUsed(IInventoryItem item)
        {
            if (item == null || lastToolSoundFrame == Time.frameCount)
                return;

            if (item.UseBehavior is TimedBoostItemBehavior)
            {
                lastToolSoundFrame = Time.frameCount;
                SoundManager.Play(SoundId.PickupItem, 0.9f, 1.12f);
            }
        }

        // ---------------------------------------------------------------- World Sorting & Economy Events

        void OnItemSorted(SortingCrate crate, TrashItem item, bool match, int payout)
        {
            lastDedicatedInteractFrame = Time.frameCount;
            lastSortSoundFrame = Time.frameCount;
            if (payout > 0)
                lastCoinSoundFrame = Time.frameCount;

            Vector3 pos = crate != null ? crate.transform.position : Vector3.zero;
            if (match)
            {
                SoundManager.PlayAt(SoundId.SortCorrect, pos);
                if (item != null)
                    SoundManager.PlayAt(TrashImpactAudio.ImpactSoundForStream(item.Stream, item.IsBulky), pos, 0.65f, 1.05f);
            }
            else
            {
                SoundManager.PlayAt(SoundId.SortWrong, pos);
            }
        }

        void OnItemBought(RecyclingBuyBack buyBack, TrashItem item, int price)
        {
            lastDedicatedInteractFrame = Time.frameCount;
            lastCoinSoundFrame = Time.frameCount;

            Vector3 pos = buyBack != null ? buyBack.transform.position : Vector3.zero;
            SoundManager.PlayAt(SoundId.SellTrash, pos);
            if (item != null)
                SoundManager.PlayAt(TrashImpactAudio.ImpactSoundForStream(item.Stream, item.IsBulky), pos, 0.55f);
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
