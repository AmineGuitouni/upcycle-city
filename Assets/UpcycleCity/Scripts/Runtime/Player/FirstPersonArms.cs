using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;

namespace UpcycleCity.Player
{
    /// <summary>
    /// First-person arms in the player's own colours (skin tone and coat from the Character Creator).
    /// <para>
    /// The arms and the held tool live on <see cref="GameLayers.FirstPersonView"/> and are drawn by an overlay
    /// view-model camera stacked on the player camera, with its own field of view and a 1 cm near plane, so
    /// they never clip into walls, never stretch with the sprint FOV, and never cast odd shadows.
    /// </para>
    /// Motion is procedural and allocation-free: look sway, walk bob, a lowered sprint pose, an airborne lift,
    /// reaching out while carrying trash, pulling back while charging a throw, a forward punch on the throw,
    /// and a tool swing on use. The right hand grips the equipped tool (the tool anchor is moved into its fist).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerController))]
    [AddComponentMenu("Upcycle City/Player/First Person Arms")]
    public sealed class FirstPersonArms : MonoBehaviour
    {
        [SerializeField] CharacterPartsLibrary library;

        [Header("View-Model Camera")]
        [SerializeField, Range(40f, 90f)] float viewModelFov = 68f;
        [SerializeField, Range(0.005f, 0.1f)] float viewModelNearClip = 0.01f;

        [Header("Poses (metres, camera space)")]
        [Tooltip("Rest offset of both arms from where they were modelled.")]
        [SerializeField] Vector3 restOffset = Vector3.zero;
        [Tooltip("Where a free hand rests: low, partly out of view.")]
        [SerializeField] Vector3 idleHandOffset = new Vector3(0f, -0.075f, -0.04f);
        [Tooltip("Where the hands go while carrying trash.")]
        [SerializeField] Vector3 carryOffset = new Vector3(0f, 0.045f, 0.07f);
        [SerializeField] Vector3 sprintOffset = new Vector3(0f, -0.05f, -0.03f);
        [SerializeField, Range(0f, 30f)] float sprintPitch = 12f;
        [SerializeField, Range(0f, 0.2f)] float chargePullBack = 0.1f;
        [SerializeField, Range(0f, 0.3f)] float throwPunch = 0.14f;
        [SerializeField, Range(0f, 0.2f)] float grabReach = 0.06f;
        [SerializeField, Range(0f, 60f)] float toolSwingDegrees = 32f;
        [Tooltip("Where the hands reach while bending down to pick litter up by hand (at full bend).")]
        [SerializeField] Vector3 handPickupReach = new Vector3(0f, -0.07f, 0.15f);
        [Tooltip("How far the litter picker jabs forward on a grab.")]
        [SerializeField, Range(0f, 0.2f)] float pickerJabReach = 0.09f;
        [Tooltip("How far the litter picker dips its claw on a grab (degrees).")]
        [SerializeField, Range(0f, 40f)] float pickerJabDegrees = 16f;

        [Header("Motion")]
        [SerializeField, Range(0f, 0.01f)] float swayPerLook = 0.0016f;
        [SerializeField, Range(0f, 0.1f)] float maxSway = 0.035f;
        [SerializeField, Range(0f, 0.3f)] float swayDegreesPerLook = 0.09f;
        [SerializeField, Range(0f, 0.05f)] float bobAmount = 0.012f;
        [SerializeField, Range(1f, 30f)] float followSharpness = 14f;

        const float ToolSwingDuration = 0.24f;

        struct ArmState
        {
            public Transform Pivot;
            public GameObject Open;
            public GameObject Grip;
            public Vector3 Position;
            public Vector3 Velocity;
        }

        PlayerController player;
        Camera playerCamera;
        Camera viewCamera;
        Transform rig;
        Transform toolHold;
        ArmState right, left;

        readonly List<CharacterColorRoles.Slot> colourSlots = new List<CharacterColorRoles.Slot>(16);
        MaterialPropertyBlock block;

        Vector2 sway;
        Vector2 swayAngles;
        float bobPhase;
        float grabKick, throwKick, swingTimer;
        bool built;

        public Camera ViewModelCamera => viewCamera;

        public void SetLibrary(CharacterPartsLibrary partsLibrary) => library = partsLibrary;

        void Awake() => player = GetComponent<PlayerController>();

        void Start()
        {
            // PlayerController.Awake has created the camera rig by now
            Build();
            Subscribe(true);
        }

        void OnDestroy()
        {
            Subscribe(false);
            CharacterAppearanceStore.Changed -= ApplyAppearance;
        }

        // ---------------------------------------------------------------- setup

        void Build()
        {
            playerCamera = player.CameraController != null ? player.CameraController.Camera : null;
            if (built || library == null || playerCamera == null || library.rightArmOpen == null || library.leftArmOpen == null)
                return;

            int layer = GameLayers.FirstPersonView;
            int layerMask = GameLayers.MaskOf(layer);

            // The world camera stops drawing the view-model layer; an overlay camera draws only that layer
            playerCamera.cullingMask &= ~layerMask;
            var camGo = new GameObject("ViewModelCamera");
            camGo.transform.SetParent(playerCamera.transform, false);
            viewCamera = camGo.AddComponent<Camera>();
            viewCamera.cullingMask = layerMask;
            viewCamera.fieldOfView = viewModelFov;
            viewCamera.nearClipPlane = viewModelNearClip;
            viewCamera.farClipPlane = 5f;
            var viewData = viewCamera.GetUniversalAdditionalCameraData();
            viewData.renderType = CameraRenderType.Overlay;
            viewData.renderShadows = false;
            playerCamera.GetUniversalAdditionalCameraData().cameraStack.Add(viewCamera);

            rig = new GameObject("ViewModelRig").transform;
            rig.SetParent(playerCamera.transform, false);
            rig.gameObject.layer = layer;

            right = CreateArm("ArmRight", library.rightArmOpen, library.rightArmGrip, layer);
            left = CreateArm("ArmLeft", library.leftArmOpen, library.leftArmGrip, layer);

            // The tool anchor moves into the right fist; tools keep their camera-aligned orientation
            toolHold = new GameObject("ToolHold").transform;
            toolHold.SetParent(right.Pivot, false);
            toolHold.gameObject.layer = layer;
            PlaceToolHold(right.Grip != null ? right.Grip.transform : null);
            Transform anchor = player.EquippedToolAnchor;
            if (anchor != null)
            {
                anchor.SetParent(toolHold, false);
                anchor.localPosition = Vector3.zero;
                anchor.localRotation = Quaternion.identity;
                GameLayers.SetLayerRecursively(anchor.gameObject, layer);
            }

            right.Position = left.Position = restOffset + idleHandOffset;
            ApplyAppearance(CharacterAppearanceStore.Current);
            CharacterAppearanceStore.Changed += ApplyAppearance;
            built = true;
        }

        /// <summary>
        /// Puts the tool hold in the middle of the fist, pointing along the handle. The grip mesh carries three
        /// empties from Blender (ToolSocket, ToolSocketForward, ToolSocketUp), so any tool lines up with the fist
        /// without hand-tuned offsets.
        /// </summary>
        void PlaceToolHold(Transform gripArm)
        {
            Transform socket = FindMarker(gripArm, "ToolSocket");
            Transform forward = FindMarker(gripArm, "ToolSocketForward");
            Transform up = FindMarker(gripArm, "ToolSocketUp");
            if (socket == null)
            {
                toolHold.localPosition = new Vector3(0.2f, -0.2f, 0.55f);
                toolHold.localRotation = Quaternion.identity;
                return;
            }

            Vector3 s = gripArm.InverseTransformPoint(socket.position);
            toolHold.localPosition = s;
            if (forward != null)
            {
                Vector3 f = gripArm.InverseTransformPoint(forward.position) - s;
                Vector3 u = up != null ? gripArm.InverseTransformPoint(up.position) - s : Vector3.up;
                // tools are modelled with their handle along +Z and up along +Y
                toolHold.localRotation = Quaternion.LookRotation(f, Vector3.Lerp(Vector3.up, u.normalized, 0.35f));
            }
        }

        /// <summary>Finds a Blender empty by name, ignoring the ".001" suffix Blender adds to duplicates.</summary>
        static Transform FindMarker(Transform root, string markerName)
        {
            if (root == null)
                return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                int dot = n.IndexOf('.');
                if (string.Equals(dot < 0 ? n : n.Substring(0, dot), markerName, System.StringComparison.Ordinal))
                    return t;
            }
            return null;
        }

        ArmState CreateArm(string name, GameObject openPrefab, GameObject gripPrefab, int layer)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(rig, false);
            pivot.gameObject.layer = layer;
            var state = new ArmState
            {
                Pivot = pivot,
                Open = SpawnPose(openPrefab, pivot, layer),
                Grip = SpawnPose(gripPrefab, pivot, layer),
            };
            if (state.Grip != null)
                state.Grip.SetActive(false);
            return state;
        }

        GameObject SpawnPose(GameObject prefab, Transform parent, int layer)
        {
            if (prefab == null)
                return null;
            GameObject go = Instantiate(prefab, parent, false);
            go.name = prefab.name;
            GameLayers.SetLayerRecursively(go, layer);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }
            CharacterColorRoles.Collect(go, colourSlots);
            return go;
        }

        void ApplyAppearance(CharacterAppearance appearance)
        {
            block ??= new MaterialPropertyBlock();
            CharacterColorRoles.Apply(colourSlots, library, library.Sanitize(appearance), block);
        }

        void Subscribe(bool on)
        {
            if (player == null)
                return;
            if (player.Inventory != null)
            {
                if (on)
                {
                    player.Inventory.ItemUsed += OnItemUsed;
                    player.Inventory.ItemAdded += OnItemAdded;
                }
                else
                {
                    player.Inventory.ItemUsed -= OnItemUsed;
                    player.Inventory.ItemAdded -= OnItemAdded;
                }
            }
            if (player.Grabber != null)
            {
                if (on)
                {
                    player.Grabber.Grabbed += OnGrabbed;
                    player.Grabber.Released += OnReleased;
                }
                else
                {
                    player.Grabber.Grabbed -= OnGrabbed;
                    player.Grabber.Released -= OnReleased;
                }
            }
        }

        void OnItemUsed(IInventoryItem item) => swingTimer = ToolSwingDuration;
        void OnGrabbed(IGrabbable item) => grabKick = 1f;

        // A quick hand "stow" motion whenever an item lands in the inventory (pickups, shop buys).
        void OnItemAdded(IInventoryItem item, int amount) => grabKick = 1f;

        /// <summary>Plays the stow hand-motion on demand (e.g. the Storage Bag capacity purchase).</summary>
        public void PlayStow() => grabKick = 1f;

        void OnReleased(IGrabbable item, bool thrown)
        {
            if (thrown)
                throwKick = 1f;
        }

        // ---------------------------------------------------------------- motion

        void LateUpdate()
        {
            if (!built)
                return;

            float dt = Time.deltaTime;
            PlayerMotor motor = player.Motor;
            PhysicsGrabController grabber = player.Grabber;

            bool holding = grabber != null && grabber.IsHolding;
            float charge = grabber != null && grabber.IsChargingThrow ? grabber.ThrowChargeNormalized : 0f;
            bool toolShown = player.EquippedToolAnchor != null && player.EquippedToolAnchor.childCount > 0;
            bool sprinting = player.StateMachine != null && player.StateMachine.Current == player.StateMachine.Sprint;
            bool grounded = motor == null || motor.IsGrounded;
            float speed = motor != null ? motor.HorizontalSpeed : 0f;

            // Look sway: the arms lag behind the camera a little
            Vector2 look = player.Input != null && player.Input.InputMode == PlayerInputMode.Gameplay
                ? player.Input.LookDelta
                : Vector2.zero;
            float k = 1f - Mathf.Exp(-followSharpness * 0.6f * dt);
            sway = Vector2.Lerp(sway, Vector2.ClampMagnitude(-look * swayPerLook, maxSway), k);
            swayAngles = Vector2.Lerp(swayAngles, new Vector2(look.y, -look.x) * swayDegreesPerLook, k);

            // Walk bob, a figure of eight that speeds up with the stride
            if (grounded && speed > 0.4f)
                bobPhase += dt * Mathf.Lerp(7f, 12f, Mathf.Clamp01(speed / 10f));
            else
                bobPhase = Mathf.MoveTowards(bobPhase, Mathf.Round(bobPhase / Mathf.PI) * Mathf.PI, dt * 4f);
            float bobScale = grounded ? Mathf.Clamp01(speed / 6f) : 0f;
            var bob = new Vector3(Mathf.Cos(bobPhase) * bobAmount, -Mathf.Abs(Mathf.Sin(bobPhase)) * bobAmount, 0f) * bobScale;

            grabKick = Mathf.MoveTowards(grabKick, 0f, dt * 5f);
            throwKick = Mathf.MoveTowards(throwKick, 0f, dt * 4f);
            swingTimer = Mathf.Max(0f, swingTimer - dt);

            Vector3 shared = restOffset + (Vector3)sway + bob;
            if (sprinting) shared += sprintOffset;
            if (!grounded) shared += new Vector3(0f, 0.02f, 0f);

            // Left hand: carries, charges and throws. When a tool (the litter picker) is doing the
            // gripping, the off hand rests naturally instead of reaching into empty air for the trash.
            bool leftCarries = holding && !toolShown;
            Vector3 leftTarget = shared + (leftCarries ? carryOffset : idleHandOffset);
            if (leftCarries)
            {
                leftTarget.z += grabKick * grabReach + Ease(throwKick) * throwPunch - charge * chargePullBack;
                leftTarget.y += charge * 0.03f;
            }

            // Right hand: holds the tool, or helps carry when the tool is put away
            bool rightCarries = holding && !toolShown;
            Vector3 rightTarget = shared + (toolShown ? Vector3.zero : rightCarries ? carryOffset : idleHandOffset);
            if (rightCarries)
                rightTarget.z += grabKick * grabReach + Ease(throwKick) * throwPunch - charge * chargePullBack;

            // Bending down for a hand pickup: the free hands reach for the litter
            float bend = grabber != null ? grabber.HandPickupBend : 0f;
            if (bend > 0f)
            {
                leftTarget += handPickupReach * bend;
                if (!toolShown)
                    rightTarget += handPickupReach * bend;
            }

            // Litter picker grab: a quick forward jab with the claw dipping to the litter
            float jab = grabber != null && grabber.CurrentPickup == PickupStyle.Picker
                ? Mathf.Sin(grabber.PickupProgress * Mathf.PI)
                : 0f;
            rightTarget.z += jab * pickerJabReach;

            float swing = swingTimer > 0f ? Mathf.Sin((1f - swingTimer / ToolSwingDuration) * Mathf.PI) : 0f;
            float pitch = (sprinting ? sprintPitch : 0f) + swayAngles.x;
            float yaw = swayAngles.y;

            Move(ref left, leftTarget, Quaternion.Euler(pitch, yaw, 0f), false);
            Move(ref right, rightTarget, Quaternion.Euler(pitch - swing * toolSwingDegrees + jab * pickerJabDegrees, yaw, 0f), toolShown);
        }

        void Move(ref ArmState arm, Vector3 target, Quaternion rotation, bool grip)
        {
            arm.Position = Vector3.SmoothDamp(arm.Position, target, ref arm.Velocity, 1f / followSharpness);
            arm.Pivot.localPosition = arm.Position;
            arm.Pivot.localRotation = rotation;
            if (arm.Grip != null && arm.Grip.activeSelf != grip)
            {
                arm.Grip.SetActive(grip);
                arm.Open.SetActive(!grip);
            }
        }

        static float Ease(float t) => t * t * (3f - 2f * t);
    }
}
