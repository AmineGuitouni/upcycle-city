using UnityEngine;

namespace UpcycleCity.Player
{
    public enum PlayerTuningPreset
    {
        ArcadeDefault = 0,
        SnappyMobile = 1,
        HeavyGrounded = 2,
        LowGravityFun = 3,
    }

    /// <summary>
    /// Tuning profile for the FSM arcade-physics player controller, camera feel, and physics trash grabber.
    /// All fields are read live each frame/physics tick, so Inspector edits apply immediately in Play Mode
    /// (and persist, since this is an asset).
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Player/Player Config", fileName = "PlayerConfig")]
    public sealed class PlayerConfig : ScriptableObject
    {
        [Header("Arcade Movement")]
        [Range(1f, 15f)] public float walkSpeed = 6.5f;
        [Range(2f, 22f)] public float sprintSpeed = 10.2f;
        [Range(1f, 10f)] public float crouchSpeed = 3.5f;
        [Tooltip("Snappy ground acceleration in m/s².")]
        [Range(5f, 120f)] public float groundAcceleration = 58f;
        [Tooltip("Snappy ground stopping deceleration in m/s².")]
        [Range(5f, 140f)] public float groundDeceleration = 68f;
        [Tooltip("Responsive arcade air steering acceleration in m/s².")]
        [Range(0f, 80f)] public float airAcceleration = 26f;
        [Range(0f, 40f)] public float airDeceleration = 6f;

        [Header("Jump & Gravity")]
        [Range(1f, 18f)] public float jumpVelocity = 7.6f;
        [Tooltip("Base downward gravity in m/s² (snappier than Earth -9.81 for arcade feel).")]
        [Range(4f, 50f)] public float gravity = 22f;
        [Tooltip("Extra gravity multiplier while falling for crisp arcade jump arcs.")]
        [Range(1f, 3f)] public float fallGravityMultiplier = 1.55f;
        [Tooltip("Multiplier applied to upward velocity when releasing Jump early.")]
        [Range(0.1f, 1f)] public float jumpCutMultiplier = 0.45f;
        [Tooltip("Terminal downward fall speed cap in m/s.")]
        [Range(8f, 60f)] public float maxFallSpeed = 32f;
        [Tooltip("Grace time after leaving a ledge where jumping is still allowed.")]
        [Range(0f, 0.35f)] public float coyoteTime = 0.14f;
        [Tooltip("Grace time before landing where pressing Jump queues a jump on touch-down.")]
        [Range(0f, 0.35f)] public float jumpBufferTime = 0.14f;

        [Header("Capsule, Slopes & Grounding")]
        [Range(0.2f, 0.65f)] public float radius = 0.38f;
        [Range(1.2f, 2.4f)] public float standHeight = 1.8f;
        [Range(0.7f, 1.6f)] public float crouchHeight = 1.08f;
        [Range(0.9f, 2.2f)] public float standEyeHeight = 1.62f;
        [Range(0.5f, 1.4f)] public float crouchEyeHeight = 0.92f;
        [Range(2f, 30f)] public float crouchLerpSpeed = 14f;
        [Range(15f, 65f)] public float maxSlopeAngle = 46f;
        [Range(0.04f, 0.45f)] public float groundProbeDistance = 0.18f;
        [Range(0f, 25f)] public float groundStickForce = 6f;

        [Header("Camera & Juice")]
        [Range(0.02f, 0.5f)] public float lookSensitivity = 0.14f;
        [Range(40f, 89f)] public float maxPitch = 82f;
        [Range(50f, 110f)] public float baseFov = 72f;
        [Range(50f, 120f)] public float sprintFov = 80f;
        [Range(1f, 25f)] public float fovLerpSpeed = 10f;
        [Range(0f, 0.12f)] public float headBobAmplitude = 0.042f;
        [Range(2f, 20f)] public float headBobFrequency = 10.5f;
        [Range(0f, 0.2f)] public float landDipAmount = 0.09f;

        [Header("Physics Trash Grab & Throw Forces")]
        [Range(1f, 6f)] public float baseGrabReach = 2.8f;
        [Range(0.6f, 3.2f)] public float holdDistance = 1.5f;
        [Range(-0.5f, 0.25f)] public float holdVerticalOffset = -0.14f;
        [Range(4f, 45f)] public float holdFollowStiffness = 22f;
        [Range(4f, 35f)] public float maxHoldFollowSpeed = 16f;
        [Range(1f, 6f)] public float maxBreakDistance = 2.9f;
        [Range(1f, 20f)] public float minThrowSpeed = 6.5f;
        [Range(4f, 36f)] public float maxThrowSpeed = 16.5f;
        [Range(0.1f, 2f)] public float maxChargeDuration = 0.7f;
        [Tooltip("Upward loft added to camera forward when throwing trash into crates.")]
        [Range(0f, 0.6f)] public float throwUpwardArc = 0.16f;
        [Tooltip("Fraction of the player's own velocity added to thrown trash.")]
        [Range(0f, 1f)] public float throwVelocityInheritance = 0.35f;
        [Tooltip("Gentle forward speed applied when dropping an object with RMB.")]
        [Range(0f, 5f)] public float dropForwardImpulse = 1.1f;
        [Tooltip("Angular spring stiffness aligning held objects with camera yaw.")]
        [Range(1f, 24f)] public float heldRotationStiffness = 8f;
        [Range(1f, 6f)] public float interactionReach = 3.2f;

        /// <summary>Overwrites all values with a starting-point feel preset.</summary>
        public void ApplyPreset(PlayerTuningPreset preset)
        {
            // Start from Arcade Default baseline
            walkSpeed = 6.5f;
            sprintSpeed = 10.2f;
            crouchSpeed = 3.5f;
            groundAcceleration = 58f;
            groundDeceleration = 68f;
            airAcceleration = 26f;
            airDeceleration = 6f;

            jumpVelocity = 7.6f;
            gravity = 22f;
            fallGravityMultiplier = 1.55f;
            jumpCutMultiplier = 0.45f;
            maxFallSpeed = 32f;
            coyoteTime = 0.14f;
            jumpBufferTime = 0.14f;

            radius = 0.38f;
            standHeight = 1.8f;
            crouchHeight = 1.08f;
            standEyeHeight = 1.62f;
            crouchEyeHeight = 0.92f;
            crouchLerpSpeed = 14f;
            maxSlopeAngle = 46f;
            groundProbeDistance = 0.18f;
            groundStickForce = 6f;

            lookSensitivity = 0.14f;
            maxPitch = 82f;
            baseFov = 72f;
            sprintFov = 80f;
            fovLerpSpeed = 10f;
            headBobAmplitude = 0.042f;
            headBobFrequency = 10.5f;
            landDipAmount = 0.09f;

            baseGrabReach = 2.8f;
            holdDistance = 1.5f;
            holdVerticalOffset = -0.14f;
            holdFollowStiffness = 22f;
            maxHoldFollowSpeed = 16f;
            maxBreakDistance = 2.9f;
            minThrowSpeed = 6.5f;
            maxThrowSpeed = 16.5f;
            maxChargeDuration = 0.7f;
            throwUpwardArc = 0.16f;
            throwVelocityInheritance = 0.35f;
            dropForwardImpulse = 1.1f;
            heldRotationStiffness = 8f;
            interactionReach = 3.2f;

            switch (preset)
            {
                case PlayerTuningPreset.SnappyMobile:
                    walkSpeed = 7.2f;
                    sprintSpeed = 11.4f;
                    crouchSpeed = 4.2f;
                    groundAcceleration = 82f;
                    groundDeceleration = 95f;
                    airAcceleration = 36f;
                    jumpVelocity = 8.0f;
                    gravity = 24f;
                    fallGravityMultiplier = 1.65f;
                    coyoteTime = 0.18f;
                    jumpBufferTime = 0.18f;
                    baseGrabReach = 3.4f;
                    interactionReach = 3.8f;
                    holdFollowStiffness = 28f;
                    maxHoldFollowSpeed = 20f;
                    minThrowSpeed = 8.0f;
                    maxThrowSpeed = 18.5f;
                    maxChargeDuration = 0.5f;
                    throwUpwardArc = 0.22f;
                    headBobAmplitude = 0.028f;
                    break;

                case PlayerTuningPreset.HeavyGrounded:
                    walkSpeed = 5.2f;
                    sprintSpeed = 8.2f;
                    crouchSpeed = 2.8f;
                    groundAcceleration = 34f;
                    groundDeceleration = 44f;
                    airAcceleration = 14f;
                    jumpVelocity = 6.6f;
                    gravity = 25f;
                    fallGravityMultiplier = 1.75f;
                    coyoteTime = 0.10f;
                    jumpBufferTime = 0.10f;
                    holdFollowStiffness = 15f;
                    maxHoldFollowSpeed = 11f;
                    minThrowSpeed = 5.0f;
                    maxThrowSpeed = 13.5f;
                    maxChargeDuration = 0.9f;
                    throwUpwardArc = 0.12f;
                    headBobAmplitude = 0.055f;
                    landDipAmount = 0.13f;
                    break;

                case PlayerTuningPreset.LowGravityFun:
                    walkSpeed = 8.5f;
                    sprintSpeed = 13.5f;
                    groundAcceleration = 48f;
                    airAcceleration = 42f;
                    jumpVelocity = 11.2f;
                    gravity = 12.5f;
                    fallGravityMultiplier = 1.15f;
                    coyoteTime = 0.22f;
                    minThrowSpeed = 9.0f;
                    maxThrowSpeed = 24.0f;
                    throwUpwardArc = 0.26f;
                    sprintFov = 88f;
                    break;
            }
        }
    }
}
