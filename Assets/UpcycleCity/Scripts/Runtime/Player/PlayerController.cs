using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Root player orchestrator wiring the FSM arcade-physics controller, keyed stat modifiers,
    /// first-person camera, tool/currency-only inventory, one-at-a-time physics grabber,
    /// feedback hub, and global <see cref="PlayerContext"/> registration.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerInputReader), typeof(PlayerMotor), typeof(PlayerCameraController))]
    [RequireComponent(typeof(PlayerInventory), typeof(PhysicsGrabController), typeof(PlayerInteractor))]
    [RequireComponent(typeof(PlayerFeedbackHub))]
    [AddComponentMenu("Upcycle City/Player/Player Controller")]
    public sealed class PlayerController : MonoBehaviour
    {
        const string EquippedToolModifierSource = "EquippedTool";

        [SerializeField] PlayerConfig config;
        [SerializeField] Transform cameraPivot;
        [SerializeField] Camera playerCamera;
        [SerializeField] Transform equippedToolAnchor;

        PlayerInputReader input;
        PlayerMotor motor;
        PlayerCameraController cameraController;
        PlayerInventory inventory;
        PhysicsGrabController grabber;
        PlayerInteractor interactor;
        PlayerFeedbackHub feedback;
        PlayerModifiers modifiers;
        PlayerStateMachine stateMachine;
        FirstPersonArms arms;

        GameObject activeToolVisual;
        IInventoryItem activeToolItem;
        float toolSwingTimer;

        public PlayerConfig Config => config;
        public PlayerInputReader Input => input;
        public PlayerMotor Motor => motor;
        public PlayerCameraController CameraController => cameraController;
        public PlayerInventory Inventory => inventory;
        public PhysicsGrabController Grabber => grabber;
        public PlayerInteractor Interactor => interactor;
        public PlayerFeedbackHub Feedback => feedback;
        public PlayerModifiers Modifiers => modifiers;
        public PlayerStateMachine StateMachine => stateMachine;
        /// <summary>Parent of the equipped tool's view model (moved into the right fist by <see cref="FirstPersonArms"/>).</summary>
        public Transform EquippedToolAnchor => equippedToolAnchor;

        public void SetConfig(PlayerConfig playerConfig) => config = playerConfig;

        public void SetCameraReferences(Transform pivot, Camera cam, Transform toolAnchor = null)
        {
            cameraPivot = pivot;
            playerCamera = cam;
            equippedToolAnchor = toolAnchor;
        }

        /// <summary>
        /// Locks or unlocks player locomotion (e.g. when opening a shop, crafting bench, or event modal)
        /// and optionally switches the Input System action map between Gameplay and UI.
        /// </summary>
        public void SetMovementLocked(bool locked, bool switchInputMode = true)
        {
            if (stateMachine != null)
            {
                if (locked)
                    stateMachine.EnterOverrideState(stateMachine.Locked);
                else
                    stateMachine.ExitOverrideState();
            }

            if (switchInputMode && input != null)
                input.SetInputMode(locked ? PlayerInputMode.UI : PlayerInputMode.Gameplay);
        }

        void Awake()
        {
            if (config == null)
                config = ScriptableObject.CreateInstance<PlayerConfig>();

            input = GetComponent<PlayerInputReader>();
            motor = GetComponent<PlayerMotor>();
            cameraController = GetComponent<PlayerCameraController>();
            inventory = GetComponent<PlayerInventory>();
            grabber = GetComponent<PhysicsGrabController>();
            interactor = GetComponent<PlayerInteractor>();
            feedback = GetComponent<PlayerFeedbackHub>();
            arms = GetComponent<FirstPersonArms>();
            modifiers = new PlayerModifiers();

            EnsureCameraRig();

            motor.Initialize(config, modifiers);
            cameraController.Initialize(config, motor, cameraPivot, playerCamera);
            grabber.Initialize(config, motor, inventory, playerCamera.transform, modifiers);
            interactor.Initialize(config, input, inventory, grabber, playerCamera.transform, modifiers, feedback);

            stateMachine = new PlayerStateMachine(this);
            stateMachine.Start(stateMachine.Idle);
        }

        void OnEnable()
        {
            PlayerContext.Register(this);

            if (motor != null)
            {
                motor.Jumped += OnJumped;
                motor.Landed += OnLanded;
            }
            if (inventory != null)
            {
                inventory.SelectedSlotChanged += OnSelectedSlotChanged;
                inventory.ItemUsed += OnItemUsed;
            }
            if (grabber != null)
            {
                grabber.Grabbed += OnObjectGrabbed;
                grabber.Released += OnObjectReleased;
            }
        }

        void OnDisable()
        {
            PlayerContext.Unregister(this);

            if (motor != null)
            {
                motor.Jumped -= OnJumped;
                motor.Landed -= OnLanded;
            }
            if (inventory != null)
            {
                inventory.SelectedSlotChanged -= OnSelectedSlotChanged;
                inventory.ItemUsed -= OnItemUsed;
            }
            if (grabber != null)
            {
                grabber.Grabbed -= OnObjectGrabbed;
                grabber.Released -= OnObjectReleased;
            }
        }

        void Start()
        {
            SyncEquippedToolModifiers();
            RefreshToolVisual();
        }

        void Update()
        {
            float dt = Time.deltaTime;

            if (input.InputMode != PlayerInputMode.UI && input.JumpPressedThisFrame)
                motor.BufferJump();

            motor.TickTimers(dt);
            stateMachine.Tick(dt);

            // Bending down to pick litter up by hand lowers and tilts the view
            float bend = grabber.HandPickupBend;
            cameraController.SetBodyPose(bend * config.handPickupDip, bend * config.handPickupPitch);

            bool isSprinting = stateMachine.Current == stateMachine.Sprint;
            Vector2 look = input.InputMode == PlayerInputMode.UI ? Vector2.zero : input.LookDelta;
            cameraController.TickLook(look, isSprinting, dt);
            interactor.Tick(dt);
            if (arms == null || !arms.isActiveAndEnabled)
                AnimateToolAnchor(dt);   // with arms, the tool rides in the right hand instead
        }

        void FixedUpdate()
        {
            float fixedDt = Time.fixedDeltaTime;
            motor.SetYaw(cameraController.Yaw);
            motor.ProbeGround();
            stateMachine.FixedTick(fixedDt);
        }

        void EnsureCameraRig()
        {
            if (cameraPivot == null)
            {
                var pivotGo = new GameObject("CameraPivot");
                pivotGo.transform.SetParent(transform, false);
                pivotGo.transform.localPosition = new Vector3(0f, config.standEyeHeight, 0f);
                cameraPivot = pivotGo.transform;
            }

            if (playerCamera == null)
            {
                playerCamera = GetComponentInChildren<Camera>();
                if (playerCamera == null)
                {
                    var camGo = new GameObject("PlayerCamera");
                    camGo.tag = "MainCamera";
                    camGo.transform.SetParent(cameraPivot, false);
                    playerCamera = camGo.AddComponent<Camera>();
                    camGo.AddComponent<AudioListener>();
                }
            }

            if (equippedToolAnchor == null)
            {
                var anchorGo = new GameObject("EquippedToolAnchor");
                anchorGo.transform.SetParent(playerCamera.transform, false);
                anchorGo.transform.localPosition = new Vector3(0.32f, -0.26f, 0.52f);
                equippedToolAnchor = anchorGo.transform;
            }
        }

        void OnJumped()
        {
            feedback?.Emit(PlayerFeedbackEvent.Jump, transform.position, 0.7f);
        }

        void OnLanded(float impactSpeed)
        {
            if (impactSpeed > 1.5f)
                feedback?.Emit(PlayerFeedbackEvent.Land, transform.position, Mathf.Clamp01(impactSpeed / 12f));
        }

        void OnSelectedSlotChanged(int index, InventorySlot slot)
        {
            grabber?.SyncGripWithEquipped();
            SyncEquippedToolModifiers();
            RefreshToolVisual();
        }

        void OnItemUsed(IInventoryItem item) => toolSwingTimer = 0.22f;

        void OnObjectGrabbed(IGrabbable item)
        {
            RefreshToolVisual();
            Vector3 pos = item != null && item.Transform != null ? item.Transform.position : transform.position;
            feedback?.Emit(PlayerFeedbackEvent.Grab, pos, item != null && item.IsBulky ? 1f : 0.65f, item?.DisplayName);
        }

        void OnObjectReleased(IGrabbable item, bool thrown)
        {
            RefreshToolVisual();
            Vector3 pos = item != null && item.Transform != null ? item.Transform.position : transform.position;
            feedback?.Emit(thrown ? PlayerFeedbackEvent.Throw : PlayerFeedbackEvent.Drop, pos, thrown ? 1f : 0.5f, item?.DisplayName);
        }

        void SyncEquippedToolModifiers()
        {
            if (modifiers == null)
                return;

            modifiers.RemoveAllFromSource(EquippedToolModifierSource);
            IInventoryItem equipped = inventory != null ? inventory.EquippedItem : null;
            if (equipped == null)
                return;

            if (equipped.GrabReachBonus > 0f)
                modifiers.SetAdditive(EquippedToolModifierSource, PlayerStat.GrabReachBonus, equipped.GrabReachBonus);
            if (equipped.ThrowForceBonus > 0f)
                modifiers.SetAdditive(EquippedToolModifierSource, PlayerStat.ThrowForceBonus, equipped.ThrowForceBonus);
            if (!Mathf.Approximately(equipped.MoveSpeedMultiplier, 1f))
                modifiers.SetMultiplier(EquippedToolModifierSource, PlayerStat.MoveSpeedMultiplier, equipped.MoveSpeedMultiplier);
            if (equipped.GrantsSmogImmunity)
                modifiers.SetFlag(EquippedToolModifierSource, PlayerModifierFlag.SmogImmune, true);
        }

        void RefreshToolVisual()
        {
            // Hide the tool while both hands carry trash; the litter picker stays out (it may be the one holding it)
            IInventoryItem equipped = inventory != null ? inventory.EquippedItem : null;
            if (equipped != null && grabber != null && grabber.IsHolding && !equipped.IsGrabTool)
                equipped = null;

            // Same tool still shown: keep it (re-spawning would snap the picker's jaws and arms back to rest)
            if (activeToolVisual != null && equipped != null && ReferenceEquals(equipped, activeToolItem))
                return;

            if (activeToolVisual != null)
            {
                Destroy(activeToolVisual);
                activeToolVisual = null;
            }
            activeToolItem = null;
            grabber?.SetClawProvider(null);

            if (equippedToolAnchor == null || equipped == null)
                return;
            activeToolItem = equipped;

            if (equipped.HeldVisualPrefab != null)
            {
                activeToolVisual = Instantiate(equipped.HeldVisualPrefab, equippedToolAnchor);
                // Per-item grip correction, layered on top of the model's authored transform (which can
                // carry an FBX axis conversion) so the defaults (zero offset, scale 1) change nothing.
                // Designers nudge HeldLocalPosition/Euler/Scale on the item asset until the grip sits in the fist.
                Transform t = activeToolVisual.transform;
                t.localPosition += equipped.HeldLocalPosition;
                t.localRotation = Quaternion.Euler(equipped.HeldLocalEuler) * t.localRotation;
                t.localScale *= equipped.HeldUniformScale;
                StripPhysicsFromViewModel(activeToolVisual);

                // A claw tool animates its jaws and tells the grabber where its claw is
                if (equipped.IsGrabTool)
                {
                    var view = activeToolVisual.AddComponent<LitterPickerView>();
                    view.Bind(this, arms != null && arms.isActiveAndEnabled ? arms.ViewModelCamera : null);
                    grabber?.SetClawProvider(view);
                }
            }
            else
            {
                activeToolVisual = BuildProceduralToolView(equipped, equippedToolAnchor, heldInHand: arms != null && arms.isActiveAndEnabled);
            }

            // Same layer as the anchor, so the view-model camera draws it with the arms
            GameLayers.SetLayerRecursively(activeToolVisual, equippedToolAnchor.gameObject.layer);
        }

        void AnimateToolAnchor(float dt)
        {
            if (equippedToolAnchor == null)
                return;

            float swing = 0f;
            if (toolSwingTimer > 0f)
            {
                toolSwingTimer = Mathf.Max(0f, toolSwingTimer - dt);
                swing = Mathf.Sin((toolSwingTimer / 0.22f) * Mathf.PI) * -28f;
            }

            Vector3 basePos = new Vector3(0.32f, -0.26f, 0.52f);
            float moveBob = motor.IsGrounded && motor.HorizontalSpeed > 0.4f
                ? Mathf.Sin(Time.time * 10f) * 0.014f
                : 0f;
            equippedToolAnchor.localPosition = basePos + new Vector3(0f, moveBob, 0f);
            equippedToolAnchor.localRotation = Quaternion.Euler(swing, 0f, 0f);
        }

        static void StripPhysicsFromViewModel(GameObject root)
        {
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
                Destroy(c);
            foreach (Rigidbody rb in root.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
        }

        /// <param name="heldInHand">The anchor is in a fist (<see cref="FirstPersonArms"/>): the handle sits at the
        /// origin and runs along +Z, already lined up with the hand.</param>
        static GameObject BuildProceduralToolView(IInventoryItem item, Transform parent, bool heldInHand)
        {
            var root = new GameObject($"ToolView_{item.ItemId}");
            root.transform.SetParent(parent, false);

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var accentMat = new Material(shader) { color = item.AccentColor };
            var metalMat = new Material(shader) { color = new Color(0.28f, 0.31f, 0.34f) };

            bool isLongPicker = item.IsGrabTool || item.GrabReachBonus > 0.1f;
            if (isLongPicker && heldInHand)
            {
                // a foam grip inside the fist, the shaft through it, a trigger under the fingers, jaws at the tip
                CreateToolPart(root.transform, "Grip", new Vector3(0f, 0f, 0.0f), new Vector3(0.03f, 0.03f, 0.12f), accentMat);
                CreateToolPart(root.transform, "Shaft", new Vector3(0f, 0f, 0.36f), new Vector3(0.016f, 0.016f, 0.66f), metalMat);
                CreateToolPart(root.transform, "Trigger", new Vector3(0f, -0.03f, 0.03f), new Vector3(0.012f, 0.04f, 0.018f), metalMat, new Vector3(-20f, 0f, 0f));
                CreateToolPart(root.transform, "JawL", new Vector3(-0.02f, 0f, 0.71f), new Vector3(0.012f, 0.02f, 0.08f), accentMat, new Vector3(0f, 12f, 0f));
                CreateToolPart(root.transform, "JawR", new Vector3(0.02f, 0f, 0.71f), new Vector3(0.012f, 0.02f, 0.08f), accentMat, new Vector3(0f, -12f, 0f));
            }
            else if (isLongPicker)
            {
                root.transform.localRotation = Quaternion.Euler(4f, -10f, 0f);
                CreateToolPart(root.transform, "Grip", new Vector3(0f, -0.02f, -0.06f), new Vector3(0.036f, 0.09f, 0.045f), accentMat);
                CreateToolPart(root.transform, "Shaft", new Vector3(0f, 0.01f, 0.28f), new Vector3(0.022f, 0.022f, 0.64f), metalMat);
                CreateToolPart(root.transform, "JawL", new Vector3(-0.026f, 0.01f, 0.63f), new Vector3(0.014f, 0.022f, 0.10f), accentMat, new Vector3(0f, 14f, 0f));
                CreateToolPart(root.transform, "JawR", new Vector3(0.026f, 0.01f, 0.63f), new Vector3(0.014f, 0.022f, 0.10f), accentMat, new Vector3(0f, -14f, 0f));
            }
            else
            {
                CreateToolPart(root.transform, "Handle", new Vector3(0f, -0.02f, 0f), new Vector3(0.045f, 0.22f, 0.045f), metalMat);
                CreateToolPart(root.transform, "Head", new Vector3(0f, 0.10f, 0.02f), new Vector3(0.11f, 0.055f, 0.06f), accentMat);
            }

            return root;
        }

        static void CreateToolPart(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = pos;
            part.transform.localRotation = Quaternion.Euler(euler);
            part.transform.localScale = scale;
            var r = part.GetComponent<Renderer>();
            if (r != null)
                r.sharedMaterial = mat;
        }
    }
}
