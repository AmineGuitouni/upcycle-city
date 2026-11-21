using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Props
{
    /// <summary>
    /// A pushable, rolling wire shopping cart for collecting litter.
    /// <para>
    /// Push: [E] on the handle grabs it and the cart rides in front of the player; tap [E] to let it roll, hold [E]
    /// to shove it away. It tracks straight on its castors, keeps a low centre of mass and spins its wheels.
    /// </para>
    /// <para>
    /// Basket: trash thrown or dropped in is stowed visibly inside the wire basket. Each item lies on its slimmest
    /// side in the free spot that keeps the pile lowest (a small height map over the floor), easing into place;
    /// items a little too long are squeezed to fit. The basket holds <see cref="ShoppingCartConfig.basketCapacity"/>
    /// items, or less when they don't fit; when full, new trash bounces back out with a "Cart full" message, and bulky
    /// rubbish (tyres, chairs, TVs) is refused. Taking a piece back out (grab it) hands it back to physics and the
    /// rest settle. Stowed trash counts as collected: it no longer pollutes the street (<see cref="TrashItem.IsContained"/>).
    /// </para>
    /// The look comes from <see cref="ShoppingCartConfig.visualPrefab"/> (Art/UpcycleProps/Tools/shopping_cart.fbx);
    /// colliders and the catch trigger are built here to match it.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("Upcycle City/Props/Shopping Cart")]
    public sealed class ShoppingCart : MonoBehaviour, IInteractable
    {
        const string PushModifierSource = "ShoppingCart";

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
        const float SettleTime = 0.18f;

        static readonly Vector3[] WheelSpots =
        {
            new Vector3(-0.20f, 0f, -0.34f), new Vector3(0.20f, 0f, -0.34f),
            new Vector3(-0.17f, 0f, 0.40f), new Vector3(0.17f, 0f, 0.40f),
        };

        struct Stowed
        {
            public TrashItem Item;
            public Vector3 OriginalScale;
            public RigidbodyInterpolation OriginalInterpolation;
            public Quaternion Orientation;     // lies on its slimmest side, long side along the cart
            public Vector3 Size;               // oriented size (x across, y up, z along), at original scale
            public Vector3 Center;             // local bounds centre, at original scale
            public float Squash;
            public Vector3 FromPos, ToPos;
            public Quaternion FromRot, ToRot;
            public float T;
        }

        [SerializeField] ShoppingCartConfig config;

        Rigidbody body;
        Transform basketContents;
        readonly List<Transform> wheels = new List<Transform>(4);
        readonly List<Collider> solidColliders = new List<Collider>(12);
        readonly List<Stowed> basket = new List<Stowed>(16);
        readonly float[] heights = new float[GridX * GridZ];

        PlayerController pusher;
        readonly List<Collider> ignoredPlayerColliders = new List<Collider>(8);
        bool isPushed;
        // The key press that grabbed the cart must be fully released before it can toggle/throw,
        // otherwise the same hold that grabbed it would immediately be read as a throw.
        bool waitingForGrabRelease;
        float togglePressStart = -1f;
        bool interactHeldLastFrame;
        float wheelSpinAngle;
        float nextRefusalMessage;

        static PhysicsMaterial wheelMaterial;
        static readonly List<MeshFilter> meshBuffer = new List<MeshFilter>(8);

        public bool IsPushed => isPushed;
        public int BasketCount => basket.Count;
        public int Capacity => config != null ? config.basketCapacity : 12;
        public bool IsFull => basket.Count >= Capacity;

        const string PushedPrompt = "[E] tap = let go  ·  hold = shove";
        string idlePrompt;
        int idlePromptCount = -1;

        public Transform Transform => transform;
        public string InteractionPrompt
        {
            get
            {
                if (isPushed)
                    return PushedPrompt;
                if (idlePromptCount != basket.Count)
                {
                    idlePromptCount = basket.Count;
                    idlePrompt = basket.Count >= Capacity
                        ? $"[E] Push cart  ·  FULL {basket.Count}/{Capacity}"
                        : $"[E] Push cart  ·  {basket.Count}/{Capacity} inside";
                }
                return idlePrompt;
            }
        }

        public void SetConfig(ShoppingCartConfig cartConfig) => config = cartConfig;

        void Awake()
        {
            if (config == null)
                config = ScriptableObject.CreateInstance<ShoppingCartConfig>();

            body = GetComponent<Rigidbody>();
            body.mass = config.mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
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

            // Whole cart is an interactable; the catch trigger sits on Ignore Raycast so the crosshair
            // passes through it to the trash resting in the basket (it still detects falling trash).
            GameLayers.SetLayerRecursively(gameObject, GameLayers.Interactable);
            Transform catchTrigger = transform.Find("CatchTrigger");
            if (catchTrigger != null)
                catchTrigger.gameObject.layer = GameLayers.IgnoreRaycast;

            if (GetComponent<FocusHighlight>() == null)
                gameObject.AddComponent<FocusHighlight>();
        }

        public bool CanInteract(PlayerInteractor interactor)
        {
            // Grabbing is handled through the crosshair; releasing is read directly off input while pushing.
            return !isPushed && interactor != null && PlayerContext.Active != null;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (isPushed)
                return;
            BeginPush(PlayerContext.Active);
        }

        // ---------------------------------------------------------------- pushing

        void BeginPush(PlayerController player)
        {
            if (player == null)
                return;

            pusher = player;
            isPushed = true;
            waitingForGrabRelease = true;
            togglePressStart = -1f;
            interactHeldLastFrame = true;

            body.linearDamping = 0f;

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
        }

        void Release()
        {
            if (!isPushed)
                return;

            body.linearDamping = config.rollDamping;

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
        }

        /// <summary>Lets the cart go and launches it forward, harder the longer the button was held.</summary>
        void ReleaseThrow(float heldSeconds)
        {
            // Capture the launch direction from the player's view before Release() clears the pusher.
            Vector3 dir = transform.forward;
            if (pusher != null)
            {
                Camera cam = pusher.CameraController != null ? pusher.CameraController.Camera : null;
                Vector3 fwd = cam != null ? cam.transform.forward : pusher.transform.forward;
                fwd = Vector3.ProjectOnPlane(fwd, Vector3.up);
                if (fwd.sqrMagnitude > 0.0001f)
                    dir = fwd.normalized;
            }

            Release();

            float charge = Mathf.Clamp01(heldSeconds / Mathf.Max(0.01f, config.throwChargeTime));
            float speed = config.throwSpeed * Mathf.Lerp(0.5f, 1f, charge);
            Vector3 v = body.linearVelocity;
            body.linearVelocity = new Vector3(dir.x * speed, v.y, dir.z * speed);
        }

        void OnDisable()
        {
            if (isPushed)
                Release();
        }

        void Update()
        {
            // While the cart is held, E is read straight off input so the player never has to re-aim at
            // the handle. The cart stays held until a fresh E press: a quick TAP lets it go gently, while
            // HOLDING E past the throw threshold LAUNCHES it forward.
            if (isPushed)
            {
                if (pusher == null || pusher.Input == null)
                {
                    Release();
                }
                else if (pusher.Input.InputMode == PlayerInputMode.Gameplay)
                {
                    PlayerInputReader input = pusher.Input;
                    // Edges from the held state as well as the per-frame triggers, so a press is never
                    // missed if the input update and this Update fall on different frames.
                    bool heldNow = input.InteractHeld;
                    bool pressed = input.InteractPressedThisFrame || (heldNow && !interactHeldLastFrame);
                    bool released = input.InteractReleasedThisFrame || (!heldNow && interactHeldLastFrame);
                    interactHeldLastFrame = heldNow;

                    // Swallow the grab press entirely: nothing happens until that button is let go,
                    // so grabbing and the first toggle can never be the same key press.
                    if (waitingForGrabRelease)
                    {
                        if (!heldNow)
                            waitingForGrabRelease = false;
                    }
                    else
                    {
                        if (pressed && togglePressStart < 0f)
                            togglePressStart = Time.time;

                        bool timing = togglePressStart >= 0f;
                        float held = timing ? Time.time - togglePressStart : 0f;

                        // Long press: throw the instant the threshold is crossed (responsive, no need to release).
                        if (timing && heldNow && held >= config.throwHoldThreshold)
                        {
                            togglePressStart = -1f;
                            ReleaseThrow(held);
                        }
                        // Short tap: let the cart go gently on release.
                        else if (timing && released)
                        {
                            togglePressStart = -1f;
                            Release();
                        }
                    }
                }
            }

            ReleaseRemovedBasketItems();
            SettleBasketItems(Time.deltaTime);
            SpinWheels(Time.deltaTime);
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (isPushed && pusher != null)
                DrivePush(dt);
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

        void SpinWheels(float dt)
        {
            if (wheels.Count == 0)
                return;

            float forwardSpeed = Vector3.Dot(body.linearVelocity, transform.forward);
            float circumference = 2f * Mathf.PI * Mathf.Max(0.01f, config.wheelRadius);
            wheelSpinAngle += (forwardSpeed * dt) / circumference * 360f;
            wheelSpinAngle %= 360f;

            for (int i = 0; i < wheels.Count; i++)
            {
                if (wheels[i] != null)
                    wheels[i].localRotation = Quaternion.Euler(wheelSpinAngle, 0f, 0f);
            }
        }

        // ---------------------------------------------------------------- basket

        void OnBasketTriggerEnter(Collider other)
        {
            TrashItem item = other.GetComponentInParent<TrashItem>();
            if (item == null || item.IsDisposed || item.IsHeld || item.IsContained || item.Body == null || Contains(item))
                return;

            if (item.IsBulky)
            {
                Refuse(item, $"{item.DisplayName} is too bulky for the cart — carry it by hand");
                return;
            }
            if (basket.Count >= Capacity)
            {
                Refuse(item, $"Cart full ({basket.Count}/{Capacity}) — unload it at the sorting bins or RÉCUP");
                return;
            }

            var entry = new Stowed { Item = item, OriginalScale = item.transform.localScale };
            Measure(item.transform, out entry.Size, out entry.Center, out entry.Orientation);
            if (!TryPlace(ref entry))
            {
                Refuse(item, basket.Count == 0
                    ? $"{item.DisplayName} doesn't fit in the cart"
                    : $"No room left in the cart ({basket.Count}/{Capacity})");
                return;
            }

            Stow(ref entry);
            basket.Add(entry);
            PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.Drop, item.transform.position, 0.45f);
        }

        bool Contains(TrashItem item)
        {
            for (int i = 0; i < basket.Count; i++)
                if (basket[i].Item == item)
                    return true;
            return false;
        }

        /// <summary>Bounces trash that won't go in back out over the rim, with a message (rate-limited).</summary>
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
                PlayerContext.ActiveInteractor?.NotifyFeedback(message, new Color(0.95f, 0.76f, 0.11f));
            }
        }

        void Stow(ref Stowed entry)
        {
            TrashItem item = entry.Item;
            Rigidbody rb = item.Body;
            entry.OriginalInterpolation = rb.interpolation;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;   // rides with the cart's transform

            // Ignore collisions with the cart body so it settles cleanly.
            foreach (Collider cc in solidColliders)
                if (cc != null)
                    item.SetIgnoreCollision(cc, true);

            item.transform.SetParent(basketContents, true);
            item.transform.localScale = entry.OriginalScale * entry.Squash;
            item.SetContained(true);
            entry.FromPos = item.transform.localPosition;
            entry.FromRot = item.transform.localRotation;
            entry.T = 0f;
        }

        /// <summary>Eases newly stowed (or re-packed) trash into its spot.</summary>
        void SettleBasketItems(float dt)
        {
            for (int i = 0; i < basket.Count; i++)
            {
                Stowed s = basket[i];
                if (s.T >= 1f || s.Item == null)
                    continue;
                s.T = Mathf.Min(1f, s.T + dt / SettleTime);
                float e = 1f - (1f - s.T) * (1f - s.T);
                Transform t = s.Item.transform;
                t.localPosition = Vector3.LerpUnclamped(s.FromPos, s.ToPos, e) + Vector3.up * (Mathf.Sin(s.T * Mathf.PI) * 0.06f);
                t.localRotation = Quaternion.Slerp(s.FromRot, s.ToRot, e);
                basket[i] = s;
            }
        }

        /// <summary>
        /// Trash grabbed back out of the basket (or sorted/destroyed) is handed back to physics at its own size, and
        /// what's left is re-packed so nothing floats where it was.
        /// </summary>
        void ReleaseRemovedBasketItems()
        {
            bool removed = false;
            for (int i = basket.Count - 1; i >= 0; i--)
            {
                Stowed s = basket[i];
                TrashItem item = s.Item;
                if (item != null && !item.IsHeld && !item.IsDisposed && item.transform.parent == basketContents)
                    continue;

                basket.RemoveAt(i);
                removed = true;
                if (item == null)
                    continue;
                if (item.transform.parent == basketContents)
                    item.transform.SetParent(null, true);
                item.transform.localScale = s.OriginalScale;
                item.SetContained(false);
                if (item.Body != null)
                {
                    item.Body.isKinematic = false;
                    item.Body.interpolation = s.OriginalInterpolation;
                }
                foreach (Collider cc in solidColliders)
                    if (cc != null)
                        item.SetIgnoreCollision(cc, false);
            }

            if (removed)
                Repack();
        }

        void Repack()
        {
            System.Array.Clear(heights, 0, heights.Length);
            for (int i = 0; i < basket.Count; i++)
            {
                Stowed s = basket[i];
                if (s.Item == null)
                    continue;
                s.FromPos = s.Item.transform.localPosition;
                s.FromRot = s.Item.transform.localRotation;
                if (TryPlace(ref s))
                {
                    s.Item.transform.localScale = s.OriginalScale * s.Squash;
                    s.T = 0f;
                }
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

            Quaternion toss = Quaternion.Euler(Random.Range(-7f, 7f), Random.value < 0.5f ? 0f : 180f, Random.Range(-7f, 7f));
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
