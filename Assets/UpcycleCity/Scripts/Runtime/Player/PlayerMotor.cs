using System;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Arcade-feel Rigidbody locomotion core: crisp acceleration/deceleration, slope projection,
    /// coyote time, jump buffering, variable jump gravity, and ceiling-safe crouching.
    /// Driven by <see cref="PlayerStateMachine"/> states.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    [AddComponentMenu("Upcycle City/Player/Player Motor")]
    public sealed class PlayerMotor : MonoBehaviour
    {
        [Tooltip("Walkable world layers (excludes Player, HeldObject, TriggerZone, and Pickups).")]
        [SerializeField] LayerMask groundMask;

        Rigidbody body;
        CapsuleCollider capsule;
        PlayerConfig config;
        PlayerModifiers modifiers;

        float currentHeight;
        float currentEyeHeight;
        float coyoteTimer;
        float jumpBufferTimer;
        float jumpLockTimer;
        bool wasGrounded;

        public Rigidbody Body => body;
        public CapsuleCollider Capsule => capsule;
        public PlayerConfig Config => config;

        public bool IsGrounded { get; private set; }
        public Vector3 GroundNormal { get; private set; } = Vector3.up;
        public float SlopeAngle { get; private set; }
        public bool IsCrouching { get; private set; }
        public float CurrentEyeHeight => currentEyeHeight;

        /// <summary>Aggregated horizontal speed multiplier from PlayerModifiers (held objects, tools, smog, buffs).</summary>
        public float SpeedMultiplier => modifiers != null ? modifiers.GetMultiplier(PlayerStat.MoveSpeedMultiplier) : 1f;

        /// <summary>Aggregated jump velocity multiplier from PlayerModifiers.</summary>
        public float JumpMultiplier => modifiers != null ? modifiers.GetMultiplier(PlayerStat.JumpMultiplier) : 1f;

        /// <summary>True when any active modifier source blocks sprinting (e.g. bulky trash).</summary>
        public bool SprintBlocked => modifiers != null && modifiers.HasFlag(PlayerModifierFlag.BlockSprint);

        /// <summary>True when any active modifier source blocks jumping.</summary>
        public bool JumpBlocked => modifiers != null && modifiers.HasFlag(PlayerModifierFlag.BlockJump);

        public float HorizontalSpeed
        {
            get
            {
                Vector3 v = body != null ? body.linearVelocity : Vector3.zero;
                return Mathf.Sqrt(v.x * v.x + v.z * v.z);
            }
        }

        public float VerticalSpeed => body != null ? body.linearVelocity.y : 0f;
        public bool HasCoyoteTime => coyoteTimer > 0f;
        public bool HasBufferedJump => jumpBufferTimer > 0f && !JumpBlocked;

        public event Action<float> Landed;
        public event Action Jumped;

        void Reset()
        {
            groundMask = GameLayers.GroundMask;
        }

        public void SetGroundMask(LayerMask mask) => groundMask = mask;

        public void Initialize(PlayerConfig playerConfig, PlayerModifiers playerModifiers = null)
        {
            config = playerConfig;
            modifiers = playerModifiers ?? new PlayerModifiers();
            if (groundMask.value == 0 || groundMask.value == ~0)
                groundMask = GameLayers.GroundMask;

            body = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();

            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.linearDamping = 0f;
            body.angularDamping = 0f;

            // Frictionless collider prevents snagging on walls and slopes; braking is handled explicitly
            var slippery = new PhysicsMaterial("PlayerSlippery")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum,
            };
            capsule.sharedMaterial = slippery;

            currentHeight = config.standHeight;
            currentEyeHeight = config.standEyeHeight;
            ApplyCapsuleShape(currentHeight);
        }

        public void BufferJump()
        {
            if (config != null)
                jumpBufferTimer = config.jumpBufferTime;
        }

        public void TickTimers(float dt)
        {
            if (jumpBufferTimer > 0f)
                jumpBufferTimer = Mathf.Max(0f, jumpBufferTimer - dt);
            if (jumpLockTimer > 0f)
                jumpLockTimer = Mathf.Max(0f, jumpLockTimer - dt);
            if (IsGrounded)
                coyoteTimer = config.coyoteTime;
            else if (coyoteTimer > 0f)
                coyoteTimer = Mathf.Max(0f, coyoteTimer - dt);
        }

        public void ProbeGround()
        {
            wasGrounded = IsGrounded;
            if (jumpLockTimer > 0f)
            {
                IsGrounded = false;
                GroundNormal = Vector3.up;
                SlopeAngle = 0f;
                return;
            }

            float sphereRadius = Mathf.Max(0.05f, config.radius - 0.04f);
            Vector3 origin = transform.position + Vector3.up * (sphereRadius + 0.08f);
            float castDistance = 0.08f + config.groundProbeDistance;

            if (Physics.SphereCast(origin, sphereRadius, Vector3.down, out RaycastHit hit, castDistance, groundMask, QueryTriggerInteraction.Ignore))
            {
                // Ignore collisions against ourselves
                if (hit.collider != capsule)
                {
                    float angle = Vector3.Angle(hit.normal, Vector3.up);
                    if (angle <= config.maxSlopeAngle)
                    {
                        IsGrounded = true;
                        GroundNormal = hit.normal;
                        SlopeAngle = angle;

                        if (!wasGrounded)
                            Landed?.Invoke(Mathf.Abs(body.linearVelocity.y));
                        return;
                    }
                }
            }

            IsGrounded = false;
            GroundNormal = Vector3.up;
            SlopeAngle = 0f;
        }

        /// <summary>Checks whether the player has room overhead to stand up from crouch.</summary>
        public bool CanStandUp()
        {
            float checkRadius = Mathf.Max(0.05f, config.radius - 0.04f);
            float standTopY = config.standHeight - checkRadius;
            float crouchTopY = currentHeight - checkRadius;
            if (standTopY <= crouchTopY + 0.02f)
                return true;

            Vector3 low = transform.position + Vector3.up * (crouchTopY + 0.05f);
            Vector3 high = transform.position + Vector3.up * standTopY;
            Collider[] hits = Physics.OverlapCapsule(low, high, checkRadius, groundMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] != capsule && !hits[i].transform.IsChildOf(transform))
                    return false;
            }
            return true;
        }

        public void SetCrouch(bool wantCrouch, float dt)
        {
            bool targetCrouch = wantCrouch || !CanStandUp();
            IsCrouching = targetCrouch;

            float targetHeight = targetCrouch ? config.crouchHeight : config.standHeight;
            float targetEye = targetCrouch ? config.crouchEyeHeight : config.standEyeHeight;
            float blend = 1f - Mathf.Exp(-config.crouchLerpSpeed * dt);

            currentHeight = Mathf.Lerp(currentHeight, targetHeight, blend);
            currentEyeHeight = Mathf.Lerp(currentEyeHeight, targetEye, blend);
            ApplyCapsuleShape(currentHeight);
        }

        /// <summary>Converts 2D move input into world-space direction aligned with the player's facing yaw.</summary>
        public Vector3 GetWishDirection(Vector2 moveInput)
        {
            if (moveInput.sqrMagnitude < 1e-4f)
                return Vector3.zero;
            Vector3 wish = transform.right * moveInput.x + transform.forward * moveInput.y;
            wish.y = 0f;
            return wish.sqrMagnitude > 1e-4f ? wish.normalized : Vector3.zero;
        }

        /// <summary>
        /// Applies snappy arcade horizontal acceleration/deceleration using ForceMode.VelocityChange /
        /// ForceMode.Acceleration while respecting slope normals when grounded.
        /// </summary>
        public void MoveHorizontal(Vector3 wishDir, float targetSpeed, float accel, float decel, float dt)
        {
            float scaledSpeed = targetSpeed * SpeedMultiplier;
            Vector3 currentVel = body.linearVelocity;

            if (IsGrounded)
            {
                Vector3 slopeDir = wishDir.sqrMagnitude > 1e-4f
                    ? Vector3.ProjectOnPlane(wishDir, GroundNormal).normalized
                    : Vector3.zero;
                Vector3 desiredVel = slopeDir * scaledSpeed;

                Vector3 planarCurrent = new Vector3(currentVel.x, 0f, currentVel.z);
                Vector3 planarDesired = new Vector3(desiredVel.x, 0f, desiredVel.z);
                float rate = wishDir.sqrMagnitude > 1e-4f ? accel : decel;

                Vector3 nextPlanar = Vector3.MoveTowards(planarCurrent, planarDesired, rate * dt);
                float nextY = wishDir.sqrMagnitude > 1e-4f ? desiredVel.y : currentVel.y;

                // Gentle downward stick so walking down ramps stays planted
                if (jumpLockTimer <= 0f)
                    nextY = Mathf.Min(nextY, -config.groundStickForce * dt);

                body.linearVelocity = new Vector3(nextPlanar.x, nextY, nextPlanar.z);
            }
            else
            {
                Vector3 planarCurrent = new Vector3(currentVel.x, 0f, currentVel.z);
                Vector3 planarDesired = wishDir * scaledSpeed;
                float rate = wishDir.sqrMagnitude > 1e-4f ? accel : decel;

                Vector3 nextPlanar = Vector3.MoveTowards(planarCurrent, planarDesired, rate * dt);
                body.linearVelocity = new Vector3(nextPlanar.x, currentVel.y, nextPlanar.z);
            }
        }

        public void LaunchJump()
        {
            coyoteTimer = 0f;
            jumpBufferTimer = 0f;
            jumpLockTimer = 0.12f;
            IsGrounded = false;

            Vector3 v = body.linearVelocity;
            v.y = config.jumpVelocity * JumpMultiplier;
            body.linearVelocity = v;
            Jumped?.Invoke();
        }

        public void CutJumpVelocity()
        {
            Vector3 v = body.linearVelocity;
            if (v.y > 0f)
            {
                v.y *= config.jumpCutMultiplier;
                body.linearVelocity = v;
            }
        }

        public void ApplyGravity(float dt, float multiplier = 1f)
        {
            if (IsGrounded && jumpLockTimer <= 0f)
                return;

            body.AddForce(Vector3.down * (config.gravity * multiplier), ForceMode.Acceleration);
        }

        public void SetYaw(float yawDegrees)
        {
            body.MoveRotation(Quaternion.Euler(0f, yawDegrees, 0f));
        }

        void ApplyCapsuleShape(float height)
        {
            capsule.radius = config.radius;
            capsule.height = Mathf.Max(config.radius * 2f + 0.05f, height);
            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);
        }
    }
}
