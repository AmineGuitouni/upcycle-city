using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Physics-based object grabber: holds strictly ONE <see cref="IGrabbable"/> at a time using
    /// velocity-tracked spring physics so grabbed objects collide naturally with the world and can
    /// be gently dropped or charge-thrown into sorting bins, hoppers, or workbenches.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Physics Grab Controller")]
    public sealed class PhysicsGrabController : MonoBehaviour
    {
        const string HeldObjectModifierSource = "HeldObject";

        [Tooltip("Solid world layers that stop held objects from clipping through walls or stations.")]
        [SerializeField] LayerMask obstacleMask;

        PlayerConfig config;
        PlayerMotor motor;
        PlayerInventory inventory;
        PlayerModifiers modifiers;
        Transform cameraTransform;

        IGrabbable heldObject;
        bool chargingThrow;
        float chargeTimer;

        public IGrabbable HeldObject => heldObject;
        public bool IsHolding => heldObject != null && heldObject.Transform != null && heldObject.Body != null;
        public bool IsChargingThrow => chargingThrow && IsHolding;
        public float ThrowChargeNormalized => config != null && config.maxChargeDuration > 0f
            ? Mathf.Clamp01(chargeTimer / config.maxChargeDuration)
            : 0f;

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

        void Reset()
        {
            obstacleMask = GameLayers.ObstacleMask;
        }

        public void SetObstacleMask(LayerMask mask) => obstacleMask = mask;

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

        public bool TryGrab(IGrabbable grabbable)
        {
            if (IsHolding || grabbable == null || grabbable.Transform == null || grabbable.Body == null)
                return false;

            if (!grabbable.CanGrab(this))
                return false;

            heldObject = grabbable;
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
            Vector3 direction = (cameraTransform.forward + Vector3.up * 0.16f).normalized;
            Vector3 playerCarryVel = motor != null && motor.Body != null ? motor.Body.linearVelocity * 0.35f : Vector3.zero;
            ReleaseInternal(direction * speed + playerCarryVel, thrown: true);
        }

        public void DropGently()
        {
            if (!IsHolding)
                return;

            Vector3 inherit = motor != null && motor.Body != null ? motor.Body.linearVelocity * 0.4f : Vector3.zero;
            ReleaseInternal(inherit + cameraTransform.forward * 1.1f, thrown: false);
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

        void FixedUpdate()
        {
            if (!IsHolding || cameraTransform == null || config == null)
                return;

            Rigidbody rb = heldObject.Body;
            if (rb == null)
            {
                ClearHeldReference();
                return;
            }

            Vector3 targetPos = ComputeSafeHoldPoint();
            Vector3 toTarget = targetPos - rb.worldCenterOfMass;
            float distance = toTarget.magnitude;

            // Auto-drop if wedged behind an obstacle too far from the player's hands
            if (distance > config.maxBreakDistance)
            {
                DropGently();
                return;
            }

            Vector3 desiredVel = toTarget * config.holdFollowStiffness;
            if (desiredVel.magnitude > config.maxHoldFollowSpeed)
                desiredVel = desiredVel.normalized * config.maxHoldFollowSpeed;

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
                rb.angularVelocity = axis.normalized * (angleDeg * Mathf.Deg2Rad * 8f);
            else
                rb.angularVelocity *= 0.6f;
        }

        Vector3 ComputeSafeHoldPoint()
        {
            float desiredDist = config.holdDistance + heldObject.HoldDistanceOffset;
            Vector3 origin = cameraTransform.position;
            Vector3 dir = cameraTransform.forward;

            // Solid-obstacle raycast keeps grabbed objects from being shoved through walls
            if (Physics.Raycast(origin, dir, out RaycastHit hit, desiredDist, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                if (!hit.transform.IsChildOf(heldObject.Transform) && !hit.transform.IsChildOf(transform))
                    desiredDist = Mathf.Max(0.55f, hit.distance - 0.25f);
            }

            float pullback = chargingThrow ? ThrowChargeNormalized * 0.22f : 0f;
            return origin + dir * Mathf.Max(0.45f, desiredDist - pullback) + cameraTransform.up * config.holdVerticalOffset;
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
            chargingThrow = false;
            chargeTimer = 0f;
            ApplyCarryConstraints();
        }
    }
}
