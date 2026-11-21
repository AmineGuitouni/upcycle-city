using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Inventory;
using UpcycleCity.Player;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Unified center-screen raycast &amp; action router built strictly on interfaces:
    /// - <see cref="IGrabbable"/>   -&gt; PhysicsGrabController (1 at a time, never enters inventory)
    /// - <see cref="IPickupItem"/>  -&gt; PlayerInventory (tools, usables, coins)
    /// - <see cref="IInteractable"/>-&gt; World stations, bins, machines, buttons
    /// - <see cref="IToolTarget"/>  -&gt; Targets affected by equipped tools/usables
    /// Automatically drives <see cref="FocusHighlight"/> on targeted objects and emits feedback signals.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Player Interactor")]
    public sealed class PlayerInteractor : MonoBehaviour
    {
        [Tooltip("Layers eligible for interaction + Environment occluders so players cannot grab through walls.")]
        [SerializeField] LayerMask interactMask;

        PlayerConfig config;
        PlayerInputReader input;
        PlayerInventory inventory;
        PhysicsGrabController grabber;
        PlayerModifiers modifiers;
        PlayerFeedbackHub feedback;
        Transform cameraTransform;

        IGrabbable hoveredGrabbable;
        IPickupItem hoveredPickup;
        IInteractable hoveredInteractable;
        IToolTarget hoveredToolTarget;
        FocusHighlight activeHighlight;

        public PlayerInventory Inventory => inventory;
        public PhysicsGrabController Grabber => grabber;
        public PlayerModifiers Modifiers => modifiers;
        public PlayerFeedbackHub Feedback => feedback;
        public IGrabbable HoveredGrabbable => hoveredGrabbable;
        public IPickupItem HoveredPickup => hoveredPickup;
        public IInteractable HoveredInteractable => hoveredInteractable;
        public IToolTarget HoveredToolTarget => hoveredToolTarget;
        public string ActivePrompt { get; private set; } = string.Empty;

        public event Action<string, Color> FeedbackRequested;

        void Reset()
        {
            interactMask = GameLayers.InteractMask;
        }

        public void SetInteractMask(LayerMask mask) => interactMask = mask;

        public void Initialize(
            PlayerConfig playerConfig,
            PlayerInputReader inputReader,
            PlayerInventory playerInventory,
            PhysicsGrabController physicsGrabber,
            Transform camTransform,
            PlayerModifiers playerModifiers = null,
            PlayerFeedbackHub feedbackHub = null)
        {
            config = playerConfig;
            input = inputReader;
            inventory = playerInventory;
            grabber = physicsGrabber;
            modifiers = playerModifiers;
            feedback = feedbackHub;
            cameraTransform = camTransform;

            if (interactMask.value == 0 || interactMask.value == ~0)
                interactMask = GameLayers.InteractMask;
        }

        void OnDisable()
        {
            ClearHoverState();
        }

        public void NotifyFeedback(string message, Color color)
        {
            if (!string.IsNullOrEmpty(message))
                FeedbackRequested?.Invoke(message, color);
        }

        public void Tick(float dt)
        {
            if (config == null || input == null || cameraTransform == null)
                return;

            if (input.InputMode != PlayerInputMode.Gameplay)
            {
                ClearHoverState();
                ActivePrompt = string.Empty;
                return;
            }

            // Hotbar selection
            if (input.HotbarDirectSlot >= 0)
                inventory?.SelectSlot(input.HotbarDirectSlot);
            else if (input.HotbarScrollDelta != 0)
                inventory?.CycleSlot(input.HotbarScrollDelta);

            ScanTarget();
            HandleHeldObjectInput(dt);
            HandleInteractAndPickupInput();
            HandleUseItemInput();
            BuildPrompt();
        }

        void ClearHoverState()
        {
            UpdateFocus(ref hoveredInteractable, null, (x, i) => x.OnFocusEnter(i), (x, i) => x.OnFocusExit(i));
            UpdateFocus(ref hoveredPickup, null, (x, i) => x.OnFocusEnter(i), (x, i) => x.OnFocusExit(i));
            UpdateFocus(ref hoveredGrabbable, null, (x, i) => x.OnFocusEnter(i), (x, i) => x.OnFocusExit(i));
            hoveredToolTarget = null;
            SetActiveHighlight(null);
        }

        void ScanTarget()
        {
            IGrabbable nextGrabbable = null;
            IPickupItem nextPickup = null;
            IInteractable nextInteractable = null;
            IToolTarget nextToolTarget = null;

            float maxReach = Mathf.Max(config.interactionReach, grabber != null ? grabber.EffectiveGrabReach : config.baseGrabReach);
            var ray = new Ray(cameraTransform.position, cameraTransform.forward);

            if (Physics.Raycast(ray, out RaycastHit hit, maxReach, interactMask, QueryTriggerInteraction.Collide)
                && !hit.transform.IsChildOf(transform))
            {
                float dist = hit.distance;

                // 1. World interactable (works even while carrying an object, e.g. quick-deposit into a SortingCrate)
                IInteractable interactable = hit.collider.GetComponentInParent<IInteractable>();
                if (interactable != null && dist <= config.interactionReach && interactable.CanInteract(this))
                    nextInteractable = interactable;

                if (grabber == null || !grabber.IsHolding)
                {
                    // 2. Tool target if holding an eligible tool/usable
                    IInventoryItem equipped = inventory != null ? inventory.EquippedItem : null;
                    if (equipped != null && dist <= config.interactionReach)
                    {
                        IToolTarget toolTarget = hit.collider.GetComponentInParent<IToolTarget>();
                        if (toolTarget != null && toolTarget.CanUseTool(this, equipped))
                            nextToolTarget = toolTarget;
                    }

                    // 3. Inventory-eligible pickup (tools, usables, coins)
                    IPickupItem pickup = hit.collider.GetComponentInParent<IPickupItem>();
                    if (pickup != null && dist <= config.interactionReach && pickup.CanCollect(this))
                    {
                        nextPickup = pickup;
                    }
                    else if (grabber != null)
                    {
                        // 4. Physical grabbable (1 at a time, never enters inventory)
                        IGrabbable grabbable = hit.collider.GetComponentInParent<IGrabbable>();
                        if (grabbable != null && dist <= grabber.EffectiveGrabReach && grabbable.CanGrab(grabber))
                            nextGrabbable = grabbable;
                    }
                }
            }

            UpdateFocus(ref hoveredInteractable, nextInteractable,
                (x, i) => x.OnFocusEnter(i), (x, i) => x.OnFocusExit(i));
            UpdateFocus(ref hoveredPickup, nextPickup,
                (x, i) => x.OnFocusEnter(i), (x, i) => x.OnFocusExit(i));
            UpdateFocus(ref hoveredGrabbable, nextGrabbable,
                (x, i) => x.OnFocusEnter(i), (x, i) => x.OnFocusExit(i));
            hoveredToolTarget = nextToolTarget;

            // Resolve visual FocusHighlight on the highest-priority hovered target
            Transform focusedTransform = ResolvePrimaryFocusedTransform();
            FocusHighlight nextHighlight = focusedTransform != null
                ? focusedTransform.GetComponentInParent<FocusHighlight>()
                : null;
            SetActiveHighlight(nextHighlight);
        }

        Transform ResolvePrimaryFocusedTransform()
        {
            if (hoveredPickup != null && IsAlive(hoveredPickup))
                return hoveredPickup.Transform;
            if (hoveredGrabbable != null && IsAlive(hoveredGrabbable))
                return hoveredGrabbable.Transform;
            if (hoveredInteractable != null && IsAlive(hoveredInteractable))
                return hoveredInteractable.Transform;
            return null;
        }

        void SetActiveHighlight(FocusHighlight next)
        {
            if (activeHighlight == next)
                return;

            if (activeHighlight != null)
                activeHighlight.SetHighlighted(false);

            activeHighlight = next;

            if (activeHighlight != null)
            {
                activeHighlight.SetHighlighted(true);
                feedback?.Emit(PlayerFeedbackEvent.FocusChanged, activeHighlight.transform.position, 0.25f);
            }
        }

        void UpdateFocus<T>(ref T current, T next, Action<T, PlayerInteractor> onEnter, Action<T, PlayerInteractor> onExit)
            where T : class
        {
            if (ReferenceEquals(current, next))
                return;

            if (current != null && IsAlive(current))
                onExit(current, this);

            current = next;

            if (current != null && IsAlive(current))
                onEnter(current, this);
        }

        static bool IsAlive(object target)
        {
            if (target is UnityEngine.Object unityObj)
                return unityObj != null;
            return target != null;
        }

        void HandleHeldObjectInput(float dt)
        {
            if (grabber == null)
                return;

            if (grabber.IsHolding)
            {
                grabber.TickCharge(dt);

                if (input.SecondaryPressedThisFrame)
                {
                    grabber.DropGently();
                    return;
                }

                if (input.PrimaryPressedThisFrame)
                    grabber.BeginChargeThrow();
                else if (input.PrimaryReleasedThisFrame && grabber.IsChargingThrow)
                    grabber.ReleaseThrow();
            }
            else if (input.PrimaryPressedThisFrame && hoveredGrabbable != null)
            {
                grabber.TryGrab(hoveredGrabbable);
            }
        }

        void HandleInteractAndPickupInput()
        {
            if (!input.InteractPressedThisFrame)
                return;

            if (hoveredPickup != null && hoveredPickup.CanCollect(this))
            {
                hoveredPickup.TryCollect(this);
                return;
            }

            if (hoveredInteractable != null && hoveredInteractable.CanInteract(this))
            {
                Vector3 pos = hoveredInteractable.Transform != null ? hoveredInteractable.Transform.position : transform.position;
                hoveredInteractable.Interact(this);
                feedback?.Emit(PlayerFeedbackEvent.Interact, pos, 0.7f);
                return;
            }

            // Pressing Interact on a physical grabbable grabs it via physics (never inventory!)
            if (grabber != null && !grabber.IsHolding && hoveredGrabbable != null)
                grabber.TryGrab(hoveredGrabbable);
        }

        void HandleUseItemInput()
        {
            if (inventory == null || (grabber != null && grabber.IsHolding))
                return;

            if (!input.UseItemPressedThisFrame)
                return;

            IInventoryItem equipped = inventory.EquippedItem;
            if (equipped == null)
                return;

            var context = new ItemUseContext(
                this,
                inventory,
                modifiers,
                feedback,
                cameraTransform,
                equipped,
                hoveredToolTarget);

            if (inventory.TryUseSelected(in context, out IInventoryItem used))
            {
                // Only emit generic toast if the item didn't have a custom behavior that already notified
                if (used.UseBehavior == null)
                {
                    NotifyFeedback($"Used {used.DisplayName}", used.AccentColor);
                    feedback?.Emit(PlayerFeedbackEvent.ToolUse, cameraTransform.position, 0.8f, used.DisplayName, used.AccentColor);
                }
            }
        }

        void BuildPrompt()
        {
            if (grabber != null && grabber.IsHolding)
            {
                string extra = hoveredInteractable != null ? $"  |  {hoveredInteractable.InteractionPrompt}" : string.Empty;
                string bulkyNote = grabber.HeldObject.IsBulky ? " (Heavy)" : string.Empty;
                ActivePrompt = $"Holding {grabber.HeldObject.DisplayName}{bulkyNote}  ·  [LMB Hold] Throw  |  [RMB] Drop{extra}";
                return;
            }

            if (hoveredPickup != null)
            {
                ActivePrompt = hoveredPickup.PickupPrompt;
                return;
            }

            if (hoveredToolTarget != null && inventory != null && inventory.EquippedItem != null)
            {
                ActivePrompt = hoveredToolTarget.GetToolPrompt(inventory.EquippedItem);
                return;
            }

            if (hoveredGrabbable != null)
            {
                ActivePrompt = hoveredGrabbable.GrabPrompt;
                return;
            }

            if (hoveredInteractable != null)
            {
                ActivePrompt = hoveredInteractable.InteractionPrompt;
                return;
            }

            ActivePrompt = string.Empty;
        }
    }
}
