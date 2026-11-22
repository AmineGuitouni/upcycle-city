using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Props
{
    /// <summary>
    /// A pushable wire shopping cart: the third carry tier, a "moving bag" bought at the Quincaillerie.
    /// <para>
    /// Push: [E] on the cart asks the host to push it (one pusher at a time). Once granted the cart rides in front of the
    /// player; tap [E] to let it roll, hold [E] to shove it away. In co-op the host hands simulation to the pusher
    /// (<see cref="INetworkedPushable"/>, NGO ownership) so pushing feels local; everyone else sees it through the
    /// owner-authoritative transform. Solo uses the same component and requests, with the local authority.
    /// </para>
    /// <para>
    /// Storage: host-authoritative <see cref="CartStorage"/> of <see cref="CarryItems.CartCapacity"/> carry ids, replicated
    /// as three synced records. Trash thrown into the basket is stored by the host (and despawned) when it fits; picked-up
    /// trash that doesn't fit the player's hands/bag overflows into the cart they push or stand next to; the carry screen
    /// moves items bag ↔ cart; sorting bins, the compost bin and RÉCUP also empty it (all in <see cref="RecyclingBagService"/>).
    /// The pile in the basket is cosmetic, rebuilt on every peer from the replicated ids.
    /// </para>
    /// The look comes from <see cref="ShoppingCartConfig.visualPrefab"/>; colliders and the catch trigger are built here.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("Upcycle City/Props/Shopping Cart")]
    public sealed class ShoppingCart : MonoBehaviour, IInteractable, IMachineIdentityReceiver
    {
        const string PushModifierSource = "ShoppingCart";

        /// <summary>How close (m) a player must be to use a cart they aren't pushing (carry screen, overflow, bins, RÉCUP).</summary>
        public const float Reach = 3.5f;

        // Cart geometry in local space (faces +Z, handle at the back). Shared with Tools/Blender/upcycle_tools.py.
        const float HandleZ = -0.50f;
        const float HandleY = 0.96f;
        const float FloorY = 0.50f;
        const float RimY = 0.90f;
        const float FloorHalfWidth = 0.21f;      // usable floor inside the wires
        const float FloorBackZ = -0.29f;
        const float FloorFrontZ = 0.29f;
        const int GridX = 4;
        const int GridZ = 6;
        const float CellW = FloorHalfWidth * 2f / GridX;
        const float CellL = (FloorFrontZ - FloorBackZ) / GridZ;
        const float SettleTime = 0.22f;
        const float PusherCheckGrace = 1.5f;

        static readonly Vector3[] WheelSpots =
        {
            new Vector3(-0.20f, 0f, -0.34f), new Vector3(0.20f, 0f, -0.34f),
            new Vector3(-0.17f, 0f, 0.40f), new Vector3(0.17f, 0f, 0.40f),
        };

        /// <summary>One cosmetic piece of the pile in the basket (a visual-only copy of the stored item).</summary>
        struct Stowed
        {
            public int Id;
            public Transform Item;
            public Vector3 OriginalScale;
            public Quaternion Orientation;     // lies on its slimmest side, long side along the cart
            public Vector3 Size;               // oriented size (x across, y up, z along), at original scale
            public Vector3 Center;             // local bounds centre, at original scale
            public float Squash;
            public Vector3 FromPos, ToPos;
            public Quaternion FromRot, ToRot;
            public float T;
        }

        static readonly List<ShoppingCart> all = new List<ShoppingCart>(8);

        /// <summary>Every cart in the scene (spawned or placed).</summary>
        public static IReadOnlyList<ShoppingCart> All => all;

        /// <summary>The cart the local player is pushing right now, or null.</summary>
        public static ShoppingCart LocalPushed { get; private set; }

        /// <summary>Raised when the local player's relevant cart (pushed or nearby) or its contents change.</summary>
        public static event Action LocalCartChanged;

        [SerializeField] ShoppingCartConfig config;
        [Tooltip("Stable id (host-assigned for bought carts; leave empty to generate one).")]
        [SerializeField] string placementId = string.Empty;

        Rigidbody body;
        INetworkedPushable net;
        INetworkMachineIdentityProvider netIdentity;
        Transform basketContents;
        readonly List<Transform> wheels = new List<Transform>(4);
        readonly List<Collider> solidColliders = new List<Collider>(12);
        readonly List<Stowed> basket = new List<Stowed>(CarryItems.CartCapacity);
        readonly float[] heights = new float[GridX * GridZ];

        CartStorage storage;
        bool storageIsMirror;
        bool isClientReplica;
        bool basketDirty;

        PlayerController pusher;
        readonly List<Collider> ignoredPlayerColliders = new List<Collider>(8);
        bool isPushed;
        bool pushRequestInFlight;
        float pushStartTime;
        // The key press that grabbed the cart must be fully released before it can toggle/throw,
        // otherwise the same hold that grabbed it would immediately be read as a throw.
        bool waitingForGrabRelease;
        float togglePressStart = -1f;
        bool interactHeldLastFrame;
        float wheelSpinAngle;
        float nextRefusalMessage;
        float pusherAssignedTime;
        Vector3 lastVisualPosition;
        bool hasPendingVelocity;
        Vector3 pendingVelocity;

        static PhysicsMaterial wheelMaterial;
        static readonly List<MeshFilter> meshBuffer = new List<MeshFilter>(8);

        public string PlacementId => placementId;
        public string SyncKey => storage != null ? storage.SyncKey : string.Empty;
        public CartStorage Storage => storage;
        public bool IsPushed => isPushed;
        public int Count => storage != null ? storage.Count : 0;
        public int Capacity => CarryItems.CartCapacity;
        public bool IsFull => storage != null && storage.IsFull;
        public ulong PusherClientId => storage != null ? storage.PusherClientId : CartStorage.NoPusher;
        public bool IsPushedByAnyone => PusherClientId != CartStorage.NoPusher;

        /// <summary>True on the machine that simulates the body (the pusher in co-op, the host when free; always in solo).</summary>
        public bool HasSimulationAuthority => net == null || !net.IsNetworkSpawned || net.HasLocalAuthority;

        bool IsAuthoritySide => !isClientReplica && GameAuthority.IsAuthoritative;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            all.Clear();
            LocalPushed = null;
            LocalCartChanged = null;
        }

        // ---------------------------------------------------------------- lookup

        /// <summary>The cart <paramref name="clientId"/> pushes, else the nearest cart within <paramref name="range"/> of <paramref name="position"/>.</summary>
        public static ShoppingCart FindFor(ulong clientId, Vector3? position, float range = Reach)
        {
            ShoppingCart nearest = null;
            float best = range * range;
            for (int i = 0; i < all.Count; i++)
            {
                ShoppingCart cart = all[i];
                if (cart == null || cart.storage == null)
                    continue;
                if (cart.PusherClientId == clientId)
                    return cart;
                if (!position.HasValue)
                    continue;
                float d = (cart.transform.position - position.Value).sqrMagnitude;
                if (d <= best)
                {
                    best = d;
                    nearest = cart;
                }
            }
            return nearest;
        }

        /// <summary>The cart the local player can use right now: the one they push, else the nearest within <see cref="Reach"/>.</summary>
        public static ShoppingCart FindForLocal()
        {
            if (LocalPushed != null)
                return LocalPushed;
            PlayerController player = PlayerContext.Active;
            return player != null ? FindFor(GameAuthority.LocalClientId, player.transform.position) : null;
        }

        public static ShoppingCart FindBySyncKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].storage != null && string.Equals(all[i].storage.SyncKey, key, StringComparison.Ordinal))
                    return all[i];
            }
            return null;
        }

        static void RaiseLocalCartChanged() => LocalCartChanged?.Invoke();

        // ---------------------------------------------------------------- prompts

        const string PushedPrompt = "[E] tap = let go  ·  hold = shove";
        const string BusyPrompt = "Someone is pushing this cart";
        string idlePrompt;
        int idlePromptCount = -1;

        public Transform Transform => transform;
        public string InteractionPrompt
        {
            get
            {
                if (isPushed)
                    return PushedPrompt;
                if (IsPushedByAnyone)
                    return BusyPrompt;
                int count = Count;
                if (idlePromptCount != count)
                {
                    idlePromptCount = count;
                    idlePrompt = count >= Capacity
                        ? $"[E] Push cart  ·  FULL {count}/{Capacity}"
                        : $"[E] Push cart  ·  {count}/{Capacity}";
                }
                return idlePrompt;
            }
        }

        public void SetConfig(ShoppingCartConfig cartConfig) => config = cartConfig;

        // ---------------------------------------------------------------- lifecycle

        void Awake()
        {
            if (config == null)
                config = ScriptableObject.CreateInstance<ShoppingCartConfig>();

            body = GetComponent<Rigidbody>();
            net = GetComponent<INetworkedPushable>();
            netIdentity = GetComponent<INetworkMachineIdentityProvider>();
            body.mass = config.mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            body.centerOfMass = new Vector3(0f, config.centerOfMassHeight, 0f);
            body.linearDamping = config.rollDamping;
            body.angularDamping = 1.5f;

            BuildIfNeeded();

            solidColliders.Clear();
            foreach (Collider c in GetComponentsInChildren<Collider>(true))
            {
                if (!c.isTrigger)
                    solidColliders.Add(c);
            }

            // Whole cart is an interactable; the catch trigger sits on Ignore Raycast so the crosshair passes through it.
            GameLayers.SetLayerRecursively(gameObject, GameLayers.Interactable);
            Transform catchTrigger = transform.Find("CatchTrigger");
            if (catchTrigger != null)
                catchTrigger.gameObject.layer = GameLayers.IgnoreRaycast;

            if (GetComponent<FocusHighlight>() == null)
                gameObject.AddComponent<FocusHighlight>();

            if (net != null)
                net.AuthorityChanged += RefreshSimulation;
            lastVisualPosition = transform.position;
        }

        void OnEnable()
        {
            if (!all.Contains(this))
                all.Add(this);

            // A cart spawned in co-op gets its id from the network spawn (NetworkMachineIdentity)
            if (netIdentity != null && netIdentity.IsAwaitingNetworkSpawn)
                return;
            if (string.IsNullOrWhiteSpace(placementId))
                placementId = Guid.NewGuid().ToString("N").Substring(0, 12);
            BindStorage();
        }

        void OnDisable()
        {
            if (isPushed)
                Release(notifyHost: false);
            all.Remove(this);
            UnbindStorage();
            if (LocalPushed == this)
                LocalPushed = null;
            RaiseLocalCartChanged();
        }

        void OnDestroy()
        {
            if (net != null)
                net.AuthorityChanged -= RefreshSimulation;
        }

        // ---------------------------------------------------------------- identity (IMachineIdentityReceiver)

        public void ApplyPlacementId(string newPlacementId)
        {
            if (string.IsNullOrWhiteSpace(newPlacementId))
                return;
            string clean = newPlacementId.Trim();
            if (clean == placementId && storage != null)
                return;
            placementId = clean;
            if (isActiveAndEnabled)
                BindStorage();
        }

        public void SetClientReplicaMode(bool replica)
        {
            if (isClientReplica == replica)
                return;
            isClientReplica = replica;
            if (storage != null && isActiveAndEnabled)
                BindStorage();
        }

        void BindStorage()
        {
            string key = CartStorage.KeyFor(placementId);
            bool mirror = isClientReplica || !GameAuthority.IsAuthoritative;
            if (storage != null && storage.BaseKey == key && storageIsMirror == mirror)
                return;
            UnbindStorage();

            storage = new CartStorage(key);
            storageIsMirror = mirror;
            storage.Changed += OnStorageChanged;
            // A client mirror receives replicated records. In one-process host + client tests the host's cart already owns
            // these keys in the shared registry: the replica then stays an empty cosmetic copy.
            if (!mirror || !storage.IsKeyTakenByOther(SyncedStateRegistry.Default))
                storage.Register(SyncedStateRegistry.Default);
            basketDirty = true;
        }

        void UnbindStorage()
        {
            if (storage == null)
                return;
            storage.Changed -= OnStorageChanged;
            storage.Unregister();
            storage = null;
            basketDirty = true;
        }

        void OnStorageChanged()
        {
            basketDirty = true;
            idlePromptCount = -1;
            if (IsPushedByAnyone)
                pusherAssignedTime = pusherAssignedTime <= 0f ? Time.unscaledTime : pusherAssignedTime;
            else
                pusherAssignedTime = 0f;
            if (LocalPushed == this || FindForLocal() == this)
                RaiseLocalCartChanged();
        }

        // ---------------------------------------------------------------- host API

        /// <summary>Host: <paramref name="clientId"/> starts pushing (hands them the simulation in co-op).</summary>
        public void AuthoritativeSetPusher(ulong clientId)
        {
            if (storage == null)
                return;
            storage.SetPusher(clientId);
            pusherAssignedTime = Time.unscaledTime;
            net?.AuthoritativeGrantControl(clientId);
        }

        /// <summary>Host: the pusher let go; the cart rolls on at <paramref name="forwardSpeed"/> along its facing.</summary>
        public void AuthoritativeRelease(float forwardSpeed)
        {
            if (storage == null)
                return;
            storage.SetPusher(CartStorage.NoPusher);
            pusherAssignedTime = 0f;
            net?.AuthoritativeReclaimControl();
            float speed = Mathf.Clamp(float.IsFinite(forwardSpeed) ? forwardSpeed : 0f, -4f, 16f);
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            pendingVelocity = fwd * speed;
            hasPendingVelocity = true;
        }

        // ---------------------------------------------------------------- interaction (local)

        public bool CanInteract(PlayerInteractor interactor)
        {
            // Grabbing is handled through the crosshair; releasing is read directly off input while pushing.
            return !isPushed && !pushRequestInFlight && interactor != null && PlayerContext.Active != null && storage != null;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (isPushed || pushRequestInFlight || storage == null)
                return;
            if (IsPushedByAnyone && PusherClientId != GameAuthority.LocalClientId)
            {
                interactor?.NotifyFeedback("Someone else is pushing this cart.", new Color(0.95f, 0.76f, 0.11f));
                return;
            }
            if (LocalPushed != null && LocalPushed != this)
                return;

            pushRequestInFlight = true;
            PlayerController player = PlayerContext.Active;
            RecyclingBagService.RequestPushCart(this, (ok, message) =>
            {
                pushRequestInFlight = false;
                if (this == null)
                    return;
                if (ok)
                    BeginPush(player != null ? player : PlayerContext.Active);
                else if (!string.IsNullOrEmpty(message))
                    PlayerContext.ActiveInteractor?.NotifyFeedback(message, new Color(0.95f, 0.76f, 0.11f));
            });
        }

        void BeginPush(PlayerController player)
        {
            if (player == null || isPushed)
                return;

            pusher = player;
            isPushed = true;
            LocalPushed = this;
            pushStartTime = Time.unscaledTime;
            waitingForGrabRelease = true;
            togglePressStart = -1f;
            interactHeldLastFrame = true;

            // Stop the cart and player from fighting each other while pushing.
            ignoredPlayerColliders.Clear();
            foreach (Collider pc in player.GetComponentsInChildren<Collider>(true))
            {
                if (pc == null || pc.isTrigger)
                    continue;
                ignoredPlayerColliders.Add(pc);
                foreach (Collider cc in solidColliders)
                    if (cc != null)
                        Physics.IgnoreCollision(cc, pc, true);
            }

            player.Modifiers?.SetMultiplier(PushModifierSource, PlayerStat.MoveSpeedMultiplier, config.pushMoveSpeedMultiplier);

            // While pushing, E belongs to the cart (tap = let go, hold = shove), so the same press must not
            // also grab trash or open a shop counter.
            if (player.Interactor != null)
                player.Interactor.InteractSuppressed = true;
            RefreshSimulation();
            RaiseLocalCartChanged();
        }

        /// <summary>Local: stop pushing. Tells the host (with the speed to roll on at) unless the host already freed the cart.</summary>
        void Release(bool notifyHost, float forwardSpeed = float.NaN)
        {
            if (!isPushed)
                return;

            foreach (Collider pc in ignoredPlayerColliders)
            {
                if (pc == null)
                    continue;
                foreach (Collider cc in solidColliders)
                    if (cc != null)
                        Physics.IgnoreCollision(cc, pc, false);
            }
            ignoredPlayerColliders.Clear();

            pusher?.Modifiers?.RemoveMultiplier(PushModifierSource, PlayerStat.MoveSpeedMultiplier);
            if (pusher != null && pusher.Interactor != null)
                pusher.Interactor.InteractSuppressed = false;
            pusher = null;
            isPushed = false;
            waitingForGrabRelease = false;
            togglePressStart = -1f;
            if (LocalPushed == this)
                LocalPushed = null;
            if (HasSimulationAuthority)
                body.linearDamping = config.rollDamping;

            if (notifyHost)
            {
                float speed = float.IsNaN(forwardSpeed) ? Vector3.Dot(body.linearVelocity, transform.forward) : forwardSpeed;
                RecyclingBagService.RequestReleaseCart(this, speed);
            }
            RaiseLocalCartChanged();
        }

        /// <summary>Lets the cart go and launches it forward, harder the longer the button was held.</summary>
        void ReleaseThrow(float heldSeconds)
        {
            float charge = Mathf.Clamp01(heldSeconds / Mathf.Max(0.01f, config.throwChargeTime));
            float speed = config.throwSpeed * Mathf.Lerp(0.5f, 1f, charge);
            if (HasSimulationAuthority && !body.isKinematic)
            {
                Vector3 v = body.linearVelocity;
                Vector3 fwd = transform.forward;
                body.linearVelocity = new Vector3(fwd.x * speed, v.y, fwd.z * speed);
            }
            Release(notifyHost: true, forwardSpeed: speed);
        }

        void RefreshSimulation()
        {
            if (body == null)
                return;
            bool simulate = HasSimulationAuthority;
            if (body.isKinematic == simulate)
                body.isKinematic = !simulate;
            if (simulate)
                body.linearDamping = isPushed ? 0f : config.rollDamping;
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (isPushed)
                UpdatePushInput();

            // Host: the pusher dropped out (NGO handed ownership back to the server)
            if (IsAuthoritySide && net != null && net.IsNetworkSpawned && IsPushedByAnyone
                && pusherAssignedTime > 0f && Time.unscaledTime - pusherAssignedTime > PusherCheckGrace
                && net.ControllerClientId != PusherClientId)
            {
                storage.SetPusher(CartStorage.NoPusher);
                GameAuthority.Current.FlushSyncedStates();
            }

            if (basketDirty)
                SyncBasketVisual();
            SettleBasketItems(Time.deltaTime);
            SpinWheels(Time.deltaTime);
        }

        void UpdatePushInput()
        {
            // The host freed the cart (or gave it to someone else) while we thought we were pushing it
            if (Time.unscaledTime - pushStartTime > PusherCheckGrace && PusherClientId != GameAuthority.LocalClientId)
            {
                Release(notifyHost: false);
                return;
            }
            if (pusher == null || pusher.Input == null)
            {
                Release(notifyHost: true);
                return;
            }
            if (pusher.Input.InputMode != PlayerInputMode.Gameplay)
                return;

            // While the cart is held, E is read straight off input so the player never has to re-aim at the handle. A quick
            // TAP lets it go gently, while HOLDING E past the throw threshold LAUNCHES it forward.
            PlayerInputReader input = pusher.Input;
            bool heldNow = input.InteractHeld;
            bool pressed = input.InteractPressedThisFrame || (heldNow && !interactHeldLastFrame);
            bool released = input.InteractReleasedThisFrame || (!heldNow && interactHeldLastFrame);
            interactHeldLastFrame = heldNow;

            // Swallow the grab press entirely: nothing happens until that button is let go.
            if (waitingForGrabRelease)
            {
                if (!heldNow)
                    waitingForGrabRelease = false;
                return;
            }

            if (pressed && togglePressStart < 0f)
                togglePressStart = Time.time;

            bool timing = togglePressStart >= 0f;
            float held = timing ? Time.time - togglePressStart : 0f;
            if (timing && heldNow && held >= config.throwHoldThreshold)
            {
                togglePressStart = -1f;
                ReleaseThrow(held);
            }
            else if (timing && released)
            {
                togglePressStart = -1f;
                Release(notifyHost: true);
            }
        }

        void FixedUpdate()
        {
            RefreshSimulation();
            if (!HasSimulationAuthority || body.isKinematic)
                return;

            if (hasPendingVelocity)
            {
                hasPendingVelocity = false;
                if (!isPushed)
                {
                    Vector3 v = body.linearVelocity;
                    body.linearVelocity = new Vector3(pendingVelocity.x, v.y, pendingVelocity.z);
                }
            }

            if (isPushed && pusher != null)
                DrivePush(Time.fixedDeltaTime);
            else
                ApplyWheelGrip();
        }

        void DrivePush(float dt)
        {
            Camera cam = pusher.CameraController != null ? pusher.CameraController.Camera : null;
            Vector3 forward = cam != null ? cam.transform.forward : pusher.transform.forward;
            Vector3 flatFwd = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 0.0001f)
                flatFwd = transform.forward;
            flatFwd.Normalize();

            // The handle sits pushDistance ahead of the player; the cart's origin is ahead of its handle.
            Vector3 targetHandle = pusher.transform.position + flatFwd * config.pushDistance;
            Vector3 targetBody = targetHandle - flatFwd * HandleZ;

            Vector3 toTarget = targetBody - body.position;
            toTarget.y = 0f;
            Vector3 desiredVel = Vector3.ClampMagnitude(toTarget * config.followStiffness, config.maxPushSpeed);
            Vector3 v = body.linearVelocity;
            body.linearVelocity = new Vector3(desiredVel.x, v.y, desiredVel.z);

            Quaternion targetRot = Quaternion.LookRotation(flatFwd, Vector3.up);
            body.MoveRotation(Quaternion.Slerp(body.rotation, targetRot, Mathf.Clamp01(config.turnSharpness * dt)));
        }

        void ApplyWheelGrip()
        {
            Vector3 v = body.linearVelocity;
            Vector3 horiz = new Vector3(v.x, 0f, v.z);
            Vector3 fwd = transform.forward;
            Vector3 fwdVel = fwd * Vector3.Dot(horiz, fwd);
            Vector3 latVel = horiz - fwdVel;
            Vector3 newHoriz = fwdVel + latVel * (1f - config.lateralGrip);
            body.linearVelocity = new Vector3(newHoriz.x, v.y, newHoriz.z);
        }

        // Spins from how far the cart actually moved, so remote (kinematic) copies spin too
        void SpinWheels(float dt)
        {
            Vector3 pos = transform.position;
            Vector3 delta = pos - lastVisualPosition;
            lastVisualPosition = pos;
            if (wheels.Count == 0 || dt <= 0f)
                return;

            float travel = Vector3.Dot(delta, transform.forward);
            float circumference = 2f * Mathf.PI * Mathf.Max(0.01f, config.wheelRadius);
            wheelSpinAngle = (wheelSpinAngle + travel / circumference * 360f) % 360f;
            for (int i = 0; i < wheels.Count; i++)
            {
                if (wheels[i] != null)
                    wheels[i].localRotation = Quaternion.Euler(wheelSpinAngle, 0f, 0f);
            }
        }

        // ---------------------------------------------------------------- basket: thrown-in trash (host)

        void OnBasketTriggerEnter(Collider other)
        {
            if (!IsAuthoritySide || storage == null)
                return;
            TrashItem item = other.GetComponentInParent<TrashItem>();
            // Only free trash this machine simulates: a kinematic body is a remote copy (or something being moved by code)
            if (item == null || item.IsDisposed || item.IsHeld || item.IsContained || item.Body == null || item.Body.isKinematic)
                return;

            int id = CarryItems.IdForTrash(item);
            if (id < 0)
            {
                Refuse(item, $"{item.DisplayName} is too bulky for the cart: drag it to RÉCUP");
                return;
            }
            if (storage.IsFull)
            {
                Refuse(item, $"Cart full ({storage.Count}/{Capacity}): unload it at the sorting bins or RÉCUP");
                return;
            }

            Vector3 at = item.transform.position;
            WasteStream stream = item.Stream;
            storage.TryAdd(id);
            item.Dispose();
            GameAuthority.Current.FlushSyncedStates();
            PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.CartStow, at, 0.45f,
                materialStream: FeedbackSignal.EncodeMaterial(stream, false));
        }

        /// <summary>Host: bounces trash that won't go in back out over the rim, with a message (rate-limited).</summary>
        void Refuse(TrashItem item, string message)
        {
            Rigidbody rb = item.Body;
            if (rb != null && !rb.isKinematic)
            {
                Vector3 outward = item.transform.position - transform.TransformPoint(new Vector3(0f, FloorY, 0f));
                outward.y = 0f;
                outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : -transform.forward;
                rb.linearVelocity = outward * 1.6f + Vector3.up * 2.4f;
            }

            if (Time.time >= nextRefusalMessage)
            {
                nextRefusalMessage = Time.time + 1.5f;
                ulong target = IsPushedByAnyone ? PusherClientId : GameAuthority.LocalClientId;
                GameAuthority.Current.SendTargetedFeedback(target, message, new Color(0.95f, 0.76f, 0.11f),
                    PlayerFeedbackEvent.InventoryFull, transform.position);
            }
        }

        // ---------------------------------------------------------------- basket: the cosmetic pile

        /// <summary>
        /// Rebuilds the pile from the stored ids: pieces that are still in the same place stay, the rest are re-made from
        /// the waste catalog's look and eased in. Runs only when the (replicated) contents change.
        /// </summary>
        void SyncBasketVisual()
        {
            basketDirty = false;
            if (basketContents == null)
                return;
            int n = storage != null ? storage.Count : 0;

            int keep = 0;
            while (keep < basket.Count && keep < n && basket[keep].Id == storage[keep] && basket[keep].Item != null)
                keep++;
            bool removed = keep < basket.Count;
            for (int i = basket.Count - 1; i >= keep; i--)
            {
                if (basket[i].Item != null)
                    Destroy(basket[i].Item.gameObject);
                basket.RemoveAt(i);
            }

            Array.Clear(heights, 0, heights.Length);
            for (int i = 0; i < basket.Count; i++)
            {
                Stowed s = basket[i];
                if (removed)
                {
                    s.FromPos = s.Item.localPosition;
                    s.FromRot = s.Item.localRotation;
                    s.T = 0f;
                }
                PlacePiece(ref s);
                basket[i] = s;
            }

            for (int i = keep; i < n; i++)
            {
                Stowed s = MakePiece(storage[i]);
                PlacePiece(ref s);
                basket.Add(s);
            }
        }

        void PlacePiece(ref Stowed s)
        {
            bool fits = TryPlace(ref s);
            s.Item.gameObject.SetActive(fits);   // a heaped-up cart shows what fits; the count is in the prompt
            if (fits)
                s.Item.localScale = s.OriginalScale * s.Squash;
        }

        Stowed MakePiece(int id)
        {
            CarryItems.Info info = CarryItems.Resolve(id);
            WasteCatalog catalog = CarryItems.Catalog;
            WasteCatalog.Entry entry = catalog != null && info.CatalogIndex >= 0 && info.CatalogIndex < catalog.Entries.Count
                ? catalog.Entries[info.CatalogIndex]
                : null;

            GameObject go;
            if (entry != null && entry.visualPrefab != null)
            {
                go = Instantiate(entry.visualPrefab, basketContents, false);
                go.transform.localScale = Vector3.one * entry.visualScale;
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                    Destroy(c);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(basketContents, false);
                go.transform.localScale = new Vector3(0.12f, 0.08f, 0.16f);
                var block = new MaterialPropertyBlock();
                Color accent = info.Valid ? CarryItems.AccentFor(info) : Color.gray;
                block.SetColor("_BaseColor", accent);
                block.SetColor("_Color", accent);
                go.GetComponent<Renderer>().SetPropertyBlock(block);
            }
            go.name = $"Stowed_{(info.Valid ? info.Name : id.ToString())}";
            GameLayers.SetLayerRecursively(go, GameLayers.IgnoreRaycast);

            var s = new Stowed { Id = id, Item = go.transform, OriginalScale = go.transform.localScale };
            Measure(go.transform, out s.Size, out s.Center, out s.Orientation);
            s.FromPos = new Vector3(UnityEngine.Random.Range(-0.1f, 0.1f), RimY - FloorY + 0.45f, UnityEngine.Random.Range(-0.15f, 0.15f));
            s.FromRot = UnityEngine.Random.rotation;
            go.transform.localPosition = s.FromPos;
            go.transform.localRotation = s.FromRot;
            s.T = 0f;
            return s;
        }

        /// <summary>Eases newly stowed (or re-packed) pieces into their spot.</summary>
        void SettleBasketItems(float dt)
        {
            for (int i = 0; i < basket.Count; i++)
            {
                Stowed s = basket[i];
                if (s.T >= 1f || s.Item == null)
                    continue;
                s.T = Mathf.Min(1f, s.T + dt / SettleTime);
                float e = 1f - (1f - s.T) * (1f - s.T);
                Transform t = s.Item;
                t.localPosition = Vector3.LerpUnclamped(s.FromPos, s.ToPos, e) + Vector3.up * (Mathf.Sin(s.T * Mathf.PI) * 0.06f);
                t.localRotation = Quaternion.Slerp(s.FromRot, s.ToRot, e);
                basket[i] = s;
            }
        }

        /// <summary>
        /// Finds the lowest spot on the basket floor/pile for the item's footprint and reserves it.
        /// False when it would stick out above the heap limit (or can't be squeezed small enough).
        /// </summary>
        bool TryPlace(ref Stowed s)
        {
            float squash = Mathf.Min(1f, (FloorHalfWidth * 2f) / Mathf.Max(0.01f, s.Size.x),
                (FloorFrontZ - FloorBackZ) / Mathf.Max(0.01f, s.Size.z));
            if (squash < config.minSquash)
                return false;

            float fx = s.Size.x * squash, fz = s.Size.z * squash, h = s.Size.y * squash;
            int cx = Mathf.Clamp(Mathf.CeilToInt(fx / CellW - 0.25f), 1, GridX);
            int cz = Mathf.Clamp(Mathf.CeilToInt(fz / CellL - 0.25f), 1, GridZ);
            float maxTop = RimY - FloorY + config.heapAboveRim;

            int bestI = -1, bestJ = -1;
            float bestBase = float.MaxValue, bestScore = float.MaxValue;
            for (int j = 0; j + cz <= GridZ; j++)
            {
                for (int i = 0; i + cx <= GridX; i++)
                {
                    float floor = 0f;
                    for (int a = i; a < i + cx; a++)
                        for (int b = j; b < j + cz; b++)
                            floor = Mathf.Max(floor, heights[b * GridX + a]);
                    // lowest first, then fill from the handle end, then hug the middle
                    float score = floor + j * 0.004f + Mathf.Abs(i + cx * 0.5f - GridX * 0.5f) * 0.002f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestBase = floor;
                        bestI = i;
                        bestJ = j;
                    }
                }
            }
            if (bestI < 0 || bestBase + h > maxTop)
                return false;

            float top = bestBase + h * 0.85f;   // pieces nest a little into each other
            for (int a = bestI; a < bestI + cx; a++)
                for (int b = bestJ; b < bestJ + cz; b++)
                    heights[b * GridX + a] = top;

            float x = -FloorHalfWidth + (bestI + cx * 0.5f) * CellW;
            float z = FloorBackZ + (bestJ + cz * 0.5f) * CellL;
            x = Mathf.Clamp(x, -FloorHalfWidth + fx * 0.5f, FloorHalfWidth - fx * 0.5f);
            z = Mathf.Clamp(z, FloorBackZ + fz * 0.5f, FloorFrontZ - fz * 0.5f);

            s.Squash = squash;
            s.ToRot = s.Orientation;
            s.ToPos = new Vector3(x, bestBase + h * 0.5f, z) - s.ToRot * (s.Center * squash);
            return true;
        }

        /// <summary>
        /// Measures the item's mesh bounds in its own space (at its current scale) and picks a resting orientation:
        /// slimmest side down, longest side along the cart, with a little random tilt so the pile looks tossed in.
        /// </summary>
        static void Measure(Transform item, out Vector3 orientedSize, out Vector3 scaledCenter, out Quaternion orientation)
        {
            Bounds local = default;
            bool any = false;
            Matrix4x4 toItem = item.worldToLocalMatrix;
            item.GetComponentsInChildren(meshBuffer);
            for (int m = 0; m < meshBuffer.Count; m++)
            {
                Mesh mesh = meshBuffer[m].sharedMesh;
                if (mesh == null)
                    continue;
                Bounds mb = mesh.bounds;
                Matrix4x4 toLocal = toItem * meshBuffer[m].transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents,
                        new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    Vector3 p = toLocal.MultiplyPoint3x4(corner);
                    if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                    else local.Encapsulate(p);
                }
            }
            meshBuffer.Clear();
            if (!any)
                local = new Bounds(Vector3.zero, Vector3.one * 0.15f);

            Vector3 scale = item.localScale;
            Vector3 size = Vector3.Scale(local.size, scale);
            scaledCenter = Vector3.Scale(local.center, scale);

            // Slimmest axis up
            Quaternion lay;
            Vector3 laid;
            if (size.y <= size.x && size.y <= size.z) { lay = Quaternion.identity; laid = size; }
            else if (size.x <= size.z) { lay = Quaternion.Euler(0f, 0f, 90f); laid = new Vector3(size.y, size.x, size.z); }
            else { lay = Quaternion.Euler(90f, 0f, 0f); laid = new Vector3(size.x, size.z, size.y); }

            // Longest side along the cart
            if (laid.x > laid.z)
            {
                lay = Quaternion.Euler(0f, 90f, 0f) * lay;
                laid = new Vector3(laid.z, laid.y, laid.x);
            }

            Quaternion toss = Quaternion.Euler(UnityEngine.Random.Range(-7f, 7f), UnityEngine.Random.value < 0.5f ? 0f : 180f, UnityEngine.Random.Range(-7f, 7f));
            orientation = toss * lay;
            orientedSize = laid;
        }

        // ---------------------------------------------------------------- build

        void BuildIfNeeded()
        {
            Transform existing = transform.Find("CartVisual");
            if (existing == null)
            {
                GameObject visual = config.visualPrefab != null
                    ? Instantiate(config.visualPrefab, transform, false)
                    : BuildPlaceholderVisual();
                visual.name = "CartVisual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                foreach (Collider c in visual.GetComponentsInChildren<Collider>(true))
                    Destroy(c);
                existing = visual.transform;
            }

            wheels.Clear();
            foreach (Transform t in existing.GetComponentsInChildren<Transform>(true))
            {
                if (t != existing && t.name.StartsWith("Wheel"))
                    wheels.Add(t);
            }

            if (transform.Find("CartColliders") == null)
                BuildColliders();

            basketContents = transform.Find("BasketContents");
            if (basketContents == null)
            {
                basketContents = new GameObject("BasketContents").transform;
                basketContents.SetParent(transform, false);
                basketContents.localPosition = new Vector3(0f, FloorY, 0f);
            }

            if (transform.Find("CatchTrigger") == null)
            {
                var trigGo = new GameObject("CatchTrigger");
                trigGo.transform.SetParent(transform, false);
                trigGo.transform.localPosition = new Vector3(0f, 0.74f, 0.03f);
                trigGo.layer = GameLayers.IgnoreRaycast;   // physics still sees it; raycasts reach the basket contents
                var trig = trigGo.AddComponent<BoxCollider>();
                trig.isTrigger = true;
                trig.size = new Vector3(0.48f, 0.42f, 0.76f);
                trigGo.AddComponent<CartBasketTrigger>().Owner = this;
            }
        }

        /// <summary>Invisible collision shapes matching the wire basket, handle, chassis and castors.</summary>
        void BuildColliders()
        {
            var root = new GameObject("CartColliders").transform;
            root.SetParent(transform, false);

            Box(root, "Floor", new Vector3(0f, FloorY - 0.01f, 0f), new Vector3(0.46f, 0.02f, 0.64f), Vector3.zero);
            for (int s = -1; s <= 1; s += 2)
                Box(root, s < 0 ? "SideL" : "SideR", new Vector3(s * 0.24f, 0.70f, 0.02f), new Vector3(0.012f, 0.41f, 0.80f), new Vector3(0f, 0f, -s * 5.7f));
            Box(root, "Front", new Vector3(0f, 0.70f, 0.37f), new Vector3(0.50f, 0.43f, 0.012f), new Vector3(19.3f, 0f, 0f));
            Box(root, "Back", new Vector3(0f, 0.70f, -0.35f), new Vector3(0.50f, 0.41f, 0.012f), new Vector3(-14f, 0f, 0f));
            Box(root, "Handle", new Vector3(0f, HandleY, HandleZ), new Vector3(0.56f, 0.04f, 0.04f), Vector3.zero);
            Box(root, "Chassis", new Vector3(0f, 0.16f, 0.02f), new Vector3(0.40f, 0.04f, 0.78f), Vector3.zero);

            if (wheelMaterial == null)
            {
                wheelMaterial = new PhysicsMaterial("CartWheel")
                {
                    dynamicFriction = 0.04f,
                    staticFriction = 0.04f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounciness = 0f,
                };
            }
            float r = config.wheelRadius;
            for (int i = 0; i < WheelSpots.Length; i++)
            {
                var go = new GameObject("WheelContact");
                go.transform.SetParent(root, false);
                go.transform.localPosition = WheelSpots[i] + new Vector3(0f, r, 0f);
                var sc = go.AddComponent<SphereCollider>();
                sc.radius = r;
                sc.sharedMaterial = wheelMaterial;
            }
        }

        static void Box(Transform parent, string name, Vector3 center, Vector3 size, Vector3 euler)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.AddComponent<BoxCollider>().size = size;
        }

        /// <summary>Plain stand-in used only when no cart model is assigned in the config.</summary>
        GameObject BuildPlaceholderVisual()
        {
            var root = new GameObject("CartVisual");
            root.transform.SetParent(transform, false);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var metal = new Material(shader) { name = "CartPlaceholder", color = new Color(0.72f, 0.74f, 0.77f) };
            Part(root.transform, new Vector3(0f, FloorY, 0f), new Vector3(0.46f, 0.02f, 0.64f), metal);
            for (int s = -1; s <= 1; s += 2)
                Part(root.transform, new Vector3(s * 0.24f, RimY, 0.02f), new Vector3(0.02f, 0.02f, 0.82f), metal);
            Part(root.transform, new Vector3(0f, HandleY, HandleZ), new Vector3(0.56f, 0.03f, 0.03f), metal);
            return root;
        }

        static void Part(Transform parent, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
        }

        /// <summary>Relays basket trigger callbacks from the child trigger object to the cart.</summary>
        sealed class CartBasketTrigger : MonoBehaviour
        {
            public ShoppingCart Owner;
            void OnTriggerEnter(Collider other) => Owner?.OnBasketTriggerEnter(other);
        }
    }
}
