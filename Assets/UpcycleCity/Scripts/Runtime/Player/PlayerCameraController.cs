using UnityEngine;

namespace UpcycleCity.Player
{
    /// <summary>
    /// First-person camera controller: mouse/gamepad yaw & pitch, crouch eye smoothing,
    /// sprint FOV boost, subtle arcade headbob, and landing dip.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Player Camera Controller")]
    public sealed class PlayerCameraController : MonoBehaviour
    {
        [SerializeField] Transform cameraPivot;
        [SerializeField] Camera playerCamera;

        PlayerConfig config;
        PlayerMotor motor;

        float yaw;
        float pitch;
        float bobPhase;
        float landDipOffset;
        float landDipVelocity;
        float poseEyeOffset;
        float posePitch;

        public Transform CameraPivot => cameraPivot;
        public Camera Camera => playerCamera;
        public float Yaw => yaw;
        public float Pitch => pitch;

        /// <summary>
        /// An extra body pose on top of look and crouch, e.g. bending down to pick litter up by hand:
        /// lowers the eyes by <paramref name="eyeDrop"/> metres and tilts the head down by <paramref name="extraPitch"/> degrees.
        /// </summary>
        public void SetBodyPose(float eyeDrop, float extraPitch)
        {
            poseEyeOffset = -Mathf.Max(0f, eyeDrop);
            posePitch = extraPitch;
        }

        public void Initialize(PlayerConfig playerConfig, PlayerMotor playerMotor, Transform pivot, Camera cam)
        {
            config = playerConfig;
            motor = playerMotor;
            if (pivot != null) cameraPivot = pivot;
            if (cam != null) playerCamera = cam;

            yaw = transform.eulerAngles.y;
            pitch = 0f;

            if (playerCamera != null)
                playerCamera.fieldOfView = config.baseFov;

            if (motor != null)
                motor.Landed += OnLanded;
        }

        void OnDestroy()
        {
            if (motor != null)
                motor.Landed -= OnLanded;
        }

        void OnLanded(float impactSpeed)
        {
            if (config == null || impactSpeed < 2.5f)
                return;
            float strength = Mathf.Clamp01((impactSpeed - 2.5f) / 10f);
            landDipOffset = -config.landDipAmount * strength;
        }

        public void TickLook(Vector2 lookDelta, bool isSprinting, float dt)
        {
            if (config == null || cameraPivot == null)
                return;

            yaw += lookDelta.x * config.lookSensitivity;
            pitch = Mathf.Clamp(pitch - lookDelta.y * config.lookSensitivity, -config.maxPitch, config.maxPitch);

            // Smooth landing dip recovery
            landDipOffset = Mathf.SmoothDamp(landDipOffset, 0f, ref landDipVelocity, 0.1f, Mathf.Infinity, dt);

            // Subtle arcade headbob while moving on ground
            float bobY = 0f;
            float bobX = 0f;
            if (motor != null && motor.IsGrounded && motor.HorizontalSpeed > 0.4f)
            {
                float speedRatio = Mathf.Clamp01(motor.HorizontalSpeed / Mathf.Max(0.1f, config.sprintSpeed));
                bobPhase += dt * config.headBobFrequency * (0.75f + speedRatio * 0.55f);
                bobY = Mathf.Sin(bobPhase * 2f) * config.headBobAmplitude * speedRatio;
                bobX = Mathf.Cos(bobPhase) * config.headBobAmplitude * 0.5f * speedRatio;
            }
            else
            {
                bobPhase = 0f;
            }

            float eyeY = motor != null ? motor.CurrentEyeHeight : config.standEyeHeight;
            cameraPivot.localPosition = new Vector3(bobX, Mathf.Max(0.35f, eyeY + bobY + landDipOffset + poseEyeOffset), 0f);
            cameraPivot.localRotation = Quaternion.Euler(Mathf.Min(pitch + posePitch, 89f), 0f, 0f);

            if (playerCamera != null)
            {
                float targetFov = isSprinting ? config.sprintFov : config.baseFov;
                float blend = 1f - Mathf.Exp(-config.fovLerpSpeed * dt);
                playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFov, blend);
            }
        }
    }
}
