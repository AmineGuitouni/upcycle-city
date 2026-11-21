using UnityEngine;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Tuning profile for the FSM arcade-physics player controller, camera feel, and physics trash grabber.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Player/Player Config", fileName = "PlayerConfig")]
    public sealed class PlayerConfig : ScriptableObject
    {
        [Header("Arcade Movement")]
        [Min(0.5f)] public float walkSpeed = 6.5f;
        [Min(0.5f)] public float sprintSpeed = 10.2f;
        [Min(0.5f)] public float crouchSpeed = 3.5f;
        [Tooltip("Snappy ground acceleration in m/s².")]
        [Min(1f)] public float groundAcceleration = 58f;
        [Tooltip("Snappy ground stopping deceleration in m/s².")]
        [Min(1f)] public float groundDeceleration = 68f;
        [Tooltip("Responsive arcade air steering acceleration in m/s².")]
        [Min(0f)] public float airAcceleration = 26f;
        [Min(0f)] public float airDeceleration = 6f;

        [Header("Jump & Gravity")]
        [Min(0.5f)] public float jumpVelocity = 7.6f;
        [Tooltip("Base downward gravity in m/s² (snappier than Earth -9.81 for arcade feel).")]
        public float gravity = 22f;
        [Tooltip("Extra gravity multiplier while falling for crisp arcade jump arcs.")]
        [Range(1f, 3f)] public float fallGravityMultiplier = 1.55f;
        [Tooltip("Multiplier applied to upward velocity when releasing Jump early.")]
        [Range(0.1f, 1f)] public float jumpCutMultiplier = 0.45f;
        [Tooltip("Grace time after leaving a ledge where jumping is still allowed.")]
        [Range(0f, 0.35f)] public float coyoteTime = 0.14f;
        [Tooltip("Grace time before landing where pressing Jump queues a jump on touch-down.")]
        [Range(0f, 0.35f)] public float jumpBufferTime = 0.14f;

        [Header("Capsule, Slopes & Grounding")]
        [Min(0.15f)] public float radius = 0.38f;
        [Min(1f)] public float standHeight = 1.8f;
        [Min(0.6f)] public float crouchHeight = 1.08f;
        [Min(0.5f)] public float standEyeHeight = 1.62f;
        [Min(0.4f)] public float crouchEyeHeight = 0.92f;
        [Min(1f)] public float crouchLerpSpeed = 14f;
        [Range(15f, 65f)] public float maxSlopeAngle = 46f;
        [Min(0.02f)] public float groundProbeDistance = 0.18f;
        [Min(0f)] public float groundStickForce = 6f;

        [Header("Camera & Juice")]
        [Min(0.01f)] public float lookSensitivity = 0.14f;
        [Range(40f, 89f)] public float maxPitch = 82f;
        [Range(50f, 110f)] public float baseFov = 72f;
        [Range(50f, 120f)] public float sprintFov = 80f;
        [Min(1f)] public float fovLerpSpeed = 10f;
        [Range(0f, 0.12f)] public float headBobAmplitude = 0.042f;
        [Min(1f)] public float headBobFrequency = 10.5f;
        [Range(0f, 0.2f)] public float landDipAmount = 0.09f;

        [Header("Physics Trash Grab (1 at a Time)")]
        [Min(1f)] public float baseGrabReach = 2.8f;
        [Min(0.6f)] public float holdDistance = 1.5f;
        [Range(-0.5f, 0.2f)] public float holdVerticalOffset = -0.14f;
        [Min(1f)] public float holdFollowStiffness = 22f;
        [Min(1f)] public float maxHoldFollowSpeed = 16f;
        [Min(1f)] public float maxBreakDistance = 2.9f;
        [Min(1f)] public float minThrowSpeed = 6.5f;
        [Min(1f)] public float maxThrowSpeed = 16.5f;
        [Min(0.1f)] public float maxChargeDuration = 0.7f;
        [Min(0.2f)] public float interactionReach = 3.2f;
    }
}
