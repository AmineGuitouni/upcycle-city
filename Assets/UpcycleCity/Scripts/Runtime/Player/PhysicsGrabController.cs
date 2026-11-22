using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;

namespace UpcycleCity.Player
{
    /// <summary>How a piece of litter is being picked up.</summary>
    public enum PickupStyle
    {
        None = 0,
        /// <summary>Bending down and grabbing it by hand: slow, and the player stands still.</summary>
        Hand = 1,
        /// <summary>Snapping it up with a litter picker's jaws: quick, and the player keeps moving.</summary>
        Picker = 2,
        /// <summary>
        /// A tool (broom, wrench…) is in hand: no bending down — the free hand reaches out and scoops the litter up
        /// while the player stays upright and can keep walking.
        /// </summary>
        ToolReach = 3,
    }

    /// <summary>Something that says where held trash should sit, e.g. the litter picker's claw.</summary>
    public interface IHoldPointProvider
    {
        bool TryGetHoldPoint(out Vector3 worldPoint);
    }

    /// <summary>
    /// Physics-based object grabber: holds strictly ONE <see cref="IGrabbable"/> at a time using
    /// velocity-tracked spring physics so grabbed objects collide naturally with the world and can
    /// be gently dropped or charge-thrown into sorting bins, hoppers, or workbenches.
    /// <para>
    /// Picking something up takes time (<see cref="BeginPickup"/>). By hand the player bends down, is rooted for
    /// <see cref="PlayerConfig.handPickupDuration"/> and the hand closes on the item part-way through. With a claw tool
    /// (<see cref="IInventoryItem.IsGrabTool"/>) the jaws snap shut in <see cref="PlayerConfig.pickerPickupDuration"/>
    /// while the player keeps walking, and the item is held at the claw. Bulky items always need hands.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Physics Grab Controller")]
    public sealed class PhysicsGrabController : MonoBehaviour
    {
        const string HeldObjectModifierSource = "HeldObject";
        const string PickupModifierSource = "Pickup";
        const float PickerGrabPoint = 0.55f;

        [Tooltip("Solid world layers that stop held objects from clipping through walls or stations.")]
        [SerializeField] LayerMask obstacleMask;

        PlayerConfig config;
        PlayerMotor motor;
        PlayerInventory inventory;
        PlayerModifiers modifiers;
        Transform cameraTransform;
        IHoldPointProvider clawProvider;

        IGrabbable heldObject;
        bool heldWithPicker;
        bool chargingThrow;
        float chargeTimer;

        IGrabbable pickupTarget;
        PickupStyle pickupStyle;
        float pickupTimer;
        float pickupDuration;
        float pickupGrabTime;

        static bool IsAlive(IGrabbable grabbable)
        {
            if (grabbable == null)
                return false;
            if (grabbable is UnityEngine.Object unityObj && unityObj == null)
                return false;
            return grabbable.Transform != null && grabbable.Body != null;
        }

        public IGrabbable HeldObject => IsAlive(heldObject) ? heldObject : null;
        public bool IsHolding => IsAlive(heldObject);
        public bool IsChargingThrow => chargingThrow && IsHolding;
        public float ThrowChargeNormalized => config != null && config.maxChargeDuration > 0f
            ? Mathf.Clamp01(chargeTimer / config.maxChargeDuration)
            : 0f;

        /// <summary>True while the held item rides in a claw tool's jaws rather than in the hands.</summary>
        public bool HeldWithPicker => heldWithPicker && IsHolding;

        /// <summary>A claw tool (litter picker) is selected in the hotbar.</summary>
        public bool PickerEquipped => inventory != null && inventory.EquippedItem != null && inventory.EquippedItem.IsGrabTool;

        /// <summary>A non-claw tool is selected in the hotbar: pickups are a standing hand reach instead of bending down.</summary>
        public bool ToolEquipped => inventory != null && inventory.EquippedItem != null
            && inventory.EquippedItem.Category == ItemCategory.Tool && !inventory.EquippedItem.IsGrabTool;

        /// <summary>
        /// 0..1 reach of the free hand during a <see cref="PickupStyle.ToolReach"/> pickup: out to the litter by the
        /// moment it is taken, then back in.
        /// </summary>
        public float ToolReachAmount
        {
            get
            {
                if (pickupStyle != PickupStyle.ToolReach)
                    return 0f;
                float t = PickupProgress, g = Mathf.Clamp(PickupGrabPoint, 0.05f, 0.95f);
                return t < g ? Smooth(t / g) : 1f - Smooth((t - g) / (1f - g));
            }
        }

        public bool IsPickingUp => pickupStyle != PickupStyle.None;
        public PickupStyle CurrentPickup => pickupStyle;
        public IGrabbable PickupTarget => pickupTarget;
        /// <summary>0..1 through the current pickup.</summary>
        public float PickupProgress => pickupDuration > 0f ? Mathf.Clamp01(pickupTimer / pickupDuration) : 0f;
        /// <summary>0..1 point of the pickup at which the item is actually taken.</summary>
        public float PickupGrabPoint => pickupDuration > 0f ? pickupGrabTime / pickupDuration : 0f;

        /// <summary>
        /// How far the player is bent over during a hand pickup: eases down to 1 when the hand closes on the item,
        /// then back up to 0 as they straighten with it.
        /// </summary>
        public float HandPickupBend
        {
            get
            {
                if (pickupStyle != PickupStyle.Hand)
                    return 0f;
                float t = PickupProgress, g = Mathf.Clamp(PickupGrabPoint, 0.05f, 0.95f);
                return t < g ? Smooth(t / g) : 1f - Smooth((t - g) / (1f - g));
            }
        }

        /// <summary>Current grab reach including all active modifier bonuses (tools, upgrades, buffs).</summary>
        public float EffectiveGrabReach
        {
            get
            {
                float baseReach = config != null ? config.baseGrabReach : 2.8f;
                float modBonus = modifiers != null
                    ? modifiers.GetAdditive(PlayerStat.GrabReachBonus)
                    : (inventory != null && inventory.EquippedItem != null ? inventory.EquippedItem.GrabReachBonus : 0f);
                return baseReach + modBonus;
            }
        }

        public event Action<IGrabbable> Grabbed;
        public event Action<IGrabbable, bool> Released;
        /// <summary>A timed pickup started (target, style). <see cref="Grabbed"/> follows when the hand/jaws close.</summary>
        public event Action<IGrabbable, PickupStyle> PickupStarted;

        void Reset()
        {
            obstacleMask = GameLayers.ObstacleMask;
        }

        public void SetObstacleMask(LayerMask mask) => obstacleMask = mask;

        /// <summary>Where picker-held trash should sit (the litter picker's claw); null falls back to a fixed offset.</summary>
        public void SetClawProvider(IHoldPointProvider provider) => clawProvider = provider;

        public void Initialize(
            PlayerConfig playerConfig,
            PlayerMotor playerMotor,
            PlayerInventory playerInventory,
            Transform camTransform,
            PlayerModifiers playerModifiers = null)
        {
            config = playerConfig;
            motor = playerMotor;
            inventory = playerInventory;
            modifiers = playerModifiers;
            cameraTransform = camTransform;

            if (obstacleMask.value == 0 || obstacleMask.value == ~0)
                obstacleMask = GameLayers.ObstacleMask;
        }

        void OnDisable() => CancelPickup();

        // ---------------------------------------------------------------- picking up

        /// <summary>
        /// Starts picking <paramref name="grabbable"/> up: by hand (slow, rooted) or with the equipped litter picker
        /// (quick, on the move). Returns false if it can't be picked up right now.
        /// </summary>
        public bool BeginPickup(IGrabbable grabbable)
        {
            if (config == null || IsHolding || IsPickingUp || grabbable == null || grabbable.Transform == null
                || grabbable.Body == null || !grabbable.CanGrab(this))
                return false;

            bool picker = PickerEquipped && !grabbable.IsBulky;
            bool toolReach = !picker && ToolEquipped && !grabbable.IsBulky;
            pickupStyle = picker ? PickupStyle.Picker : toolReach ? PickupStyle.ToolReach : PickupStyle.Hand;
            pickupTarget = grabbable;
            pickupTimer = 0f;
            pickupDuration = picker ? config.pickerPickupDuration
                : toolReach ? config.toolReachPickupDuration
                : config.handPickupDuration;
            pickupGrabTime = pickupDuration * (picker ? PickerGrabPoint : config.handPickupGrabPoint);

            if (toolReach && modifiers != null)
                modifiers.SetFlag(PickupModifierSource, PlayerModifierFlag.BlockSprint, true);
            else if (!picker && modifiers != null)
            {
                modifiers.SetFlag(PickupModifierSource, PlayerModifierFlag.BlockMove, true);
                modifiers.SetFlag(PickupModifierSource, PlayerModifierFlag.BlockJump, true);
                modifiers.SetFlag(PickupModifierSource, PlayerModifierFlag.BlockSprint, true);
            }

            if (grabbable.Transform.TryGetComponent(out INetworkedPhysicsGrabbable netGrabbable))
                netGrabbable.OnLocalPickupStarted();

            PickupStarted?.Invoke(grabbable, pickupStyle);
            return true;
        }

        /// <summary>Call when the hotbar selection changes: putting the picker away moves its trash into the hands.</summary>
        public void SyncGripWithEquipped()
        {
            if (heldWithPicker && !PickerEquipped)
                heldWithPicker = false;
        }

        /// <summary>Abandons a pickup in progress (the item rolled away, was taken, the player was interrupted).</summary>
        public void CancelPickup()
        {
            if (pickupStyle == PickupStyle.None)
                return;
            if (IsAlive(pickupTarget)
                && (!IsHolding || !ReferenceEquals(heldObject, pickupTarget))
                && pickupTarget.Transform.TryGetComponent(out INetworkedPhysicsGrabbable netGrabbable))
            {
                netGrabbable.OnLocalPickupCancelled();
            }
            EndPickup();
        }

        void EndPickup()
        {
            pickupStyle = PickupStyle.None;
            pickupTarget = null;
            pickupTimer = 0f;
            modifiers?.RemoveAllFromSource(PickupModifierSource);
        }

        void Update()
        {
            if (heldObject != null && !IsAlive(heldObject))
                ClearHeldReference();

            if (pickupStyle == PickupStyle.None)
                return;

            pickupTimer += Time.deltaTime;

            bool taken = IsHolding && heldObject == pickupTarget;
            if (!taken)
            {
                if (!PickupTargetStillValid())
                {
                    CancelPickup();
                    return;
                }
                if (pickupTimer >= pickupGrabTime && !GrabNow(pickupTarget, pickupStyle == PickupStyle.Picker))
                {
                    CancelPickup();
                    return;
                }
            }

            if (pickupTimer >= pickupDuration)
                EndPickup();
        }

        bool PickupTargetStillValid()
        {
            if (!IsAlive(pickupTarget) || !pickupTarget.CanGrab(this))
                return false;
            if (cameraTransform == null)
                return true;
            float reach = EffectiveGrabReach + 0.75f;
            return (pickupTarget.Transform.position - cameraTransform.position).sqrMagnitude <= reach * reach;
        }

        static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // ---------------------------------------------------------------- holding

        /// <summary>Grabs instantly with no pickup motion (stations and scripted hand-overs).</summary>
        public bool TryGrab(IGrabbable grabbable)
        {
            if (IsPickingUp)
                CancelPickup();
            return GrabNow(grabbable, grabbable != null && PickerEquipped && !grabbable.IsBulky);
        }

        bool GrabNow(IGrabbable grabbable, bool withPicker)
        {
            if (IsHolding || !IsAlive(grabbable))
                return false;

            if (!grabbable.CanGrab(this))
                return false;

            heldObject = grabbable;
            heldWithPicker = withPicker;
            chargingThrow = false;
            chargeTimer = 0f;

            grabbable.SetIgnoreCollision(motor != null ? motor.Capsule : null, true);
            grabbable.OnGrabbed(this);
            ApplyCarryConstraints();

            Grabbed?.Invoke(grabbable);
            return true;
        }

        public void BeginChargeThrow()
        {
            if (!IsHolding)
                return;
            chargingThrow = true;
            chargeTimer = 0f;
        }

        public void TickCharge(float dt)
        {
            if (!IsHolding)
            {
                ClearHeldReference();
                return;
            }

            if (chargingThrow)
                chargeTimer = Mathf.Min(chargeTimer + dt, config.maxChargeDuration);
        }

        public void ReleaseThrow()
        {
            if (!IsHolding)
                return;

            float t = ThrowChargeNormalized;
            float throwBonus = modifiers != null
                ? modifiers.GetAdditive(PlayerStat.ThrowForceBonus)
                : (inventory != null && inventory.EquippedItem != null ? inventory.EquippedItem.ThrowForceBonus : 0f);
            float speed = Mathf.Lerp(config.minThrowSpeed, config.maxThrowSpeed, t) + throwBonus;

            // Slight upward arc makes tossing bottles and cans into crates feel great
            Vector3 direction = (cameraTransform.forward + Vector3.up * config.throwUpwardArc).normalized;
            Vector3 playerCarryVel = motor != null && motor.Body != null ? motor.Body.linearVelocity * config.throwVelocityInheritance : Vector3.zero;
            ReleaseInternal(direction * speed + playerCarryVel, thrown: true);
        }

        public void DropGently()
        {
            if (!IsHolding)
                return;

            float inheritFactor = config != null ? config.throwVelocityInheritance : 0.35f;
            float forwardPush = config != null ? config.dropForwardImpulse : 1.1f;
            Vector3 inherit = motor != null && motor.Body != null ? motor.Body.linearVelocity * inheritFactor : Vector3.zero;
            ReleaseInternal(inherit + cameraTransform.forward * forwardPush, thrown: false);
        }

        /// <summary>Releases ownership cleanly so a SortingCrate/station can consume or accept the held object immediately.</summary>
        public IGrabbable ReleaseForDeposit()
        {
            if (!IsHolding)
                return null;

            IGrabbable item = heldObject;
            item.SetIgnoreCollision(motor != null ? motor.Capsule : null, false);
            item.OnReleased(this, Vector3.zero, thrown: false);
            ClearHeldReference();
            Released?.Invoke(item, false);
            return item;
        }

        /// <summary>Forcefully drops <paramref name="item"/> without applying release impulse (e.g. on despawn or if another player won a grab race).</summary>
        public void ForceReleaseIfHolding(IGrabbable item)
        {
            if (item == null)
                return;
            if (IsPickingUp && ReferenceEquals(pickupTarget, item))
                EndPickup();
            if (!ReferenceEquals(heldObject, item))
                return;

            if (IsAlive(item))
                item.SetIgnoreCollision(motor != null ? motor.Capsule : null, false);
            ClearHeldReference();
            Released?.Invoke(item, false);
        }

        void FixedUpdate()
        {
            if (heldObject != null && !IsAlive(heldObject))
            {
                ClearHeldReference();
                return;
            }
            if (!IsHolding || cameraTransform == null || config == null)
                return;

            Rigidbody rb = heldObject.Body;
            if (rb == null)
            {
                ClearHeldReference();
                return;
            }

            // While waiting a frame for host ownership transfer in co-op, skip velocity writes on a kinematic body
            if (rb.isKinematic)
                return;

            Vector3 targetPos = ComputeSafeHoldPoint();
            Vector3 toTarget = targetPos - rb.worldCenterOfMass;
            float distance = toTarget.magnitude;

            // Auto-drop if wedged behind an obstacle too far from the player's hands
            if (distance > config.maxBreakDistance)
            {
                DropGently();
                return;
            }

            // The picker's jaws grip tighter than hands carry, so trash stays in the claw while walking and turning
            float stiffness = config.holdFollowStiffness * (heldWithPicker ? config.pickerHoldStiffness : 1f);
            float maxSpeed = config.maxHoldFollowSpeed * (heldWithPicker ? 1.6f : 1f);
            Vector3 desiredVel = toTarget * stiffness;
            if (desiredVel.magnitude > maxSpeed)
                desiredVel = desiredVel.normalized * maxSpeed;

            // Pull gently back while charging a throw for clear tactile anticipation
            if (chargingThrow)
                desiredVel -= cameraTransform.forward * (ThrowChargeNormalized * 2.5f);

            rb.linearVelocity = desiredVel;

            // Smoothly align yaw with camera while keeping upright
            Quaternion targetRot = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
            Quaternion deltaRot = targetRot * Quaternion.Inverse(rb.rotation);
            deltaRot.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) angleDeg -= 360f;
            if (Mathf.Abs(angleDeg) > 0.5f && axis.sqrMagnitude > 1e-4f)
                rb.angularVelocity = axis.normalized * (angleDeg * Mathf.Deg2Rad * config.heldRotationStiffness);
            else
                rb.angularVelocity *= 0.6f;
        }

        // Fallback claw position (camera space) when no picker view model reports where its jaws are
        const float PickerHoldDistance = 1.05f;
        const float PickerHoldRight = 0.33f;
        const float PickerHoldDown = -0.29f;

        Vector3 ComputeSafeHoldPoint()
        {
            Vector3 origin = cameraTransform.position;

            if (heldWithPicker && clawProvider != null && clawProvider.TryGetHoldPoint(out Vector3 claw))
            {
                // Held in the jaws; if a wall is between the eyes and the claw, keep the item on this side of it
                Vector3 toClaw = claw - origin;
                float clawDist = toClaw.magnitude;
                if (clawDist > 1e-3f && Physics.Raycast(origin, toClaw / clawDist, out RaycastHit wall, clawDist, obstacleMask, QueryTriggerInteraction.Ignore)
                    && !wall.transform.IsChildOf(heldObject.Transform) && !wall.transform.IsChildOf(transform))
                    claw = origin + toClaw / clawDist * Mathf.Max(0.35f, wall.distance - 0.15f);
                return claw;
            }

            float desiredDist = (heldWithPicker ? PickerHoldDistance : config.holdDistance) + heldObject.HoldDistanceOffset;
            Vector3 dir = cameraTransform.forward;

            // Solid-obstacle raycast keeps grabbed objects from being shoved through walls
            if (Physics.Raycast(origin, dir, out RaycastHit hit, desiredDist, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                if (!hit.transform.IsChildOf(heldObject.Transform) && !hit.transform.IsChildOf(transform))
                    desiredDist = Mathf.Max(0.55f, hit.distance - 0.25f);
            }

            float pullback = chargingThrow ? ThrowChargeNormalized * 0.22f : 0f;
            Vector3 point = origin + dir * Mathf.Max(0.45f, desiredDist - pullback) + cameraTransform.up * config.holdVerticalOffset;
            if (heldWithPicker)
                point += cameraTransform.right * PickerHoldRight + cameraTransform.up * PickerHoldDown;
            return point;
        }

        void ReleaseInternal(Vector3 releaseVelocity, bool thrown)
        {
            IGrabbable item = heldObject;
            item.SetIgnoreCollision(motor != null ? motor.Capsule : null, false);
            item.OnReleased(this, releaseVelocity, thrown);
            ClearHeldReference();
            Released?.Invoke(item, thrown);
        }

        void ApplyCarryConstraints()
        {
            if (modifiers == null)
                return;

            if (IsHolding)
            {
                modifiers.SetMultiplier(HeldObjectModifierSource, PlayerStat.MoveSpeedMultiplier, heldObject.CarrySpeedMultiplier);
                modifiers.SetFlag(HeldObjectModifierSource, PlayerModifierFlag.BlockSprint, heldObject.IsBulky);
            }
            else
            {
                modifiers.RemoveAllFromSource(HeldObjectModifierSource);
            }
        }

        void ClearHeldReference()
        {
            heldObject = null;
            heldWithPicker = false;
            chargingThrow = false;
            chargeTimer = 0f;
            ApplyCarryConstraints();
        }
    }
}
