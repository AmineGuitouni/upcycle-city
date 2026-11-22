using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UpcycleCity.Player
{
    public enum PlayerInputMode
    {
        Gameplay = 0,
        UI = 1,
        Placement = 2,
    }

    /// <summary>
    /// Reads all player controls exclusively through Unity's Input System (PlayerInput + InputActionAsset).
    /// Supports switching input contexts (Gameplay / UI / Placement) and desktop/gamepad/mobile controls
    /// with zero direct Keyboard/Mouse polling.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerInput))]
    [AddComponentMenu("Upcycle City/Player/Player Input Reader")]
    public sealed class PlayerInputReader : MonoBehaviour
    {
        [Header("Action Maps")]
        [SerializeField] string gameplayActionMapName = "Player";
        [SerializeField] string uiActionMapName = "UI";

        [Tooltip("Optional fallback InputActionAsset if PlayerInput.actions is not assigned yet.")]
        [SerializeField] InputActionAsset fallbackActionsAsset;

        [Header("Cursor & Look Scaling")]
        [Tooltip("Lock mouse cursor automatically when playing with Keyboard & Mouse on desktop.")]
        [SerializeField] bool lockCursorOnDesktop = true;

        [Tooltip("Multiplier applied to Touchscreen pointer delta for mobile look drag.")]
        [SerializeField] float touchLookMultiplier = 1.15f;

        [Tooltip("Virtual pixels-per-second equivalent when looking with Gamepad or mobile OnScreenStick.")]
        [SerializeField] float stickLookSpeed = 900f;

        PlayerInput playerInput;
        InputAction moveAction;
        InputAction lookAction;
        InputAction jumpAction;
        InputAction sprintAction;
        InputAction crouchAction;
        InputAction interactAction;
        InputAction attackAction;
        InputAction dropAction;
        InputAction useItemAction;
        InputAction previousAction;
        InputAction nextAction;
        InputAction hotbarScrollAction;
        InputAction toggleCursorAction;
        readonly InputAction[] hotbarSlotActions = new InputAction[6];

        bool actionsBound;

        // Previous-frame state of the on-screen touch buttons (edge detection)
        bool prevTouchJump, prevTouchAttack, prevTouchInteract, prevTouchDrop, prevTouchUse;

        public PlayerInput PlayerInput => playerInput;
        public PlayerInputMode InputMode { get; private set; } = PlayerInputMode.Gameplay;
        public string CurrentControlScheme => playerInput != null ? playerInput.currentControlScheme : string.Empty;
        public bool IsUsingTouchOrMobile =>
            Application.isMobilePlatform ||
            VirtualTouchInput.Active ||
            string.Equals(CurrentControlScheme, "Touch", StringComparison.OrdinalIgnoreCase);

        public Vector2 Move { get; private set; }
        public Vector2 LookDelta { get; private set; }
        public bool SprintHeld { get; private set; }
        public bool CrouchHeld { get; private set; }
        public bool JumpHeld { get; private set; }
        public bool JumpPressedThisFrame { get; private set; }
        public bool JumpReleasedThisFrame { get; private set; }
        public bool PrimaryPressedThisFrame { get; private set; }
        public bool PrimaryHeld { get; private set; }
        public bool PrimaryReleasedThisFrame { get; private set; }
        public bool SecondaryPressedThisFrame { get; private set; }
        public bool InteractPressedThisFrame { get; private set; }
        public bool InteractHeld { get; private set; }
        public bool InteractReleasedThisFrame { get; private set; }
        public bool UseItemPressedThisFrame { get; private set; }
        public int HotbarScrollDelta { get; private set; }
        public int HotbarDirectSlot { get; private set; } = -1;
        public bool CursorLocked { get; private set; }

        /// <summary>Fired when PlayerInput switches control scheme (e.g. Keyboard&amp;Mouse, Gamepad, Touch).</summary>
        public event Action<string> ControlSchemeChanged;

        /// <summary>Fired when gameplay switches between Gameplay, UI, and Placement input contexts.</summary>
        public event Action<PlayerInputMode> InputModeChanged;

        public void SetFallbackActionsAsset(InputActionAsset asset) => fallbackActionsAsset = asset;

        public void SetInputMode(PlayerInputMode mode)
        {
            if (InputMode == mode && actionsBound)
                return;

            InputMode = mode;
            if (mode == PlayerInputMode.UI)
            {
                ClearAllInputs();
                SetCursorLocked(false);
                SwitchActionMap(uiActionMapName);
            }
            else
            {
                SwitchActionMap(gameplayActionMapName);
                ResolveActions();
                if (lockCursorOnDesktop && !IsUsingTouchOrMobile)
                    SetCursorLocked(true);
            }

            InputModeChanged?.Invoke(mode);
        }

        void SwitchActionMap(string mapName)
        {
            if (playerInput == null || playerInput.actions == null || string.IsNullOrEmpty(mapName))
                return;

            InputActionMap target = playerInput.actions.FindActionMap(mapName, throwIfNotFound: false);
            if (target == null)
                return;

            if (playerInput.enabled)
                playerInput.SwitchCurrentActionMap(mapName);
            else
                target.Enable();
        }

        void Awake()
        {
            playerInput = GetComponent<PlayerInput>();
            EnsureActionsAssigned();
        }

        void OnEnable()
        {
            if (playerInput == null)
                playerInput = GetComponent<PlayerInput>();

            EnsureActionsAssigned();
            ResolveActions();

            if (playerInput != null)
                playerInput.onControlsChanged += OnControlsChanged;

            if (lockCursorOnDesktop && !IsUsingTouchOrMobile && InputMode != PlayerInputMode.UI)
                SetCursorLocked(true);
        }

        void OnDisable()
        {
            if (playerInput != null)
                playerInput.onControlsChanged -= OnControlsChanged;

            actionsBound = false;
            SetCursorLocked(false);
        }

        void EnsureActionsAssigned()
        {
            if (playerInput == null)
                return;

            if (playerInput.actions == null && fallbackActionsAsset != null)
            {
                playerInput.actions = fallbackActionsAsset;
                playerInput.defaultActionMap = gameplayActionMapName;
            }
        }

        void ResolveActions()
        {
            actionsBound = false;
            if (playerInput == null || playerInput.actions == null)
                return;

            InputActionMap map = playerInput.currentActionMap;
            if (map == null || !string.Equals(map.name, gameplayActionMapName, StringComparison.Ordinal))
            {
                map = playerInput.actions.FindActionMap(gameplayActionMapName, throwIfNotFound: false);
                if (map == null)
                    return;

                if (InputMode != PlayerInputMode.UI)
                {
                    if (playerInput.enabled)
                        playerInput.SwitchCurrentActionMap(gameplayActionMapName);
                    else
                        map.Enable();
                }
            }

            if (InputMode != PlayerInputMode.UI && !map.enabled)
                map.Enable();

            moveAction = map.FindAction("Move", false);
            lookAction = map.FindAction("Look", false);
            jumpAction = map.FindAction("Jump", false);
            sprintAction = map.FindAction("Sprint", false);
            crouchAction = map.FindAction("Crouch", false);
            interactAction = map.FindAction("Interact", false);
            attackAction = map.FindAction("Attack", false);
            dropAction = map.FindAction("Drop", false);
            useItemAction = map.FindAction("UseItem", false);
            previousAction = map.FindAction("Previous", false);
            nextAction = map.FindAction("Next", false);
            hotbarScrollAction = map.FindAction("HotbarScroll", false);
            toggleCursorAction = map.FindAction("ToggleCursor", false);

            for (int i = 0; i < hotbarSlotActions.Length; i++)
                hotbarSlotActions[i] = map.FindAction($"HotbarSlot{i + 1}", false);

            actionsBound = true;
        }

        void OnControlsChanged(PlayerInput input)
        {
            string scheme = input != null ? input.currentControlScheme : string.Empty;
            if (IsUsingTouchOrMobile && CursorLocked)
                SetCursorLocked(false);

            ControlSchemeChanged?.Invoke(scheme);
        }

        public void SetCursorLocked(bool locked)
        {
            if (IsUsingTouchOrMobile || InputMode == PlayerInputMode.UI)
                locked = false;

            CursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void Update()
        {
            if (InputMode == PlayerInputMode.UI)
            {
                ClearAllInputs();
                return;
            }

            if (!actionsBound)
                ResolveActions();

            if (!actionsBound)
            {
                ClearAllInputs();
                return;
            }

            // Desktop cursor lock toggle (Escape) and click-to-relock when using Mouse
            if (!IsUsingTouchOrMobile)
            {
                if (toggleCursorAction != null && toggleCursorAction.WasPressedThisFrame())
                    SetCursorLocked(!CursorLocked);

                if (lockCursorOnDesktop && !CursorLocked && attackAction != null && attackAction.WasPressedThisFrame())
                {
                    if (attackAction.activeControl?.device is Mouse)
                    {
                        SetCursorLocked(true);
                        ClearFrameTriggers();
                        return;
                    }
                }
            }

            // Movement (WASD / Arrow keys / Gamepad Left Stick / Mobile OnScreenStick)
            Vector2 rawMove = moveAction != null ? moveAction.ReadValue<Vector2>() : Vector2.zero;
            Move = rawMove.sqrMagnitude > 1f ? rawMove.normalized : rawMove;

            // Look (Mouse delta when locked / Touch drag / Gamepad Right Stick or Mobile OnScreenStick)
            LookDelta = ReadLookDelta();

            // Jump
            JumpPressedThisFrame = jumpAction != null && jumpAction.WasPressedThisFrame();
            JumpReleasedThisFrame = jumpAction != null && jumpAction.WasReleasedThisFrame();
            JumpHeld = jumpAction != null && jumpAction.IsPressed();

            // Sprint & Crouch
            SprintHeld = sprintAction != null && sprintAction.IsPressed();
            CrouchHeld = crouchAction != null && crouchAction.IsPressed();

            // Primary (Grab / Charge-Throw), Secondary (Drop), Interact (Pick up Tool/Coins), Use Tool
            PrimaryPressedThisFrame = attackAction != null && attackAction.WasPressedThisFrame();
            PrimaryHeld = attackAction != null && attackAction.IsPressed();
            PrimaryReleasedThisFrame = attackAction != null && attackAction.WasReleasedThisFrame();

            SecondaryPressedThisFrame = dropAction != null && dropAction.WasPressedThisFrame();
            InteractPressedThisFrame = interactAction != null && interactAction.WasPressedThisFrame();
            InteractHeld = interactAction != null && interactAction.IsPressed();
            InteractReleasedThisFrame = interactAction != null && interactAction.WasReleasedThisFrame();
            UseItemPressedThisFrame = useItemAction != null && useItemAction.WasPressedThisFrame();

            MergeVirtualTouchInput();

            // Hotbar cycling (Mouse wheel + Previous/Next buttons)
            int scroll = 0;
            if (hotbarScrollAction != null)
            {
                float yScroll = hotbarScrollAction.ReadValue<float>();
                if (yScroll > 0.1f) scroll = -1;
                else if (yScroll < -0.1f) scroll = 1;
            }
            if (previousAction != null && previousAction.WasPressedThisFrame()) scroll = -1;
            if (nextAction != null && nextAction.WasPressedThisFrame()) scroll = 1;
            HotbarScrollDelta = scroll;

            // Direct hotbar slot buttons (1..6)
            HotbarDirectSlot = -1;
            for (int i = 0; i < hotbarSlotActions.Length; i++)
            {
                if (hotbarSlotActions[i] != null && hotbarSlotActions[i].WasPressedThisFrame())
                {
                    HotbarDirectSlot = i;
                    break;
                }
            }
        }

        /// <summary>Adds the on-screen touch controls (<see cref="VirtualTouchInput"/>) on top of the action values.</summary>
        void MergeVirtualTouchInput()
        {
            if (!VirtualTouchInput.Active)
            {
                prevTouchJump = prevTouchAttack = prevTouchInteract = prevTouchDrop = prevTouchUse = false;
                return;
            }

            // Raw screen taps/clicks would also fire Attack — the GRAB button owns that on touch
            InputDevice attackDevice = attackAction != null ? attackAction.activeControl?.device : null;
            if (attackDevice is Touchscreen || attackDevice is Mouse)
            {
                PrimaryPressedThisFrame = false;
                PrimaryHeld = false;
                PrimaryReleasedThisFrame = false;
            }

            Vector2 stick = VirtualTouchInput.Move;
            if (stick.sqrMagnitude > Move.sqrMagnitude)
                Move = stick.sqrMagnitude > 1f ? stick.normalized : stick;

            Vector2 look = VirtualTouchInput.ConsumeLook();
            if (look.sqrMagnitude > 0f)
                LookDelta += look * touchLookMultiplier;

            SprintHeld |= VirtualTouchInput.SprintHeld;
            CrouchHeld |= VirtualTouchInput.CrouchHeld;

            bool jump = VirtualTouchInput.JumpHeld;
            JumpPressedThisFrame |= jump && !prevTouchJump;
            JumpReleasedThisFrame |= !jump && prevTouchJump;
            JumpHeld |= jump;
            prevTouchJump = jump;

            bool attack = VirtualTouchInput.AttackHeld;
            PrimaryPressedThisFrame |= attack && !prevTouchAttack;
            PrimaryReleasedThisFrame |= !attack && prevTouchAttack;
            PrimaryHeld |= attack;
            prevTouchAttack = attack;

            bool interact = VirtualTouchInput.InteractHeld;
            InteractPressedThisFrame |= interact && !prevTouchInteract;
            InteractReleasedThisFrame |= !interact && prevTouchInteract;
            InteractHeld |= interact;
            prevTouchInteract = interact;

            bool drop = VirtualTouchInput.DropHeld;
            SecondaryPressedThisFrame |= drop && !prevTouchDrop;
            prevTouchDrop = drop;

            bool use = VirtualTouchInput.UseItemHeld;
            UseItemPressedThisFrame |= use && !prevTouchUse;
            prevTouchUse = use;
        }

        Vector2 ReadLookDelta()
        {
            if (lookAction == null)
                return Vector2.zero;

            Vector2 raw = lookAction.ReadValue<Vector2>();
            if (raw.sqrMagnitude < 1e-6f)
                return Vector2.zero;

            InputDevice device = lookAction.activeControl?.device;
            if (device is Mouse)
                return CursorLocked ? raw : Vector2.zero;

            if (device is Touchscreen)
                return VirtualTouchInput.Active ? Vector2.zero : raw * touchLookMultiplier;

            if (device is Pointer)
                return raw;

            // Continuous stick input (Gamepad rightStick or mobile OnScreenStick)
            return raw * (stickLookSpeed * Time.deltaTime);
        }

        void ClearAllInputs()
        {
            Move = Vector2.zero;
            SprintHeld = false;
            CrouchHeld = false;
            JumpHeld = false;
            PrimaryHeld = false;
            InteractHeld = false;
            ClearFrameTriggers();
        }

        void ClearFrameTriggers()
        {
            LookDelta = Vector2.zero;
            JumpPressedThisFrame = false;
            JumpReleasedThisFrame = false;
            PrimaryPressedThisFrame = false;
            PrimaryReleasedThisFrame = false;
            SecondaryPressedThisFrame = false;
            InteractPressedThisFrame = false;
            InteractReleasedThisFrame = false;
            UseItemPressedThisFrame = false;
            HotbarScrollDelta = 0;
            HotbarDirectSlot = -1;
        }
    }
}
